using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using HarmonyLib;
using UnityEngine;
using Il2CppInterop.Runtime;
using CodeBase.Components.Hero;
using CodeBase.Components.Potions;
using CodeBase.Components.Slots;
using CodeBase.Components.Vats;
using CodeBase.Services.PersistentDataService;
using CodeBase.Services.PotionsPersistenceService;
using CodeBase.Services.UndoService;
using CodeBase.Services.RuntimeDataService;
using IL2List = Il2CppSystem.Collections.Generic.List<eSectionFamily>;
using IL2RO = Il2CppSystem.Collections.Generic.IReadOnlyList<eSectionFamily>;

namespace PotionCoop;

// Шаг 6в: ходы игроков. Каждый рассылает СВОИ ходы, остальные повторяют их у себя.
// Передаём не «что нажал», а результат: итоговое содержимое сосудов и куда встало зелье.
public static class Sync
{
    public enum Op : byte { Grab = 1, Contents = 2, VatContents = 3, Shelve = 4, Drop = 5, Pose = 6, HandRow = 7, Pour = 8, Cat = 9, Toast = 10, Live = 11, Unshelve = 12, Hang = 13, Left = 14 }

    static readonly Dictionary<int, PotionFacade> Potions = new();
    static readonly Dictionary<int, SlotFacade> Slots = new();
    static readonly Dictionary<int, VatFacade> Vats = new();
    static readonly Dictionary<int, Transform> HomeParent = new();   // откуда забрали зелье в чужую руку
    static readonly HashSet<int> MyDropped = new();                   // мои брошенные — шлём, где улеглись
    public static readonly Dictionary<int, Avatar> RemoteHeld = new(); // зелья в руках у других: номер → чей аптекарь
    public static readonly Dictionary<string, int> Steals = new();     // счёт краж: кто → сколько раз отобрал
    static HeroPotionPicker _picker;
    public static string StealsLine()
    {
        if (Steals.Count == 0) return null;
        var parts = new List<string>();
        foreach (var kv in Steals) parts.Add($"{kv.Key} {kv.Value}");
        return Loc.L("Кражи: ", "Steals: ") + string.Join(" · ", parts);
    }
    static void CountSteal(string thief)
    {
        // счёт ведёт хост (он же хранит его в папке аптеки); у гостей — только до прихода счёта от хоста
        if (Coop.Role == Role.Guest) return;
        if (thief == "Ты" || thief == "я") thief = NetDriver.I?.MyName ?? thief;
        Steals[thief] = Steals.TryGetValue(thief, out var n) ? n + 1 : 1;
        _stealsDirty = true;
    }
    static readonly Dictionary<int, float> Flying = new();            // мои брошенные в полёте: номер → когда бросил
    // полёт чужого зелья к месту, где оно улеглось у бросившего
    class Glide { public PotionFacade P; public Vector3 From, To; public Quaternion FromR, ToR; public float T0; public string Thrower; }
    static readonly List<Glide> Glides = new();
    public static PotionsPersistence Persistence;
    public static IPersistentDataService PersistentSvc;   // ловим, когда игра собирает меню
    public static bool Applying;                                       // сейчас применяем чужой ход — не пересылать

    // ---------- кот: живёт у хоста, у гостей только повторяет ----------
    static CodeBase.Components.Cat.CatFacade _cat;
    static Vector3 _catPos; static Quaternion _catRot; static bool _catHas; static float _catNextSend;
    static readonly string[] CatKeep = { "CatFacade", "CatAnimator", "CatSpineFader", "CatHighlighter", "CatHeadTurner", "CatMeower" };

    static void SetupCat()
    {
        _cat = UnityEngine.Object.FindFirstObjectByType<CodeBase.Components.Cat.CatFacade>();
        _catHas = false;
        if (_cat == null || Coop.Role != Role.Guest) return;
        int off = 0;
        foreach (var mb in _cat.GetComponentsInChildren<MonoBehaviour>(true))
        {
            if (mb == null) continue;
            var n = mb.GetIl2CppType().Name;
            if (n.StartsWith("Cat") && Array.IndexOf(CatKeep, n) < 0) { mb.enabled = false; off++; }
        }
        Plugin.L.LogInfo($"SYNC: кот у гостя ходит за котом хоста (выключено частей мозга: {off})");
    }

    public static void Tick(float dt)
    {
        TickThrows();
        TickGlides();
        TickLiveSend();
        TickLiveFollow(dt);
        TickOwnership();
        TickInitialScrub();
        TickHookReturn();
        TickSteals();
        if (_cat == null || Coop.Role == Role.Solo || Coop.State != eGameState.Playing) return;
        var t = _cat.transform;
        if (Coop.Role == Role.Host)
        {
            if (Time.unscaledTime < _catNextSend) return;
            _catNextSend = Time.unscaledTime + 0.1f;
            var p = t.position; var q = t.rotation;
            Out(Op.Cat, w => WritePose(w, p, q));
        }
        else if (_catHas)
        {
            float k = 1f - Mathf.Exp(-12f * dt);
            t.position = Vector3.Lerp(t.position, _catPos, k);
            t.rotation = Quaternion.Slerp(t.rotation, _catRot, k);
        }
    }

    // Зелье ушло из моих рук на пол (бросок, дырявый карман): сообщаем и дальше ведём его полёт
    public static void LocalDropped(PotionFacade p, bool noBreak = false)
    {
        if (p == null) return;
        MyDropped.Add(p.Uid);
        Flying[p.Uid] = Time.unscaledTime;
        if (noBreak) _respawned.Add(p.Uid);
        var u = UnitsOf(p.Contents);
        var pos = p.transform.position; var rot = p.transform.rotation; int uid = p.Uid;
        Out(Op.Drop, w => { w.Write(uid); WritePose(w, pos, rot); w.Write((byte)u.Length); w.Write(u); });
    }

    // Пока мои зелья летят — 10 раз в секунду шлём, где они (одним пакетом)
    static float _nextLive;
    static void TickLiveSend()
    {
        if (Flying.Count == 0 || Time.unscaledTime < _nextLive) return;
        _nextLive = Time.unscaledTime + 0.1f;
        var list = new List<PotionFacade>();
        foreach (var uid in Flying.Keys) if (Potions.TryGetValue(uid, out var p) && p != null) list.Add(p);
        if (list.Count == 0) return;
        Out(Op.Live, w =>
        {
            w.Write((ushort)list.Count);
            foreach (var p in list) { w.Write(p.Uid); WritePose(w, p.transform.position, p.transform.rotation); }
        });
    }

    // Чужие зелья в полёте: плавно ведём к последней присланной точке
    class LiveTarget { public Vector3 Pos; public Quaternion Rot; public string Thrower; public float Last = Time.unscaledTime; }
    static readonly Dictionary<int, LiveTarget> Live = new();
    static void TickLiveFollow(float dt)
    {
        if (Live.Count == 0) return;
        float k = 1f - Mathf.Exp(-18f * dt);
        List<int> stale = null;
        foreach (var kv in Live)
        {
            if (!Potions.TryGetValue(kv.Key, out var p) || p == null) continue;
            var t = p.transform;
            t.SetPositionAndRotation(Vector3.Lerp(t.position, kv.Value.Pos, k), Quaternion.Slerp(t.rotation, kv.Value.Rot, k));
            if (Time.unscaledTime - kv.Value.Last > 8f) (stale ??= new List<int>()).Add(kv.Key);
        }
        if (stale == null) return;
        foreach (var uid in stale)
        {   // бросивший давно молчит — кладём склянку туда, где она была последний раз, и отпускаем
            var lt = Live[uid]; Live.Remove(uid);
            if (!Potions.TryGetValue(uid, out var p) || p == null) continue;
            p.transform.SetPositionAndRotation(lt.Pos, lt.Rot);
            try { p.Unfreeze(); if (Coop.Role == Role.Host) Persistence?.RecordFloor(p); } catch { }
            Plugin.L.LogInfo($"SYNC: склянка {uid} зависла в полёте — отпустил");
        }
    }

    // Моё брошенное зелье улеглось (или летит уже 6 с) — шлём точное место
    static readonly List<int> _done = new();
    static readonly List<PotionFacade> _breakCheck = new();
    static readonly HashSet<int> _respawned = new();   // перенесённая после разбития — второй раз не бьём
    public static bool ForceBreakNext;                  // F5 у хоста: следующий бросок точно разобьётся
    static readonly HashSet<int> _forced = new();

