using System;
using System.Collections.Generic;
using Il2CppInterop.Runtime.InteropTypes.Arrays;
using UnityEngine;

namespace PotionCoop;

// Аптекарь другого игрока: собран из простых форм прямо в коде (как в 3D-превью).
public class Avatar
{
    // палитра аптекарей: 12 цветов, у каждого игрока в аптеке свой (выбирается в меню «Общие аптеки»)
    public static readonly Color[] Robe = {
        Hex(0x2fa59a), Hex(0x8b55b8), Hex(0xe0a22c), Hex(0xd0574f), Hex(0x4f7fd8), Hex(0x77a344),
        Hex(0xe07aa8), Hex(0x8fd8c0), Hex(0xe9803a), Hex(0x55555e), Hex(0xe8e2d4), Hex(0x7a5134) };
    public static readonly Color[] RobeDark = {
        Hex(0x1d6b64), Hex(0x5a3279), Hex(0x9c6a12), Hex(0x8c302a), Hex(0x2c4f93), Hex(0x4a6a26),
        Hex(0x9c4570), Hex(0x4f9580), Hex(0xa0501c), Hex(0x2e2e34), Hex(0xa8a294), Hex(0x4a2f1c) };
    public static readonly string[] ColorNames = {
        "Бирюзовый", "Сливовый", "Горчичный", "Кирпичный", "Васильковый", "Травяной",
        "Розовый", "Мятный", "Оранжевый", "Угольный", "Белый", "Шоколадный" };
    public static readonly string[] ColorNamesEn = {
        "Teal", "Plum", "Mustard", "Brick", "Cornflower", "Grass",
        "Pink", "Mint", "Orange", "Charcoal", "White", "Chocolate" };
    public static string ColorName(int i) => i < 0 ? (Loc.Ru ? "сам подберу" : "auto") : (Loc.Ru ? ColorNames : ColorNamesEn)[i % 12];

    public Vector3 SpawnEye;   // где аптекарь впервые появился — там висит его «фартук на крючке»
    public bool HasSpawn;

    public const float EyeHeight = 1.43f;   // высота глаз модели при масштабе 1

    public string Name;
    public GameObject Root;
    Transform _body, _head;
    public Transform Hand, HandL;
    Transform _pourGiver, _pourTaker;
    Transform _shL, _shR, _hat;
    readonly List<Transform> _pupils = new();
    public int EmoteId = -1; public float EmoteStart;
    string _act; float _actStart;
    readonly List<Transform> _held = new();
    Material _robe, _robeDark;
    int _colorIndex = -1;

    Vector3 _targetPos, _pos;
    float _targetYaw, _yaw, _targetPitch, _pitch;
    float _speed, _walkPhase;
    bool _hasTarget;
    public float LastSeen;

    public Avatar(string name)
    {
        Name = name;
        Build();
        Root.SetActive(false);
    }

    public void SetState(Vector3 eyePos, float yaw, float pitch)
    {
        _targetPos = eyePos; _targetYaw = yaw; _targetPitch = pitch;
        LastSeen = Time.unscaledTime;
        if (!HasSpawn) { SpawnEye = eyePos; HasSpawn = true; }
        if (!_hasTarget) { _pos = eyePos; _yaw = yaw; _pitch = pitch; _hasTarget = true; Root.SetActive(true); }
    }

    public void SetColor(int i)
    {
        if (i == _colorIndex) return;
        _colorIndex = i;
        Paint(_robe, Robe[i % Robe.Length]);
        Paint(_robeDark, RobeDark[i % RobeDark.Length]);
        Paint(TwoSided(_robe), Robe[i % Robe.Length]);          // двусторонние копии (поля шляпы) тоже
        Paint(TwoSided(_robeDark), RobeDark[i % RobeDark.Length]);
    }

    public Color CurrentColor => Robe[Math.Max(0, _colorIndex) % Robe.Length];

