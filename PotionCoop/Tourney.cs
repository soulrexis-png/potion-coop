using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using UnityEngine;
using CodeBase.Services.ConfigsService;
using CodeBase.Components.Slots;

namespace PotionCoop;

// Соревнование (первая версия).
// • До 6 команд, у каждого цвета — постоянные 2 семейства (таблица Groups). Игроки раскладываются по командам (хост — тоже, если не арбитр).
// • Общие — случайные на старте: Исцеление + семейства, не занятые командами; 4 ячейки на команду (вся лаборатория — 8).
// • Своя зона = 1000 очков по проценту. Общие вместе — 500 (500/N за штуку), ставить их можно только закрыв свою зону.
// • Конец — когда общие разобраны; ничья — выше тот, кто раньше закрыл свою зону.
// • Досрочная победа: лидера уже никто не догонит, даже забрав все оставшиеся очки.
// • Быстрый режим: в каждом семействе считаются только первые N полок.
// • Арбитр: хост не соревнуется и не трогает склянки, только смотрит.
// Считает всё хост, остальным рассылает табло.
public static class Tourney
{
    public static bool On;          // эта аптека — соревнование
    public static bool Arbiter;     // хост — арбитр
    public static int Rows;         // 0 — вся лаборатория, N > 0 — полок на семейство, -1 — тест: 1 ряд своего цвета + 1 ряд общих
    public static string Board = "";  // табло (у всех), строками
    public static bool Started;       // старт дан (до старта склянки брать нельзя, ждём участников)
    static float _startAt;            // хост: сколько секунд осталось до старта (0 — отсчёт не запущен)
    static int _countdown = -1;       // у всех: сколько секунд до старта (-1 — отсчёта нет)
    public static string Winner;    // кто победил (у всех)

    // Настройки организатора: сколько команд, сколько игроков в команде (0 — без ограничения), цвет каждой команды.
    // Цвет команды = цвет из палитры аптекарей (индекс 0..11); по нему названия, подложки, табло и цвет мантий.
    public static int NumTeams = 2, TeamSize = 0;

    // Постоянная таблица «цвет команды → 2 семейства» (по цветам жидкостей из игры, см. COLORDUMP 10.11).
    // Общие — всегда Исцеление (белое). Семейства невыбранных цветов в соревновании не участвуют.
    public static readonly (int pal, eSectionFamily a, eSectionFamily b)[] Groups =
    {
        (3, eSectionFamily.Vitality,  eSectionFamily.Might),    // Красные: Жизненная сила + Мощь
        (2, eSectionFamily.Swiftness, eSectionFamily.Fortune),  // Жёлтые: Стремительность + Удача
        (5, eSectionFamily.Venoms,    eSectionFamily.Vigor),    // Зелёные: Яды + Бодрость
        (0, eSectionFamily.Veils,     eSectionFamily.Wards),    // Бирюзовые: Завесы + Обереги
        (4, eSectionFamily.Arcana,    eSectionFamily.Curses),   // Синие: Аркана + Проклятия
        (1, eSectionFamily.Sorcery,   eSectionFamily.Charms),   // Фиолетовые: Колдовство + Чары
    };
    public const eSectionFamily CommonFamily = eSectionFamily.Remedy;
    public static int GroupOfPal(int pal) { for (int i = 0; i < Groups.Length; i++) if (Groups[i].pal == pal) return i; return -1; }
    public static int[] TeamPal = { 3, 4, 5, 2, 0, 1 };
    static readonly string[] PalRu = { "Бирюзовые", "Фиолетовые", "Жёлтые", "Красные", "Синие", "Зелёные", "Розовые", "Мятные", "Оранжевые", "Чёрные", "Белые", "Коричневые" };
    static readonly string[] PalEn = { "Teal", "Purple", "Yellow", "Red", "Blue", "Green", "Pink", "Mint", "Orange", "Black", "White", "Brown" };
    public static string PalName(int pal) { int k = ((pal % 12) + 12) % 12; return Loc.Ru ? PalRu[k] : Loc.Tr(PalEn[k]); }
    public static string TeamName(int t) => t < 0 ? Loc.L("зрители", "spectators") : PalName(TeamPal[t % 6]);
    static string TeamNameRu(int t) => t < 0 ? "зрители" : PalRu[TeamPal[t % 6] % 12];
    static string TeamNameEn(int t) => t < 0 ? "spectators" : PalEn[TeamPal[t % 6] % 12];
    public static int TeamColorOf(int t) => TeamPal[t % 6];
    static Color Tint(int t) { var c = Avatar.Robe[TeamPal[t % 6] % 12]; return Color.Lerp(c, Color.white, 0.15f); }
    public static bool IsSpectator(ulong id) => On && Teams.TryGetValue(id, out var t) && t < 0;