    // ---------- модификатор «Хрупкие склянки» (решает хост) ----------
    // Брошенная склянка с шансом разбивается: её жидкость по порции разливается в случайные колбы на полу,
    // а сама она, пустая, появляется в другом месте. Ни зелья, ни склянок меньше не становится.
    static void HostMaybeBreak(PotionFacade p, string thrower)
    {
        if (p == null || Coop.Role != Role.Host || !Plugin.ModBreak.Value) return;
        bool forced = _forced.Remove(p.Uid) || ForceBreakNext;
        if (!forced && UnityEngine.Random.value * 100f > Plugin.ModBreakChance.Value) return;
        ForceBreakNext = false;
        var units = UnitsOf(p.Contents);
        if (units.Length == 0) return;

        var targets = Potions.Values.Where(x => x != null && x != p && !RemoteHeld.ContainsKey(x.Uid) && Array.IndexOf(_row, x.Uid) < 0 && IsLoose(x) && x.Contents != null && x.Contents.FreeSpace > 0)
                                    .OrderBy(_ => UnityEngine.Random.value).Take(12).ToList();
        if (targets.Count == 0) return;

        var changed = new HashSet<PotionFacade>();
        var left = new List<byte>(units);
        int ti = 0;
        // разливаем сверху вниз, по одной порции в разные колбы
        for (int i = left.Count - 1; i >= 0 && targets.Count > 0; i--)
        {
            for (int tries = 0; tries < targets.Count; tries++)
            {
                var t = targets[ti % targets.Count]; ti++;
                if (t.Contents.FreeSpace <= 0) continue;
                t.Contents.AddRun((eSectionFamily)left[i], 1);
                changed.Add(t);
                left.RemoveAt(i);
                break;
            }
        }
        foreach (var t in changed) { t.RepaintLiquid(); Persistence?.RecordFloor(t); OutContents(t); }

        // сама склянка — пустая (или с тем, что не влезло) — появляется в случайном месте у другой кучки
        p.SetLiquidContents(ToIl2(left.ToArray())); p.RepaintLiquid();
        var spot = Potions.Values.Where(x => x != null && x != p && IsLoose(x)).OrderBy(_ => UnityEngine.Random.value).FirstOrDefault();
        if (spot != null)
        {
            var off = UnityEngine.Random.insideUnitCircle * 0.4f;
            p.transform.position = spot.transform.position + new Vector3(off.x, 0.35f, off.y);
            p.Unfreeze();
            MyDropped.Add(p.Uid); Flying[p.Uid] = Time.unscaledTime; _respawned.Add(p.Uid);   // когда уляжется — разошлём место
        }
        OutContents(p);
        SendPose(p);
        // остальным — обоими языками сразу (через \u001f), каждый покажет свой
        string me = thrower == "Ты" ? null : thrower;
        var msgRu = me == null ? $"Ты разбил склянку! Зелье разлилось по {changed.Count} колбам" : $"Склянка разбилась у {me}! Зелье разлилось по {changed.Count} колбам";
        var msgEn = me == null ? $"You broke a flask! The potion spilled into {changed.Count} flasks" : $"{me} broke a flask! The potion spilled into {changed.Count} flasks";
        Coop.Say(Loc.L(msgRu, msgEn), 4f);
        var outRu = $"Склянка разбилась у {(me ?? "хоста")}! Зелье разлилось по {changed.Count} колбам";
        var outEn = $"{(me ?? "The host")} broke a flask! The potion spilled into {changed.Count} flasks";
        Out(Op.Toast, w => w.Write(outRu + "\u001f" + outEn));
        var msg = msgRu;
        Plugin.L.LogInfo($"MOD: {msg}, склянка {p.Uid} перенесена");
    }

    static bool IsLoose(PotionFacade x) { try { return x.IsLooseAtRest; } catch { return false; } }

    public static void ClearUndo()
    {
        try { if (_picker == null) _picker = UnityEngine.Object.FindFirstObjectByType<HeroPotionPicker>(); _picker?._undoService?.Clear(); } catch { }
    }

    // Землетрясение (хост): трясёт пол и неготовые полки; правильно расставленные полки не трогаем
    public static int HostQuake(int max)
    {
        if (Coop.Role != Role.Host) return 0;
        var shelfOf = new Dictionary<int, SlotFacade>();
        foreach (var s in Slots.Values) { try { var sp = s.Potion; if (sp != null) shelfOf[sp.Uid] = s; } catch { } }
        var cands = Potions.Values.Where(p =>
        {
            if (p == null || RemoteHeld.ContainsKey(p.Uid) || Array.IndexOf(_row, p.Uid) >= 0) return false;
            try
            {
                if (shelfOf.TryGetValue(p.Uid, out var slot)) return !slot.IsCorrectlyFilled && !p.IsSealed;   // итоговые полки — не трогаем
                return p.IsLooseAtRest || p.IsSocketed;
            }
            catch { return false; }
        }).OrderBy(_ => UnityEngine.Random.value).Take(max).ToList();

        foreach (var p in cands)
        {
            try
            {
                if (shelfOf.TryGetValue(p.Uid, out var slot)) { slot.ClearPotion(false); int su = slot.Uid, pu = p.Uid; Out(Op.Unshelve, w => { w.Write(pu); w.Write(su); }); }
                p.ReleaseSupported(true);
                p.Unfreeze();
                var v = new Vector3(UnityEngine.Random.Range(-1.5f, 1.5f), UnityEngine.Random.Range(1.5f, 3.5f), UnityEngine.Random.Range(-1.5f, 1.5f));
                p.Toss(v, UnityEngine.Random.insideUnitSphere * 8f);
                LocalDropped(p, noBreak: true);
            }
            catch (Exception e) { Plugin.L.LogError("QUAKE: " + e.Message); }
        }
        ClearUndo();
        return cands.Count;
    }

    // Дырявый карман (у того, на кого наслали): 1–2 зелья выскальзывают из рук
    public static int LeakyPocket()
    {
        if (_picker == null) _picker = UnityEngine.Object.FindFirstObjectByType<HeroPotionPicker>();
        if (_picker == null || _row.Length == 0) return 0;
        int n = Mathf.Min(_row.Length, UnityEngine.Random.Range(1, 3)), dropped = 0;
        var uids = _row.OrderBy(_ => UnityEngine.Random.value).Take(n).ToList();
        foreach (var uid in uids)
        {
            if (!Potions.TryGetValue(uid, out var p) || p == null) continue;
            try
            {
                ScrubFromMe(p);   // вынимаем по-настоящему: из списка руки, фартука, поднятой — иначе игра считает её «в руке»
                p.Unfreeze();
                var fwd = Camera.main != null ? Camera.main.transform.forward : Vector3.forward;
                p.Toss(fwd * 1.2f + Vector3.up * 1.5f + UnityEngine.Random.insideUnitSphere * 0.6f, UnityEngine.Random.insideUnitSphere * 6f);
                LocalDropped(p);
                dropped++;
            }
            catch (Exception e) { Plugin.L.LogError("LEAKY: " + e.Message); }
        }
        try { _picker.ReseatRow(true); _picker.RecordCarryRow(false); } catch { }
        RefreshRow(); SendRow();   // всем — ряд уже без выпавших
        ClearUndo();
        return dropped;
    }
    static void TickThrows()
    {
        if (Flying.Count == 0) return;
        _done.Clear();
        foreach (var kv in Flying)
        {
            if (!Potions.TryGetValue(kv.Key, out var p) || p == null) { _done.Add(kv.Key); continue; }
            float age = Time.unscaledTime - kv.Value;
            bool rest = false;
            try { rest = age > 0.4f && (p.IsResting || p.IsLooseAtRest); } catch { }
            if (rest || age > 6f)
            {
                _done.Add(kv.Key); SendPose(p);
                if (Coop.Role == Role.Host) { try { Persistence?.RecordFloor(p); } catch { } }
                bool force = ForceBreakNext && !_respawned.Contains(kv.Key);
                if (!_respawned.Remove(kv.Key)) _breakCheck.Add(p);
                if (force) _forced.Add(p.Uid);
            }
        }
        foreach (var u in _done) { Flying.Remove(u); MyDropped.Remove(u); }
        foreach (var p in _breakCheck) HostMaybeBreak(p, "Ты");
        _breakCheck.Clear();
    }

    static void SendPose(PotionFacade p)
    {
        var pos = p.transform.position; var rot = p.transform.rotation; int uid = p.Uid;
        Out(Op.Pose, w => { w.Write(uid); WritePose(w, pos, rot); });
    }

