using System;
using System.Collections.Generic;
using System.Linq;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using Il2CppInterop.Runtime.InteropTypes.Arrays;
using Steamworks;
using UnityEngine;
using UnityEngine.InputSystem;

namespace PotionCoop;

// Связь через Steam + аптекари других игроков.
// Мод сам находит друзей, которые сейчас в этой игре с этим модом. F10 — пинг.
public class NetDriver : MonoBehaviour
{
    public NetDriver(IntPtr ptr) : base(ptr) { }

    const int Channel = 7;          // свой канал, чтобы не пересекаться с игрой
    const int SendReliable = 8;     // k_nSteamNetworkingSend_Reliable
    const int SendUnreliable = 0;
    const byte MsgHello = 1, MsgHelloAck = 2, MsgPing = 3, MsgPong = 4, MsgState = 5, MsgRoom = 6, MsgSaveReq = 7, MsgSave = 8, MsgMove = 9, MsgEmote = 10, MsgMod = 11, MsgXp = 12, MsgXpTotal = 13, MsgSkills = 14, MsgSkillsSet = 15, MsgWorldSkills = 16, MsgSteals = 17, MsgScored = 18, MsgMark = 19, MsgColors = 20, MsgHook = 21;
    const string Proto = "PotionCoop/0.0.27";
    const float StateRate = 0.1f;   // 10 раз в секунду
    const float PeerTimeout = 20f;   // загрузка лаборатории может подвесить игру на 10+ секунд

    // метка этого запуска игры: по ней отличаем «перезапустился» от «повторил привет»
    static readonly string LaunchId = Guid.NewGuid().ToString("N").Substring(0, 8);
    // в «привете» — версия, метка запуска и цвет, который я хочу
    static byte[] HelloBytes => Encoding.UTF8.GetBytes(Proto + "|" + LaunchId + "|" + Plugin.AvatarColor.Value);

    class Peer
    {
        public string Launch;
        public string Name;
        public string Proto;
        public float LastSeen;
        public int PingMs = -1;
        public bool Hosting;
        public bool Tired;
        public Avatar Avatar;
        public float MarkAt = -99f;     // когда позвал «я тут»
        public int WantColor = -1;      // какой цвет хочет
        public string RoomName = "", RoomId = "";   // какую аптеку открыл (если хост)
    }
    public string MyName => _ready ? SteamFriends.GetPersonaName() : null;
    float _selfMarkAt = -99f;

    bool _ready;
    uint _appId;
    ulong _me;
    float _nextScan, _nextState;
    int _lastInGame = -1;
    readonly Dictionary<ulong, Peer> _peers = new();
    Il2CppStructArray<IntPtr> _inbox;
    GUIStyle _tagStyle, _hudStyle, _btnStyle, _boxStyle;
    string _joinStatus;

    // ---- эмоции, колесо, манекен ----
    static readonly string[] EmoSym = { "♥", "☺", "?", "!", "#@!", "Zz", "♪", "★" };
    static readonly Color[] EmoCol = {
        new Color(0.91f,0.22f,0.35f), new Color(0.96f,0.79f,0.27f), new Color(0.24f,0.48f,0.88f), new Color(0.91f,0.39f,0.17f),
        new Color(0.84f,0.19f,0.23f), new Color(0.42f,0.44f,0.84f), new Color(0.18f,0.65f,0.6f), new Color(0.95f,0.72f,0.19f) };
    // 8 эмоций, 4 действия и 13-я плитка — письмо-инструкция
    static string[] WheelLabels => Loc.Ru
        ? new[] { "♥", "☺", "?", "!", "#@!", "Zz", "♪", "★", "Дэб", "Руки\nвверх", "Танец", "Испуг", "Письмо" }
        : new[] { "♥", "☺", "?", "!", "#@!", "Zz", "♪", "★", "Dab", "Hands\nup", "Dance", "Scared", "Letter" };
    const int WheelN = 13;
    const float WheelStep = 360f / WheelN;
    static readonly int[] ActionEmote = { -1, 7, 6, 3 };   // дэб — без пузыря, руки вверх — ★, танец — ♪, испуг — !
    bool _wheelOpen; Vector2 _wheelVec; int _wheelSel = -1;
    CodeBase.Components.Hero.HeroLook _look;   // пока колесо открыто — камеру не крутим
    float _selfEmoteAt = -99; int _selfEmote = -1;
    Avatar _dummy;
    bool _dummyFollows;            // false — стоит на месте лицом к тебе, true — ходит следом
    float _dummyDelay = 3f;        // задержка повтора, меняется клавишами − и +
    Vector3 _dummyEye; float _dummyYaw;
    readonly Queue<(float t, Vector3 p, float yaw, float pitch)> _dummyTrail = new();
    readonly Queue<(float t, int kind, int id)> _dummyEmotes = new();
    readonly Queue<(float t, string anim)> _dummyAnims = new();
    public void LocalEmoteFlash(int emo) { _selfEmote = emo; _selfEmoteAt = Time.unscaledTime; }
    public void LocalAnim(string anim) { if (_dummy != null) _dummyAnims.Enqueue((Time.unscaledTime, anim)); }
    // «тени» зелий в руке манекена
    readonly Queue<(float t, int uid, GameObject shadow, bool take)> _dummyHands = new();
    readonly Dictionary<int, GameObject> _dummyShadows = new();
    public void DummyGrab(CodeBase.Components.Potions.PotionFacade p)
    {
        if (_dummy == null || p == null) return;
        try { var sh = Shadow.Make(p); _allShadows[p.Uid] = sh; _dummyHands.Enqueue((Time.unscaledTime, p.Uid, sh, true)); }
        catch (Exception e) { Plugin.L.LogError("SHADOW: " + e.Message); }
    }
    // все тени по номеру зелья (создаются сразу, в руку манекену попадают с задержкой)
    readonly Dictionary<int, GameObject> _allShadows = new();
    class PendingColors { public float CaptureAt, ApplyAt; public GameObject Shadow; public List<MaterialPropertyBlock> Snap; }
    readonly List<PendingColors> _pendingColors = new();
    readonly Queue<(float t, int giver, int taker)> _dummyPours = new();
    public void DummyPour(CodeBase.Components.Potions.PotionFacade g, CodeBase.Components.Potions.PotionFacade tk)
    {
        if (_dummy == null) return;
        float now = Time.unscaledTime;
        _dummyPours.Enqueue((now, g?.Uid ?? 0, tk?.Uid ?? 0));
        foreach (var p in new[] { g, tk })
            if (p != null && _allShadows.TryGetValue(p.Uid, out var sh))   // цвета снимаем, когда анимация переливания у тебя закончится
                _pendingColors.Add(new PendingColors { CaptureAt = now + 1.3f, ApplyAt = now + _dummyDelay + 1.0f, Shadow = sh });
    }
    public void DummyRelease(int uid) { if (_dummy != null) _dummyHands.Enqueue((Time.unscaledTime, uid, null, false)); }
    readonly Queue<(float t, int[] row, int active)> _dummyRows = new();
    public void DummyRow(int[] row, int active) { if (_dummy != null) _dummyRows.Enqueue((Time.unscaledTime, row, active)); }
    // последнее, что манекен показал — он повторяет это по кругу, чтобы можно было разглядеть
    string _dummyLastAnim; int _dummyLastKind = -1, _dummyLastId; float _dummyNextRepeat;
    GUIStyle _emoStyle, _wheelStyle, _bubble, _bubbleSel, _dot, _banner;

    static Texture2D Rounded(Color fill, Color border, int size = 64, int radius = 20, int bw = 3)
    {
        var t = new Texture2D(size, size, TextureFormat.RGBA32, false) { hideFlags = HideFlags.HideAndDontSave, filterMode = FilterMode.Bilinear };
        var px = new Color32[size * size];
        for (int y = 0; y < size; y++)
            for (int x = 0; x < size; x++)
            {
                // расстояние до скруглённого прямоугольника
                float dx = Mathf.Max(radius - x - 0.5f, x + 0.5f - (size - radius), 0f);
                float dy = Mathf.Max(radius - y - 0.5f, y + 0.5f - (size - radius), 0f);
                float d = radius - Mathf.Sqrt(dx * dx + dy * dy);       // >0 внутри
                float a = Mathf.Clamp01(d + 0.5f);
                var c = d < bw ? border : fill;
                c.a *= a;
                px[y * size + x] = c;
            }
        t.SetPixels32(px);
        t.Apply();
        return t;
    }
    static GUIStyle Skin(Texture2D tex, int b)
    {
        var st = new GUIStyle();
        st.normal.background = tex;
        st.border = new RectOffset(b, b, b, b);
        return st;
    }
    float _joinRequestedAt;

    public static NetDriver I;
    public bool HasPeers => _peers.Count > 0;
    public void BroadcastMove(byte[] payload) { foreach (var id in _peers.Keys) Send(id, MsgMove, payload, SendReliable); }

    // Клики мышью по окнам мода ловим сами: стандартные кнопки IMGUI в этой игре мышь не получают
    Vector2? _click; int _clickFrame, _guardFrame, _typingEndedFrame = -1;
    void TrackClicks()
    {
        var ms = Mouse.current;
        if (ms != null && ms.leftButton.wasPressedThisFrame)
        {
            var p = ms.position.ReadValue();
            _click = new Vector2(p.x, Screen.height - p.y); _clickFrame = Time.frameCount;
        }
        else if (_click.HasValue && Time.frameCount > _clickFrame + 2) _click = null;
    }
    Texture2D _hoverTex;
    bool MBtn(Rect r, string text)
    {
        GUI.Box(r, text, _btnStyle);
        var ms = Mouse.current;
        if (ms != null)
        {
            var mp = ms.position.ReadValue(); mp.y = Screen.height - mp.y;
            if (r.Contains(mp) && Event.current.type == EventType.Repaint)
            {   // подсветка под мышью (своя текстура: встроенная белая в этой игре роняла отрисовку остальных кнопок)
                try
                {
                    if (_hoverTex == null)
                    {
                        _hoverTex = new Texture2D(1, 1);
                        _hoverTex.SetPixel(0, 0, new Color(1f, 0.9f, 0.6f, 0.22f));
                        _hoverTex.Apply();
                        _hoverTex.hideFlags = HideFlags.HideAndDontSave;
                    }
                    GUI.DrawTexture(r, _hoverTex);
                }
                catch { }
            }
        }
        // клик, которым окно только что открыли, не считается нажатием кнопки в нём
        if (_click.HasValue && _clickFrame > _guardFrame && Event.current.type == EventType.Repaint && r.Contains(_click.Value)) { _click = null; return true; }
        return false;
    }