    // ---- настройки аптеки (tourney.txt в её папке) ----
    public static void Write(string room, bool arbiter, int rows, int teams, int size, int[] pal)
    {
        try { File.Delete(Path.Combine(Coop.RoomsRoot, room, "commons.txt")); } catch { }
        try { File.WriteAllText(Path.Combine(Coop.RoomsRoot, room, "tourney.txt"), $"on\n{(arbiter ? 1 : 0)}\n{rows}\n{teams}\n{size}\n{string.Join(",", pal)}"); } catch (Exception e) { Plugin.L.LogWarning("TOURNEY write: " + e.Message); }
    }
    public static void Load()
    {
        On = false; Arbiter = false; Rows = 0; Board = ""; Winner = null;
        NumTeams = 2; TeamSize = 0; TeamPal = new[] { 3, 4, 5, 2, 0, 1 };
        Teams.Clear(); _zones = null; Credited.Clear(); Dead.Clear(); _ownDone.Clear(); _finished = false;
        _events.Clear(); _evId = 0; _evSeen = -1; _zonesResendAt = 0;
        Started = false; _startAt = 0; _countdown = -1; _okBefore.Clear(); _slotTeam.Clear(); _markerZones = null; ClearMarkers(); TourneyIntro.Reset();
        try
        {
            var f = Path.Combine(Coop.RoomDir, "tourney.txt");
            if (!File.Exists(f)) return;
            var l = File.ReadAllLines(f);
            On = l.Length > 0 && l[0] == "on";
            Arbiter = l.Length > 1 && l[1] == "1";
            Rows = l.Length > 2 && int.TryParse(l[2], out var r) ? r : 0;
            if (l.Length > 3 && int.TryParse(l[3], out var nt)) NumTeams = Mathf.Clamp(nt, 2, 6);
            if (l.Length > 4 && int.TryParse(l[4], out var ts)) TeamSize = Mathf.Clamp(ts, 0, 6);
            if (l.Length > 5) { var pp = l[5].Split(','); for (int i = 0; i < 6 && i < pp.Length; i++) if (int.TryParse(pp[i], out var pv)) TeamPal[i] = ((pv % 12) + 12) % 12; }
            Started = File.Exists(Path.Combine(Coop.RoomDir, "started.txt"));
            var tf = Path.Combine(Coop.RoomDir, "teams.txt");
            if (File.Exists(tf))
                foreach (var line in File.ReadAllLines(tf))
                {
                    var p = line.Split(':');
                    if (p.Length == 2 && ulong.TryParse(p[0], out var id) && int.TryParse(p[1], out var t)) Teams[id] = t;
                }
            var cf = Path.Combine(Coop.RoomDir, "credits.txt");
            if (File.Exists(cf))
                foreach (var line in File.ReadAllLines(cf))
                {
                    var p = line.Split(':');
                    if (p.Length == 2 && int.TryParse(p[0], out var s) && int.TryParse(p[1], out var t)) { if (t >= 0) Credited[s] = t; else Dead.Add(s); }
                }
            if (On) Plugin.L.LogInfo($"TOURNEY: соревнование, {(Arbiter ? "хост — арбитр" : "хост играет")}, {(Rows == 0 ? "вся лаборатория" : Rows < 0 ? "тест: по ряду" : Rows + " полки на семейство")}, команд {NumTeams} по {(TeamSize == 0 ? "сколько придёт" : TeamSize.ToString())}, цвета {string.Join("/", Enumerable.Range(0, NumTeams).Select(TeamNameRu))}; игроков в файле: {Teams.Count}");
        }
        catch (Exception e) { Plugin.L.LogWarning("TOURNEY load: " + e.Message); }
    }
    static void SaveState()
    {
        try
        {
            File.WriteAllLines(Path.Combine(Coop.RoomDir, "teams.txt"), Teams.Select(kv => kv.Key + ":" + kv.Value));
            File.WriteAllLines(Path.Combine(Coop.RoomDir, "credits.txt"), Credited.Select(kv => kv.Key + ":" + kv.Value).Concat(Dead.Select(d => d + ":-1")));
        }
        catch { }
    }

    // ---- команды ----
    public static readonly Dictionary<ulong, int> Teams = new();
    public static int TeamOf(ulong id) => Teams.TryGetValue(id, out var t) ? t : -1;
    // хост: раскладывает игроков по командам (кто уже в команде — там и остаётся)
    public static void AssignTeams(ulong me, IEnumerable<ulong> peers)
    {
        if (!On) return;
        var all = new List<ulong>();
        if (!Arbiter) all.Add(me);
        all.AddRange(peers);
        foreach (var id in all) if (!Teams.ContainsKey(id)) Place(id);
        if (Arbiter) Teams.Remove(me);
        _zones = null;   // состав команд поменялся — зоны пересчитать
        SaveState();
    }
    // в команду, где меньше всего игроков и есть место; мест нет — зритель (-1)
    static void Place(ulong id)
    {
        var counts = new int[6];
        foreach (var kv in Teams) if (kv.Value >= 0 && kv.Value < 6) counts[kv.Value]++;
        int best = -1;
        for (int t = 0; t < NumTeams; t++)
            if ((TeamSize == 0 || counts[t] < TeamSize) && (best < 0 || counts[t] < counts[best])) best = t;
        Teams[id] = best;
        Plugin.L.LogInfo($"TOURNEY: игрок {id} — {(best >= 0 ? "команда " + TeamNameRu(best) : "зритель (команды полные)")}");
    }
    // хост, до старта: перемешать игроков по командам случайно (а не по порядку входа)
    public static bool Shuffle(ulong me, IEnumerable<ulong> peers)
    {
        if (!On || Started) return false;
        var all = new List<ulong>();
        if (!Arbiter) all.Add(me);
        all.AddRange(peers);
        var rnd = new System.Random();
        all = all.OrderBy(_ => rnd.Next()).ToList();
        Teams.Clear();
        foreach (var id in all) Place(id);
        _zones = null; _zonesDirty = true;
        SaveState();
        Event("Команды перемешаны", "Teams shuffled", 3f);
        return true;
    }
    static List<int> ActiveTeams() => Teams.Values.Where(t => t >= 0 && t < NumTeams).Distinct().OrderBy(t => t).ToList();

    // ---- зоны: семейство → команда (-1 — общее), ячейки ----
    static Dictionary<int, int> _zones;               // семейство → команда
    static Dictionary<int, int> _slotFamily;          // ячейка → семейство (только те, что считаются)
    static readonly Dictionary<int, int> _slotSection = new();   // ячейка → полка
    static readonly Dictionary<int, List<int>> _zoneSec = new(); // семейство → полки (подвиды) зоны в тестовом режиме
    public static readonly Dictionary<int, int> Credited = new();   // общая ячейка → команда, получившая очки
    public static readonly HashSet<int> Dead = new();                // общие, поставленные раньше времени
    static bool _finished;
    static readonly List<int> _ownDone = new();   // команды в порядке закрытия своей зоны

    static Color FamilyColor(LiquidLookConfig cfg, int fam)
    {
        try { return cfg.ResolveColor((eSectionFamily)fam, false); } catch { return Color.gray; }
    }