    public void Tick(float dt)
    {
        if (!_hasTarget) return;
        float k = 1f - Mathf.Exp(-12f * dt);
        var prev = _pos;
        _pos = Vector3.Lerp(_pos, _targetPos, k);
        _yaw = Mathf.LerpAngle(_yaw, _targetYaw, k);
        _pitch = Mathf.LerpAngle(_pitch, _targetPitch, k);

        var flat = _pos - prev; flat.y = 0;
        float v = dt > 0 ? flat.magnitude / dt : 0;
        _speed = Mathf.Lerp(_speed, v, 1f - Mathf.Exp(-8f * dt));
        _walkPhase += dt * (3f + _speed * 2.5f);

        float s = Plugin.AvatarScale.Value;
        Root.transform.localScale = Vector3.one * s;
        Root.transform.position = _pos - new Vector3(0, EyeHeight * s, 0);
        Root.transform.rotation = Quaternion.Euler(0, _yaw, 0);

        // покачивание: сильнее, когда идёт
        float walk = Mathf.Clamp01(_speed / 2f);
        float bob = Mathf.Abs(Mathf.Sin(_walkPhase)) * (0.015f + 0.05f * walk);
        var pz = Pose(Time.unscaledTime);
        if ((_pourGiver != null || _pourTaker != null) && _act != "pour") { _pourGiver = _pourTaker = null; Restack(); } // колбы обратно на пояс
        _body.localPosition = new Vector3(0, bob + pz.By, 0);
        _body.localRotation = Quaternion.Euler(pz.Bx, pz.Yaw, Mathf.Sin(_walkPhase) * (1.5f + 4f * walk) + pz.Bz);
        _head.localRotation = Quaternion.Euler(Mathf.Clamp(_pitch, -60f, 60f) + pz.Hx, 0, pz.Hz);
        _shR.localRotation = Quaternion.Euler(pz.Rx, 0, pz.Rz);
        _shL.localRotation = Quaternion.Euler(pz.Lx, 0, pz.Lz);
        Hand.localRotation = Quaternion.Euler(0, 0, pz.HandRoll);
        if (_pourTaker != null) _pourTaker.rotation = Root.transform.rotation;   // во что льём — держим ровно
        _hat.localPosition = new Vector3(0, 0.2f + pz.HatHop, -0.03f);
        _hat.localRotation = Quaternion.Euler(-12.6f, pz.HatSpin, 4.6f);
        foreach (var p in _pupils) p.localScale = new Vector3(pz.Pupil, pz.Pupil, 0.4f);
        if (_held.Count > 0) Restack();   // держим колбы на месте каждый кадр — даже если игра пытается их куда-то тянуть
    }

    // ---------- эмоции и движения (как в 3D-превью) ----------
    public static readonly string[] Actions = { "dab", "up", "dance", "scare" };
    static readonly Dictionary<string, float> ActLen = new() { ["dab"] = 1.8f, ["up"] = 2.0f, ["dance"] = 4.0f, ["scare"] = 1.7f,
                                                                        ["grab"] = 0.7f, ["throw"] = 0.6f, ["shelve"] = 0.8f, ["pour"] = 1.0f };

    public static float Len(string a) => a != null && ActLen.TryGetValue(a, out var d) ? d : 0f;
    public void ShowEmote(int id) { EmoteId = id; EmoteStart = Time.unscaledTime; }
    public void PlayAction(string a) { _act = a; _actStart = Time.unscaledTime; }

    struct P { public float Rx, Rz, Lx, Lz, Hx, Hz, Bx, Bz, By, Yaw, HatHop, HatSpin, Pupil, HandRoll; }
    static float Ease(float x) => x < 0 ? 0 : x > 1 ? 1 : x * x * (3 - 2 * x);
    static float Env(float a, float d, float i, float o) => Mathf.Min(1f, Mathf.Min(Ease(a / i), Ease((d - a) / o)));
    const float R = Mathf.Rad2Deg;