    // Печать названия аптеки — с клавиатуры напрямую (события клавиш IMGUI тоже не доходят)
    void TickTyping(Keyboard kb)
    {
        if (!_typingRoom || kb == null) return;
        if (kb.escapeKey.wasPressedThisFrame) { if (!MenuButtons.ScreenOpen) { _typingRoom = false; _typingEndedFrame = Time.frameCount; } return; }
        if (kb.enterKey.wasPressedThisFrame || kb.numpadEnterKey.wasPressedThisFrame) { _typingRoom = false; _typingEndedFrame = Time.frameCount; CreateRoom(Coop.ListRooms(), false); return; }
        if (kb.backspaceKey.wasPressedThisFrame) { if (_newRoomName.Length > 0) _newRoomName = _newRoomName.Substring(0, _newRoomName.Length - 1); return; }
        if (_newRoomName.Length >= 40) return;
        if (kb.spaceKey.wasPressedThisFrame) { _newRoomName += " "; return; }
        bool shift = kb.shiftKey.isPressed;
        foreach (Key key in Enum.GetValues(typeof(Key)))
        {
            if (key == Key.None || key == Key.Space || key == Key.Enter || key == Key.Backspace || key == Key.Escape || key == Key.Tab) continue;
            try
            {
                var k = kb[key];
                if (k == null || !k.wasPressedThisFrame) continue;
                var name = k.displayName;   // символ с учётом раскладки: «ф», «a», «1»
                if (string.IsNullOrEmpty(name) || name.Length != 1 || char.IsControl(name[0])) continue;
                _newRoomName += shift ? char.ToUpper(name[0]) : char.ToLower(name[0]);
            }
            catch { }
        }
    }

    void Update()
    {
        I = this;
        TrackClicks();
        try
        {
            if (!_ready && !TryInit()) return;
            TickCoopScreen(Keyboard.current);
            TickTyping(Keyboard.current);

            if (Time.unscaledTime >= _nextScan)
            {
                _nextScan = Time.unscaledTime + 5f;
                ScanFriends();
                DropStalePeers();
                if (Coop.Role == Role.Host) BroadcastRoom();   // повторяем «аптека открыта» раз в 5 с — вдруг сообщение потерялось
            }

            Receive();

            if (Time.unscaledTime >= _nextState)
            {
                _nextState = Time.unscaledTime + StateRate;
                SendState();
            }

            float dt = Time.unscaledDeltaTime;
            foreach (var p in _peers.Values) p.Avatar?.Tick(dt);
            TickWheel();
            Letter.Tick();
            TickDummy(dt);
            if (Coop.InLobby)
            {
                var fh = _peers.FirstOrDefault(kv => kv.Value.Hosting);
                MenuButtons.Tick(BecomeHost, JoinHost, fh.Value != null ? fh.Key : 0, fh.Value?.Name ?? "", Coop.AutoJoin || _joinStatus != null);
            }
            Modifiers.HostTick(_peers.Keys.Append(_me).ToList(), SendMod);
            TickAutoJoin();
            TickAutoHost();
            TickAutoContinue();
            TickHooks();
            Skills.Tick(
                mine => { if (_peers.ContainsKey(Coop.HostId)) Send(Coop.HostId, MsgSkillsSet, Skills.Pack(mine), SendReliable); },
                world => { foreach (var id in _peers.Keys) Send(id, MsgWorldSkills, Skills.Pack(world), SendReliable); });
            Xp.Tick(SendXp);
            Sync.Tick(dt);
            TickTiredBubbles();

            var kb = Keyboard.current;
            if (kb != null && kb.f10Key.wasPressedThisFrame) PingAll();
            if (kb != null && kb.f11Key.wasPressedThisFrame) LogCollector.Collect();   // логи в zip на рабочий стол
            // «я тут»: колёсико мыши (нажать) или F2 — у всех над тобой метка и стрелка по краю экрана
            if (Coop.State == eGameState.Playing && Coop.Role != Role.Solo && !_wheelOpen
                && ((kb != null && kb.f2Key.wasPressedThisFrame) || (Mouse.current != null && Mouse.current.middleButton.wasPressedThisFrame))
                && Time.unscaledTime - _selfMarkAt > 1.5f)
                SendMark();
            if (kb != null && kb.f9Key.wasPressedThisFrame) ToggleDummy();
            if (kb != null && kb.f6Key.wasPressedThisFrame) Modifiers.SetTired(!Modifiers.Tired, "F6, проверка");
            // выбор аптеки с клавиатуры: 0 — мой сейв, 1–9 — аптеки по списку, Esc — закрыть
            if (kb != null && _roomPicker && Coop.InLobby && !_typingRoom && _typingEndedFrame != Time.frameCount)
            {
                var rooms = Coop.ListRooms();
                var digits = new[] { kb.digit1Key, kb.digit2Key, kb.digit3Key, kb.digit4Key, kb.digit5Key, kb.digit6Key, kb.digit7Key, kb.digit8Key, kb.digit9Key };
                for (int i = 0; i < digits.Length && i < rooms.Length; i++)
                    if (digits[i].wasPressedThisFrame) { _roomPicker = false; Coop.RelaunchAsHost(rooms[i]); }
                if (kb.escapeKey.wasPressedThisFrame) _roomPicker = false;
                if (kb.nKey.wasPressedThisFrame) CreateRoom(rooms, false);
            }
            // проверочные клавиши хоста
            if (kb != null && Coop.Role == Role.Host && Coop.State == eGameState.Playing)
            {
                if (kb.f5Key.wasPressedThisFrame) { Sync.ForceBreakNext = true; Coop.Say(Loc.L("Следующий бросок разобьёт склянку", "The next throw will break the flask"), 3f); }
                if (kb.f4Key.wasPressedThisFrame) SendMod(Modifiers.Quake, 0, true);
                if (kb.f3Key.wasPressedThisFrame) SendMod(Modifiers.Leaky, _peers.Count > 0 ? _peers.Keys.First() : _me, true);
                if (kb.f7Key.wasPressedThisFrame) SendMod(Modifiers.Blind, _peers.Count > 0 ? _peers.Keys.First() : _me, true);
                // F8 — аварийная кнопка: снять все модификаторы у всех и больше не насылать (ещё раз — включить обратно)
                if (kb.f8Key.wasPressedThisFrame)
                {
                    Modifiers.Paused = !Modifiers.Paused;
                    if (Modifiers.Paused) SendMod(Modifiers.ClearAll, 0, true);
                    Coop.Say(Modifiers.Paused ? Loc.L("Модификаторы выключены у всех (F8 — включить)", "Modifiers are off for everyone (F8 to turn back on)")
                                              : Loc.L("Модификаторы снова включены", "Modifiers are back on"), 4f);
                    Plugin.L.LogInfo(Modifiers.Paused ? "MOD: хост выключил модификаторы у всех" : "MOD: хост включил модификаторы обратно");
                }
            }
            // Дальтоник-дух можно проверить и одному: F7 в лаборатории
            if (kb != null && Coop.Role == Role.Solo && Coop.State == eGameState.Playing && kb.f7Key.wasPressedThisFrame)
                ColorCurse.Set(!ColorCurse.On, "F7, проверка");
            ColorCurse.Tick();
            // запасной путь без мыши: F7 — создать аптеку, F8 — зайти к первому, кто её открыл
            if (kb != null && kb.f7Key.wasPressedThisFrame)
            {
                if (Coop.InLobby) BecomeHost();
            }
            if (kb != null && kb.f8Key.wasPressedThisFrame)
            {
                var host = _peers.FirstOrDefault(kv => kv.Value.Hosting);
                if (!Coop.InLobby) Plugin.L.LogInfo($"COOP: F8 работает только в главном меню (сейчас {Coop.State})");
                else if (host.Value == null) Plugin.L.LogInfo("COOP: F8 — никто из друзей пока не открыл аптеку");
                else JoinHost(host.Key);
            }
        }
        catch (Exception e)
        {
            Plugin.L.LogError("Net: " + e);
            _nextScan = Time.unscaledTime + 10f;
        }
    }

    bool TryInit()
    {
        if (Time.unscaledTime < _nextScan) return false;
        _nextScan = Time.unscaledTime + 3f;
        try
        {
            if (!SteamAPI.IsSteamRunning() || !SteamUser.BLoggedOn()) return false;
        }
        catch { return false; } // Steam ещё не поднят игрой — тихо ждём
        _appId = SteamUtils.GetAppID().m_AppId;
        _me = SteamUser.GetSteamID().m_SteamID;
        _inbox = new Il2CppStructArray<IntPtr>(64);
        _ready = true;
        _nextScan = 0;
        Plugin.L.LogInfo($"NET ready: я {SteamFriends.GetPersonaName()} ({_me}), appId={_appId}");
        return true;
    }

    // ---------- поиск друзей ----------

    void ScanFriends()
    {
        int n = SteamFriends.GetFriendCount(EFriendFlags.k_EFriendFlagImmediate);
        int inGame = 0;
        for (int i = 0; i < n; i++)
        {
            var friend = SteamFriends.GetFriendByIndex(i, EFriendFlags.k_EFriendFlagImmediate);
            if (!SteamFriends.GetFriendGamePlayed(friend, out FriendGameInfo_t info)) continue;
            if ((uint)(info.m_gameID.m_GameID & 0xFFFFFF) != _appId) continue;
            inGame++;
            if (!_peers.ContainsKey(friend.m_SteamID))
                Send(friend.m_SteamID, MsgHello, HelloBytes, SendReliable);
        }
        if (inGame != _lastInGame)
        {
            _lastInGame = inGame;
            Plugin.L.LogInfo($"NET: друзей в игре: {inGame}, на связи: {_peers.Count}");
        }
    }

    void DropStalePeers()
    {
        foreach (var id in _peers.Keys.ToList())
        {
            var p = _peers[id];
            if (Time.unscaledTime - p.LastSeen > PeerTimeout)
            {
                Plugin.L.LogInfo($"NET: <<< {p.Name} пропал (нет сигнала {PeerTimeout:0} с)");
                try { Sync.DropAllHeldBy(p.Avatar, id, null); } catch (Exception e) { Plugin.L.LogError("SYNC drop held: " + e.Message); }
                p.Avatar?.Destroy();
                _peers.Remove(id);
                RecolorAll();
                if (Coop.Role == Role.Guest && Coop.HostId == id && !Coop.AutoJoin) HostGone($"{p.Name} пропал из сети");
            }
        }
    }

    // ---------- положение игрока ----------