    // ---- диагностика: таблица «цвет команды → семейства» (один раз за запуск, в любой лаборатории) ----
    // Чтобы собрать постоянную таблицу и не зависеть от пересчёта на лету: цвета семейств, их подвиды и,
    // для каждого из 12 цветов палитры, 4 ближайших семейства по оттенку.
    static bool _colorDumpDone;
    public static void DumpColors()
    {
        if (_colorDumpDone || Coop.State != eGameState.Playing) return;
        try
        {
            LiquidLookConfig cfg = null;
            foreach (var o in Resources.FindObjectsOfTypeAll(Il2CppInterop.Runtime.Il2CppType.Of<LiquidLookConfig>())) { cfg = o.TryCast<LiquidLookConfig>(); if (cfg != null) break; }
            if (cfg == null || Catalog() == null || LocSvc == null) return;
            _colorDumpDone = true;
            var sb = new StringBuilder("COLORDUMP: семейства (имя | hex | H S V | подвиды)\n");
            var famHsv = new Dictionary<int, (float h, float s, float v)>();
            for (int f = 1; f <= 13; f++)
            {
                var c = FamilyColor(cfg, f);
                Color.RGBToHSV(c, out var h, out var sa, out var v);
                famHsv[f] = (h, sa, v);
                var subs = new List<string>();
                for (int sec = 0; sec < 6; sec++)
                {
                    string n = null;
                    try { n = Clean(LocSvc.Get(Catalog().GetSpecNameTerm((eSectionFamily)f, sec))); } catch { }
                    if (string.IsNullOrEmpty(n) || n.StartsWith("ERR") || subs.Contains(n)) break;
                    subs.Add(n);
                }
                sb.Append($"COLORDUMP: {(eSectionFamily)f} «{FamilyName(f)}» | #{ColorUtility.ToHtmlStringRGB(c)} | H={h:0.000} S={sa:0.00} V={v:0.00} | {string.Join(", ", subs)}\n");
            }
            sb.Append("COLORDUMP: палитра команд → ближайшие семейства (по оттенку; бледные — по насыщенности)\n");
            for (int pal = 0; pal < 12; pal++)
            {
                Color.RGBToHSV(Avatar.Robe[pal], out var th, out var ts, out _);
                var ranked = famHsv.Select(kv =>
                {
                    float d;
                    if (ts < 0.25f) d = kv.Value.s;
                    else { float dh = Mathf.Abs(kv.Value.h - th); dh = Mathf.Min(dh, 1f - dh); d = dh + (kv.Value.s < 0.25f ? 0.5f : 0f); }
                    return (fam: kv.Key, d);
                }).OrderBy(x => x.d).Take(4);
                sb.Append($"COLORDUMP: {PalRu[pal]} (#{ColorUtility.ToHtmlStringRGB(Avatar.Robe[pal])}, H={th:0.000} S={ts:0.00}) → {string.Join(", ", ranked.Select(x => $"{(eSectionFamily)x.fam} {x.d:0.000}"))}\n");
            }
            Plugin.L.LogInfo(sb.ToString().TrimEnd());
        }
        catch (Exception e) { Plugin.L.LogWarning("COLORDUMP: " + e.Message); _colorDumpDone = true; }
    }

    static bool BuildZones()
    {
        var teams = ActiveTeams();
        if (teams.Count == 0 || Sync.Persistence == null || Sync.Slots.Count == 0) return false;
        _zoneSec.Clear();
        var manifest = Sync.Persistence.SlotManifest();
        if (manifest == null) return false;
        // семейство → команда по постоянной таблице; общие — Исцеление; остальные (-2) не считаются
        _zones = new Dictionary<int, int>();
        for (int f = 1; f <= 13; f++) _zones[f] = -2;
        foreach (var t in teams)
        {
            int g = GroupOfPal(TeamPal[t % 6]);
            if (g < 0) g = t % Groups.Length;   // старый сохранённый цвет вне таблицы — по номеру команды
            _zones[(int)Groups[g].a] = t; _zones[(int)Groups[g].b] = t;
        }
        // цвета, выбранные в настройке, но без игроков — их семейства тоже не идут в общие
        for (int t = 0; t < NumTeams; t++)
        {
            if (teams.Contains(t)) continue;
            int g = GroupOfPal(TeamPal[t % 6]); if (g < 0) continue;
            if (_zones[(int)Groups[g].a] == -2) _zones[(int)Groups[g].a] = -3;
            if (_zones[(int)Groups[g].b] == -2) _zones[(int)Groups[g].b] = -3;
        }
        // общие — после старта: Исцеление + все семейства, не занятые командами (до старта их не видно и не набрать заранее)
        if (Started) for (int f = 1; f <= 13; f++) if (_zones[f] == -2) _zones[f] = -1;
        // ячейки нужных семейств
        var bySection = new Dictionary<int, SortedSet<int>>();
        var cand = new Dictionary<int, int>();   // ячейка → семейство
        _slotSection.Clear();
        foreach (var e in manifest._entries)
        {
            if (e == null) continue;
            int fam = (int)e.Family; if (fam <= 0 || !_zones.TryGetValue(fam, out var z) || z < -1) continue;
            if (!Sync.Slots.ContainsKey(e.Uid) || Sync.Slots[e.Uid] == null) continue;
            cand[e.Uid] = fam; _slotSection[e.Uid] = e.SectionIndex;
            if (!bySection.TryGetValue(fam, out var ss)) bySection[fam] = ss = new SortedSet<int>();
            ss.Add(e.SectionIndex);
        }
        _slotFamily = new Dictionary<int, int>();
        // свои зоны
        foreach (var t in teams)
            foreach (var fam in _zones.Where(z => z.Value == t).Select(z => z.Key))
            {
                if (!bySection.TryGetValue(fam, out var secs)) continue;
                var slots = cand.Where(kv => kv.Value == fam).Select(kv => kv.Key).ToList();
                if (Rows == -1)
                {
                    // тест: по 6 ячеек из каждого из двух семейств, со второго шкафа (первый занят обучением)
                    int sec = secs.Count > 1 ? secs.ElementAt(1) : secs.First();
                    foreach (var u in PickRow(slots, sec, TestRowSize / 2)) _slotFamily[u] = fam;
                    _zoneSec[fam] = new List<int> { sec };
                }
                else
                {
                    var use = Rows > 0 ? secs.Take(Rows).ToList() : secs.ToList();
                    foreach (var u in slots) if (use.Contains(_slotSection[u])) _slotFamily[u] = fam;
                    if (Rows > 0) _zoneSec[fam] = use;
                }
            }
        // общий пул: 4 ячейки на команду (вся лаборатория — 8). Берём ОДНО случайное свободное семейство
        // и в нём 2–4 этикетки подряд, чтобы видов зелий было мало. Если семейства не хватило — добираем следующим.
        // Сид хранится в комнате: после перезахода пул тот же. Заранее не набрать — до старта семейство неизвестно.
        if (Started)
        {
            int n = teams.Count * (Rows == 0 ? 8 : 4);
            int perLabel = Mathf.Max(4, (n + 3) / 4);   // этикеток выходит не больше четырёх
            var rnd = new System.Random(CommonSeed());
            var free = _zones.Where(z => z.Value == -1).Select(z => z.Key).OrderBy(f => f).ToList();
            var labels = new List<(int fam, int sec)>();
            foreach (var fam in free.OrderBy(_ => rnd.Next()).ToList())
            {
                if (!bySection.TryGetValue(fam, out var cs)) continue;
                var secs = cs.ToList();
                if (secs.Count > 1 && Rows == -1) secs = secs.Skip(1).ToList();   // в тесте первый шкаф — под обучение
                foreach (var sec in secs.OrderBy(_ => rnd.Next()).ToList()) labels.Add((fam, sec));
                if (labels.Count * perLabel >= n) break;   // одного семейства хватило — остальные не трогаем
            }
            int left = n;
            foreach (var lb in labels)
            {
                if (left <= 0) break;
                var slots = cand.Where(kv => kv.Value == lb.fam).Select(kv => kv.Key).ToList();
                var got = PickRow(slots, lb.sec, Mathf.Min(perLabel, left), rnd);
                if (got.Count == 0) continue;
                foreach (var u in got) _slotFamily[u] = lb.fam;
                left -= got.Count;
                if (!_zoneSec.TryGetValue(lb.fam, out var zl)) _zoneSec[lb.fam] = zl = new List<int>();
                zl.Add(lb.sec);
            }
            foreach (var fam in free)
                if (!_zoneSec.ContainsKey(fam)) _zones[fam] = -2;   // не выпало — не участвует
        }
        var sb = new StringBuilder("TOURNEY: зоны —");
        foreach (var t in teams) sb.Append($" {TeamNameRu(t)}: {string.Join(", ", _zones.Where(z => z.Value == t).Select(z => ((eSectionFamily)z.Key).ToString()))} ({_slotFamily.Count(kv => _zones[kv.Value] == t)} яч.);");
        sb.Append(Started ? $" общие: {string.Join(", ", _zoneSec.Where(z => _zones[z.Key] == -1).Select(z => (eSectionFamily)z.Key + ":" + string.Join("/", z.Value)))} ({_slotFamily.Count(kv => _zones[kv.Value] == -1)} яч.)" : " общие — после старта");
        Plugin.L.LogInfo(sb.ToString());
        return true;
    }