    P Pose(float t)
    {
        var p = new P { Pupil = 1f };
        if (_act == null) return p;
        float a = t - _actStart, D = ActLen[_act];
        if (a > D) { _act = null; return p; }
        switch (_act)
        {
            case "dab":
            { float k = Env(a, D, 0.25f, 0.4f);
              p.Rz = 2.2f * k * R; p.Rx = -0.9f * k * R; p.Lx = -1.7f * k * R; p.Lz = 0.9f * k * R;
              p.Hx = 0.45f * k * R; p.Hz = 0.35f * k * R; p.Bz = -0.12f * k * R; break; }
            case "up":
            { float k = Env(a, D, 0.3f, 0.4f), w = Mathf.Sin(a * 12) * 0.15f * k;
              p.Rz = (2.7f + w) * k * R; p.Rx = -0.5f * k * R; p.Lz = (-2.7f - w) * k * R;
              p.Hx = -0.25f * k * R; p.By = Mathf.Abs(Mathf.Sin(a * 6)) * 0.08f * k; p.HatHop = Mathf.Abs(Mathf.Sin(a * 6)) * 0.05f * k; break; }
            case "dance":
            { float k = Env(a, D, 0.3f, 0.5f), b = a * 7;
              p.Rz = (1.4f + Mathf.Sin(b) * 1.1f) * k * R; p.Lz = (-1.4f + Mathf.Sin(b) * 1.1f) * k * R;
              p.Rx = -0.3f * k * R; p.Lx = -0.3f * k * R; p.By = Mathf.Abs(Mathf.Sin(b)) * 0.12f * k;
              p.Bz = Mathf.Sin(b) * 0.16f * k * R; p.Yaw = Mathf.Sin(b * 0.5f) * 0.5f * k * R; p.Hz = -Mathf.Sin(b) * 0.2f * k * R;
              p.HatHop = Mathf.Abs(Mathf.Sin(b)) * 0.04f * k; break; }
            // ---- рабочие движения: подобрать, бросить, поставить на полку, перелить ----
            case "grab":   // наклон и рука тянется вниз-вперёд
            { float k = Mathf.Sin(Mathf.PI * Mathf.Clamp01(a / D));
              p.Bx = 18f * k; p.Rx = -75f * k; p.Rz = 8f * k; p.Hx = 20f * k; break; }
            case "throw":  // замах назад и резкий бросок вперёд
            { float wind = Ease(a / 0.25f), fling = Ease((a - 0.25f) / 0.15f), back = Ease((a - 0.4f) / 0.2f);
              float rx = Mathf.Lerp(0f, 55f, wind); rx = Mathf.Lerp(rx, -115f, fling); rx = Mathf.Lerp(rx, 0f, back);
              p.Rx = rx; p.Yaw = Mathf.Lerp(Mathf.Lerp(0, 18f, wind), 0, fling); p.Bx = 8f * fling * (1 - back); break; }
            case "shelve": // рука вверх-вперёд к полке, чуть на носочки
            { float k = Mathf.Sin(Mathf.PI * Mathf.Clamp01(a / D));
              p.Rx = -120f * k; p.Rz = 6f * k; p.By = 0.04f * k; p.Hx = -10f * k; break; }
            case "pour":   // рука вперёд, кисть наклоняет колбу, голова смотрит вниз
            { float k = Env(a, D, 0.2f, 0.25f);
              p.Rx = -80f * k; p.Rz = -14f * k; p.Hx = 15f * k;   // правая (из чего льём) — к центру
              p.HandRoll = (35f + Mathf.Sin(a * 9f) * 10f) * k;    // кисть поворачивается: горлышко наклоняется к колбе
              p.Lx = (_pourTaker != null ? -70f : -30f) * k; p.Lz = (_pourTaker != null ? 16f : 0f) * k; break; }  // левая (во что) — навстречу
            case "scare":
            { float jump = a < 0.5f ? Mathf.Sin(Mathf.PI * a / 0.5f) * 0.35f : 0f;
              float hatUp = a < 0.9f ? Mathf.Max(0, Mathf.Sin(Mathf.PI * a / 0.9f)) * 0.45f : Mathf.Max(0, Mathf.Sin((a - 0.9f) * 14) * 0.04f * (1.7f - a));
              float k = Env(a, D, 0.08f, 0.5f), sh = Mathf.Sin(a * 40) * 0.08f * k;
              p.By = jump; p.HatHop = hatUp; p.HatSpin = a < 0.9f ? a * 3 * R : 0;
              p.Rz = (2.5f + sh) * k * R; p.Lz = (-2.5f - sh) * k * R; p.Rx = -0.4f * k * R; p.Lx = 0.1f * k * R;
              p.Hx = -0.3f * k * R; p.Pupil = 1 - 0.55f * k; break; }
        }
        return p;
    }