    void SendState()
    {
        if (_peers.Count == 0) return;
        var cam = Camera.main;
        if (cam == null) return;
        var t = cam.transform;
        var pos = t.position;
        var e = t.rotation.eulerAngles;

        try { Sync.NoteMyPos(pos); } catch { }
        var b = new byte[20];
        Buffer.BlockCopy(BitConverter.GetBytes(pos.x), 0, b, 0, 4);
        Buffer.BlockCopy(BitConverter.GetBytes(pos.y), 0, b, 4, 4);
        Buffer.BlockCopy(BitConverter.GetBytes(pos.z), 0, b, 8, 4);
        Buffer.BlockCopy(BitConverter.GetBytes(e.y), 0, b, 12, 4);
        Buffer.BlockCopy(BitConverter.GetBytes(e.x), 0, b, 16, 4);
        foreach (var id in _peers.Keys) Send(id, MsgState, b, SendUnreliable);
    }

    void OnState(Peer p, byte[] b)
    {
        if (b.Length < 21) return;
        var pos = new Vector3(BitConverter.ToSingle(b, 1), BitConverter.ToSingle(b, 5), BitConverter.ToSingle(b, 9));
        float yaw = BitConverter.ToSingle(b, 13), pitch = BitConverter.ToSingle(b, 17);
        if (p.Avatar == null)
        {
            p.Avatar = new Avatar(p.Name);
            RecolorAll();
            Plugin.L.LogInfo($"NET: появился аптекарь {p.Name}");
            try { Sync.ResendRow(); } catch { }
            if (Coop.Role == Role.Host)
                foreach (var kv in _peers) if (kv.Value == p)
                {
                    Send(kv.Key, MsgSteals, Encoding.UTF8.GetBytes(Sync.PackSteals()), SendReliable);
                    _hookAt[kv.Key] = Time.unscaledTime + 8f;   // его «фартук на крючке» — когда он точно разложится
                }
        }
        p.Avatar.SetState(pos, yaw, pitch);
    }

    // Цвета: в аптеке их раздаёт хост (каждому свой, закреплён за игроком в этой аптеке).
    // Пока раздачи нет — по порядку Steam ID, у всех одинаково и без повторов.
    readonly Dictionary<ulong, int> _colors = new();
    void RecolorAll()
    {
        var ids = _peers.Keys.Append(_me).OrderBy(x => x).ToList();
        foreach (var kv in _peers) kv.Value.Avatar?.SetColor(_colors.TryGetValue(kv.Key, out var c) ? c : ids.IndexOf(kv.Key));
    }

    // ---- хост: книга цветов аптеки (colors.txt в её папке) ----
    Dictionary<ulong, int> _colorBook; string _colorBookRoom;
    string ColorBookPath => Path.Combine(Coop.RoomDir, "colors.txt");
    Dictionary<ulong, int> ColorBook()
    {
        if (_colorBook != null && _colorBookRoom == Coop.RoomDir) return _colorBook;
        _colorBook = new Dictionary<ulong, int>(); _colorBookRoom = Coop.RoomDir;
        try
        {
            if (File.Exists(ColorBookPath))
                foreach (var line in File.ReadAllLines(ColorBookPath))
                {
                    var pr = line.Split(':');
                    if (pr.Length == 2 && ulong.TryParse(pr[0], out var id) && int.TryParse(pr[1], out var c) && c >= 0 && c < 12) _colorBook[id] = c;
                }
        }
        catch (Exception e) { Plugin.L.LogWarning("COLORS: " + e.Message); }
        return _colorBook;
    }
    void AssignColor(ulong id, int want, string who)
    {
        var book = ColorBook();
        bool Taken(int c) => book.Any(kv => kv.Key != id && kv.Value == c);
        int before = book.TryGetValue(id, out var had) ? had : -1;
        if (want >= 0 && want < 12 && !Taken(want)) book[id] = want;
        else if (before < 0 || Taken(before))
            for (int k = 0; k < 12; k++) if (!Taken(k)) { book[id] = k; break; }
        if (book.TryGetValue(id, out var now) && now != before)
        {
            try { Directory.CreateDirectory(Coop.RoomDir); File.WriteAllLines(ColorBookPath, book.Select(kv => kv.Key + ":" + kv.Value)); } catch { }
            Plugin.L.LogInfo($"COLORS: {who} — {Avatar.ColorNames[now]}" + (want >= 0 && want != now ? $" (хотел {Avatar.ColorNames[want % 12]}, занят)" : ""));
        }
    }
    void BroadcastColors()
    {
        if (Coop.Role != Role.Host) return;
        var book = ColorBook();
        var sb = new StringBuilder();
        foreach (var id in _peers.Keys.Append(_me)) if (book.TryGetValue(id, out var c)) sb.Append(id).Append(':').Append(c).Append('\n');
        var bytes = Encoding.UTF8.GetBytes(sb.ToString());
        foreach (var id in _peers.Keys) Send(id, MsgColors, bytes, SendReliable);
        _colors.Clear(); foreach (var kv in book) _colors[kv.Key] = kv.Value;
        RecolorAll();
    }
    void OnColors(string packed)
    {
        _colors.Clear();
        foreach (var line in packed.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var pr = line.Split(':');
            if (pr.Length == 2 && ulong.TryParse(pr[0], out var id) && int.TryParse(pr[1], out var c)) _colors[id] = c;
        }
        RecolorAll();
        if (_colors.TryGetValue(_me, out var mine) && Plugin.AvatarColor.Value >= 0 && mine != Plugin.AvatarColor.Value)
            Coop.Say(Loc.L($"Цвет «{Avatar.ColorName(Plugin.AvatarColor.Value)}» занят — ты {Avatar.ColorName(mine)}", $"Colour {Avatar.ColorName(Plugin.AvatarColor.Value)} is taken — you are {Avatar.ColorName(mine)}"), 4f);
    }

    // ---- «фартук на крючке»: хост отдаёт вернувшемуся его склянки ----
    readonly Dictionary<ulong, float> _hookAt = new();
    void TickHooks()
    {
        if (_hookAt.Count == 0) return;
        foreach (var kv in _hookAt.ToList())
        {
            if (Time.unscaledTime < kv.Value) continue;
            _hookAt.Remove(kv.Key);
            if (Coop.Role != Role.Host || !_peers.ContainsKey(kv.Key)) continue;
            var l = Sync.HookOf(kv.Key);
            if (l.Count == 0) continue;
            var b = new byte[2 + 4 * l.Count];
            Buffer.BlockCopy(BitConverter.GetBytes((ushort)l.Count), 0, b, 0, 2);
            for (int i = 0; i < l.Count; i++) Buffer.BlockCopy(BitConverter.GetBytes(l[i]), 0, b, 2 + 4 * i, 4);
            Send(kv.Key, MsgHook, b, SendReliable);
            Plugin.L.LogInfo($"HOOK: {_peers[kv.Key].Name} вернулся — отдаю ему {l.Count} склянок с крючка");
        }
    }

    // ---------- комната ----------

    // «аптека открыта/закрыта» + её номер и название — гость видит, куда заходит
    void SendRoom(ulong to)
    {
        bool hosting = Coop.Role == Role.Host;
        var tail = hosting ? Encoding.UTF8.GetBytes(Coop.RoomId + "\t" + Coop.RoomName) : Array.Empty<byte>();
        var b = new byte[1 + tail.Length]; b[0] = (byte)(hosting ? 1 : 0); Buffer.BlockCopy(tail, 0, b, 1, tail.Length);
        Send(to, MsgRoom, b, SendReliable);
    }
    void BroadcastRoom() { foreach (var id in _peers.Keys) SendRoom(id); }

    void CloseSession(ulong id)
    {
        try { var ident = new SteamNetworkingIdentity(); ident.SetSteamID64(id); SteamNetworkingMessages.CloseSessionWithUser(ref ident); } catch { }
    }

    // Хост ушёл — гостя возвращаем в главное меню
    void HostGone(string why)
    {
        Coop.Role = Role.Solo;
        Coop.Say(Loc.L($"Общая аптека закрыта: {why}. Ты в главном меню. «Продолжить» вернёт тебя в твою игру.", $"The shared pharmacy is closed ({why}). You are in the main menu; Continue takes you back to your own game."), 10f);
        if (!Coop.InLobby && Coop.Machine != null) Coop.Machine.Enter(eGameState.LoadLobby);
    }

    // Смена состояния игры (зовётся из патча машины состояний)
    public void OnGameState(eGameState id)
    {
        if (id != eGameState.LoadLobby && id != eGameState.Lobby) return;
        if (Coop.Role == Role.Host)
        {
            Coop.Role = Role.Solo;
            BroadcastRoom();                       // «аптека закрыта» всем гостям
            Plugin.L.LogInfo("COOP: вышел в меню — аптека закрыта");
        }
        else if (Coop.Role == Role.Guest && !Coop.AutoJoin)
        {
            try { Sync.AnnounceLeft(); } catch { }
            Coop.Role = Role.Solo;
            Coop.Say(Loc.L("Ты вышел из общей аптеки. «Продолжить» вернёт тебя в твою игру (с перезапуском).", "You left the shared pharmacy. Continue takes you back to your own game (with a restart)."), 8f);
        }
    }

    void OnApplicationQuit()
    {
        if (Coop.Role == Role.Host) { Coop.Role = Role.Solo; BroadcastRoom(); }
        else if (Coop.Role == Role.Guest) try { Sync.AnnounceLeft(); } catch { }
    }

    bool _roomPicker, _typingRoom; string _newRoomName = "";
    void BecomeHost()
    {
        _newRoomName = "Аптека " + (Coop.ListRooms().Length + 1);
        if (MenuButtons.Ok) { OpenCoopScreen(); return; }
        _roomPicker = true; _typingRoom = false; _guardFrame = Time.frameCount + 1;   // запасной путь — старое окно
    }