    static void TickGlides()
    {
        for (int i = Glides.Count - 1; i >= 0; i--)
        {
            var g = Glides[i];
            if (g.P == null) { Glides.RemoveAt(i); continue; }
            float k = Mathf.Clamp01((Time.unscaledTime - g.T0) / 0.6f);
            float e = k * k * (3 - 2 * k);
            var pos = Vector3.Lerp(g.From, g.To, e) + Vector3.up * Mathf.Sin(Mathf.PI * k) * Mathf.Min(1.5f, Vector3.Distance(g.From, g.To) * 0.25f);
            g.P.transform.SetPositionAndRotation(pos, Quaternion.Slerp(g.FromR, g.ToR, e));
            if (k >= 1f)
            {
                Glides.RemoveAt(i);
                Applying = true;
                try { g.P.Unfreeze(); Persistence?.RecordFloor(g.P); } catch { } finally { Applying = false; }
                if (g.Thrower != null) HostMaybeBreak(g.P, g.Thrower);
            }
        }
    }

    // Индексируем заново только если лаборатория действительно новая (загрузка), а не возврат из паузы/меню навыков.
    // Раньше каждое меню стирало память: кто что держит, что летит — и склянки зависали в воздухе.
    public static bool NeedsReindex()
    {
        if (Potions.Count == 0 || _picker == null) return true;
        foreach (var p in Potions.Values) return p == null;   // первое же зелье уничтожено — сцена новая
        return true;
    }
    public static void Resume()
    {
        Plugin.L.LogInfo("SYNC: вернулся в игру — память о склянках сохранена");
        ResendRow();
    }

    public static void Reindex()
    {
        _initialScrubAt = Coop.Role == Role.Guest ? Time.unscaledTime + 6f : 0f;   // ждём, пока игра разложит руку (до 6 с), и сразу чистим
        Holders.Clear();
        Potions.Clear(); Slots.Clear(); Vats.Clear(); HomeParent.Clear(); MyDropped.Clear(); RemoteHeld.Clear(); Flying.Clear(); Glides.Clear(); Live.Clear(); RecentlyReleased.Clear(); _prd = null;
        foreach (var p in UnityEngine.Object.FindObjectsByType<PotionFacade>(FindObjectsInactive.Include, FindObjectsSortMode.None))
            if (p != null && !p.IsViewModel) Potions[p.Uid] = p;
        foreach (var s in UnityEngine.Object.FindObjectsByType<SlotFacade>(FindObjectsInactive.Include, FindObjectsSortMode.None))
            if (s != null) Slots[s.Uid] = s;
        foreach (var v in UnityEngine.Object.FindObjectsByType<VatFacade>(FindObjectsInactive.Include, FindObjectsSortMode.None))
            if (v != null) Vats[v.Uid] = v;
        try
        {
            foreach (var h in UnityEngine.Object.FindObjectsByType<CodeBase.Components.EssenceSeparator.PourOutHolderFacade>(FindObjectsInactive.Include, FindObjectsSortMode.None))
                if (h != null && !h.IsViewModel) Holders[HolderKey(h)] = h;
        }
        catch (Exception e) { Plugin.L.LogWarning("SYNC: подставки не нашёл: " + e.Message); }
        if (Coop.Role == Role.Host) { LoadSteals(); LoadHooks(); }
        _hasMySpawn = false;
        try { FindLooseRoot(); } catch (Exception e) { Plugin.L.LogWarning("SYNC loose root: " + e.Message); }
        var picker = UnityEngine.Object.FindFirstObjectByType<HeroPotionPicker>();
        _picker = picker;
        if (picker != null) Persistence = picker._potionsPersistence?.TryCast<PotionsPersistence>();
        Plugin.L.LogInfo($"SYNC: проиндексировал зелий {Potions.Count}, ячеек {Slots.Count}, чанов {Vats.Count}");
        SetupCat();
    }

    // ---------- содержимое сосудов ----------

    static byte[] UnitsOf(CodeBase.Components.Potions.Liquid.LiquidContents c)
    {
        var e = new PotionPersistentEntry();
        PotionsPersistence.WriteUnits(e, c);
        var u = e.Units;
        var r = new byte[u == null ? 0 : u.Length];
        for (int i = 0; i < r.Length; i++) r[i] = (byte)u[i];
        return r;
    }

    static IL2RO ToIl2(byte[] units)
    {
        var l = new IL2List();
        foreach (var x in units) l.Add((eSectionFamily)x);
        return l.Cast<IL2RO>();
    }

    // ---------- отправка своих ходов ----------

    static void Out(Op op, Action<BinaryWriter> body)
    {
        if (Applying || NetDriver.I == null || !NetDriver.I.HasPeers) return;
        using var ms = new MemoryStream();
        using var w = new BinaryWriter(ms);
        w.Write((byte)op);
        body(w);
        NetDriver.I.BroadcastMove(ms.ToArray());
    }

    static void OutContents(PotionFacade p)
    {
        if (p == null) return;
        var u = UnitsOf(p.Contents);
        Out(Op.Contents, w => { w.Write(p.Uid); w.Write((byte)u.Length); w.Write(u); });
    }

    static void OutVat(VatFacade v)
    {
        if (v == null) return;
        var u = UnitsOf(v.Contents);
        Out(Op.VatContents, w => { w.Write(v.Uid); w.Write((byte)u.Length); w.Write(u); });
    }

    static void WritePose(BinaryWriter w, Vector3 p, Quaternion q)
    {
        w.Write(p.x); w.Write(p.y); w.Write(p.z); w.Write(q.x); w.Write(q.y); w.Write(q.z); w.Write(q.w);
    }
    static void ReadPose(BinaryReader r, out Vector3 p, out Quaternion q)
    {
        p = new Vector3(r.ReadSingle(), r.ReadSingle(), r.ReadSingle());
        q = new Quaternion(r.ReadSingle(), r.ReadSingle(), r.ReadSingle(), r.ReadSingle());
    }

    public static void OnLocalGrab(PotionFacade potion, SlotFacade fromSlot)
    {
        if (potion == null) return;
        Touched(potion.Uid);
        NetDriver.I?.LocalAnim("grab");
        NetDriver.I?.DummyGrab(potion);
        Out(Op.Grab, w => { w.Write(potion.Uid); w.Write(fromSlot != null ? fromSlot.Uid : 0); });
    }

    public static void OnLocalMove(UndoRecord r)
    {
        NetDriver.I?.LocalAnim(r.Kind switch
        {
            eUndoMoveKind.Shelve => "shelve",
            eUndoMoveKind.Drop => "throw",
            _ => "pour"
        });
        if ((r.Kind == eUndoMoveKind.Shelve || r.Kind == eUndoMoveKind.Drop) && r.Potion != null) NetDriver.I?.DummyRelease(r.Potion.Uid);
        switch (r.Kind)
        {
            case eUndoMoveKind.PotionToPotion:
            case eUndoMoveKind.PotionToShelvedPotion:
            {
                var g = r.Giver; var tk = r.Taker;
                NetDriver.I?.DummyPour(g, tk);
                if (g != null && tk != null) Out(Op.Pour, w => { w.Write(g.Uid); w.Write(tk.Uid); });
                OutContents(g); OutContents(tk); break;
            }
            case eUndoMoveKind.PotionToVat:
                OutContents(r.Giver); OutVat(r.Vat); break;
            case eUndoMoveKind.VatToPotion:
                OutVat(r.Vat); OutContents(r.Taker); break;
            case eUndoMoveKind.Shelve:
            {
                var p = r.Potion; var s = r.Slot;
                if (p == null || s == null) break;
                var u = UnitsOf(p.Contents);
                Out(Op.Shelve, w => { w.Write(p.Uid); w.Write(s.Uid); w.Write((byte)u.Length); w.Write(u); });
                break;
            }
            case eUndoMoveKind.Drop:
            {
                LocalDropped(r.Potion);
                break;
            }
            case eUndoMoveKind.HangOnHolder:
            {
                var p = r.Potion; var h = r.Holder;
                if (p == null || h == null) break;
                int uid = p.Uid; var key = HolderKey(h);
                Out(Op.Hang, w => { w.Write(uid); w.Write(key); });
                break;
            }
            default:
                Plugin.L.LogInfo($"SYNC: ход {r.Kind} пока не синхронизируется");
                break;
        }
    }