    // Зелья в руке у этого аптекаря (настоящие зелья игры)
    // Зелья у аптекаря: активное — в руке, остальные — в кобуре на поясе
    int _active = -1;
    Transform _belt;
    readonly Dictionary<IntPtr, Vector3> _worldScale = new();   // исходный мировой размер каждого зелья

    public void Hold(Transform potion)
    {
        if (potion == null) return;
        if (!_worldScale.ContainsKey(potion.Pointer)) _worldScale[potion.Pointer] = potion.lossyScale;
        if (!_held.Contains(potion)) _held.Add(potion);
        if (_active < 0) _active = _held.Count - 1;
        Restack();
    }
    public void Unhold(Transform potion)
    {
        if (potion == null) return;
        int i = _held.IndexOf(potion);
        if (i >= 0) _held.RemoveAt(i);
        if (_worldScale.TryGetValue(potion.Pointer, out var w)) { SetWorldScale(potion, w); _worldScale.Remove(potion.Pointer); }
        if (_active >= _held.Count) _active = _held.Count - 1;
        Restack();
    }
    // Порядок как в руке у игрока + какое выбрано
    // Ряд — главный: чего в нём нет, того у аптекаря больше нет (возвращаем список выпавших)
    public List<Transform> SetRow(List<Transform> row, int active)
    {
        var gone = new List<Transform>();
        for (int i = _held.Count - 1; i >= 0; i--)
        {
            var t = _held[i];
            if (t == null || row.Contains(t)) continue;
            _held.RemoveAt(i);
            if (_worldScale.TryGetValue(t.Pointer, out var ws)) { SetWorldScale(t, ws); _worldScale.Remove(t.Pointer); }
            if (_pourGiver == t) _pourGiver = null;
            if (_pourTaker == t) _pourTaker = null;
            gone.Add(t);
        }
        foreach (var t in row) if (t != null) { if (!_worldScale.ContainsKey(t.Pointer)) _worldScale[t.Pointer] = t.lossyScale; if (!_held.Contains(t)) _held.Add(t); }
        _held.Sort((a, b) => Order(row, a).CompareTo(Order(row, b)));
        _active = Mathf.Clamp(active, -1, _held.Count - 1);
        Restack();
        return gone;
    }
    public bool Holds(Transform t) => t != null && _held.Contains(t);
    static int Order(List<Transform> row, Transform t) { int i = row.IndexOf(t); return i < 0 ? 999 : i; }
    // Переливание: из чего — в правую руку, во что (если оно у него же) — в левую; потом всё обратно
    public void PlayPour(Transform giver, Transform taker)
    {
        _pourGiver = giver != null && _held.Contains(giver) ? giver : null;
        _pourTaker = taker != null && _held.Contains(taker) ? taker : null;
        PlayAction("pour");
        Restack();
    }

    public void SetActive(int active) { _active = Mathf.Clamp(active, -1, _held.Count - 1); Restack(); }

    void Restack()
    {
        _held.RemoveAll(t => t == null);
        int beltSlot = 0;
        bool pouring = _act == "pour";
        var right = pouring && _pourGiver != null ? _pourGiver : (_active >= 0 && _active < _held.Count ? _held[_active] : null);
        var left = pouring ? _pourTaker : null;
        for (int i = 0; i < _held.Count; i++)
        {
            var t = _held[i];
            var w = _worldScale.TryGetValue(t.Pointer, out var ws) ? ws : t.lossyScale;
            if (t == left)
            {
                t.SetParent(HandL, true);
                t.position = HandL.position;
                t.rotation = Root.transform.rotation;   // стоит ровно, горлышком вверх — в неё льют
                SetWorldScale(t, w);
            }
            else if (t == right)
            {
                t.SetParent(Hand, true);
                t.position = Hand.position;
                t.localRotation = Quaternion.Euler(0, 0, 90f);   // поперёк руки — буквой «Т», этикетка смотрит вперёд
                SetWorldScale(t, w);
            }
            else
            {
                // кобура: по бокам пояса, по очереди справа/слева, каждое следующее чуть дальше назад
                int j = beltSlot++;
                float side = (j % 2 == 0) ? 1f : -1f;
                float ang = side * (100f + 26f * (j / 2)) * Mathf.Deg2Rad;   // на бёдрах и ближе к спине — не мешают рукам
                var local = new Vector3(Mathf.Sin(ang) * 0.43f, -0.02f, Mathf.Cos(ang) * 0.43f);
                t.SetParent(_belt, true);
                t.position = _belt.TransformPoint(local);
                t.rotation = _belt.rotation * Quaternion.Euler(0, ang * Mathf.Rad2Deg, 0);
                SetWorldScale(t, w * 0.7f);
            }
        }
    }
    static void SetWorldScale(Transform t, Vector3 world)
    {
        var ps = t.parent != null ? t.parent.lossyScale : Vector3.one;
        t.localScale = new Vector3(world.x / ps.x, world.y / ps.y, world.z / ps.z);
    }