    // Экран «Общие аптеки» из копий родных кнопок меню
    string HostsKey() => string.Join(",", _peers.Where(kv => kv.Value.Hosting).Select(kv => kv.Key + ":" + kv.Value.RoomName));
    void OpenCoopScreen()
    {
        var list = new List<MenuButtons.Entry>();
        foreach (var kv in _peers.Where(kv => kv.Value.Hosting).ToList())
        {
            var id = kv.Key;
            var room = kv.Value.RoomName;
            list.Add(new MenuButtons.Entry { Label = room != "" ? $"Зайти: {kv.Value.Name} · «{room}»" : $"Зайти в аптеку {kv.Value.Name}", Click = () => AskRestart(Loc.L($"Игра перезапустится. Зайти к {kv.Value.Name}?", $"The game will restart. Join {kv.Value.Name}?"), () => { MenuButtons.CloseScreen(); JoinHost(id); }, OpenCoopScreen) });
        }
        foreach (var room in Coop.ListRooms().Take(4))
        {
            var r = room;
            list.Add(new MenuButtons.Entry { Label = $"Открыть: «{r}»", Click = () => AskRestart(Loc.L($"Игра перезапустится. Открыть «{r}»?", $"The game will restart. Open \"{r}\"?"), () => { MenuButtons.CloseScreen(); Coop.RelaunchAsHost(r); }, OpenCoopScreen) });
        }
        list.Add(new MenuButtons.Entry { Label = NameLabel(), IsName = true, Click = () => CreateRoom(Coop.ListRooms(), false) });
        list.Add(new MenuButtons.Entry { Label = "+ Новая аптека из моего сейва", Click = () => CreateRoom(Coop.ListRooms(), true) });
        // цвет и удаление — на отдельном экране «Ещё…»: иначе кнопки уезжают за край экрана
        list.Add(new MenuButtons.Entry { Label = "Ещё: цвет, удаление аптек…", Click = OpenMoreScreen });
        list.Add(new MenuButtons.Entry { Label = "Назад", Click = () => MenuButtons.CloseScreen() });
        MenuButtons.OpenScreen(list, HostsKey());
        _typingRoom = true;   // на этом экране буквы сразу идут в название новой аптеки
    }

    // «Игра перезапустится. Продолжить?» — Да / Отмена. Без родных кнопок меню — сразу делаем
    public void AskRestart(string question, Action yes, Action no)
    {
        if (!MenuButtons.Ok) { yes(); return; }
        var list = new List<MenuButtons.Entry>
        {
            new MenuButtons.Entry { Label = question, Click = () => { } },
            new MenuButtons.Entry { Label = Loc.L("Да, перезапустить", "Yes, restart"), Click = yes },
            new MenuButtons.Entry { Label = Loc.L("Отмена", "Cancel"), Click = no ?? (() => MenuButtons.CloseScreen()) },
        };
        MenuButtons.OpenScreen(list, HostsKey());
        _typingRoom = false;
    }

    void OpenMoreScreen()
    {
        var list = new List<MenuButtons.Entry> { new MenuButtons.Entry { Label = ColorLabel(), Click = () => { NextColorQuiet(); OpenMoreScreen(); } } };
        if (Coop.ListRooms().Length > 0) list.Add(new MenuButtons.Entry { Label = "Удалить аптеку…", Click = OpenDeleteScreen });
        list.Add(new MenuButtons.Entry { Label = "Назад", Click = OpenCoopScreen });
        MenuButtons.OpenScreen(list, HostsKey());
        _typingRoom = false;
    }
    void NextColorQuiet()
    {
        int c = Plugin.AvatarColor.Value + 1; if (c > 11) c = -1;
        Plugin.AvatarColor.Value = c;
        foreach (var id in _peers.Keys) Send(id, MsgHello, HelloBytes, SendReliable);   // хост узнает и перераздаст
    }

    // ---- удаление аптеки: список → подтверждение ----
    void OpenDeleteScreen()
    {
        var list = new List<MenuButtons.Entry>();
        foreach (var room in Coop.ListRooms().Take(6))
        {
            var r = room;
            list.Add(new MenuButtons.Entry { Label = $"Удалить: «{r}»", Click = () => OpenConfirmDelete(r) });
        }
        list.Add(new MenuButtons.Entry { Label = "Назад", Click = OpenMoreScreen });
        MenuButtons.OpenScreen(list, HostsKey());
        _typingRoom = false;
    }
    void OpenDeleteScreenOrBack() { if (Coop.ListRooms().Length > 0) OpenDeleteScreen(); else OpenMoreScreen(); }
    void OpenConfirmDelete(string room)
    {
        var list = new List<MenuButtons.Entry>
        {
            new MenuButtons.Entry { Label = $"Точно удалить «{room}»?", Click = () => { } },
            new MenuButtons.Entry { Label = "Да, удалить", Click = () => { Coop.DeleteRoom(room); OpenDeleteScreenOrBack(); } },
            new MenuButtons.Entry { Label = "Отмена", Click = OpenDeleteScreen },
        };
        MenuButtons.OpenScreen(list, HostsKey());
        _typingRoom = false;
    }
    string ColorLabel() => $"Мой цвет: {Avatar.ColorName(Plugin.AvatarColor.Value)}";
    void NextColor()
    {
        int c = Plugin.AvatarColor.Value + 1; if (c > 11) c = -1;
        Plugin.AvatarColor.Value = c;
        foreach (var id in _peers.Keys) Send(id, MsgHello, HelloBytes, SendReliable);   // хост узнает и перераздаст
        OpenCoopScreen();
    }
    string NameLabel() => $"+ Новая аптека: «{_newRoomName}{((Time.unscaledTime * 2f % 2f) < 1f ? "|" : " ")}»";
    void TickCoopScreen(Keyboard kb)
    {
        if (!MenuButtons.ScreenOpen) return;
        if (!Coop.InLobby) { MenuButtons.CloseScreen(); _typingRoom = false; return; }
        bool main = MenuButtons.HasNameButton;   // главный экран (с полем названия), а не удаление/подтверждение
        _typingRoom = main;
        if (kb != null && kb.escapeKey.wasPressedThisFrame) { if (main) { MenuButtons.CloseScreen(); _typingRoom = false; } else OpenCoopScreen(); return; }
        if (!main) return;
        MenuButtons.SetNameLabel(NameLabel());
        if (MenuButtons.HostsChanged(HostsKey())) OpenCoopScreen();   // друг открыл/закрыл аптеку — обновляем список
    }

    void JoinHost(ulong hostId)
    {
        Coop.Role = Role.Guest;
        Coop.HostId = hostId; Coop.HostName = _peers[hostId].Name;
        _joinStatus = $"Получаю аптеку {Coop.HostName}…";
        _joinRequestedAt = Time.unscaledTime;
        Send(hostId, MsgSaveReq, Array.Empty<byte>(), SendReliable);
        Plugin.L.LogInfo($"COOP: прошусь в аптеку {Coop.HostName}");
    }

    // После перезапуска хостом своей аптеки: в меню сами жмём «Продолжить» (или «Новая игра» для новой аптеки)
    void TickAutoHost()
    {
        if (!Coop.AutoHost || !Coop.InLobby || Coop.Machine == null) return;
        if (Coop.AutoHostSince == 0) { Coop.AutoHostSince = Time.unscaledTime; Coop.Say(Loc.L($"Открываю аптеку «{Coop.RoomName}»…", $"Opening pharmacy \"{Coop.RoomName}\"…"), 10f); return; }
        if (Time.unscaledTime - Coop.AutoHostSince < 1.5f) return;   // даём меню собраться
        Coop.AutoHost = false;
        Coop.Role = Role.Host; Coop.HostId = _me; Coop.HostName = SteamFriends.GetPersonaName();
        BroadcastRoom();
        AssignColor(_me, Plugin.AvatarColor.Value, "я");
        foreach (var kv in _peers) AssignColor(kv.Key, kv.Value.WantColor, kv.Value.Name);
        BroadcastColors();
        // новая аптека (метка new.flag) → «Новая игра»; иначе «Продолжить»
        bool fresh = Coop.TakeFresh();   // метка ставится при «+ Новая аптека»
        if (!fresh) Coop.PressContinue();
        else { Plugin.L.LogInfo($"COOP: новая аптека «{Coop.RoomName}» — начинаю новую игру"); Coop.Machine.Enter(eGameState.NewGame); }
    }

    // После перезапуска «в личную игру»: сами жмём «Продолжить»
    void TickAutoContinue()
    {
        if (!Coop.AutoContinue || !Coop.InLobby || Coop.Machine == null) return;
        if (Coop.AutoContinueSince == 0) { Coop.AutoContinueSince = Time.unscaledTime; Coop.Say(Loc.L("Открываю твою личную игру…", "Opening your own game…"), 5f); return; }
        if (Time.unscaledTime - Coop.AutoContinueSince < 1.5f) return;
        Coop.AutoContinue = false;
        Coop.PressContinue();
    }

    // После перезапуска в гости: ждём хоста и сами жмём «Продолжить»
    void TickAutoJoin()
    {
        if (!Coop.AutoJoin || !Coop.InLobby) return;
        if (Coop.AutoJoinSince == 0) { Coop.AutoJoinSince = Time.unscaledTime; Coop.Say(Loc.L($"Захожу в аптеку {Coop.HostName}…", $"Joining {Coop.HostName}'s pharmacy…"), 30f); }
        if (_peers.TryGetValue(Coop.HostId, out var h) && h.Hosting && (Coop.ContinueStarter != null || Coop.Machine != null))
        {
            Coop.AutoJoin = false; Coop.Banner = null;
            Coop.PressContinue();
            return;
        }
        if (Time.unscaledTime - Coop.AutoJoinSince > 45f)
        {
            Coop.AutoJoin = false; Coop.Role = Role.Solo;
            Coop.Say(Loc.L($"{Coop.HostName} не открыл аптеку. Перезапусти игру, чтобы играть со своим сейвом.", $"{Coop.HostName} has not opened a pharmacy. Restart the game to play your own save."), 12f);
        }
    }

    // опыт: гость → хосту прирост, хост → всем итог
    void SendXp(bool toHost, int value)
    {
        var b = BitConverter.GetBytes(value);
        if (toHost) { if (_peers.ContainsKey(Coop.HostId)) Send(Coop.HostId, MsgXp, b, SendReliable); }
        else foreach (var id in _peers.Keys) Send(id, MsgXpTotal, b, SendReliable);
    }

    // ---------- модификаторы ----------

    // хост насылает модификатор на игрока (или на себя)
    void SendMod(byte mod, ulong target, bool on)
    {
        var b = new byte[10]; b[0] = mod; b[1] = (byte)(on ? 1 : 0); Buffer.BlockCopy(BitConverter.GetBytes(target), 0, b, 2, 8);
        if (mod == Modifiers.Fatigue)
        {
            if (target == _me) { Modifiers.SetTired(on, "хост"); return; }
            foreach (var id in _peers.Keys) Send(id, MsgMod, b, SendReliable);
            if (_peers.TryGetValue(target, out var tp)) tp.Tired = on;
            return;
        }
        foreach (var id in _peers.Keys) Send(id, MsgMod, b, SendReliable);
        ApplyMod(mod, target, "хост");   // у себя тоже
        if (mod == Modifiers.ClearAll) foreach (var p in _peers.Values) p.Tired = false;
    }

