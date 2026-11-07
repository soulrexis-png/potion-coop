using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEngine;
using CodeBase.Services.PersistentDataService;

namespace PotionCoop;

// Навыки в общей аптеке:
//  • навыки МИРА (чаны, сепаратор) — общие, покупает и держит хост;
//  • навыки ИГРОКА (руки, фартук, спринт, прыжок, подсказки…) — у каждого свои, хост хранит их по Steam ID.
// Опыт общий, очков у всех одинаково — каждый тратит на себя.
public static class Skills
{
    public static bool IsWorld(int skill)
    {
        var n = ((eSkill)skill).ToString();
        return n.StartsWith("GreaterVat") || n.StartsWith("Separator");
    }

    static CodeBase.Common.Utilities.ReactiveList<eSkill> Owned => Xp.Progress?.OwnedSkills;

    public static List<int> Read(bool world)
    {
        var res = new List<int>();
        var o = Owned; if (o == null) return res;
        var inner = o._innerList;
        for (int i = 0; i < inner.Count; i++) { int s = (int)inner[i]; if (IsWorld(s) == world) res.Add(s); }
        res.Sort();
        return res;
    }

    // заменить в своём дереве одну из частей (мира или игрока) на заданный список
    public static void Replace(bool world, IList<int> wanted)
    {
        var o = Owned; if (o == null) return;
        var have = Read(world);
        foreach (var s in have) if (!wanted.Contains(s)) o.Remove((eSkill)s);
        foreach (var s in wanted) if (!o.Contains((eSkill)s)) o.Add((eSkill)s);
    }

    // ---------- хост: хранилище навыков игроков ----------
    static Dictionary<ulong, List<int>> _store;
    static string StorePath => Path.Combine(Coop.RoomDir, "players.txt");

    static Dictionary<ulong, List<int>> Store()
    {
        if (_store != null) return _store;
        _store = new Dictionary<ulong, List<int>>();
        try
        {
            if (File.Exists(StorePath))
                foreach (var line in File.ReadAllLines(StorePath))
                {
                    var p = line.Split(':');
                    if (p.Length != 2 || !ulong.TryParse(p[0], out var id)) continue;
                    _store[id] = p[1].Split(',', StringSplitOptions.RemoveEmptyEntries).Select(int.Parse).ToList();
                }
        }
        catch (Exception e) { Plugin.L.LogError("SKILLS: не прочитал хранилище: " + e.Message); }
        return _store;
    }

    public static List<int> PersonalOf(ulong id) => Store().TryGetValue(id, out var l) ? l : new List<int>();

    public static void SavePersonal(ulong id, List<int> skills, string who)
    {
        Store()[id] = skills;
        try
        {
            Directory.CreateDirectory(Coop.RoomDir);
            File.WriteAllLines(StorePath, Store().Select(kv => kv.Key + ":" + string.Join(",", kv.Value)));
        }
        catch (Exception e) { Plugin.L.LogError("SKILLS: не сохранил: " + e.Message); }
        Plugin.L.LogInfo($"SKILLS: навыки {who} сохранены ({skills.Count})");
    }

    // ---------- гость ----------
    static List<int> _pendingPersonal, _pendingWorld;
    static List<int> _lastSentPersonal;
    static float _nextCheck;

    // Копия моих навыков у меня (гостя) — по номеру аптеки. Главный — хост: копия нужна, только если у хоста пусто
    // (хост переустановил мод, потерял файл и т.п.).
    static string LocalPath => string.IsNullOrEmpty(Coop.RoomId) ? null : Path.Combine(Application.persistentDataPath, "PotionCoop_mine", Coop.RoomId + ".skills.txt");
    static void SaveLocal(List<int> s)
    {
        var f = LocalPath; if (f == null) return;
        try { Directory.CreateDirectory(Path.GetDirectoryName(f)); File.WriteAllText(f, string.Join(",", s)); } catch (Exception e) { Plugin.L.LogWarning("SKILLS local: " + e.Message); }
    }
    static List<int> LoadLocal()
    {
        var f = LocalPath; if (f == null || !File.Exists(f)) return null;
        try { return File.ReadAllText(f).Split(',', StringSplitOptions.RemoveEmptyEntries).Select(int.Parse).ToList(); } catch { return null; }
    }
    static bool _sendAfterApply;

    // свои навыки от хоста применяем один раз за вход — дальше источник правды мы сами
    public static void GuestReceivedPersonal(List<int> s)
    {
        if (_lastSentPersonal != null) return;
        if (s.Count == 0)
        {
            var local = LoadLocal();
            if (local != null && local.Count > 0)
            {
                Plugin.L.LogInfo($"SKILLS: у хоста моих навыков нет — беру свою копию ({local.Count}) и отдаю ему");
                s = local; _sendAfterApply = true;
            }
        }
        _pendingPersonal = s;
    }
    public static void GuestReceivedWorld(List<int> s) { _pendingWorld = s; }