    // Что у меня в руке и какое выбрано — шлём всем и показываем манекену
    static int[] _row = Array.Empty<int>();
    static int _active = -1;
    static bool _rowWarned;
    public static void OnLocalHandRow(Il2CppSystem.Collections.Generic.IReadOnlyList<PotionFacade> held)
    {
        // ряд берём прямо из данных игры (раньше пытались привести held к List — не выходило ни разу)
        if (!RefreshRow())
        {
            var list = held?.TryCast<Il2CppSystem.Collections.Generic.List<PotionFacade>>();
            if (list == null) { if (!_rowWarned) { _rowWarned = true; Plugin.L.LogWarning("SYNC: не смог прочитать ряд в руке"); } return; }
            var row = new int[list.Count];
            for (int i = 0; i < row.Length; i++) row[i] = list[i] != null ? list[i].Uid : 0;
            _row = row;
        }
        SendRow();
    }
    public static void ResendRow() { RefreshRow(); SendRow(); }
    public static void OnLocalSelected(int index) { _active = index; RefreshRow(); SendRow(); }
    // Ряд читаем прямо из игры: при броске выбор меняется раньше, чем игра сохраняет новый ряд
    static bool RefreshRow()
    {
        try
        {
            var l = Prd()?.HeldPotions?._innerList;
            if (l == null) return false;
            var row = new int[l.Count];
            for (int i = 0; i < row.Length; i++) row[i] = l[i] != null ? l[i].Uid : 0;
            _row = row;
            try { _active = Prd().SelectedIndex.Value; } catch { }
            return true;
        }
        catch { return false; }
    }
    static void SendRow()
    {
        NetDriver.I?.DummyRow(_row, _active);
        var row = _row; int act = _active;
        Out(Op.HandRow, w => { w.Write((byte)row.Length); foreach (var u in row) w.Write(u); w.Write((sbyte)act); });
    }

    // Зелье, которое я бросил, улеглось — сообщаем точное место
    public static void OnLocalSettled(PotionFacade p)
    {
        if (p == null) return;
        if (Flying.ContainsKey(p.Uid)) return;          // своё брошенное отправит TickThrows, когда точно уляжется
        bool mine = MyDropped.Remove(p.Uid);
        if (!mine && Coop.Role != Role.Host) return;   // хост — главный по физике: шлёт, где улеглось любое зелье
        Out(Op.Pose, w => { w.Write(p.Uid); WritePose(w, p.transform.position, p.transform.rotation); });
    }

    // ---------- применение чужих ходов ----------

    public static void Apply(ulong from, Avatar who, byte[] b, int offset)
    {
        if (Potions.Count == 0) return; // ещё не в лаборатории
        using var ms = new MemoryStream(b, offset, b.Length - offset);
        using var r = new BinaryReader(ms);
        var op = (Op)r.ReadByte();
        Applying = true;
        try
        {
            switch (op)
            {
                case Op.Grab:
                {
                    int uid = r.ReadInt32(), slotUid = r.ReadInt32();
                    Touched(uid);
                    if (!Potions.TryGetValue(uid, out var p)) break;
                    if (slotUid != 0 && Slots.TryGetValue(slotUid, out var s)) s.ClearPotion(true);
                    p.Freeze();
                    if (!HomeParent.ContainsKey(uid)) HomeParent[uid] = p.transform.parent;
                    Glides.RemoveAll(g => g.P == p);
                    Live.Remove(uid);
                    if (RemoteHeld.TryGetValue(uid, out var prev) && prev != null && prev != who)
                    {   // отобрал у другого гостя
                        prev.Unhold(p.transform);
                        if (who != null) { CountSteal(who.Name); Coop.Say(Loc.L($"{who.Name} отобрал зелье у {prev.Name}!", $"{who.Name} snatched a potion from {prev.Name}!"), 3f); }
                    }
                    DetachFromSlots(p, null);                                  // у вора/жертвы не остаётся «занятой пустой» ячейки
                    DetachFromHolders(p);
                    BurnUndoFor(uid, null);
                    p.gameObject.SetActive(true);
                    RecentlyReleased.Remove(uid);
                    if (IsMine(p)) LoseToThief(p, who);                        // отобрали у меня (из руки, кармана или поднятую)
                    if (who != null) RemoteHeld[uid] = who;
                    if (who != null) { who.Hold(p.transform); who.PlayAction("grab"); }
                    else p.gameObject.SetActive(false);
                    break;
                }
                case Op.Contents:
                {
                    int uid = r.ReadInt32(); var u = r.ReadBytes(r.ReadByte());
                    if (Potions.TryGetValue(uid, out var p)) { p.SetLiquidContents(ToIl2(u)); p.RepaintLiquid(); BurnUndoFor(uid, null); HostRecordContents(p); }
                    who?.PlayAction("pour");
                    break;
                }
                case Op.VatContents:
                {
                    int uid = r.ReadInt32(); var u = r.ReadBytes(r.ReadByte());
                    if (Vats.TryGetValue(uid, out var v)) { v.SetLiquidContents(ToIl2(u)); v.RepaintLiquid(); Persistence?.RecordVat(v); BurnUndoFor(0, v); }
                    break;
                }
                case Op.Shelve:
                {
                    int uid = r.ReadInt32(), slotUid = r.ReadInt32(); var u = r.ReadBytes(r.ReadByte());
                    Touched(uid);
                    if (!Potions.TryGetValue(uid, out var p) || !Slots.TryGetValue(slotUid, out var s)) break;
                    if (IsMine(p)) ScrubFromMe(p);    // моё зелье переставил другой — моя игра должна о нём забыть
                    DetachFromSlots(p, s);
                    DetachFromHolders(p);
                    BurnUndoFor(uid, null);
                    Release(uid, p, who);
                    who?.PlayAction("shelve");
                    p.SetLiquidContents(ToIl2(u)); p.RepaintLiquid();
                    s.PlacePotion(p, false);
                    p.BeginFlyToSlot(s.PotionMount, false);
                    Persistence?.RecordShelved(p, s);
                    // у хоста это и есть начисление опыта за склянку гостя (единственное); у гостей — просто отметка «засчитано»
                    try { bool scored = Persistence != null && Persistence.TryScoreShelve(p); if (scored && Coop.Role == Role.Host) Plugin.L.LogInfo($"XP: склянку {uid} поставил {who?.Name} — засчитал у себя"); } catch { }
                    break;
                }
                case Op.Drop:
                {
                    int uid = r.ReadInt32(); ReadPose(r, out var pos, out var rot); var u = r.ReadBytes(r.ReadByte());
                    Touched(uid);
                    if (!Potions.TryGetValue(uid, out var p)) break;
                    if (IsMine(p)) ScrubFromMe(p);
                    DetachFromSlots(p, null);
                    DetachFromHolders(p);
                    BurnUndoFor(uid, null);
                    Release(uid, p, who);
                    who?.PlayAction("throw");
                    p.SetLiquidContents(ToIl2(u)); p.RepaintLiquid();
                    // дальше зелье летит за точками, которые шлёт бросивший, 10 раз в секунду
                    p.Freeze();
                    p.transform.SetPositionAndRotation(pos, rot);
                    Live[uid] = new LiveTarget { Pos = pos, Rot = rot, Thrower = who?.Name };
                    break;
                }
                case Op.Pour:
                {
                    int g = r.ReadInt32(), tk = r.ReadInt32();
                    Potions.TryGetValue(g, out var gp); Potions.TryGetValue(tk, out var tp);
                    who?.PlayPour(gp != null ? gp.transform : null, tp != null ? tp.transform : null);
                    break;
                }
                case Op.Cat:
                {
                    ReadPose(r, out _catPos, out _catRot);
                    if (!_catHas && _cat != null) { _cat.transform.SetPositionAndRotation(_catPos, _catRot); }
                    _catHas = true;
                    break;
                }
                case Op.Live:
                {
                    int n = r.ReadUInt16();
                    for (int i = 0; i < n; i++)
                    {
                        int uid = r.ReadInt32(); ReadPose(r, out var pos, out var rot);
                        if (RemoteHeld.ContainsKey(uid) || !Potions.TryGetValue(uid, out var p)) continue;
                        if (IsMine(p)) continue;
                        if (!Live.TryGetValue(uid, out var lt)) { p.Freeze(); Glides.RemoveAll(g => g.P == p); Live[uid] = lt = new LiveTarget { Pos = pos, Rot = rot, Thrower = who?.Name }; }
                        lt.Pos = pos; lt.Rot = rot; lt.Last = Time.unscaledTime;
                    }
                    break;
                }
                case Op.Unshelve:   // землетрясение сбило зелье с полки
                {
                    int uid = r.ReadInt32(), slotUid = r.ReadInt32();
                    if (Slots.TryGetValue(slotUid, out var s)) s.ClearPotion(true);
                    if (Potions.TryGetValue(uid, out var p)) p.Freeze();
                    break;
                }
                case Op.Hang:   // поставил мензурку на подставку
                {
                    int uid = r.ReadInt32(); var key = r.ReadString();
                    Touched(uid);
                    if (!Potions.TryGetValue(uid, out var p) || !Holders.TryGetValue(key, out var h)) { Plugin.L.LogWarning($"SYNC: подставка {key} не найдена"); break; }
                    if (IsMine(p)) ScrubFromMe(p);
                    DetachFromSlots(p, null);
                    BurnUndoFor(uid, null);
                    Release(uid, p, who);
                    who?.PlayAction("shelve");
                    try { h.Hang(p, false); } catch (Exception e) { Plugin.L.LogWarning("SYNC Hang: " + e.Message); }
                    if (Coop.Role == Role.Host) try { Persistence?.RecordHung(p, h); } catch { }
                    break;
                }
                case Op.Left:   // гость ушёл из аптеки — его склянки висят на крючке у точки, где он появился
                {
                    Vector3? eye = null;
                    if (r.ReadByte() == 1) eye = new Vector3(r.ReadSingle(), r.ReadSingle(), r.ReadSingle());
                    DropAllHeldBy(who, from, eye);
                    break;
                }
                case Op.Toast:
                {
                    var parts = r.ReadString().Split('\u001f');
                    Coop.Say(parts.Length > 1 ? Loc.L(parts[0], parts[1]) : parts[0], 4f);
                }
                    break;
                case Op.HandRow:
                {
                    int n = r.ReadByte(); var row = new List<Transform>();
                    for (int i = 0; i < n; i++)
                    {
                        int uid = r.ReadInt32();
                        if (who == null || !Potions.TryGetValue(uid, out var p) || p == null) continue;
                        // только что брошенное/летящее/чужое/моё в ряд не берём — иначе аптекарь тянет его к себе на пояс
                        if (Live.ContainsKey(uid)) continue;
                        if (RecentlyReleased.ContainsKey(uid)) continue;   // брошенное/поставленное вернётся в ряд только через «взял»
                        if (RemoteHeld.TryGetValue(uid, out var h) && h != null && h != who) continue;
                        if (IsMine(p)) continue;   // если это кража — придёт Grab и всё сделает
                        if (!HomeParent.ContainsKey(uid)) HomeParent[uid] = p.transform.parent;
                        Glides.RemoveAll(g => g.P == p);
                        p.Freeze();
                        p.gameObject.SetActive(true);
                        RemoteHeld[uid] = who;
                        row.Add(p.transform);
                    }
                    int act = r.ReadSByte();
                    if (who == null) break;
                    foreach (var t in who.SetRow(row, act))
                    {   // выпало из ряда (бросил/поставил) — отвязываем от аптекаря
                        foreach (var kv in Potions)
                        {
                            if (kv.Value == null || kv.Value.transform.Pointer != t.Pointer) continue;
                            if (RemoteHeld.TryGetValue(kv.Key, out var h2) && h2 == who) RemoteHeld.Remove(kv.Key);
                            ToWorld(kv.Value);
                            break;
                        }
                    }
                    break;
                }
                case Op.Pose:
                {
                    int uid = r.ReadInt32(); ReadPose(r, out var pos, out var rot);
                    if (!Potions.TryGetValue(uid, out var p)) break;
                    if (RemoteHeld.ContainsKey(uid)) break;   // его уже кто-то взял
                    Glides.RemoveAll(g => g.P == p);
                    if (Live.TryGetValue(uid, out var flying))
                    {   // долетело: ставим точно на место
                        Live.Remove(uid);
                        p.transform.SetPositionAndRotation(pos, rot);
                        Applying = false;
                        try { p.Unfreeze(); Persistence?.RecordFloor(p); } catch { }
                        if (flying.Thrower != null) HostMaybeBreak(p, flying.Thrower);
                        break;
                    }
                    var cur = p.transform;
                    if (Vector3.Distance(cur.position, pos) < 0.05f) { cur.SetPositionAndRotation(pos, rot); Persistence?.RecordFloor(p); break; }
                    p.Freeze();
                    Glides.Add(new Glide { P = p, From = cur.position, To = pos, FromR = cur.rotation, ToR = rot, T0 = Time.unscaledTime, Thrower = who?.Name });
                    break;
                }
            }
        }
        catch (Exception e) { Plugin.L.LogError($"SYNC: не смог применить {op}: {e.Message}"); }
        finally { Applying = false; }
    }