    public Vector3 HeadTop => Root.transform.position + Vector3.up * (2.35f * Plugin.AvatarScale.Value);

    public void Destroy() { if (Root != null) UnityEngine.Object.Destroy(Root); }

    // ---------------- сборка модели ----------------

    void Build()
    {
        Root = new GameObject("PotionCoop_Avatar_" + Name);
        UnityEngine.Object.DontDestroyOnLoad(Root);
        _robe = NewMat(Robe[0]);
        _robeDark = NewMat(RobeDark[0]);
        var apron = NewMat(Hex(0xefe1c2));
        var headM = NewMat(Hex(0x5a3c28));
        var boots = NewMat(Hex(0x24180f));
        var leather = NewMat(Hex(0x5a3b22));
        var brass = NewMat(Hex(0xc9933f), metal: 0.85f, smooth: 0.7f);
        var gold = NewMat(Hex(0xe2b04a), metal: 0.9f, smooth: 0.75f);
        var lens = NewMat(Hex(0xfff6dc), smooth: 0.8f);
        var pupil = NewMat(Hex(0x140d08));

        var rt = Root.transform;
        _body = Child(rt, "body");

        foreach (var x in new[] { -0.13f, 0.13f })
        {
            var b = Part(rt, "boot", Sphere(0.11f), boots);
            b.localPosition = new Vector3(x, 0.08f, 0.04f); b.localScale = new Vector3(1, 0.7f, 1.35f);
        }
        Part(_body, "robe", Lathe(new[] { V(0.001f, 0.16f), V(0.44f, 0.16f), V(0.47f, 0.22f), V(0.43f, 0.4f), V(0.31f, 1.0f), V(0.25f, 1.1f), V(0.001f, 1.14f) }, 32), _robe);
        Part(_body, "apron", Lathe(new[] { V(0.48f, 0.21f), V(0.445f, 0.4f), V(0.33f, 0.97f) }, 20, -0.95f, 1.9f), apron, twoSided: true);
        var belt = Part(_body, "belt", Lathe(new[] { V(0.36f, 0.77f), V(0.355f, 0.83f) }, 32), leather, twoSided: true);
        var collar = Part(_body, "collar", Lathe(Circle(0.2f, 0.05f, 12), 32), _robeDark);
        collar.localPosition = new Vector3(0, 1.13f, 0); collar.localScale = new Vector3(1, 0.55f, 1);

        // руки
        _shL = Child(_body, "shoulderL"); _shL.localPosition = new Vector3(-0.32f, 1.0f, 0);
        _shR = Child(_body, "shoulderR"); _shR.localPosition = new Vector3(0.32f, 1.0f, 0);
        Arm(_shL, Vector3.zero, new Vector3(-0.1f, -0.38f, 0.06f), _robe);
        Arm(_shR, Vector3.zero, new Vector3(0.06f, -0.36f, 0.12f), _robe);
        Hand = Child(_shR, "hand"); Hand.localPosition = new Vector3(0.06f, -0.4f, 0.28f);
        HandL = Child(_shL, "handL"); HandL.localPosition = new Vector3(-0.1f, -0.44f, 0.16f);
        _belt = Child(_body, "holster"); _belt.localPosition = new Vector3(0, 0.74f, 0);

        // голова
        _head = Child(_body, "head"); _head.localPosition = new Vector3(0, 1.4f, 0);
        Part(_head, "skull", Sphere(0.27f), headM);
        var strap = Part(_head, "strap", Lathe(new[] { V(0.276f, 0.015f), V(0.276f, 0.045f) }, 32, Mathf.PI / 2 + 1.05f, 2 * Mathf.PI - 2.1f), leather, twoSided: true);
        foreach (var x in new[] { -0.12f, 0.12f })
        {
            var n = new Vector3(x, 0.03f, Mathf.Sqrt(0.27f * 0.27f - x * x - 0.03f * 0.03f)).normalized;
            var g = Child(_head, "goggle");
            g.localPosition = n * 0.262f;
            g.localRotation = Quaternion.FromToRotation(Vector3.forward, n);
            var rim = Part(g, "rim", Lathe(new[] { V(0.095f, -0.02f), V(0.095f, 0.04f), V(0.108f, 0.045f), V(0.108f, 0.03f) }, 24), brass, twoSided: true);
            rim.localRotation = Quaternion.Euler(90, 0, 0);
            var l = Part(g, "lens", Lathe(new[] { V(0.001f, 0.035f), V(0.088f, 0.035f) }, 24), lens, twoSided: true);
            l.localRotation = Quaternion.Euler(90, 0, 0);
            var p = Part(g, "pupil", Sphere(0.035f), pupil); _pupils.Add(p);
            p.localPosition = new Vector3(0, -0.01f, 0.042f); p.localScale = new Vector3(1, 1, 0.4f);
        }
        // шляпа
        var hat = Child(_head, "hat"); _hat = hat;
        hat.localPosition = new Vector3(0, 0.2f, -0.03f);
        hat.localRotation = Quaternion.Euler(-12.6f, 0, 4.6f);
        Part(hat, "brim", Lathe(new[] { V(0.001f, 0), V(0.42f, -0.03f), V(0.44f, 0), V(0.27f, 0.05f), V(0.001f, 0.05f) }, 32), _robeDark, twoSided: true);
        Part(hat, "cone", Lathe(new[] { V(0.27f, 0.03f), V(0.25f, 0.15f), V(0.16f, 0.38f), V(0.06f, 0.58f), V(0.001f, 0.62f) }, 32), _robe);
        Part(hat, "band", Lathe(new[] { V(0.262f, 0.045f), V(0.279f, 0.045f), V(0.267f, 0.125f), V(0.25f, 0.125f) }, 32), gold, twoSided: true);
    }

