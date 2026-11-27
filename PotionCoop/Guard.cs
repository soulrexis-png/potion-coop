using System.Linq;
using System.Reflection;
using System.Collections.Generic;
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
        // перенос по словам внутри рамки (раньше длинные строки обрезались по краям)
        float w = Mathf.Min(900, Screen.width - 40), h = 120;
        GUIStyle st;
        try { st = new GUIStyle(GUI.skin.box) { wordWrap = true, alignment = TextAnchor.MiddleCenter, fontSize = 14 }; }
        catch { st = GUI.skin.box; }   // если стиль в игре вырезан — как раньше
        GUI.Box(new Rect((Screen.width - w) / 2, 40, w, h),
            "Общая аптека: игра обновилась, а мод собран под прошлую версию. Мод выключен, играть можно как обычно.\n" +
            "PotionCoop: the game was updated and the mod was built for the previous version. The mod is off; play as usual.\n" +
            "Дождись новой версии мода / Wait for a new mod version: github.com/soulrexis-png/potion-coop", st);
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

    static string[] MyFiles => new[] {
        Path.Combine(Paths.BepInExRootPath, "LogOutput.log"),
        Path.Combine(Paths.BepInExRootPath, "potioncoop_diag.txt"),
        Path.Combine(Paths.ConfigPath, "roma.potioncoop.cfg") };

    static byte[] Tail(string path, int max)
    {
        using var src = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        long start = Math.Max(0, src.Length - max);
        src.Seek(start, SeekOrigin.Begin);
        var buf = new byte[src.Length - start];
        int read = 0; while (read < buf.Length) { int k = src.Read(buf, read, buf.Length - read); if (k <= 0) break; read += k; }
        return buf;
    }

    // ---- обезличивание: имя пользователя Windows и Steam ID прячем; публичный ник Steam оставляем — по нему видно, кто есть кто ----
    static byte[] Anon(byte[] data)
    {
        try
        {
            var t = System.Text.Encoding.UTF8.GetString(data);
            t = System.Text.RegularExpressions.Regex.Replace(t, @"(Users[\\/])[^\\/\r\n]+", "$1<user>");
            t = System.Text.RegularExpressions.Regex.Replace(t, @"\b7656\d{9}(\d{4})\b", "id…$1");
            // начало путей (диск и папки до игры) тоже убираем: «E:\Games\…\steamapps» → «…\steamapps»
            t = System.Text.RegularExpressions.Regex.Replace(t, @"[A-Za-z]:[\\/][^\r\n:*?""<>|]*?(?=(AppData|steamapps|BepInEx|PotionCoop_|A Place for Every Potion)[\\/])", "…\\");
            return System.Text.Encoding.UTF8.GetBytes(t);
        }
        catch { return data; }
    }

    // мои логи одним маленьким zip — отправить хосту по сети (сообщение Steam не больше ~500 КБ)
    public static byte[] PackMine()
    {
        foreach (var max in new[] { 4_000_000, 1_500_000, 500_000, 150_000 })
        {
            using var ms = new MemoryStream();
            using (var z = new ZipArchive(ms, ZipArchiveMode.Create, true))
                foreach (var p in MyFiles)
                {
                    if (!File.Exists(p)) continue;
                    var data = Anon(Tail(p, max));
                    using var dst = z.CreateEntry(Path.GetFileName(p), System.IO.Compression.CompressionLevel.Optimal).Open();
                    dst.Write(data, 0, data.Length);
                }
            if (ms.Length <= 450_000) return ms.ToArray();
        }
        return Array.Empty<byte>();
    }

    static string SafeName(string n)
    {
        foreach (var c in Path.GetInvalidFileNameChars()) n = n.Replace(c, '_');
        return string.IsNullOrWhiteSpace(n) ? "гость" : n.Trim();
    }

    public static void Collect() => Collect(null);
    public static void Collect(Dictionary<string, byte[]> guests, string me = null)
    {
        try
        {
            var desk = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
            var to = Path.Combine(desk, $"PotionCoop_logs_{DateTime.Now:yyyy-MM-dd_HH-mm-ss}.zip");
            int n = 0;
            using (var fs = File.Create(to))
            using (var z = new ZipArchive(fs, ZipArchiveMode.Create))
            {
                foreach (var p in MyFiles)
                {
                    if (!File.Exists(p)) continue;
                    var mine = Anon(Tail(p, 20_000_000));
                    // свои логи — в папку со своим ником (если собираем вместе с другими), иначе в корень
                    var mineName = guests != null && guests.Count > 0 && !string.IsNullOrEmpty(me) ? $"{SafeName(me)}/{Path.GetFileName(p)}" : Path.GetFileName(p);
                    using var dst = z.CreateEntry(mineName).Open();
                    dst.Write(mine, 0, mine.Length); n++;
                }
                // логи остальных игроков — каждому своя папка с его ником
                if (guests != null)
                    foreach (var g in guests)
                    {
                        try
                        {
                            using var gz = new ZipArchive(new MemoryStream(g.Value), ZipArchiveMode.Read);
                            foreach (var e in gz.Entries)
                            {
                                if (gz.Entries.Count > 8) throw new Exception("слишком много файлов");
                                using var src = e.Open();
                                using var dst = z.CreateEntry($"{SafeName(g.Key)}/{SafeName(e.Name)}").Open();
                                // не больше 25 МБ на файл: маленький архив не должен развернуться в гигабайты на рабочем столе
                                var buf = new byte[81920]; long total = 0; int k;
                                while ((k = src.Read(buf, 0, buf.Length)) > 0)
                                {
                                    total += k; if (total > 25_000_000) { Plugin.L.LogWarning($"LOGS: файл {e.Name} от {g.Key} обрезан на 25 МБ"); break; }
                                    dst.Write(buf, 0, k);
                                }
                                n++;
                            }
                        }
                        catch (Exception ex) { Plugin.L.LogWarning($"LOGS: логи {g.Key} не разобрал: {ex.Message}"); }
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
// Свойства ищем по имени при запуске: игра переименовывает их в обновлениях (было ApronCapacity).
// Не нашли — бонус просто не работает, мод грузится дальше.
static class CapacityPatches
{
    public static void Apply(Harmony h)
    {
        var props = typeof(HeroConfig).GetProperties(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)
            .Where(p => p.PropertyType == typeof(int) && p.GetMethod != null && !p.Name.StartsWith("_")).ToList();   // _поля — обёртки полей, их не перехватить
        Plugin.L.LogInfo("CAPACITY: свойства HeroConfig: " + string.Join(", ", props.Select(p => p.Name)));
        Hook(h, props, "Hand", nameof(Hand_Post));
        Hook(h, props, "Apron", nameof(Apron_Post));
    }

    static void Hook(Harmony h, List<PropertyInfo> props, string word, string post)
    {
        var p = props.FirstOrDefault(x => x.Name.IndexOf(word, StringComparison.OrdinalIgnoreCase) >= 0
                                       && x.Name.IndexOf("Cap", StringComparison.OrdinalIgnoreCase) >= 0)
             ?? props.FirstOrDefault(x => x.Name.IndexOf(word, StringComparison.OrdinalIgnoreCase) >= 0);
        if (p == null) { Plugin.L.LogWarning($"CAPACITY: не нашёл свойство «{word}» — бонус выключен"); return; }
        h.Patch(p.GetMethod, postfix: new HarmonyMethod(typeof(CapacityPatches).GetMethod(post, BindingFlags.Static | BindingFlags.NonPublic)));
        Plugin.L.LogInfo($"CAPACITY: {word} → {p.Name}");
    }

    static void Hand_Post(ref int __result) { __result += Plugin.EffHand; }
    static void Apron_Post(ref int __result) { __result += Plugin.EffApron; }
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
