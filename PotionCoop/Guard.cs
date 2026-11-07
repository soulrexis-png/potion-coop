using System;
using System.IO;
using System.IO.Compression;
using System.Security.Cryptography;
using BepInEx;
using HarmonyLib;
using UnityEngine;
using CodeBase.Components.Hero;
using CodeBase.Components.Slots;
using CodeBase.Components.Potions;
using CodeBase.Services.ConfigsService;
using CodeBase.Services.PotionsPersistenceService;

namespace PotionCoop;

// ---------- 1. Проверка версии игры ----------
// При сборке мода рядом с ним пишется отпечаток GameAssembly.dll. Если игра обновилась и отпечаток не совпал —
// мод ничего не патчит (иначе возможны вылеты) и показывает предупреждение.
static class VersionGuard
{
    public static bool Check()
    {
        try
        {
            var hf = Path.Combine(Paths.PluginPath, "PotionCoop.gamehash");
            if (!File.Exists(hf)) { Plugin.L.LogInfo("GUARD: отпечатка версии игры нет — проверку пропускаю"); return false; }
            var want = File.ReadAllText(hf).Trim().ToUpperInvariant();
            var ga = Path.Combine(Paths.GameRootPath, "GameAssembly.dll");
            string have;
            using (var sha = SHA256.Create())
            using (var fs = new FileStream(ga, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                have = Convert.ToHexString(sha.ComputeHash(fs));
            if (have == want) { Plugin.L.LogInfo("GUARD: версия игры совпадает с той, под которую собран мод"); return false; }
            Plugin.L.LogWarning($"GUARD: игра обновилась (ждал {want.Substring(0, 12)}…, вижу {have.Substring(0, 12)}…) — мод выключен до новой версии");
            return true;
        }
        catch (Exception e) { Plugin.L.LogWarning("GUARD: не смог проверить версию игры: " + e.Message); return false; }
    }
}

// Плашка «игра обновилась» — висит в главном меню первые 2 минуты
public class GameChangedBanner : MonoBehaviour
{
    public GameChangedBanner(IntPtr p) : base(p) { }
    void OnGUI()
    {
        if (Time.unscaledTime > 120f) return;
        float w = 640, h = 90;
        GUI.Box(new Rect((Screen.width - w) / 2, 40, w, h),
            "Общая аптека: игра обновилась, а мод собран под прошлую версию.\n" +
            "Мод выключен, чтобы ничего не сломать. Играть можно как обычно.\n" +
            "Дождись новой версии мода (github.com — страница PotionCoop).");
    }
}

// ---------- 2. Логи одним нажатием (F11) ----------
static class LogCollector
{
    // zip — в буфер обмена: в чате просто Ctrl+V
    static void HandOver(string zipPath)
    {
        try
        {
            var ps = new System.Diagnostics.ProcessStartInfo("powershell.exe",
                "-NoProfile -WindowStyle Hidden -Command \"Set-Clipboard -LiteralPath '" + zipPath.Replace("'", "''") + "'\"")
            { CreateNoWindow = true, UseShellExecute = false };
            System.Diagnostics.Process.Start(ps);   // не ждём — игра не должна подвиснуть
        }
        catch { }
        // папку не открываем: проводник поверх игры свернул бы её
    }

    public static void Collect()
    {
        try
        {
            var desk = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
            var to = Path.Combine(desk, $"PotionCoop_logs_{DateTime.Now:yyyy-MM-dd_HH-mm-ss}.zip");
            int n = 0;
            using (var fs = File.Create(to))
            using (var z = new ZipArchive(fs, ZipArchiveMode.Create))
            {
                foreach (var p in new[] {
                    Path.Combine(Paths.BepInExRootPath, "LogOutput.log"),
                    Path.Combine(Paths.BepInExRootPath, "potioncoop_diag.txt"),
                    Path.Combine(Paths.ConfigPath, "roma.potioncoop.cfg") })
                {
                    if (!File.Exists(p)) continue;
                    using var src = new FileStream(p, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                    using var dst = z.CreateEntry(Path.GetFileName(p)).Open();
                    src.CopyTo(dst); n++;
                }
            }
            HandOver(to);
            Coop.Say(Loc.L("Логи собраны (zip на рабочем столе) и скопированы — вставь Ctrl+V туда, куда пишешь о проблеме", "Logs collected (zip on the Desktop) and copied — paste with Ctrl+V where you report the problem"), 8f);
            Plugin.L.LogInfo($"LOGS: собрал {n} файлов -> {to}");
        }
        catch (Exception e) { Coop.Say(Loc.L("Не получилось собрать логи: ", "Couldn't collect logs: ") + e.Message, 6f); }
    }
}

// ---------- 3. Руки и фартук побольше (по желанию, в конфиге) ----------
static class CapacityPatches
{
    [HarmonyPostfix]
    [HarmonyPatch(typeof(HeroConfig), nameof(HeroConfig.HandCapacity), MethodType.Getter)]
    static void Hand_Post(ref int __result) { __result += Math.Max(0, Plugin.HandBonus.Value); }

    [HarmonyPostfix]
    [HarmonyPatch(typeof(HeroConfig), nameof(HeroConfig.ApronCapacity), MethodType.Getter)]
    static void Apron_Post(ref int __result) { __result += Math.Max(0, Plugin.ApronBonus.Value); }
}

// ---------- 4. Разведка: сколько лабораторий и склянок в игре ----------
static class LabScout
{
    static bool _done;
    public static void Report()
    {
        if (_done) return;
        _done = true;
        try
        {
            var picker = UnityEngine.Object.FindFirstObjectByType<HeroPotionPicker>();
            var pers = picker?._potionsPersistence?.TryCast<PotionsPersistence>();
            int slots = UnityEngine.Object.FindObjectsByType<SlotFacade>(FindObjectsInactive.Include, FindObjectsSortMode.None).Length;
            int potions = UnityEngine.Object.FindObjectsByType<PotionFacade>(FindObjectsInactive.Include, FindObjectsSortMode.None).Length;
            Plugin.L.LogInfo($"LABS: в сцене ячеек {slots}, склянок {potions}");
            if (pers == null) { Plugin.L.LogInfo("LABS: сервис сейва не найден"); return; }
            var active = pers.ActiveManifest;
            if (active != null) Plugin.L.LogInfo($"LABS: текущая раскладка: склянок с этикеткой {active.LabelledPotionCount}, мензурок {active.BeakerCount}, всего записей {active._entries?.Length ?? 0}");
            var deals = pers.LabDeals();
            if (deals == null) return;
            Plugin.L.LogInfo($"LABS: лабораторий в игре: {deals.LabCount} (первая — №{LabDealsConfig.FIRST_LAB})");
            var later = deals._laterLabs;
            for (int i = 0; later != null && i < later.Length; i++)
            {
                var d = later[i]; var m = d?.Current;
                if (m == null) continue;
                Plugin.L.LogInfo($"LABS: лаборатория {i + 2}: склянок {m.LabelledPotionCount}, мензурок {m.BeakerCount}, вариантов раскладки {d._manifests?.Length ?? 0}");
            }
        }
        catch (Exception e) { Plugin.L.LogWarning("LABS: " + e.Message); }
    }
}