    // раз в секунду: применить пришедшее от хоста, заметить свои покупки
    public static void Tick(Action<List<int>> sendPersonalToHost, Action<List<int>> broadcastWorld)
    {
        // раньше любое меню (в т.ч. дерево навыков!) обнуляло память, и покупки гостя так и не уходили хосту
        if (Coop.Role == Role.Solo) { _lastSentPersonal = null; return; }
        if (Owned == null) return;
        if (Time.unscaledTime < _nextCheck) return;
        _nextCheck = Time.unscaledTime + 1f;
        bool playing = Coop.State == eGameState.Playing;

        if (Coop.Role == Role.Host)
        {
            var w = Read(true);
            if (_lastWorld == null || !w.SequenceEqual(_lastWorld) || Time.unscaledTime > _nextWorldBroadcast)
            { _lastWorld = w; _nextWorldBroadcast = Time.unscaledTime + 10f; broadcastWorld(w); }
            return;
        }

        // гость
        if (playing && _pendingWorld != null) { Replace(true, _pendingWorld); _pendingWorld = null; }
        if (playing && _pendingPersonal != null)
        {
            Replace(false, _pendingPersonal);
            _lastSentPersonal = new List<int>(_pendingPersonal);
            Plugin.L.LogInfo($"SKILLS: мои навыки в этой аптеке восстановлены ({_pendingPersonal.Count})");
            if (_sendAfterApply) { _sendAfterApply = false; sendPersonalToHost(_lastSentPersonal); }
            SaveLocal(_lastSentPersonal);
            _pendingPersonal = null;
            return;
        }
        if (_lastSentPersonal == null) return;   // ещё не получили свои навыки от хоста — не трогаем

        // навыки мира гостю покупать нельзя — откатываем (очки вернутся сами)
        var worldNow = Read(true);
        if (_hostWorld != null && !worldNow.SequenceEqual(_hostWorld))
        {
            Replace(true, _hostWorld);
            Coop.Say(Loc.L("Навыки мира (чаны, сепаратор) общие — их покупает хост", "World skills (vats, separator) are shared — the host buys them"), 4f);
        }

        var mine = Read(false);
        if (!mine.SequenceEqual(_lastSentPersonal)) { _lastSentPersonal = mine; sendPersonalToHost(mine); SaveLocal(mine); }
    }

    static List<int> _lastWorld, _hostWorld;
    static float _nextWorldBroadcast;
    public static void RememberHostWorld(List<int> w) => _hostWorld = w;

    public static byte[] Pack(List<int> s) { var b = new byte[1 + s.Count]; b[0] = (byte)s.Count; for (int i = 0; i < s.Count; i++) b[1 + i] = (byte)s[i]; return b; }
    public static List<int> Unpack(byte[] b, int off) { var n = b[off]; var l = new List<int>(); for (int i = 0; i < n && off + 1 + i < b.Length; i++) l.Add(b[off + 1 + i]); return l; }
}

// Навыки мира (чаны, сепаратор) гостю достаются от хоста бесплатно — не списываем за них очки гостя
static class SkillCostPatches
{
    static int WorldCost(CodeBase.Services.DataMediatorService.SkillsDataMediator m)
    {
        if (Coop.Role != Role.Guest) return 0;
        int sum = 0;
        try { foreach (var s in Skills.Read(true)) sum += m.CostOf((eSkill)s); } catch { }
        return sum;
    }

    [HarmonyLib.HarmonyPostfix]
    [HarmonyLib.HarmonyPatch(typeof(CodeBase.Services.DataMediatorService.SkillsDataMediator), nameof(CodeBase.Services.DataMediatorService.SkillsDataMediator.CalculatePointsUnspent))]
    static void Unspent_Post(CodeBase.Services.DataMediatorService.SkillsDataMediator __instance, ref int __result) { __result += WorldCost(__instance); }

    [HarmonyLib.HarmonyPostfix]
    [HarmonyLib.HarmonyPatch(typeof(CodeBase.Services.DataMediatorService.SkillsDataMediator), nameof(CodeBase.Services.DataMediatorService.SkillsDataMediator.CalculateDisplayedPointsUnspent))]
    static void Displayed_Post(CodeBase.Services.DataMediatorService.SkillsDataMediator __instance, ref int __result) { __result += WorldCost(__instance); }

    [HarmonyLib.HarmonyPostfix]
    [HarmonyLib.HarmonyPatch(typeof(CodeBase.Services.DataMediatorService.SkillsDataMediator), nameof(CodeBase.Services.DataMediatorService.SkillsDataMediator.PointsSpent), HarmonyLib.MethodType.Getter)]
    static void Spent_Post(CodeBase.Services.DataMediatorService.SkillsDataMediator __instance, ref int __result) { __result -= WorldCost(__instance); }
}