    float _shakeUntil;
    // Дырявый карман и землетрясение — что происходит у каждого игрока
    void ApplyMod(byte mod, ulong target, string from)
    {
        if (mod == Modifiers.ClearAll)
        {   // всё снять: усталость, духа, тряску, «следующий бросок разобьёт»
            Modifiers.SetTired(false, "хост выключил модификаторы");
            ColorCurse.Set(false, "хост выключил модификаторы");
            _shakeUntil = 0; Sync.ForceBreakNext = false;
            foreach (var p in _peers.Values) p.Tired = false;
            if (Coop.Role != Role.Host) Coop.Say(Loc.L("Хост выключил модификаторы", "The host turned modifiers off"), 4f);
            return;
        }
        if (mod == Modifiers.Leaky)
        {
            if (target == _me)
            {
                int n = Sync.LeakyPocket();
                Coop.Say(n > 0 ? Loc.L($"Дырявый карман! Ты выронил зелий: {n}", $"Leaky pocket! You dropped {n} potion(s)") : Loc.L("Дырявый карман! Но в руках было пусто — повезло", "Leaky pocket! Lucky you, your hands were empty"), 4f);
                LocalEmoteFlash(3);
            }
            else if (_peers.TryGetValue(target, out var tp)) { Coop.Say(Loc.L($"У {tp.Name} дырявый карман!", $"{tp.Name} has a leaky pocket!"), 3f); tp.Avatar?.ShowEmote(3); }
        }
        else if (mod == Modifiers.Blind)
        {
            if (target == _me) ColorCurse.Set(true, from);
            else if (_peers.TryGetValue(target, out var tp)) { Coop.Say(Loc.L($"На {tp.Name} напал Дальтоник-дух!", $"The colour-blind spirit got {tp.Name}!"), 3f); tp.Avatar?.ShowEmote(3); }
        }
        else if (mod == Modifiers.Quake)
        {
            _shakeUntil = Time.unscaledTime + 3f;
            Sync.ClearUndo();
            int n = Sync.HostQuake(30);            // двигает зелья только хост, остальным прилетят их полёты
            Coop.Say(Loc.L("Землетрясение!" + (n > 0 ? $" Разлетелось зелий: {n}" : ""), "Earthquake!" + (n > 0 ? $" {n} potion(s) went flying" : "")), 4f);
        }
    }

    // тряска камеры поверх движения игры
    void LateUpdate()
    {
        float left = _shakeUntil - Time.unscaledTime;
        if (left <= 0) return;
        var cam = Camera.main; if (cam == null) return;
        float a = Mathf.Clamp01(left / 3f) * 0.06f;
        cam.transform.position += new Vector3(UnityEngine.Random.Range(-a, a), UnityEngine.Random.Range(-a, a), UnityEngine.Random.Range(-a, a));
    }

    // моё состояние поменялось — сообщаем всем (чтобы над моим аптекарем висело Zz)
    public void OnLocalModChanged(byte mod, bool on)
    {
        var b = new byte[10]; b[0] = mod; b[1] = (byte)(on ? 1 : 0); Buffer.BlockCopy(BitConverter.GetBytes(_me), 0, b, 2, 8);
        foreach (var id in _peers.Keys) Send(id, MsgMod, b, SendReliable);
        if (on) { _selfEmote = 5; _selfEmoteAt = Time.unscaledTime; }
        if (_dummy != null && on) _dummyEmotes.Enqueue((Time.unscaledTime, 0, 5));
    }

    float _nextTiredBubble;
    void TickTiredBubbles()
    {
        if (Time.unscaledTime < _nextTiredBubble) return;
        _nextTiredBubble = Time.unscaledTime + 3f;
        foreach (var p in _peers.Values) if (p.Tired) p.Avatar?.ShowEmote(5);
        if (Modifiers.Tired) { _selfEmote = 5; _selfEmoteAt = Time.unscaledTime; }
    }

    // ---------- эмоции ----------

    static void ApplyEmote(Avatar a, int kind, int id)
    {
        if (kind == 0) a.ShowEmote(id);
        else if (id >= 0 && id < Avatar.Actions.Length)
        {
            a.PlayAction(Avatar.Actions[id]);
            if (ActionEmote[id] >= 0) a.ShowEmote(ActionEmote[id]);
        }
    }

    void DoEmote(int kind, int id)
    {
        foreach (var pid in _peers.Keys) Send(pid, MsgEmote, new[] { (byte)kind, (byte)id }, SendReliable);
        _selfEmote = kind == 0 ? id : ActionEmote[id]; _selfEmoteAt = Time.unscaledTime;
        if (_dummy != null) _dummyEmotes.Enqueue((Time.unscaledTime, kind, id));
        Plugin.L.LogInfo($"EMOTE: {(kind == 0 ? EmoSym[id] : Avatar.Actions[id])}");
    }

    // Колесо: зажал T, повёл мышью в сторону, отпустил
    void TickWheel()
    {
        var kb = Keyboard.current; var mouse = Mouse.current;
        if (kb == null) return;
        // колесо — только в лаборатории и не когда печатаешь название аптеки
        if (kb.tKey.wasPressedThisFrame && Camera.main != null && Coop.State == eGameState.Playing && !_typingRoom && !MenuButtons.ScreenOpen && !Letter.Open)
        {
            _wheelOpen = true; _wheelVec = Vector2.zero; _wheelSel = -1;
            _look = UnityEngine.Object.FindFirstObjectByType<CodeBase.Components.Hero.HeroLook>();
            if (_look != null) _look.enabled = false;
        }
        if (!_wheelOpen) return;
        if (mouse != null) _wheelVec += mouse.delta.ReadValue();
        if (_wheelVec.magnitude > 40f)
        {
            float ang = Mathf.Atan2(_wheelVec.x, _wheelVec.y) * Mathf.Rad2Deg; if (ang < 0) ang += 360f;
            _wheelSel = Mathf.RoundToInt(ang / WheelStep) % WheelN;
            if (_wheelVec.magnitude > 300f) _wheelVec = _wheelVec.normalized * 300f;
        }
        if (kb.tKey.wasReleasedThisFrame)
        {
            _wheelOpen = false;
            if (_look != null) { _look.enabled = true; _look = null; }
            if (_wheelSel == 12) Letter.Toggle();
            else if (_wheelSel >= 0) DoEmote(_wheelSel < 8 ? 0 : 1, _wheelSel < 8 ? _wheelSel : _wheelSel - 8);
        }
    }

    // ---------- манекен для тестов в одиночку (F9) ----------

    // F9 по кругу: стоит перед тобой → ходит следом → убран
    void ToggleDummy()
    {
        var cam = Camera.main;
        if (_dummy == null)
        {
            if (cam == null) return;
            _dummy = new Avatar("Манекен");
            _dummy.SetColor(5);
            _dummyFollows = false;
            var fwd = cam.transform.forward; fwd.y = 0; fwd.Normalize();
            _dummyEye = cam.transform.position + fwd * 2.5f;
            _dummyYaw = Quaternion.LookRotation(-fwd).eulerAngles.y;   // лицом к тебе
            _dummy.SetState(_dummyEye, _dummyYaw, 0);
            Plugin.L.LogInfo("DUMMY: стоит перед тобой, повторяет твои действия через 3 с и крутит последнее по кругу. F9 — пусть ходит следом");
            return;
        }
        if (!_dummyFollows)
        {
            _dummyFollows = true; _dummyTrail.Clear();
            Plugin.L.LogInfo("DUMMY: ходит за тобой с задержкой 3 с. F9 — убрать");
            return;
        }
        _dummy.Destroy(); _dummy = null; _dummyTrail.Clear(); _dummyEmotes.Clear(); _dummyAnims.Clear();
        _dummyLastAnim = null; _dummyLastKind = -1; _dummyNextRepeat = 0;
        foreach (var h in _dummyHands) if (h.shadow != null) UnityEngine.Object.Destroy(h.shadow);
        foreach (var sh in _allShadows.Values) { Shadow.Forget(sh); if (sh != null) UnityEngine.Object.Destroy(sh); }
        _dummyHands.Clear(); _dummyShadows.Clear(); _dummyRows.Clear(); _allShadows.Clear(); _pendingColors.Clear(); _dummyPours.Clear();
        Plugin.L.LogInfo("DUMMY: убран");
    }