    static void Arm(Transform parent, Vector3 a, Vector3 b, Material m)
    {
        var dir = b - a;
        var arm = Part(parent, "arm", Capsule(0.085f, dir.magnitude), m);
        arm.localPosition = a;
        arm.localRotation = Quaternion.FromToRotation(Vector3.down, dir.normalized);
    }

    static Vector2 V(float r, float y) => new Vector2(r, y);
    static Color Hex(int h) => new Color(((h >> 16) & 255) / 255f, ((h >> 8) & 255) / 255f, (h & 255) / 255f);

    static Transform Child(Transform parent, string name)
    {
        var go = new GameObject(name);
        go.transform.SetParent(parent, false);
        return go.transform;
    }

    static Transform Part(Transform parent, string name, Mesh mesh, Material mat, bool twoSided = false)
    {
        var go = new GameObject(name);
        go.transform.SetParent(parent, false);
        go.layer = 2; // Ignore Raycast — чтобы прицел и зелья его не задевали
        go.AddComponent<MeshFilter>().sharedMesh = mesh;
        var r = go.AddComponent<MeshRenderer>();
        r.sharedMaterial = twoSided ? TwoSided(mat) : mat;
        return go.transform;
    }

    // ---------------- материалы ----------------

    static Material _template;
    static readonly Dictionary<IntPtr, Material> _twoSided = new();

    static Material NewMat(Color c, float metal = 0f, float smooth = 0.25f)
    {
        if (_template == null) _template = FindTemplate();
        var m = new Material(_template);
        if (m.HasProperty("_BaseMap")) m.SetTexture("_BaseMap", null);
        if (m.HasProperty("_MainTex")) m.SetTexture("_MainTex", null);
        if (m.HasProperty("_BumpMap")) m.SetTexture("_BumpMap", null);
        if (m.HasProperty("_Metallic")) m.SetFloat("_Metallic", metal);
        if (m.HasProperty("_Smoothness")) m.SetFloat("_Smoothness", smooth);
        Paint(m, c);
        return m;
    }