    const int TestRowSize = 12;   // тестовый режим: ячеек в зоне (своя и общая)

    // ячейки одной полки на одной высоте (самый длинный ряд), не больше count, подряд вдоль полки с одного края
    static List<int> PickRow(List<int> slots, int sec, int count, System.Random rnd = null)
    {
        var inSec = slots.Where(u => _slotSection.TryGetValue(u, out var sc) && sc == sec).ToList();
        if (inSec.Count == 0 || count <= 0) return new List<int>();
        var all = inSec.GroupBy(u => Mathf.RoundToInt(Sync.Slots[u].transform.position.y * 10f))
                       .Select(g => (y: g.Key, cells: g.OrderBy(u => u).ToList())).ToList();
        var rows = all.OrderByDescending(r => r.cells.Count).ThenBy(r => r.y).Select(r => r.cells).ToList();
        List<int> row;
        if (rnd != null)
        {   // общие: нижний или второй снизу ряд — на уровне глаз, а не под потолком (случайным остаётся место в ряду)
            var low = all.Where(r => r.cells.Count >= count).OrderBy(r => r.y).Take(2).Select(r => r.cells).ToList();
            row = low.Count > 0 ? low[rnd.Next(low.Count)] : rows[0];
        }
        else row = rows[0];
        if (row.Count <= count) return row;
        if (rnd != null)
        {
            var p00 = Sync.Slots[row[0]].transform.position;
            var far0 = row.OrderByDescending(u => (Sync.Slots[u].transform.position - p00).sqrMagnitude).ThenBy(u => u).First();
            var ax = Sync.Slots[far0].transform.position - p00; ax.y = 0; ax.Normalize();
            var ordered = row.OrderBy(u => Vector3.Dot(Sync.Slots[u].transform.position - p00, ax)).ThenBy(u => u).ToList();
            return ordered.Skip(rnd.Next(ordered.Count - count + 1)).Take(count).ToList();
        }
        var p0 = Sync.Slots[row[0]].transform.position;
        var far = row.OrderByDescending(u => (Sync.Slots[u].transform.position - p0).sqrMagnitude).First();
        var axis = Sync.Slots[far].transform.position - p0; axis.y = 0; axis.Normalize();
        return row.OrderBy(u => Vector3.Dot(Sync.Slots[u].transform.position - p0, axis)).Take(count).ToList();
    }
    static int CommonSeed()
    {
        var f = Path.Combine(Coop.RoomDir, "commons.txt");
        try { if (File.Exists(f) && int.TryParse(File.ReadAllText(f).Trim(), out var v)) return v; } catch { }
        int seed = new System.Random().Next(1, int.MaxValue);
        try { File.WriteAllText(f, seed.ToString()); } catch { }
        return seed;
    }

    // ---- старт: до него ждём участников, склянки не берутся ----
    public static string WaitText() => _countdown > 0
        ? Loc.L($"Старт через {_countdown}…", $"Starting in {_countdown}…")
        : Coop.Role == Role.Host
            ? Loc.L("Ждём участников. F9 — старт, F8 → «Перемешать команды»", "Waiting for players. F9 — start, F8 → «Shuffle teams»")
            : Loc.L("Ждём участников — хост даст старт", "Waiting for players — the host will start");
    // коротко для табло (длинный текст — во всплывашке)
    static string ShortWait() => _countdown > 0 ? Loc.L($"Старт через {_countdown}…", $"Start in {_countdown}…")
        : Coop.Role == Role.Host ? Loc.L("F9 — старт · F8 — перемешать", "F9 start · F8 shuffle") : Loc.L("Ждём старта", "Waiting to start");

    public static void PressStart()
    {
        if (Started || _startAt > 0) return;
        _startAt = 6f;   // секунд до старта (первый такт сразу снимет одну)
        Plugin.L.LogInfo("TOURNEY: хост дал старт — отсчёт 5 с");
    }