    // ---------- куда класть отпущенную склянку ----------
    // Раньше возвращали туда, откуда взяли. Но если взяли из руки героя (кража, дырявый карман),
    // «дом» оказывался рукой — и склянка ездила за героем или пропадала у него в руке.
    static Transform LooseRoot; static bool _looseKnown;
    static void FindLooseRoot()
    {
        var count = new Dictionary<IntPtr, int>(); var tr = new Dictionary<IntPtr, Transform>();
        foreach (var p in Potions.Values)
        {
            if (p == null || !IsLoose(p)) continue;
            var par = p.transform.parent; var key = par == null ? IntPtr.Zero : par.Pointer;
            count[key] = count.TryGetValue(key, out var n) ? n + 1 : 1; tr[key] = par;
        }
        if (count.Count == 0) { LooseRoot = null; _looseKnown = false; return; }
        var best = count.OrderByDescending(kv => kv.Value).First().Key;
        LooseRoot = tr[best]; _looseKnown = true;
        Plugin.L.LogInfo($"SYNC: склянки на полу живут в {(LooseRoot != null ? LooseRoot.name : "корне сцены")} ({count[best]} шт.)");
    }
    static bool UnderHero(Transform t)
    {
        if (t == null) return false;
        try
        {
            if (_picker != null && t.IsChildOf(_picker.transform)) return true;
            var cam = Camera.main; if (cam != null && t.IsChildOf(cam.transform)) return true;
        }
        catch { }
        return false;
    }
    static void ToWorld(PotionFacade p)
    {
        if (p == null) return;
        Transform target = null;
        if (HomeParent.TryGetValue(p.Uid, out var hp)) { HomeParent.Remove(p.Uid); if (hp != null && !UnderHero(hp)) target = hp; }
        if (target == null && _looseKnown) target = LooseRoot;
        if (target != null && UnderHero(target)) target = null;
        p.transform.SetParent(target, true);
    }

    // где я появился в этой аптеке (первая позиция камеры в игре)
    static Vector3 _mySpawn; static bool _hasMySpawn;
    public static void NoteMyPos(Vector3 eye) { if (!_hasMySpawn && Potions.Count > 0 && Coop.State == eGameState.Playing) { _mySpawn = eye; _hasMySpawn = true; } }
    public static void AnnounceLeft()
    {
        var sp = _mySpawn; bool has = _hasMySpawn;
        Out(Op.Left, w => { w.Write((byte)(has ? 1 : 0)); if (has) { w.Write(sp.x); w.Write(sp.y); w.Write(sp.z); } });
    }

    // ---------- подставки (мензурки на столе / сепараторе) ----------
    static readonly Dictionary<string, CodeBase.Components.EssenceSeparator.PourOutHolderFacade> Holders = new();
    static string HolderKey(CodeBase.Components.EssenceSeparator.PourOutHolderFacade h)
    {
        Vector3 v; try { v = (h.Mount != null ? h.Mount : h.transform).position; } catch { v = h.transform.position; }
        return $"{Mathf.RoundToInt(v.x * 20)},{Mathf.RoundToInt(v.y * 20)},{Mathf.RoundToInt(v.z * 20)}";
    }
    static void DetachFromHolders(PotionFacade p)
    {
        foreach (var h in Holders.Values)
            try { if (h != null && Same(h.Vessel, p)) { h.Release(); Plugin.L.LogInfo($"SYNC: снял склянку {p.Uid} с подставки"); } } catch { }
    }