    void TickDummy(float dt)
    {
        if (_dummy == null) return;
        var cam = Camera.main;
        float Delay = _dummyDelay; const float RepeatEvery = 3f;
        var kb = Keyboard.current;
        if (kb != null)
        {
            float old = _dummyDelay;
            if (!_typingRoom && (kb.equalsKey.wasPressedThisFrame || kb.numpadPlusKey.wasPressedThisFrame)) _dummyDelay = Mathf.Min(15f, _dummyDelay + 1f);
            if (!_typingRoom && (kb.minusKey.wasPressedThisFrame || kb.numpadMinusKey.wasPressedThisFrame)) _dummyDelay = Mathf.Max(1f, _dummyDelay - 1f);
            if (old != _dummyDelay) Plugin.L.LogInfo($"DUMMY: задержка {_dummyDelay:0} с");
        }
        if (_dummyFollows)
        {
            if (cam != null)
            {
                var e = cam.transform.rotation.eulerAngles;
                _dummyTrail.Enqueue((Time.unscaledTime, cam.transform.position, e.y, e.x));
            }
            while (_dummyTrail.Count > 0 && Time.unscaledTime - _dummyTrail.Peek().t >= Delay)
            {
                var s = _dummyTrail.Dequeue();
                _dummy.SetState(s.p, s.yaw, s.pitch);
            }
        }
        else _dummy.SetState(_dummyEye, _dummyYaw, 0); // стоит на месте
        float now = Time.unscaledTime;
        while (_dummyHands.Count > 0 && now - _dummyHands.Peek().t >= Delay)
        {
            var h = _dummyHands.Dequeue();
            if (h.take && h.shadow != null)
            {
                if (_dummyShadows.TryGetValue(h.uid, out var old) && old != null) { _dummy.Unhold(old.transform); UnityEngine.Object.Destroy(old); }
                _dummyShadows[h.uid] = h.shadow;
                h.shadow.SetActive(true);
                _dummy.Hold(h.shadow.transform);
            }
            else if (!h.take && _dummyShadows.TryGetValue(h.uid, out var sh))
            {
                _dummyShadows.Remove(h.uid);
                if (sh != null) { _dummy.Unhold(sh.transform); Shadow.Forget(sh); _allShadows.Remove(h.uid); UnityEngine.Object.Destroy(sh); }
            }
        }
        while (_dummyRows.Count > 0 && now - _dummyRows.Peek().t >= Delay)
        {
            var rw = _dummyRows.Dequeue(); var list = new List<Transform>();
            foreach (var uid in rw.row) if (_dummyShadows.TryGetValue(uid, out var sh) && sh != null) list.Add(sh.transform);
            _dummy.SetRow(list, rw.active);
        }
        while (_dummyPours.Count > 0 && now - _dummyPours.Peek().t >= Delay)
        {
            var pr = _dummyPours.Dequeue();
            _dummyShadows.TryGetValue(pr.giver, out var gs); _dummyShadows.TryGetValue(pr.taker, out var ts);
            _dummy.PlayPour(gs != null ? gs.transform : null, ts != null ? ts.transform : null);
            _dummyLastAnim = "pour"; _dummyNextRepeat = now + 4f;
        }
        for (int i = _pendingColors.Count - 1; i >= 0; i--)
        {
            var pc = _pendingColors[i];
            if (pc.Snap == null && now >= pc.CaptureAt) pc.Snap = Shadow.Snapshot(pc.Shadow);
            if (pc.Snap != null && now >= Mathf.Max(pc.ApplyAt, pc.CaptureAt)) { Shadow.Apply(pc.Shadow, pc.Snap); _pendingColors.RemoveAt(i); }
        }
        while (_dummyAnims.Count > 0 && now - _dummyAnims.Peek().t >= Delay)
        {
            _dummyLastAnim = _dummyAnims.Dequeue().anim; _dummyLastKind = -1;
            _dummy.PlayAction(_dummyLastAnim); _dummyNextRepeat = now + Mathf.Max(RepeatEvery, Avatar.Len(_dummyLastAnim) + 1f);
        }
        while (_dummyEmotes.Count > 0 && now - _dummyEmotes.Peek().t >= Delay)
        {
            var m = _dummyEmotes.Dequeue();
            _dummyLastKind = m.kind; _dummyLastId = m.id; _dummyLastAnim = null;
            ApplyEmote(_dummy, m.kind, m.id); _dummyNextRepeat = now + Mathf.Max(RepeatEvery, (m.kind == 1 ? Avatar.Len(Avatar.Actions[m.id]) : 0f) + 1f);
        }
        // повтор последнего действия каждые 3 секунды
        if (_dummyNextRepeat > 0 && now >= _dummyNextRepeat)
        {
            if (_dummyLastAnim != null) _dummy.PlayAction(_dummyLastAnim);
            else if (_dummyLastKind >= 0) ApplyEmote(_dummy, _dummyLastKind, _dummyLastId);
            string a = _dummyLastAnim ?? (_dummyLastKind == 1 ? Avatar.Actions[_dummyLastId] : null);
            _dummyNextRepeat = now + Mathf.Max(RepeatEvery, Avatar.Len(a) + 1f);
        }
        _dummy.Tick(dt);
    }

    // ---------- пинг ----------

    void PingAll()
    {
        if (_peers.Count == 0) { Plugin.L.LogInfo("NET: пинговать некого — никого нет на связи"); return; }
        var ticks = BitConverter.GetBytes(DateTime.UtcNow.Ticks);
        foreach (var id in _peers.Keys) Send(id, MsgPing, ticks, SendReliable);
        Plugin.L.LogInfo($"NET: пинг отправлен {_peers.Count} игрокам");
    }

    // ---------- отправка / приём ----------

    void Send(ulong to, byte type, byte[] payload, int flags)
    {
        var bytes = new byte[1 + payload.Length];
        bytes[0] = type;
        Buffer.BlockCopy(payload, 0, bytes, 1, payload.Length);

        var id = new SteamNetworkingIdentity();
        id.SetSteamID64(to);

        IntPtr buf = Marshal.AllocHGlobal(bytes.Length);
        try
        {
            Marshal.Copy(bytes, 0, buf, bytes.Length);
            var r = SteamNetworkingMessages.SendMessageToUser(ref id, buf, (uint)bytes.Length, flags, Channel);
            if (r != EResult.k_EResultOK && r != EResult.k_EResultNoConnection)
            {
                // сломанная сессия Steam сама не чинится — сбрасываем её, следующий «привет» откроет новую
                _resetAt.TryGetValue(to, out var next);
                if (Time.unscaledTime >= next)
                {
                    _resetAt[to] = Time.unscaledTime + 15f;
                    CloseSession(to);
                    Plugin.L.LogWarning($"NET: отправка {Name(to)} не удалась: {r} — пересоздаю соединение");
                }
            }
        }
        finally { Marshal.FreeHGlobal(buf); }
    }

    readonly Dictionary<ulong, float> _resetAt = new();
    public void BroadcastSteals(string packed) { var b = Encoding.UTF8.GetBytes(packed); foreach (var id in _peers.Keys) Send(id, MsgSteals, b, SendReliable); }
    public void BroadcastScored(byte kind, int id)
    {
        var b = new byte[5]; b[0] = kind; Buffer.BlockCopy(BitConverter.GetBytes(id), 0, b, 1, 4);
        foreach (var pid in _peers.Keys) Send(pid, MsgScored, b, SendReliable);
    }
    void SendMark()
    {
        _selfMarkAt = Time.unscaledTime;
        foreach (var id in _peers.Keys) Send(id, MsgMark, Array.Empty<byte>(), SendReliable);
        Coop.Say(Loc.L("Ты позвал остальных к себе", "You called the others over"), 2.5f);
    }

    void Receive()
    {
        for (int guard = 0; guard < 8; guard++)
        {
            int n = SteamNetworkingMessages.ReceiveMessagesOnChannel(Channel, _inbox, _inbox.Length);
            if (n <= 0) return;
            for (int i = 0; i < n; i++)
            {
                IntPtr m = _inbox[i];
                try
                {
                    // Раскладка SteamNetworkingMessage_t: m_pData @0, m_cbSize @8, m_identityPeer @16 (steamID64 @ +8)
                    IntPtr data = Marshal.ReadIntPtr(m, 0);
                    int size = Marshal.ReadInt32(m, 8);
                    ulong from = (ulong)Marshal.ReadInt64(m, 24);
                    var bytes = new byte[size];
                    if (size > 0) Marshal.Copy(data, bytes, 0, size);
                    Handle(from, bytes);
                }
                finally { SteamNetworkingMessage_t.Release(m); }
            }
        }
    }