    // ---- хост: счёт раз в секунду ----
    static float _next;
    public static void HostTick(Action<string> broadcast)
    {
        if (!On || Coop.Role != Role.Host || Coop.State != eGameState.Playing) return;
        if (Time.unscaledTime < _next) return;
        _next = Time.unscaledTime + 1f;
        if (_zones == null) { if (!BuildZones()) return; _zonesDirty = true; }
        // список зон: при изменении и ещё раз в 10 с — гость, зашедший позже, иначе подложек не увидит (п. 43)
        if (_zonesDirty || Time.unscaledTime >= _zonesResendAt)
        {
            _zonesDirty = false; _zonesResendAt = Time.unscaledTime + 10f;
            var zp = ZonesPacket(); SetMarkerZones(zp); broadcast(zp);
        }
        // обратный отсчёт
        // отсчёт — по секунде за такт, а такт идёт только в самой лаборатории: в паузе и меню отсчёт стоит
        if (!Started && _startAt > 0)
        {
            _startAt -= 1f;
            int left = Mathf.CeilToInt(_startAt);
            _countdown = left;
            if (left <= 0)
            {
                Started = true; _startAt = 0; _countdown = -1; _zones = null;   // пересобрать — появятся общие
                try { File.WriteAllText(Path.Combine(Coop.RoomDir, "started.txt"), DateTime.Now.ToString("s")); } catch { }
                Plugin.L.LogInfo("TOURNEY: СТАРТ");
                Coop.Say(Loc.L("Старт! Собирайте свои ряды", "Go! Fill your rows"), 4f);
                return;
            }
        }
        var teams = ActiveTeams();
        var own = teams.ToDictionary(t => t, t => 0);
        var total = teams.ToDictionary(t => t, t => 0);
        int commonTotal = 0, commonLeft = 0;
        foreach (var kv in _slotFamily)
        {
            int team = _zones[kv.Value];
            if (team == -2) continue;
            bool ok = Started && Sync.Slots.TryGetValue(kv.Key, out var slot) && slot != null && slot.IsCorrectlyFilled;
            if (ok != _okBefore.Contains(kv.Key))
            {
                if (ok) _okBefore.Add(kv.Key); else _okBefore.Remove(kv.Key);
                Plugin.L.LogInfo($"TOURNEY: ячейка {kv.Key} ({(eSectionFamily)kv.Value}, {(team >= 0 ? TeamNameRu(team) : "общая")}) {(ok ? "заполнена верно" : "опустела")}");
            }
            Sync.Slots.TryGetValue(kv.Key, out slot);
            if (team >= 0) { if (total.ContainsKey(team)) { total[team]++; if (ok) own[team]++; } continue; }
            commonTotal++;
            if (Credited.ContainsKey(kv.Key) || Dead.Contains(kv.Key)) continue;
            if (!ok) { commonLeft++; continue; }
            // общую только что поставили правильно — кому очки?
            int by = TeamOf(Sync.LastBy(slot.Potion));
            if (by >= 0 && total.ContainsKey(by) && own[by] >= total[by]) { Credited[kv.Key] = by; Event($"{TeamNameRu(by)}: +общая склянка", $"{TeamNameEn(by)}: +common flask", 2f); }
            else { Dead.Add(kv.Key); Plugin.L.LogInfo($"TOURNEY: общая {kv.Key} занята без очков (поставил {(by >= 0 ? TeamNameRu(by) : "?")}, своя зона не готова)"); }
            SaveState();
        }
        // своя зона — 1000 по проценту, все общие вместе — 500 (по 500/N за ячейку)
        int commonValue = commonTotal == 0 ? 0 : Mathf.Max(1, Mathf.RoundToInt(500f / commonTotal));
        foreach (var t in teams) if (Started && total[t] > 0 && own[t] >= total[t] && !_ownDone.Contains(t)) { _ownDone.Add(t); Event($"{TeamNameRu(t)}: своя зона готова — теперь общие", $"{TeamNameEn(t)}: own zone done — now the commons", 3f); }
        var score = new Dictionary<int, int>();
        var max = new Dictionary<int, int>();
        foreach (var t in teams)
        {
            int ownPts = total[t] == 0 ? 0 : Mathf.RoundToInt(1000f * own[t] / total[t]);
            int com = Credited.Values.Count(x => x == t) * commonValue;
            score[t] = ownPts + com;
            max[t] = 1000 + com + commonLeft * commonValue;
        }
        // досрочная победа / конец
        if (!_finished && teams.Count > 1)
        {
            // ничья — выше тот, кто раньше закрыл свою зону
            var lead = score.OrderByDescending(kv => kv.Value).ThenBy(kv => { int i = _ownDone.IndexOf(kv.Key); return i < 0 ? 99 : i; }).First();
            bool early = score.Where(kv => kv.Key != lead.Key).All(kv => max[kv.Key] < lead.Value);
            bool over = Started && commonTotal > 0 && commonLeft == 0;   // все общие разобраны
            if (early || over)
            {
                _finished = true; Winner = TeamName(lead.Key);
                Event($"Соревнование окончено! Победили {TeamNameRu(lead.Key)}", $"Competition over! {TeamNameEn(lead.Key)} win", 10f);
                Plugin.L.LogInfo($"TOURNEY: победили {TeamNameRu(lead.Key)} ({lead.Value}){(early ? ", досрочно" : "")}");
            }
        }
        var sbd = new StringBuilder();
        foreach (var t in teams.OrderByDescending(t => score[t]))
            sbd.Append(t).Append('|').Append(score[t]).Append('|').Append(total[t] == 0 ? 0 : 100 * own[t] / total[t]).Append('|').Append(Credited.Values.Count(x => x == t)).Append('\n');
        sbd.Append("#|").Append(commonLeft).Append('|').Append(commonTotal).Append('|').Append(_finished ? Winner : "").Append('\n');
        sbd.Append("!|").Append(Started ? 1 : 0).Append('|').Append(_countdown).Append('\n');
        foreach (var ev in _events) sbd.Append("E|").Append(ev.id).Append('|').Append(ev.dur.ToString(System.Globalization.CultureInfo.InvariantCulture)).Append('|').Append(ev.ru).Append('|').Append(ev.en).Append('\n');
        // что собирать: семейства каждой команды и общие (-1)
        foreach (var t in teams.Append(-1))
        {
            var fs = new List<string>();
            foreach (var z in _zones.Where(zz => zz.Value == t))
            {
                var fn = ((eSectionFamily)z.Key).ToString();
                if (_zoneSec.TryGetValue(z.Key, out var zs) && zs.Count > 0) foreach (var sc in zs) fs.Add(fn + ":" + sc);
                else fs.Add(fn);
            }
            if (fs.Count > 0) sbd.Append("F|").Append(t).Append('|').Append(string.Join(", ", fs)).Append('\n');
        }
        foreach (var kv in Teams) sbd.Append('@').Append(kv.Key).Append('|').Append(kv.Value).Append('\n');
        sbd.Append("P|").Append(string.Join(",", TeamPal)).Append('|').Append(NumTeams).Append('\n');
        Board = sbd.ToString();
        broadcast(Board);
    }