    // ---------- «шаг назад» ----------
    // Своё «шаг назад» рассылаем: склянка вернулась в руку / содержимое откатилось
    public static void OnLocalUndo(UndoRecord r)
    {
        switch (r.Kind)
        {
            case eUndoMoveKind.Drop:
            case eUndoMoveKind.Shelve:
            case eUndoMoveKind.HangOnHolder:
            {
                var p = r.Potion; if (p == null) break;
                int uid = p.Uid, slotUid = r.Slot != null ? r.Slot.Uid : 0;
                Flying.Remove(uid); MyDropped.Remove(uid); _respawned.Remove(uid); _forced.Remove(uid);
                Out(Op.Grab, w => { w.Write(uid); w.Write(slotUid); });
                OutContents(p);
                break;
            }
            case eUndoMoveKind.PotionToPotion:
            case eUndoMoveKind.PotionToShelvedPotion:
                OutContents(r.Giver); OutContents(r.Taker); break;
            case eUndoMoveKind.PotionToVat:
                OutContents(r.Giver); OutVat(r.Vat); break;
            case eUndoMoveKind.VatToPotion:
                OutVat(r.Vat); OutContents(r.Taker); break;
        }
        ResendRow();
        Plugin.L.LogInfo($"SYNC: шаг назад ({r.Kind}) разослан");
    }

    // Склянку (или чан) тронул другой игрок — мой «шаг назад» для неё сгорает (вместе со всем, что новее)
    static void BurnUndoFor(int uid, VatFacade vat)
    {
        try
        {
            if (_picker == null) _picker = UnityEngine.Object.FindFirstObjectByType<HeroPotionPicker>();
            var svc = _picker?._undoService?.TryCast<UndoService>();
            var st = svc?._stack;
            if (st == null || st.Count == 0) return;
            int cut = -1;
            for (int i = 0; i < st.Count && cut < 0; i++)
            {
                var r = st[i];
                bool hit = (uid != 0 && ((r.Potion != null && r.Potion.Uid == uid) || (r.Giver != null && r.Giver.Uid == uid) || (r.Taker != null && r.Taker.Uid == uid)))
                           || (vat != null && r.Vat != null && r.Vat.Pointer == vat.Pointer);
                if (hit) cut = i;
            }
            if (cut < 0) return;
            int removed = st.Count - cut;
            while (st.Count > cut) st.RemoveAt(st.Count - 1);
            try { svc.PublishDepth(); } catch { }
            Plugin.L.LogInfo($"SYNC: шаг назад сгорел ({removed}) — склянку тронул другой");
        }
        catch (Exception e) { Plugin.L.LogWarning("SYNC undo: " + e.Message); }
    }

    // ---------- у хоста: чужая переливка сразу в сейв ----------
    static void HostRecordContents(PotionFacade p)
    {
        if (Coop.Role != Role.Host || p == null || Persistence == null || RemoteHeld.ContainsKey(p.Uid)) return;
        try
        {
            foreach (var s in Slots.Values) if (s != null && Same(s.Potion, p)) { Persistence.RecordShelved(p, s); return; }
            if (IsLoose(p)) Persistence.RecordFloor(p);
        }
        catch { }
    }
    public static void FlushSave()
    {
        try { Persistence?.Flush(); } catch (Exception e) { Plugin.L.LogWarning("SAVE flush: " + e.Message); }
        // общий Save у игры только generic (Save<T>) — его из мода звать нельзя; склянки пишет Flush выше
        Plugin.L.LogInfo("COOP: сейв записан перед отправкой");
    }

    // ---------- гость: в сейве хоста «рука» одна — у гостя при входе она должна быть пустой ----------
    static float _initialScrubAt;
    static void TickInitialScrub()
    {
        if (_initialScrubAt <= 0) return;
        if (Time.unscaledTime > _initialScrubAt) { _initialScrubAt = 0; return; }
        var d = Prd(); if (d == null) return;
        var mine = new List<PotionFacade>();
        try
        {
            var a = d.HeldPotions?._innerList; if (a != null) for (int i = 0; i < a.Count; i++) if (a[i] != null) mine.Add(a[i]);
            var b = d.ApronPotions?._innerList; if (b != null) for (int i = 0; i < b.Count; i++) if (b[i] != null) mine.Add(b[i]);
        }
        catch { }
        if (mine.Count == 0) return;   // рука ещё не разложена — попробуем в следующем кадре
        _initialScrubAt = 0;
        foreach (var p in mine)
        {
            ScrubFromMe(p);
            p.Freeze();
            if (!RemoteHeld.ContainsKey(p.Uid)) p.gameObject.SetActive(false);   // это склянки хоста: покажутся, когда придёт его ряд
        }
        ResendRow();
        Plugin.L.LogInfo($"SYNC: убрал из рук {mine.Count} склянок хоста, доставшихся с его сейвом");
    }

    // ---------- «фартук на крючке» ----------
    // Аптекарь ушёл — всё, что было у него в руках, висит там, где он появился в аптеке.
    // Остальные могут забрать (тронутая склянка с крючка снимается). Вернулся — нетронутые сами прыгают ему в руки,
    // что не влезло — падает рядом, как при дырявом кармане. Учёт ведёт хост (файл hooks.txt в папке аптеки).
    static readonly Dictionary<ulong, List<int>> Hooks = new();
    static string HooksPath => Path.Combine(Coop.RoomDir, "hooks.txt");
    static void LoadHooks()
    {
        Hooks.Clear();
        try
        {
            if (!File.Exists(HooksPath)) return;
            foreach (var line in File.ReadAllLines(HooksPath))
            {
                var parts = line.Split(':'); if (parts.Length != 2 || !ulong.TryParse(parts[0], out var id)) continue;
                var l = new List<int>();
                foreach (var x in parts[1].Split(',', StringSplitOptions.RemoveEmptyEntries)) if (int.TryParse(x, out var u)) l.Add(u);
                if (l.Count > 0) Hooks[id] = l;
            }
            Plugin.L.LogInfo($"HOOK: на крючках склянок: {Hooks.Values.Sum(l => l.Count)}");
        }
        catch (Exception e) { Plugin.L.LogWarning("HOOK load: " + e.Message); }
    }
    static void SaveHooks()
    {
        if (Coop.Role != Role.Host) return;
        try { Directory.CreateDirectory(Coop.RoomDir); File.WriteAllLines(HooksPath, Hooks.Where(kv => kv.Value.Count > 0).Select(kv => kv.Key + ":" + string.Join(",", kv.Value))); }
        catch (Exception e) { Plugin.L.LogWarning("HOOK save: " + e.Message); }
    }
    public static List<int> HookOf(ulong id) => Hooks.TryGetValue(id, out var l) ? new List<int>(l) : new List<int>();
    // склянку кто-то тронул — она больше не «чья-то на крючке»
    static void Touched(int uid)
    {
        if (Coop.Role != Role.Host || Hooks.Count == 0) return;
        bool changed = false;
        foreach (var l in Hooks.Values) changed |= l.Remove(uid);
        if (changed) SaveHooks();
    }

    public static void DropAllHeldBy(Avatar a) => DropAllHeldBy(a, 0, null);
    public static void DropAllHeldBy(Avatar a, ulong owner, Vector3? hookEye)
    {
        if (a == null) return;
        Vector3 eye;
        try { eye = hookEye ?? (a.HasSpawn ? a.SpawnEye : a.Root.transform.position + Vector3.up * 1.4f); } catch { eye = Vector3.zero; }
        var at = eye - Vector3.up * 0.45f;   // примерно на уровне груди
        var mine = new List<int>();
        foreach (var kv in Potions)
        {
            var p = kv.Value; if (p == null) continue;
            bool his = (RemoteHeld.TryGetValue(kv.Key, out var h) && h == a) || a.Holds(p.transform);
            if (his) mine.Add(kv.Key);
        }
        for (int i = 0; i < mine.Count; i++)
        {
            var p = Potions[mine[i]];
            a.Unhold(p.transform);
            RemoteHeld.Remove(mine[i]);
            Live.Remove(mine[i]); Glides.RemoveAll(g => g.P == p);
            ToWorld(p);
            p.gameObject.SetActive(true);
            float ang = i * Mathf.PI * 2f / Mathf.Max(1, mine.Count);
            float rad = mine.Count == 1 ? 0f : 0.22f + 0.02f * mine.Count;
            p.transform.SetPositionAndRotation(at + new Vector3(Mathf.Cos(ang) * rad, (i % 2) * 0.08f, Mathf.Sin(ang) * rad), Quaternion.identity);
            p.Freeze();   // висит в воздухе, пока кто-нибудь не возьмёт
            Applying = true;
            try { if (Coop.Role == Role.Host) Persistence?.RecordFloor(p); } catch { } finally { Applying = false; }
        }
        if (Coop.Role == Role.Host && owner != 0 && mine.Count > 0)
        {
            if (!Hooks.TryGetValue(owner, out var l)) Hooks[owner] = l = new List<int>();
            foreach (var u in mine) if (!l.Contains(u)) l.Add(u);
            SaveHooks();
        }
        if (mine.Count > 0) Plugin.L.LogInfo($"HOOK: {a.Name} ушёл — {mine.Count} склянок висят на крючке у точки появления");
    }