    void Handle(ulong from, byte[] b)
    {
        if (b.Length == 0) return;
        _peers.TryGetValue(from, out var p);
        if (p != null) p.LastSeen = Time.unscaledTime;

        switch (b[0])
        {
            case MsgHello:
            case MsgHelloAck:
            {
                var raw = Encoding.UTF8.GetString(b, 1, b.Length - 1);
                var parts = raw.Split('|');
                var proto = parts[0]; var launch = parts.Length > 1 ? parts[1] : "";
                int want = parts.Length > 2 && int.TryParse(parts[2], out var wc) ? wc : -1;
                if (p != null && p.Launch != null && launch != "" && p.Launch != launch)
                {
                    // другая метка запуска — человек перезапустил игру: забываем старое состояние (сессию Steam НЕ рвём)
                    Plugin.L.LogInfo($"NET: {p.Name} перезапустил игру — начинаю с чистого листа");
                    try { Sync.DropAllHeldBy(p.Avatar, from, null); } catch { }
                    p.Avatar?.Destroy(); _peers.Remove(from);
                    if (Coop.Role == Role.Guest && Coop.HostId == from && !Coop.AutoJoin) HostGone($"{p.Name} перезапустил игру");
                    p = null;
                }
                bool isNew = p == null;
                if (p == null)
                {
                    p = new Peer { Name = Name(from), LastSeen = Time.unscaledTime };
                    _peers[from] = p;
                    Plugin.L.LogInfo($"NET: >>> на связи {p.Name} ({proto}). Всего на связи: {_peers.Count}");
                    RecolorAll();
                }
                p.Proto = proto; p.Launch = launch;
                bool colorChanged = p.WantColor != want; p.WantColor = want;
                if (b[0] == MsgHello) Send(from, MsgHelloAck, HelloBytes, SendReliable);
                SendRoom(from);
                // личные навыки гостю — только при первом «привете» этого запуска (раньше слались на каждый переподключающийся привет и затирали перки)
                if (Coop.Role == Role.Host && isNew) Send(from, MsgSkills, Skills.Pack(Skills.PersonalOf(from)), SendReliable);
                if (Coop.Role == Role.Host && (isNew || colorChanged)) { AssignColor(from, want, p.Name); BroadcastColors(); }
                if (proto != Proto)
                    Plugin.L.LogWarning($"NET: у {p.Name} другая версия мода ({proto}), у меня {Proto} — обновитесь до одной");
                break;
            }
            case MsgPing:
                Send(from, MsgPong, b.AsSpan(1).ToArray(), SendReliable);
                break;
            case MsgPong:
            {
                long sent = BitConverter.ToInt64(b, 1);
                var ms = (int)((DateTime.UtcNow.Ticks - sent) / TimeSpan.TicksPerMillisecond);
                if (p != null) p.PingMs = ms;
                Plugin.L.LogInfo($"NET: пинг до {Name(from)}: {ms} мс");
                break;
            }
            case MsgState:
                if (p != null && p.Proto == Proto) OnState(p, b);
                break;
            case MsgMod:
                if (p == null || b.Length < 11) break;
                // наслать модификатор может только хост; про себя (усталость) — сам игрок
                if (!(Coop.Role == Role.Guest && Coop.HostId == from) && !(b[1] == Modifiers.Fatigue && BitConverter.ToUInt64(b, 3) == from)) break;
                {
                    byte mod = b[1]; bool on = b[2] == 1; ulong target = BitConverter.ToUInt64(b, 3);
                    if (mod == Modifiers.Fatigue)
                    {
                        if (target == _me) Modifiers.SetTired(on, $"наслал {p.Name}");
                        else if (_peers.TryGetValue(target, out var tp)) tp.Tired = on;
                    }
                    else ApplyMod(mod, target, p.Name);
                }
                break;
            case MsgSkills:       // хост прислал мои личные навыки в этой аптеке
                if (p != null && Coop.Role == Role.Guest && Coop.HostId == from) Skills.GuestReceivedPersonal(Skills.Unpack(b, 1));
                break;
            case MsgSkillsSet:    // гость поменял свои навыки — хост запоминает
                if (p != null && Coop.Role == Role.Host) Skills.SavePersonal(from, Skills.Unpack(b, 1), p.Name);
                break;
            case MsgWorldSkills:  // общие навыки мира от хоста
                if (p != null && Coop.Role == Role.Guest && Coop.HostId == from) { var w = Skills.Unpack(b, 1); Skills.RememberHostWorld(w); Skills.GuestReceivedWorld(w); }
                break;
            case MsgXp:       // гость заработал опыт → хосту
                if (p != null && Coop.Role == Role.Host) Plugin.L.LogInfo($"XP: {p.Name} прислал свой опыт — не беру, опыт считает только хост");
                break;
            case MsgXpTotal:  // хост прислал общий итог
                if (p != null && Coop.Role == Role.Guest && Coop.HostId == from && b.Length >= 5) Xp.SetTotal(BitConverter.ToInt32(b, 1));
                break;
            case MsgSteals:   // счёт краж от хоста
                if (p != null && Coop.Role == Role.Guest && Coop.HostId == from) Sync.SetSteals(Encoding.UTF8.GetString(b, 1, b.Length - 1));
                break;
            case MsgScored:   // кто-то получил опыт за полку/секцию — у всех она засчитана
                if (p != null && b.Length >= 6) Xp.MarkScored(b[1], BitConverter.ToInt32(b, 2));
                break;
            case MsgColors:   // хост раздал цвета
                if (p != null && Coop.HostId == from && Coop.Role == Role.Guest) OnColors(Encoding.UTF8.GetString(b, 1, b.Length - 1));
                break;
            case MsgHook:     // хост: вот твои склянки с крючка
                if (p != null && Coop.Role == Role.Guest && Coop.HostId == from && b.Length >= 3)
                {
                    int n = BitConverter.ToUInt16(b, 1); var l = new List<int>();
                    for (int i = 0; i < n && 3 + 4 * i + 4 <= b.Length; i++) l.Add(BitConverter.ToInt32(b, 3 + 4 * i));
                    Sync.ReturnHook(l);
                }
                break;
            case MsgMark:     // «я тут»
                if (p != null) { p.MarkAt = Time.unscaledTime; Coop.Say(Loc.L($"{p.Name} зовёт к себе", $"{p.Name} is calling you over"), 2.5f); }
                break;
            case MsgEmote:
                if (p?.Avatar != null && b.Length >= 3) ApplyEmote(p.Avatar, b[1], b[2]);
                break;
            case MsgMove:
                if (p != null && p.Proto == Proto && Coop.Role != Role.Solo) Sync.Apply(from, p.Avatar, b, 1);
                break;
            case MsgRoom:
                if (p != null && b.Length >= 2)
                {
                    bool was = p.Hosting; p.Hosting = b[1] == 1;
                    if (p.Hosting && b.Length > 2)
                    {
                        var info = Encoding.UTF8.GetString(b, 2, b.Length - 2).Split('\t');
                        p.RoomId = info[0]; p.RoomName = info.Length > 1 ? info[1] : "";
                        if (Coop.Role == Role.Guest && Coop.HostId == from && (Coop.RoomId != p.RoomId || Coop.RoomName != p.RoomName))
                        { Coop.RoomId = p.RoomId; Coop.RoomName = p.RoomName; Plugin.L.LogInfo($"COOP: аптека хоста — «{p.RoomName}» ({p.RoomId})"); }
                    }
                    else if (!p.Hosting) { p.RoomId = ""; p.RoomName = ""; }
                    if (was != p.Hosting) Plugin.L.LogInfo(p.Hosting ? $"COOP: {p.Name} открыл общую аптеку" : $"COOP: {p.Name} закрыл общую аптеку");
                    if (was && !p.Hosting && Coop.Role == Role.Guest && Coop.HostId == from && !Coop.AutoJoin) HostGone($"{p.Name} вышел");
                }
                break;
            case MsgSaveReq:
                if (p == null) break;
                if (Coop.Role != Role.Host) { Plugin.L.LogWarning($"COOP: {p.Name} просится в аптеку, но я не хост"); break; }
                Sync.FlushSave();   // последние ходы (в том числе гостей) — сначала на диск
                var pack = Coop.PackHostSave();
                if (pack == null) { Plugin.L.LogError("COOP: нечего отправить — сейв не найден"); break; }
                Send(from, MsgSave, pack, SendReliable);
                Plugin.L.LogInfo($"COOP: отправил сейв игроку {p.Name} ({pack.Length} байт)");
                break;
            case MsgSave:
                if (p == null || Coop.Role != Role.Guest || Coop.HostId != from) break;
                try
                {
                    Coop.UnpackToGuestDir(b, 1);
                    _joinStatus = $"Перезапускаю игру, чтобы зайти в аптеку {Coop.HostName}…";
                    Coop.Say(_joinStatus, 10f);
                    Coop.RelaunchAsGuest(from, Coop.HostName);
                }
                catch (Exception e) { _joinStatus = "Не удалось принять сейв: " + e.Message; Plugin.L.LogError("COOP: " + e); Coop.Role = Role.Solo; }
                break;
        }
    }

    // ---------- экран: ники над головами и строка статуса ----------

    void OnGUI()
    {
        if (!_ready) return;
        try
        {
            if (_tagStyle == null)
            {
                _tagStyle = new GUIStyle(GUI.skin.label) { alignment = TextAnchor.MiddleCenter, fontSize = 18, fontStyle = FontStyle.Bold };
                _hudStyle = new GUIStyle(GUI.skin.label) { fontSize = 14 };
                _btnStyle = new GUIStyle(GUI.skin.button) { fontSize = 20, fontStyle = FontStyle.Bold };
                _boxStyle = new GUIStyle(GUI.skin.box) { fontSize = 16 };
                _emoStyle = new GUIStyle(GUI.skin.label) { alignment = TextAnchor.MiddleCenter, fontSize = 40, fontStyle = FontStyle.Bold };
                var ink = new Color(0.23f, 0.16f, 0.12f, 1f);
                _bubble = Skin(Rounded(new Color(1f, 0.98f, 0.94f, 0.97f), ink), 22);
                _bubbleSel = Skin(Rounded(new Color(1f, 0.84f, 0.42f, 1f), ink), 22);
                _dot = Skin(Rounded(new Color(1f, 0.78f, 0.3f, 1f), ink, 32, 16, 3), 15);
                _banner = Skin(Rounded(new Color(0.12f, 0.08f, 0.06f, 0.85f), new Color(0.89f, 0.69f, 0.29f, 1f)), 22);
                _wheelStyle = new GUIStyle(GUI.skin.box) { alignment = TextAnchor.MiddleCenter, fontSize = 22, fontStyle = FontStyle.Bold, wordWrap = true };
            }
            // сбоку — только кто на связи и счёт краж (подсказки по клавишам — в письме из колеса эмоций)
            var lines = new List<string>();
            if (_peers.Count > 0) lines.Add(string.Join(", ", _peers.Values.Select(p => p.PingMs >= 0 ? $"{p.Name} ({p.PingMs} {Loc.L("мс", "ms")})" : p.Name)));
            if (_dummy != null) lines.Add(Loc.L($"Манекен: {(_dummyFollows ? "ходит следом" : "стоит")}, задержка {_dummyDelay:0} с (− / +, F9 дальше)", $"Dummy: {(_dummyFollows ? "following" : "standing")}, delay {_dummyDelay:0} s (− / +, F9 next)"));
            var steals = Sync.StealsLine(); if (steals != null) lines.Add(steals);
            if (lines.Count > 0) Shadowed(new Rect(12, 112, 900, 80), string.Join("\n", lines), _hudStyle, Color.white);  // ниже иконки SP игры
            if (Coop.InLobby && _roomPicker) RoomPicker();
            if (Coop.InLobby && !MenuButtons.Ok) LobbyPanel();   // запасная панель — только если родные кнопки не получились
            if (Coop.Banner != null && Time.unscaledTime < Coop.BannerUntil)
            {
                var r = new Rect(Screen.width / 2f - 360, Screen.height * 0.18f, 720, 64);
                GUI.Box(r, GUIContent.none, _banner);
                _hudStyle.alignment = TextAnchor.MiddleCenter; _hudStyle.fontSize = 17; _hudStyle.wordWrap = true;
                Shadowed(new Rect(r.x + 16, r.y, r.width - 32, r.height), Coop.Banner, _hudStyle, new Color(1f, 0.9f, 0.6f));
                _hudStyle.alignment = TextAnchor.UpperLeft; _hudStyle.fontSize = 14; _hudStyle.wordWrap = false;
            }
            if (Modifiers.Tired && !Coop.InLobby)
            {
                var r = new Rect(Screen.width / 2f - 260, 150, 520, 56);
                GUI.Box(r, GUIContent.none, _banner);
                _hudStyle.alignment = TextAnchor.MiddleCenter; _hudStyle.fontSize = 18;
                Shadowed(r, Loc.L("Переутомление! Найди кота и погладь его", "Exhausted! Find the cat and pet it"), _hudStyle, new Color(1f, 0.85f, 0.45f));
                _hudStyle.alignment = TextAnchor.UpperLeft; _hudStyle.fontSize = 14;
            }

            var cam = Camera.main;
            if (cam == null) return;
            var avatars = _peers.Values.Where(p => p.Avatar != null).Select(p => p.Avatar).ToList();
            if (_dummy != null) avatars.Add(_dummy);
            foreach (var av in avatars)
            {
                if (!av.Root.activeSelf) continue;
                var sp = cam.WorldToScreenPoint(av.HeadTop);
                if (sp.z <= 0) continue;
                float sx = sp.x, sy = Screen.height - sp.y;
                Shadowed(new Rect(sx - 120, sy - 14, 240, 28), av.Name, _tagStyle, av.CurrentColor);
                EmoteBubble(av.EmoteId, av.EmoteStart, sx, sy - 70);
            }
            foreach (var pr in _peers.Values) if (pr.Avatar != null && Time.unscaledTime - pr.MarkAt < 6f) DrawMark(cam, pr.Avatar, pr.MarkAt);
            if (Time.unscaledTime - _selfEmoteAt < 2.6f) EmoteBubble(_selfEmote, _selfEmoteAt, Screen.width / 2f, 110);
            if (_wheelOpen) DrawWheel();
            Letter.DrawFallback();
        }
        catch (Exception e) { Plugin.L.LogError("OnGUI: " + e.Message); }
    }