    static void Paint(Material m, Color c)
    {
        if (m.HasProperty("_BaseColor")) m.SetColor("_BaseColor", c);
        if (m.HasProperty("_Color")) m.SetColor("_Color", c);
    }

    static Material TwoSided(Material m)
    {
        if (_twoSided.TryGetValue(m.Pointer, out var t)) return t;
        t = new Material(m);
        if (t.HasProperty("_Cull")) t.SetFloat("_Cull", 0f);
        _twoSided[m.Pointer] = t;
        return t;
    }

    static Material FindTemplate()
    {
        foreach (var name in new[] { "Universal Render Pipeline/Lit", "Universal Render Pipeline/Simple Lit", "Standard" })
        {
            var sh = Shader.Find(name);
            if (sh != null) { Plugin.L.LogInfo("Avatar: шейдер " + name); return new Material(sh); }
        }
        // запасной путь: берём материал у любого объекта на сцене
        var any = UnityEngine.Object.FindObjectsByType<MeshRenderer>(FindObjectsSortMode.None);
        foreach (var r in any)
            if (r != null && r.sharedMaterial != null) { Plugin.L.LogWarning("Avatar: шейдер не найден, беру материал у " + r.name); return new Material(r.sharedMaterial); }
        throw new Exception("нет ни одного материала для аватара");
    }

    // ---------------- геометрия ----------------

    static Vector2[] Circle(float R, float r, int n) // профиль тора
    {
        var p = new Vector2[n + 1];
        for (int i = 0; i <= n; i++) { float a = i * 2 * Mathf.PI / n; p[i] = new Vector2(R + Mathf.Cos(a) * r, Mathf.Sin(a) * r); }
        return p;
    }

    static Mesh Sphere(float r)
    {
        var p = new Vector2[13];
        for (int i = 0; i <= 12; i++) { float a = -Mathf.PI / 2 + Mathf.PI * i / 12; p[i] = new Vector2(Mathf.Max(0.0005f, Mathf.Cos(a) * r), Mathf.Sin(a) * r); }
        return Lathe(p, 24);
    }

    static Mesh Capsule(float r, float len) // вдоль -Y, от 0 до -len
    {
        var p = new List<Vector2>();
        for (int i = 0; i <= 6; i++) { float a = -Mathf.PI / 2 + Mathf.PI / 2 * i / 6; p.Add(new Vector2(Mathf.Max(0.0005f, Mathf.Cos(a) * r * 1.1f), -len + Mathf.Sin(a) * r * 1.1f)); }
        for (int i = 0; i <= 6; i++) { float a = Mathf.PI / 2 * i / 6; p.Add(new Vector2(Mathf.Max(0.0005f, Mathf.Cos(a) * r), Mathf.Sin(a) * r)); }
        return Lathe(p.ToArray(), 16);
    }

    // Тело вращения по профилю (радиус, высота) — как LatheGeometry в превью
    static Mesh Lathe(Vector2[] pts, int seg, float phiStart = 0f, float phiLen = Mathf.PI * 2)
    {
        int cols = seg + 1;
        var verts = new Vector3[pts.Length * cols];
        var uv = new Vector2[verts.Length];
        for (int i = 0; i < pts.Length; i++)
            for (int j = 0; j < cols; j++)
            {
                float a = phiStart + phiLen * j / seg;
                verts[i * cols + j] = new Vector3(Mathf.Sin(a) * pts[i].x, pts[i].y, Mathf.Cos(a) * pts[i].x);
                uv[i * cols + j] = new Vector2((float)j / seg, (float)i / (pts.Length - 1));
            }
        var tris = new int[(pts.Length - 1) * seg * 6];
        int t = 0;
        for (int i = 0; i < pts.Length - 1; i++)
            for (int j = 0; j < seg; j++)
            {
                int a = i * cols + j, b = a + cols;
                tris[t++] = a; tris[t++] = a + 1; tris[t++] = b;
                tris[t++] = a + 1; tris[t++] = b + 1; tris[t++] = b;
            }
        var mesh = new Mesh();
        mesh.vertices = verts;
        mesh.uv = uv;
        mesh.triangles = tris;
        mesh.RecalculateNormals();
        mesh.RecalculateBounds();
        return mesh;
    }
}