    // гость: хост прислал, что висит на моём крючке — забираю, когда игра готова
    static List<int> _hookReturn;
    public static void ReturnHook(List<int> uids) { if (uids != null && uids.Count > 0) { _hookReturn = uids; Plugin.L.LogInfo($"HOOK: на моём крючке ждут {uids.Count} склянок"); } }
    static void TickHookReturn()
    {
        if (_hookReturn == null || Coop.Role != Role.Guest || Coop.State != eGameState.Playing || Potions.Count == 0 || _initialScrubAt > 0) return;
        if (_picker == null) _picker = UnityEngine.Object.FindFirstObjectByType<HeroPotionPicker>();
        if (_picker == null) return;
        var list = _hookReturn; _hookReturn = null;
        int hand = 0, fell = 0;
        foreach (var uid in list)
        {
            if (!Potions.TryGetValue(uid, out var p) || p == null || RemoteHeld.ContainsKey(uid) || IsMine(p)) continue;
            bool shelved = false;
            foreach (var sl in Slots.Values) if (sl != null && Same(sl.Potion, p)) { shelved = true; break; }
            if (shelved) continue;
            bool ok = false;
            try
            {
                p.gameObject.SetActive(true);
                if (_picker.HasHandRoom) ok = _picker.Grab(p, null);
                else if (_picker.HasApronRoom) ok = _picker.GrabIntoApron(p, null);
            }
            catch (Exception e) { Plugin.L.LogWarning("HOOK grab: " + e.Message); }
            if (ok) { hand++; continue; }
            try
            {   // не влезло — падает рядом, как при дырявом кармане
                ToWorld(p);
                p.Unfreeze();
                var cam = Camera.main;
                if (cam != null) p.transform.position = cam.transform.position + cam.transform.forward * 0.6f;
                p.Toss(Vector3.up * 1.2f + UnityEngine.Random.insideUnitSphere * 0.5f, UnityEngine.Random.insideUnitSphere * 4f);
                LocalDropped(p);
                fell++;
            }
            catch (Exception e) { Plugin.L.LogWarning("HOOK drop: " + e.Message); }
        }
        if (hand + fell > 0)
        {
            Coop.Say(fell > 0 ? Loc.L($"Фартук ждал тебя на крючке: в руки {hand}, не влезло и упало рядом {fell}", $"Your apron waited on the hook: {hand} back in your hands, {fell} didn't fit and fell nearby")
                              : Loc.L($"Фартук ждал тебя на крючке: вернул {hand} склянок", $"Your apron waited on the hook: {hand} potion(s) back in your hands"), 5f);
            Plugin.L.LogInfo($"HOOK: вернул себе {hand}, упало рядом {fell}");
        }
    }

    // ---------- счёт краж: у хоста, в папке аптеки ----------
    static bool _stealsDirty; static float _nextSteals;
    static string StealsPath => Path.Combine(Coop.RoomDir, "steals.txt");
    static void LoadSteals()
    {
        try
        {
            Steals.Clear();
            if (File.Exists(StealsPath))
                foreach (var line in File.ReadAllLines(StealsPath))
                {
                    var i = line.LastIndexOf('\t'); if (i <= 0) continue;
                    if (int.TryParse(line.Substring(i + 1), out var n)) Steals[line.Substring(0, i)] = n;
                }
        }
        catch (Exception e) { Plugin.L.LogWarning("STEALS: " + e.Message); }
        _stealsDirty = true;
    }
    public static string PackSteals() => string.Join("\n", Steals.Select(kv => kv.Key + "\t" + kv.Value));
    public static void SetSteals(string packed)
    {
        Steals.Clear();
        foreach (var line in packed.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var i = line.LastIndexOf('\t'); if (i <= 0) continue;
            if (int.TryParse(line.Substring(i + 1), out var n)) Steals[line.Substring(0, i)] = n;
        }
    }
    static void TickSteals()
    {
        if (Coop.Role != Role.Host) return;
        if (!_stealsDirty && Time.unscaledTime < _nextSteals) return;
        _nextSteals = Time.unscaledTime + 10f;
        if (_stealsDirty)
        {
            _stealsDirty = false;
            try { Directory.CreateDirectory(Coop.RoomDir); File.WriteAllText(StealsPath, PackSteals()); } catch { }
        }
        NetDriver.I?.BroadcastSteals(PackSteals());
    }

    // ---------- кто чем владеет ----------
    static readonly Dictionary<int, float> RecentlyReleased = new();   // чужие только что брошенные/поставленные
    static PlayerRuntimeData _prd;
    static PlayerRuntimeData Prd()
    {
        if (_prd != null) return _prd;
        try
        {
            if (_picker == null) _picker = UnityEngine.Object.FindFirstObjectByType<HeroPotionPicker>();
            var svc = _picker?._runtimeDataService?.TryCast<RuntimeDataService>();
            if (svc?._data == null) return null;
            foreach (var kv in svc._data)
            {
                var x = kv.Value?.TryCast<PlayerRuntimeData>();
                if (x != null) { _prd = x; break; }
            }
        }
        catch (Exception e) { Plugin.L.LogWarning("SYNC: не нашёл данные игрока: " + e.Message); }
        return _prd;
    }
    static bool Same(PotionFacade a, PotionFacade b) => a != null && b != null && a.Pointer == b.Pointer;
    static bool ListHas(CodeBase.Common.Utilities.ReactiveList<PotionFacade> l, PotionFacade p)
    {
        var inner = l?._innerList;
        if (inner == null) return false;
        for (int i = 0; i < inner.Count; i++) if (Same(inner[i], p)) return true;
        return false;
    }
    static bool ListRemove(CodeBase.Common.Utilities.ReactiveList<PotionFacade> l, PotionFacade p)
    {
        var inner = l?._innerList;
        if (inner == null) return false;
        for (int i = 0; i < inner.Count; i++) if (Same(inner[i], p)) { l.RemoveAt(i); return true; }
        return false;
    }

    // Зелье у меня: в руке, в кармане-фартуке или поднято для переливания
    static bool IsMine(PotionFacade p)
    {
        if (p == null) return false;
        if (Array.IndexOf(_row, p.Uid) >= 0) return true;
        try
        {
            var d = Prd();
            if (d == null) return false;
            if (ListHas(d.HeldPotions, p) || ListHas(d.ApronPotions, p)) return true;
            if (Same(d.RaisedPotion?.Value, p)) return true;
        }
        catch { }
        return false;
    }

    // Полностью стираем зелье из моих рук: из ряда, кармана, поднятого и прицела — чтобы не осталось «фантома»
    static void ScrubFromMe(PotionFacade p)
    {
        if (p == null) return;
        if (_picker == null) _picker = UnityEngine.Object.FindFirstObjectByType<HeroPotionPicker>();
        var d = Prd();
        bool hand = false, apron = false;
        try { hand = d != null ? ListHas(d.HeldPotions, p) : Array.IndexOf(_row, p.Uid) >= 0; apron = d != null && ListHas(d.ApronPotions, p); } catch { }
        try
        {
            if (d != null && Same(d.RaisedPotion?.Value, p))
            {
                var raiser = UnityEngine.Object.FindFirstObjectByType<HeroHandRaiser>();
                try { raiser?.Lower(true); } catch { }
                if (Same(d.RaisedPotion.Value, p)) d.RaisedPotion.Value = null;
            }
        }
        catch (Exception e) { Plugin.L.LogWarning("SYNC scrub raised: " + e.Message); }
        try { if (hand) _picker?.ReleaseFromCarry(p, false); } catch (Exception e) { Plugin.L.LogWarning("SYNC scrub hand: " + e.Message); }
        try { if (apron) _picker?.ReleaseFromCarry(p, true); } catch (Exception e) { Plugin.L.LogWarning("SYNC scrub apron: " + e.Message); }
        try
        {   // если игра не убрала сама — убираем руками
            if (d != null)
            {
                if (ListRemove(d.HeldPotions, p)) Plugin.L.LogWarning($"SYNC: зелье {p.Uid} пришлось вынуть из руки вручную");
                if (ListRemove(d.ApronPotions, p)) Plugin.L.LogWarning($"SYNC: зелье {p.Uid} пришлось вынуть из кармана вручную");
                if (Same(d.FocusedPotion?.Value, p)) d.FocusedPotion.Value = null;
                if (Same(d.AimedPotion?.Value, p)) d.AimedPotion.Value = null;
                int cnt = d.HeldPotions?._innerList?.Count ?? 0;
                if (d.SelectedIndex != null && d.SelectedIndex.Value >= cnt) d.SelectedIndex.Value = cnt - 1;
            }
        }
        catch (Exception e) { Plugin.L.LogWarning("SYNC scrub lists: " + e.Message); }
        try { _picker?.SetTakenDown(p, false); } catch { }
        try { _picker?.ReseatRow(true); _picker?.ReseatApronRow(true); _picker?.RecordCarryRow(false); _picker?.RecordCarryRow(true); } catch { }
        try { _picker?._undoService?.Clear(); } catch { }   // «шаг назад» не должен вернуть отобранное
        try { DG.Tweening.DOTween.Kill(p.transform, false); DG.Tweening.DOTween.Kill(p.gameObject, false); p.Carrier()?.MarkIdle(); } catch { }
        _row = _row.Where(u => u != p.Uid).ToArray();
        if (UnderHero(p.transform.parent)) ToWorld(p);   // анимацию игры мы погасили — сама она склянку от руки уже не отцепит
        RefreshRow();
    }