    // Метка «я тут»: пульсирует над аптекарем; если он за краем экрана — стрелка по краю с именем и расстоянием
    void DrawMark(Camera cam, Avatar av, float at)
    {
        float a = Time.unscaledTime - at;
        float fade = a > 5f ? Mathf.Clamp01(6f - a) : 1f;
        var world = av.HeadTop + Vector3.up * 0.6f;
        var sp = cam.WorldToScreenPoint(world);
        float dist = Vector3.Distance(cam.transform.position, av.Root.transform.position);
        bool behind = sp.z <= 0;
        float x = sp.x, y = Screen.height - sp.y;
        if (behind) { x = Screen.width - x; y = Screen.height - y; }
        const float m = 60f;
        bool off = behind || x < m || x > Screen.width - m || y < m || y > Screen.height - m;
        var old = GUI.color;
        var c = av.CurrentColor; c.a = fade;
        string label;
        if (off)
        {
            var center = new Vector2(Screen.width / 2f, Screen.height / 2f);
            var dir = new Vector2(x, y) - center; if (dir.sqrMagnitude < 1f) dir = Vector2.up * -1f;
            dir.Normalize();
            float k = Mathf.Min((Screen.width / 2f - m) / Mathf.Max(0.001f, Mathf.Abs(dir.x)), (Screen.height / 2f - m) / Mathf.Max(0.001f, Mathf.Abs(dir.y)));
            x = center.x + dir.x * k; y = center.y + dir.y * k;
            string arrow = Mathf.Abs(dir.x) > Mathf.Abs(dir.y) ? (dir.x > 0 ? "►" : "◄") : (dir.y > 0 ? "▼" : "▲");
            label = $"{arrow}\n{av.Name} · {dist:0} м";
        }
        else label = $"▼\n{av.Name} · {dist:0} м";
        float pulse = 1f + 0.15f * Mathf.Sin(a * 8f);
        float w = 220, h = 70 * pulse;
        GUI.color = new Color(1, 1, 1, fade);
        GUI.Box(new Rect(x - w / 2, y - h / 2, w, h), GUIContent.none, _banner);
        _hudStyle.alignment = TextAnchor.MiddleCenter; _hudStyle.fontSize = 18; _hudStyle.wordWrap = true;
        Shadowed(new Rect(x - w / 2, y - h / 2, w, h), label, _hudStyle, c);
        _hudStyle.alignment = TextAnchor.UpperLeft; _hudStyle.fontSize = 14; _hudStyle.wordWrap = false;
        GUI.color = old;
    }

    void EmoteBubble(int id, float start, float x, float y)
    {
        if (id < 0) return;
        float a = Time.unscaledTime - start, D = 2.6f;
        if (a > D) return;
        float pop = a < 0.25f ? a / 0.25f * 1.25f : a < 0.4f ? 1.25f - (a - 0.25f) / 0.15f * 0.25f : 1f;
        float fade = a > D - 0.35f ? (D - a) / 0.35f : 1f;
        float s = 84f * pop;
        y -= (1 - fade) * 20f + Mathf.Sin(a * 4) * 3f;
        var old = GUI.color;
        GUI.color = new Color(1, 1, 1, fade);
        GUI.Box(new Rect(x - s / 2, y - s / 2, s, s), GUIContent.none, _bubble);
        GUI.color = new Color(EmoCol[id].r, EmoCol[id].g, EmoCol[id].b, fade);
        _emoStyle.fontSize = Mathf.Max(8, (int)(40 * pop));
        GUI.Label(new Rect(x - s / 2, y - s / 2, s, s), EmoSym[id], _emoStyle);
        GUI.color = old;
    }

    void DrawWheel()
    {
        float cx = Screen.width / 2f, cy = Screen.height / 2f, R = 215f, s = 96f;
        var old = GUI.color;
        var labels = WheelLabels;
        for (int i = 0; i < WheelN; i++)
        {
            float ang = i * WheelStep * Mathf.Deg2Rad;
            float x = cx + Mathf.Sin(ang) * R, y = cy - Mathf.Cos(ang) * R;
            bool sel = i == _wheelSel;
            GUI.color = Color.white;
            var r = new Rect(x - s / 2, y - s / 2, s, s);
            GUI.Box(r, GUIContent.none, sel ? _bubbleSel : _bubble);
            GUI.color = i < 8 ? EmoCol[i] : new Color(0.23f, 0.16f, 0.12f, 1f);
            _wheelStyle.fontSize = i < 8 ? 34 : 18;
            GUI.Label(r, labels[i], _wheelStyle);
        }
        // указатель: куда сейчас ведёт мышь
        var d = _wheelVec.magnitude > 1f ? _wheelVec.normalized * Mathf.Min(R - 60f, _wheelVec.magnitude * 0.5f) : Vector2.zero;
        GUI.color = Color.white;
        GUI.Box(new Rect(cx + d.x - 14, cy - d.y - 14, 28, 28), GUIContent.none, _dot);
        GUI.color = Color.white;
        _hudStyle.alignment = TextAnchor.MiddleCenter;
        string hint = _wheelSel < 0 ? Loc.L("Веди мышь в сторону и отпусти T", "Move the mouse to a side and release T") : Loc.L("Отпусти T: ", "Release T: ") + labels[_wheelSel].Replace("\n", " ");
        Shadowed(new Rect(cx - 200, cy + 30, 400, 28), hint, _hudStyle, Color.white);
        _hudStyle.alignment = TextAnchor.UpperLeft;
        GUI.color = old;
    }

    // Выбор аптеки для хоста: личный сейв или отдельная аптека для своей компании.
    // Без GUI.TextField и ScrollView — в этой игре они вырезаны и роняли окно после первой кнопки.
    void RoomPicker()
    {
        var rooms = Coop.ListRooms();
        int shown = Mathf.Min(rooms.Length, 9);
        float w = 480, h = 52 + 48 * shown + 56 + 48, x = (Screen.width - w) / 2, y = (Screen.height - h) / 2;
        GUI.Box(new Rect(x, y, w, h), GUIContent.none, _banner);
        _hudStyle.fontSize = 20; _hudStyle.alignment = TextAnchor.MiddleCenter;
        Shadowed(new Rect(x, y + 10, w, 32), "Какую аптеку открыть?", _hudStyle, new Color(1f, 0.9f, 0.6f));
        _hudStyle.fontSize = 14; _hudStyle.alignment = TextAnchor.UpperLeft;
        float cy = y + 52;
        for (int i = 0; i < shown; i++, cy += 48)
            if (MBtn(new Rect(x + 20, cy, w - 40, 40), $"{i + 1}. Аптека «{rooms[i]}»")) { _roomPicker = false; Coop.RelaunchAsHost(rooms[i]); }

        // своё поле ввода: кликнул — печатаешь (буквы ловит TickTyping), Enter — создать, Esc — перестать печатать
        string caret = _typingRoom && (Time.unscaledTime * 2f % 2f) < 1f ? "|" : "";
        string shownName = _newRoomName.Length == 0 && !_typingRoom ? "название…" : _newRoomName + caret;
        if (MBtn(new Rect(x + 20, cy + 8, w - 190, 40), shownName)) _typingRoom = true;
        if (MBtn(new Rect(x + w - 160, cy + 8, 140, 40), "+ Новая")) { _typingRoom = false; CreateRoom(rooms, false); }
        if (MBtn(new Rect(x + 20, y + h - 46, w - 40, 36), "Отмена")) { _typingRoom = false; _roomPicker = false; }
    }

    void CreateRoom(string[] rooms, bool fromMySave)
    {
        var name = Coop.SafeRoomName(_newRoomName);
        if (Array.IndexOf(rooms, name) >= 0) { Coop.Say(Loc.L($"Аптека «{name}» уже есть — придумай другое название", $"Pharmacy \"{name}\" already exists — pick another name"), 3f); return; }
        _roomPicker = false;
        // аптеку создаём только после «Да» — на «Отмена» ничего не остаётся
        AskRestart(Loc.L($"Игра перезапустится. Создать «{name}»?", $"The game will restart. Create \"{name}\"?"), () =>
        {
            if (fromMySave && !Coop.CreateRoomFromMySave(name)) { Coop.Say(Loc.L("Не нашёл твой личный сейв — начни новую аптеку", "Couldn't find your own save — start a new pharmacy"), 5f); OpenCoopScreen(); return; }
            if (!fromMySave) Coop.MarkFresh(name);
            Coop.EnsureRoomId(name);
            MenuButtons.CloseScreen();
            Coop.RelaunchAsHost(name);
        }, OpenCoopScreen);
    }

    // Панель в главном меню: создать комнату или зайти к другу
    void LobbyPanel()
    {
        var hosts = _peers.Where(kv => kv.Value.Hosting).ToList();
        int rows = 1 + hosts.Count + (_joinStatus != null ? 1 : 0);
        float w = 420, h = 54 * rows + 70, x = Screen.width - w - 24, y = 24;
        GUI.Box(new Rect(x, y, w, h), "Общая аптека", _boxStyle);
        float cy = y + 34;

        if (_joinStatus != null)
        {
            if (Coop.Role == Role.Guest && Time.unscaledTime - _joinRequestedAt > 15f)
            { _joinStatus = "Хост не ответил. Он точно открыл аптеку?"; Coop.Role = Role.Solo; }
            GUI.Label(new Rect(x + 16, cy, w - 32, 44), _joinStatus, _hudStyle);
            cy += 54;
        }
        if (MBtn(new Rect(x + 16, cy, w - 32, 44), "Создать общую аптеку  [F7]")) { Plugin.L.LogInfo("COOP: клик «Создать»"); BecomeHost(); }
        cy += 54;
        foreach (var kv in hosts)
        {
            if (MBtn(new Rect(x + 16, cy, w - 32, 44), (kv.Value.RoomName != "" ? $"Зайти: {kv.Value.Name} · «{kv.Value.RoomName}»" : $"Зайти в аптеку {kv.Value.Name}") + "  [F8]")) { Plugin.L.LogInfo("COOP: клик «Зайти»"); JoinHost(kv.Key); }
            cy += 54;
        }
        GUI.Label(new Rect(x + 16, cy, w - 32, 22), "Если кнопки не жмутся мышкой — жми F7 / F8", _hudStyle);
    }

    static void Shadowed(Rect r, string text, GUIStyle st, Color c)
    {
        var old = GUI.color;
        GUI.color = new Color(0, 0, 0, 0.8f);
        GUI.Label(new Rect(r.x + 1.5f, r.y + 1.5f, r.width, r.height), text, st);
        GUI.color = Color.Lerp(c, Color.white, 0.35f);
        GUI.Label(r, text, st);
        GUI.color = old;
    }

    static string Name(ulong id)
    {
        try { return SteamFriends.GetFriendPersonaName(new CSteamID(id)); }
        catch { return id.ToString(); }
    }
}