    // у всех: табло пришло от хоста
    public static void OnBoard(string board)
    {
        if (board.StartsWith("Z\n")) { SetMarkerZones(board); return; }   // зоны для подсветки ячеек
        Board = board; On = true;
        SeeEvents(board);
        Teams.Clear();
        foreach (var line in board.Split('\n'))
        {
            if (line.StartsWith("P|"))
            {
                var pp = line.Split('|'); var cs = pp[1].Split(',');
                for (int i = 0; i < 6 && i < cs.Length; i++) if (int.TryParse(cs[i], out var pv)) TeamPal[i] = ((pv % 12) + 12) % 12;
                if (pp.Length > 2 && int.TryParse(pp[2], out var nt)) NumTeams = nt;
            }
            if (line.StartsWith("@")) { var p = line.Substring(1).Split('|'); if (p.Length == 2 && ulong.TryParse(p[0], out var id) && int.TryParse(p[1], out var t)) Teams[id] = t; }
            if (line.StartsWith("!|"))
            {
                var p = line.Split('|');
                bool was = Started;
                Started = p.Length > 1 && p[1] == "1";
                _countdown = p.Length > 2 && int.TryParse(p[2], out var cd) ? cd : -1;
                if (Started && !was) Coop.Say(Loc.L("Старт! Собирайте свои ряды", "Go! Fill your rows"), 4f);
            }
            if (line.StartsWith("#|")) { var p = line.Split('|'); var w = p.Length > 3 ? p[3] : ""; if (!string.IsNullOrEmpty(w) && Winner == null) Winner = w; }   // плашка о победе приходит событием
        }
    }

    // табло в правом верхнем углу
    static GUIStyle _st, _small;
    // надпись табло: шрифтом игры (на своём холсте), а если шрифт не нашёлся — простым IMGUI
    static void Lbl(string key, string text, Color c, float x, float y, int size, GUIStyle fb)
    {
        if (Sigils.FontReady) { Sigils.WantText(key, text, c, x, y - 2, size + 3); return; }
        fb.normal.textColor = c;
        GUI.Label(new Rect(x, y, 420, size + 8), text, fb);
    }

    public static void Draw()
    {
        if (!On || string.IsNullOrEmpty(Board) || Coop.State != eGameState.Playing) return;
        if (_st == null) _st = new GUIStyle(GUI.skin.label) { fontSize = 18, fontStyle = FontStyle.Bold };
        // у правого края под подсказками управления; на тёмной подложке-панели игры, чтобы читалось поверх вывесок
        float x = Screen.width - 330, y = Screen.height * 0.26f, y0 = y;
        Lbl("title", Loc.L("Соревнование", "Competition"), Color.white, x, y, 18, _st); y += 28;
        int myTeam = TeamOf(Sync.MyId);
        if (myTeam >= 0) { Lbl("you", Loc.L($"Ты: {TeamName(myTeam)}", $"You: {TeamName(myTeam)}"), Tint(myTeam), x, y, 18, _st); y += 26; }
        else if (IsSpectator(Sync.MyId)) { Lbl("you", Loc.L("Ты зритель: команды полные", "You're a spectator: teams are full"), new Color(0.85f, 0.85f, 0.85f), x, y, 16, _st); y += 26; }
        List<(int fam, int sec)> mine = null, common = null;
        foreach (var line in Board.Split('\n'))
        {
            if (!line.StartsWith("F|")) continue;
            var p = line.Split('|');
            if (p.Length < 3 || !int.TryParse(p[1], out var ft)) continue;
            var fl = new List<(int fam, int sec)>();
            foreach (var part in p[2].Split(new[] { ", " }, StringSplitOptions.RemoveEmptyEntries))
            {
                var fp = part.Trim().Split(':');
                if (!Enum.TryParse<eSectionFamily>(fp[0], out var f) || (int)f <= 0) continue;
                fl.Add(((int)f, fp.Length > 1 && int.TryParse(fp[1], out var sc) ? sc : -1));
            }
            if (ft == -1) common = fl; else if (ft == myTeam) mine = fl;
        }
        if (_small == null) _small = new GUIStyle(GUI.skin.label) { fontSize = 15, fontStyle = FontStyle.Bold };
        // после победы списки «что собирать» не нужны — остаётся итоговая таблица
        if (Winner != null) { mine = null; common = null; }
        if (mine != null && mine.Count > 0)
        {
            Lbl("collect", Loc.L("Собирай:", "Collect:"), Tint(myTeam), x, y, 15, _small); y += 22;
            y = SafeFamilies(mine, x, y);
        }
        if (common != null && common.Count > 0)
        {
            Lbl("common", Loc.L("Потом общие:", "Then common:"), Color.white, x, y, 15, _small); y += 22;
            y = SafeFamilies(common, x, y);
        }
        else if (!Started && mine != null) { Lbl("common", Loc.L("Общие — откроются на старте", "Commons — revealed at start"), Color.white, x, y, 15, _small); y += 22; }
        if (!Started) { Lbl("wait", ShortWait(), new Color(1f, 0.85f, 0.4f), x, y, 18, _st); y += 26; }
        foreach (var line in Board.Split('\n'))
        {
            var p = line.Split('|');
            if (p.Length >= 4 && int.TryParse(p[0], out var t))
            {
                Lbl("team" + t, $"{TeamName(t)}: {p[1]} {Loc.L("очк.", "pts")} · {p[2]}%", Tint(t), x, y, 18, _st);
                y += 24;
                if (!Started)   // до старта видно, кто в какой команде
                {
                    var who = Teams.Where(kv => kv.Value == t).Select(kv => NetDriver.NameOf(kv.Key) ?? "?").ToList();
                    if (who.Count > 0) { Lbl("mem" + t, string.Join(", ", who), new Color(0.9f, 0.9f, 0.9f, 0.85f), x + 10, y, 14, _small); y += 20; }
                }
            }
            else if (p.Length >= 3 && p[0] == "#" && p[2] != "0")
            {
                // раньше «Общих: 2 из 8» значило «осталось 2» и читалось как «собрано 2»
                int cl = int.TryParse(p[1], out var clv) ? clv : 0, ct = int.TryParse(p[2], out var ctv) ? ctv : 0;
                Lbl("left", Loc.L($"Общие: взято {ct - cl} из {ct}", $"Commons: {ct - cl} of {ct} taken"), Color.white, x, y, 18, _st); y += 24;
            }
        }
        if (!Started)
        {
            var spec = Teams.Where(kv => kv.Value < 0).Select(kv => NetDriver.NameOf(kv.Key) ?? "?").ToList();
            if (spec.Count > 0) { Lbl("spec", Loc.L("Зрители: ", "Spectators: ") + string.Join(", ", spec), new Color(0.8f, 0.8f, 0.8f), x, y, 14, _small); y += 22; }
        }
        if (Winner != null) { Lbl("win", Loc.L($"Победили {Winner}!", $"{Winner} win!"), new Color(1f, 0.85f, 0.4f), x, y, 20, _st); y += 28; }
        if (Sigils.FontReady)
            Sigils.WantSprite("board_bg", "panel-modal", new Color(1f, 1f, 1f, 0.92f), x + 145, (y0 + y) / 2f, 330, y - y0 + 30, 0f, back: true);
        _st.normal.textColor = Color.white;
    }