    // Ячейка, где зелья уже нет, не должна оставаться «занятой»
    static void DetachFromSlots(PotionFacade p, SlotFacade except)
    {
        if (p == null) return;
        foreach (var s in Slots.Values)
        {
            if (s == null || (except != null && s.Pointer == except.Pointer)) continue;
            try { if (Same(s.Potion, p)) { s.ClearPotion(true); Plugin.L.LogInfo($"SYNC: освободил ячейку {s.Uid} от зелья {p.Uid}"); } } catch { }
        }
    }

    // Страховка раз в полсекунды: если у меня в руках то, что держит другой, — отдаём
    static float _nextOwn;
    static void TickOwnership()
    {
        if (RemoteHeld.Count == 0 || Time.unscaledTime < _nextOwn) return;
        _nextOwn = Time.unscaledTime + 0.5f;
        foreach (var kv in RemoteHeld.ToList())
        {
            if (!Potions.TryGetValue(kv.Key, out var p) || p == null) continue;
            if (IsMine(p)) { Plugin.L.LogWarning($"SYNC: зелье {p.Uid} одновременно у меня и у {kv.Value?.Name} — отдаю"); ScrubFromMe(p); }
        }
    }

    // У меня из рук утащили зелье: игра должна про него забыть
    static void LoseToThief(PotionFacade p, Avatar thief)
    {
        try
        {
            ScrubFromMe(p);
        }
        catch (Exception e) { Plugin.L.LogError("SYNC: не смог отдать зелье: " + e.Message); }
        // игра запускает анимацию «уронить» и каждый кадр тянет колбу к точке — гасим её
        try { DG.Tweening.DOTween.Kill(p.transform, false); DG.Tweening.DOTween.Kill(p.gameObject, false); p.Carrier()?.MarkIdle(); } catch { }
        p.Freeze();
        if (thief != null) { CountSteal(thief.Name); Coop.Say(Loc.L($"{thief.Name} отобрал у тебя зелье!", $"{thief.Name} snatched a potion from you!"), 3f); NetDriver.I?.LocalEmoteFlash(3); }
        Plugin.L.LogInfo($"SYNC: зелье {p.Uid} отобрал {thief?.Name}");
    }

    // Я хватаю зелье, которое держит чужой аптекарь, — сначала забираем его у аптекаря
    public static void BeforeLocalGrab(PotionFacade p)
    {
        if (p != null) RecentlyReleased.Remove(p.Uid);
        _initialScrubAt = 0;   // гость уже сам что-то взял — его руку больше не чистим
        if (p == null || !RemoteHeld.TryGetValue(p.Uid, out var holder)) return;
        RemoteHeld.Remove(p.Uid);
        holder?.Unhold(p.transform);
        ToWorld(p);
        p.gameObject.SetActive(true);
        p.Unfreeze();
        CountSteal(Coop.Role == Role.Solo ? "я" : "Ты");
        Coop.Say(Loc.L($"Ты отобрал зелье у {holder?.Name}!", $"You snatched a potion from {holder?.Name}!"), 3f);
    }

    static void Release(int uid, PotionFacade p, Avatar who)
    {
        if (RemoteHeld.TryGetValue(uid, out var h) && h != null && h != who) h.Unhold(p.transform);
        RemoteHeld.Remove(uid);
        RecentlyReleased[uid] = Time.unscaledTime;
        who?.Unhold(p.transform);
        p.gameObject.SetActive(true);
        ToWorld(p);
    }
}

static class SyncPatches
{
    // Отбирать можно (так веселее), но с учётом: зелье сначала забираем у чужого аптекаря
    [HarmonyPrefix]
    [HarmonyPatch(typeof(HeroPotionPicker), nameof(HeroPotionPicker.Grab))]
    static void Grab_Pre(PotionFacade potion) { try { Sync.BeforeLocalGrab(potion); } catch (Exception e) { Plugin.L.LogError("SYNC steal: " + e.Message); } }

    [HarmonyPrefix]
    [HarmonyPatch(typeof(HeroPotionPicker), nameof(HeroPotionPicker.GrabIntoApron))]
    static void GrabApron_Pre(PotionFacade potion) { try { Sync.BeforeLocalGrab(potion); } catch (Exception e) { Plugin.L.LogError("SYNC steal: " + e.Message); } }

    // Кнопка «Продолжить» получает сервис сохранений при создании меню — забираем ссылку себе
    [HarmonyPostfix]
    [HarmonyPatch(typeof(CodeBase.Components.UI.AdvancedButtonContinueStarter), nameof(CodeBase.Components.UI.AdvancedButtonContinueStarter.Construct))]
    static void Construct_Post(IPersistentDataService persistentDataService) => Sync.PersistentSvc = persistentDataService;

    [HarmonyPostfix]
    [HarmonyPatch(typeof(UndoService), nameof(UndoService.Record))]
    static void Record_Post(UndoRecord record)
    {
        try { Sync.OnLocalMove(record); } catch (Exception e) { Plugin.L.LogError("SYNC Record: " + e.Message); }
    }

    [HarmonyPostfix]
    [HarmonyPatch(typeof(HeroPotionPicker), nameof(HeroPotionPicker.Grab))]
    static void Grab_Post(PotionFacade potion, SlotFacade fromSlot, bool __result)
    {
        if (__result) try { Sync.OnLocalGrab(potion, fromSlot); } catch (Exception e) { Plugin.L.LogError("SYNC Grab: " + e.Message); }
    }

    [HarmonyPostfix]
    [HarmonyPatch(typeof(HeroPotionPicker), nameof(HeroPotionPicker.GrabIntoApron))]
    static void GrabApron_Post(PotionFacade potion, SlotFacade fromSlot, bool __result)
    {
        if (__result) try { Sync.OnLocalGrab(potion, fromSlot); } catch (Exception e) { Plugin.L.LogError("SYNC Apron: " + e.Message); }
    }

    [HarmonyPostfix]
    [HarmonyPatch(typeof(PotionsPersistence), nameof(PotionsPersistence.RecordHandRow))]
    static void HandRow_Post(Il2CppSystem.Collections.Generic.IReadOnlyList<PotionFacade> held)
    {
        try { Sync.OnLocalHandRow(held); } catch (Exception e) { Plugin.L.LogError("SYNC HandRow: " + e.Message); }
    }

    [HarmonyPostfix]
    [HarmonyPatch(typeof(PotionsPersistence), nameof(PotionsPersistence.SelectedIndex_OnChanged))]
    static void Selected_Post(int index)
    {
        try { Sync.OnLocalSelected(index); } catch (Exception e) { Plugin.L.LogError("SYNC Select: " + e.Message); }
    }

    [HarmonyPostfix]
    [HarmonyPatch(typeof(PotionsPersistence), nameof(PotionsPersistence.RecordFloor))]
    static void RecordFloor_Post(PotionFacade potion)
    {
        if (!Sync.Applying) try { Sync.OnLocalSettled(potion); } catch { }
    }
}

// Своё «шаг назад» — рассылаем результат (отдельно: если игра поменяется, остальная синхронизация не пострадает)
static class UndoPatches
{
    [HarmonyPostfix]
    [HarmonyPatch(typeof(HeroMoveUndoer), nameof(HeroMoveUndoer.Reverse))]
    static void Reverse_Post(UndoRecord record, bool __result)
    {
        if (__result) try { Sync.OnLocalUndo(record); } catch (Exception e) { Plugin.L.LogError("SYNC Undo: " + e.Message); }
    }
}