    // ---- названия и цвета семейств из самой игры (на языке игры) ----
    public static CodeBase.Services.LocalizationService.LocalizationService LocSvc;
    static FamilyCatalogConfig _catalog;
    static readonly Dictionary<int, string> _famName = new();
    static readonly Dictionary<int, Color> _famColor = new();
    static FamilyCatalogConfig Catalog()
    {
        if (_catalog != null) return _catalog;
        try { foreach (var o in Resources.FindObjectsOfTypeAll(Il2CppInterop.Runtime.Il2CppType.Of<FamilyCatalogConfig>())) { _catalog = o.TryCast<FamilyCatalogConfig>(); if (_catalog != null) break; } } catch { }
        return _catalog;
    }
    // название как на табличке над полкой: подвид («Анютины глазки»), если зона — одна полка, иначе семейство
    public static string FamilyName(int fam, int sec = -1)
    {
        int key = fam * 1000 + (sec + 1);
        if (_famName.TryGetValue(key, out var n)) return n;
        string name = null;
        try
        {
            var cat = Catalog();
            if (cat != null && LocSvc != null)
            {
                var fname = Clean(LocSvc.Get(cat.GetNameTerm((eSectionFamily)fam)));
                if (sec >= 0)
                {
                    var spec = Clean(LocSvc.Get(cat.GetSpecNameTerm((eSectionFamily)fam, sec)));
                    name = string.IsNullOrEmpty(spec) ? fname : spec;
                }
                else name = fname;
            }
        }
        catch (Exception e) { Plugin.L.LogWarning("TOURNEY family name: " + e.Message); }
        if (string.IsNullOrEmpty(name)) return ((eSectionFamily)fam).ToString() + (sec >= 0 ? $" #{sec + 1}" : "");   // не кешируем — переведём, когда сервис появится
        _famName[key] = name;
        return name;
    }
    static string Clean(string t) => string.IsNullOrEmpty(t) ? t : System.Text.RegularExpressions.Regex.Replace(t, "<[^>]*>", "").Trim();
    static Color FamColor(int fam)
    {
        if (_famColor.TryGetValue(fam, out var c)) return c;
        c = Color.gray;
        try { var cat = Catalog(); if (cat != null) { c = cat.ResolveColor((eSectionFamily)fam, false); _famColor[fam] = c; } } catch { }
        return c;
    }
    static bool _famDrawFailed;
    static GUIStyle _swatchSt;
    static float SafeFamilies(List<(int fam, int sec)> fams, float x, float y)
    {
        try { return DrawFamilies(fams, x, y); }
        catch (Exception e)
        {
            if (!_famDrawFailed) { _famDrawFailed = true; Plugin.L.LogWarning("TOURNEY табло: " + e); }
            // запасной вариант — одним текстом
            _small.normal.textColor = Color.white;
            foreach (var f in fams) { GUI.Label(new Rect(x + 8, y, 400, 22), "• " + FamilyName(f.fam, f.sec), _small); y += 22; }
            return y + 4;
        }
    }
    static float DrawFamilies(List<(int fam, int sec)> fams, float x, float y)
    {
        foreach (var f in fams)
        {
            // эмблема подвида как у игры (кружок цвета семейства + знак); не вышло — квадратик «■»
            if (Sigils.Ready)
            {
                Sigils.Want(f.fam, f.sec, FamColor(f.fam), x, y - 2, 26);
                Lbl($"fam{f.fam}_{f.sec}", FamilyName(f.fam, f.sec), Color.white, x + 32, y + 1, 16, _small);
                y += 28;
                continue;
            }
            if (_swatchSt == null) _swatchSt = new GUIStyle(GUI.skin.label) { fontSize = 22, fontStyle = FontStyle.Bold };
            _swatchSt.normal.textColor = Color.black;
            GUI.Label(new Rect(x + 1, y - 2, 26, 26), "■", _swatchSt);
            _swatchSt.normal.textColor = FamColor(f.fam);
            GUI.Label(new Rect(x, y - 3, 26, 26), "■", _swatchSt);
            _small.normal.textColor = Color.white;
            GUI.Label(new Rect(x + 28, y, 380, 22), FamilyName(f.fam, f.sec), _small);
            y += 22;
        }
        return y + 4;
    }

    // ---- подсветка ячеек: под каждой ячейкой зоны — цветная подложка команды (общие — белые) ----
    static readonly HashSet<int> _okBefore = new();
    static bool _zonesDirty;
    static float _zonesResendAt;

    // ---- события счёта (общая засчитана/сгорела, победа): хост показывает у себя и рассылает в табло — у всех одинаково (п. 45) ----
    static readonly List<(int id, float dur, string ru, string en)> _events = new();
    static int _evId, _evSeen = -1;
    static void Event(string ru, string en, float dur)
    {
        Coop.Say(Loc.L(ru, en), dur);
        ru = ru.Replace("|", "/"); en = en.Replace("|", "/");
        _events.Add((++_evId, dur, ru, en));
        while (_events.Count > 6) _events.RemoveAt(0);
    }
    static void SeeEvents(string board)
    {
        int max = _evSeen;
        foreach (var line in board.Split('\n'))
        {
            if (!line.StartsWith("E|")) continue;
            var p = line.Split('|');
            if (p.Length < 5 || !int.TryParse(p[1], out var id)) continue;
            if (id > max) max = id;
            if (_evSeen < 0 || id <= _evSeen) continue;   // первое табло после входа — старое не показываем
            float.TryParse(p[2], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var dur);
            Coop.Say(Loc.L(p[3], p[4]), dur > 0 ? dur : 3f);
        }
        _evSeen = Math.Max(max, 0);
    }
    static string _markerZones, _markersBuiltFor;
    static readonly List<GameObject> _markers = new();
    static readonly Dictionary<int, Material> _markerMat = new();
    static readonly Color CommonTint = new Color(0.95f, 0.95f, 0.95f);

    static string ZonesPacket()
    {
        var sb = new StringBuilder("Z\n");
        if (_slotFamily != null)
            foreach (var kv in _slotFamily)
            {
                int team = _zones != null && _zones.TryGetValue(kv.Value, out var t) ? t : -2;
                if (team == -2) continue;
                sb.Append(kv.Key).Append(':').Append(team).Append('\n');
            }
        return sb.ToString();
    }
    static void SetMarkerZones(string packet)
    {
        _markerZones = packet;
        _slotTeam.Clear();
        foreach (var line in packet.Split('\n'))
        {
            var p = line.Split(':');
            if (p.Length == 2 && int.TryParse(p[0], out var u) && int.TryParse(p[1], out var t)) _slotTeam[u] = t;
        }
    }
    static readonly Dictionary<int, int> _slotTeam = new();   // ячейка → команда (-1 общая), у всех из пакета зон

    // у всех: можно ли мне ставить в эту ячейку (общая — только после своей зоны)
    public static bool BlockCommon(SlotFacade slot)
    {
        if (!On || !Started || slot == null || _slotTeam.Count == 0) return false;
        int uid = -1;
        foreach (var kv in Sync.Slots) if (kv.Value != null && kv.Value.Pointer == slot.Pointer) { uid = kv.Key; break; }
        if (uid < 0 || !_slotTeam.TryGetValue(uid, out var team) || team != -1) return false;
        int me = TeamOf(Sync.MyId);
        if (me < 0) return false;
        int mine = 0, done = 0;
        foreach (var kv in _slotTeam)
        {
            if (kv.Value != me) continue;
            mine++;
            if (Sync.Slots.TryGetValue(kv.Key, out var s) && s != null && s.IsCorrectlyFilled) done++;
        }
        return mine > 0 && done < mine;
    }

    public static void ClearMarkers()
    {
        foreach (var go in _markers) try { if (go != null) UnityEngine.Object.Destroy(go); } catch { }
        _markers.Clear(); _markersBuiltFor = null;
    }

    static Mesh _pad;
    static Mesh PadMesh()
    {
        if (_pad != null) return _pad;
        const float h = 0.0375f;
        _pad = new Mesh();
        _pad.vertices = new[] { new Vector3(-h, 0, -h), new Vector3(-h, 0, h), new Vector3(h, 0, h), new Vector3(h, 0, -h) };
        _pad.triangles = new[] { 0, 1, 2, 0, 2, 3 };
        _pad.RecalculateNormals(); _pad.RecalculateBounds();
        return _pad;
    }

    static Material MarkerMat(int team)
    {
        int key = team >= 0 ? 100 + TeamPal[team % 6] : -1;   // по цвету, а не по номеру команды: цвета приходят табло
        if (_markerMat.TryGetValue(key, out var m) && m != null) return m;
        m = Avatar.NewMat(team >= 0 ? Avatar.Robe[TeamPal[team % 6] % 12] : CommonTint, 0f, 0.1f);
        _markerMat[key] = m;
        return m;
    }

    public static void MarkersTick()
    {
        Sigils.Tick();
        // общие подложки мигают (белый ↔ розовый): белые терялись на фоне шкафов, а розового нет ни у одной команды
        if (On && _markerMat.TryGetValue(-1, out var cm) && cm != null)
        {
            try
            {
                var pc = Color.Lerp(Color.white, new Color(1f, 0.45f, 0.85f), 0.5f + 0.5f * Mathf.Sin(Time.unscaledTime * 5f));
                if (cm.HasProperty("_BaseColor")) cm.SetColor("_BaseColor", pc);
                if (cm.HasProperty("_Color")) cm.SetColor("_Color", pc);
            }
            catch { }
        }
        if (!On || Coop.State != eGameState.Playing || _markerZones == null || _markerZones == _markersBuiltFor) return;
        if (Sync.Slots.Count == 0) return;
        ClearMarkers();
        _markersBuiltFor = _markerZones;
        int made = 0, missing = 0;
        foreach (var line in _markerZones.Split('\n'))
        {
            var p = line.Split(':');
            if (p.Length != 2 || !int.TryParse(p[0], out var uid) || !int.TryParse(p[1], out var team)) continue;
            if (made >= 6000) break;
            if (!Sync.Slots.TryGetValue(uid, out var slot) || slot == null) { missing++; continue; }
            try
            {
                // плоская подложка без коллайдера (своя сетка — физику не трогаем)
                var go = new GameObject("PotionCoop_zone");
                go.layer = 2;   // Ignore Raycast — прицел и зелья её не задевают
                go.AddComponent<MeshFilter>().sharedMesh = PadMesh();
                go.AddComponent<MeshRenderer>().sharedMaterial = MarkerMat(team);
                var t = slot.transform;
                // центр подставки берём по её видимой сетке: точка ячейки у игры смещена от центра
                var pos = t.position;
                try
                {
                    var rs = slot.GetComponentsInChildren<Renderer>();
                    if (rs != null && rs.Length > 0)
                    {
                        var bd = rs[0].bounds;
                        for (int i = 1; i < rs.Length; i++) bd.Encapsulate(rs[i].bounds);
                        pos = new Vector3(bd.center.x, bd.min.y, bd.center.z);
                    }
                }
                catch { }
                go.transform.position = pos + Vector3.up * 0.004f;
                go.transform.rotation = Quaternion.Euler(0f, t.rotation.eulerAngles.y, 0f);
                _markers.Add(go); made++;
            }
            catch (Exception e) { Plugin.L.LogWarning("TOURNEY marker: " + e.Message); break; }
        }
        Plugin.L.LogInfo($"TOURNEY: подсветил ячеек {made}{(missing > 0 ? $", не нашёл {missing}" : "")}");
    }
}

static class TourneyLocPatches
{
    // сервис локализации игры — нужен, чтобы писать названия семейств на языке игры
    [HarmonyLib.HarmonyPostfix]
    [HarmonyLib.HarmonyPatch(typeof(CodeBase.Services.LocalizationService.LocalizationService), nameof(CodeBase.Services.LocalizationService.LocalizationService.Get), new[] { typeof(eLocTerm) })]
    static void Get_Post(CodeBase.Services.LocalizationService.LocalizationService __instance)
    {
        if (Tourney.LocSvc == null) { Tourney.LocSvc = __instance; Plugin.L.LogInfo("TOURNEY: нашёл перевод игры"); }
    }
}
