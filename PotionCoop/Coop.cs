using System;
using System.Diagnostics;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Linq;
using HarmonyLib;
using UnityEngine;
using CodeBase.Components.UI;
using CodeBase.Services.GameStateMachineService;
using CodeBase.Services.SaveLoadService;

namespace PotionCoop;

public enum Role { Solo, Host, Guest }

// Состояние комнаты: кто хост, куда подменяем сейв, как запустить «Продолжить».
public static class Coop
{
    public static Role Role = Role.Solo;
    public static ulong HostId;
    public static string HostName = "";
    public static eGameState State = eGameState.None;
    public static GameStateMachine Machine;
    public static AdvancedButtonContinueStarter ContinueStarter;

    public static string GuestDir => Path.Combine(Application.persistentDataPath, "PotionCoop_guest");
    public static string RoomsRoot => Path.Combine(Application.persistentDataPath, "PotionCoop_rooms");
    // куда сейчас подменяются сейвы: гостевая папка или папка выбранной аптеки
    public static string SaveDir;
    public static string RoomName = "";      // имя аптеки хоста (у гостя — имя аптеки, куда зашёл)
    public static string RoomId = "";        // уникальный номер аптеки: по нему гость хранит свои выборы
    public static string RoomDir => RoomName == "" ? Path.Combine(RoomsRoot, "_mysave") : Path.Combine(RoomsRoot, RoomName);
    public static bool AutoHost; public static float AutoHostSince;
    // после перезапуска «в личную игру» — сам нажать «Продолжить»
    public static bool AutoContinue; public static float AutoContinueSince;

    // номер аптеки: создаётся один раз и живёт в её папке
    public static string EnsureRoomId(string room)
    {
        try
        {
            var dir = Path.Combine(RoomsRoot, room); Directory.CreateDirectory(dir);
            var f = Path.Combine(dir, "room.id");
            if (File.Exists(f)) { var id = File.ReadAllText(f).Trim(); if (id.Length > 0) return id; }
            var nid = Guid.NewGuid().ToString("N").Substring(0, 12);
            File.WriteAllText(f, nid);
            return nid;
        }
        catch (Exception e) { Plugin.L.LogWarning("ROOM id: " + e.Message); return ""; }
    }

    // личный сейв игрока (настоящая папка игры, без подмены)
    public static string PersonalSaveDir()
    {
        try { return AccountSaveFolder.FolderPath; } catch { return null; }
    }
    static bool IsSettings(string file) => Path.GetFileName(file).IndexOf("Settings", StringComparison.OrdinalIgnoreCase) >= 0;
    static int CopySaveFiles(string from, string to)
    {
        int n = 0;
        if (string.IsNullOrEmpty(from) || !Directory.Exists(from)) return 0;
        Directory.CreateDirectory(to);
        foreach (var f in Directory.GetFiles(from))
        {
            var name = Path.GetFileName(f);
            if (IsSettings(f) || name.EndsWith(".vdf") || name.EndsWith(".tmp") || name.StartsWith(".")) continue;
            try { File.Copy(f, Path.Combine(to, name), true); n++; } catch (Exception e) { Plugin.L.LogWarning($"COPY {name}: {e.Message}"); }
        }
        return n;
    }

    // «+ Новая аптека из моего сейва»: копия личного сейва становится отдельной аптекой, личный остаётся нетронутым
    public static bool CreateRoomFromMySave(string room)
    {
        var src = PersonalSaveDir();
        var dst = Path.Combine(RoomsRoot, room, "save");
        int n = CopySaveFiles(src, dst);
        EnsureRoomId(room);
        Plugin.L.LogInfo($"COOP: аптека «{room}» создана из личного сейва ({n} файлов)");
        return n > 0;
    }

    // Удалить аптеку: не стираем насовсем, а уносим в корзину PotionCoop_rooms/_удалённые (вернуть — перетащить папку обратно)
    public static void DeleteRoom(string room)
    {
        try
        {
            if (RedirectSaves && room == RoomName) { Say(Loc.L($"«{room}» сейчас загружена — перезапусти игру и удали её из меню", $"\"{room}\" is loaded right now — restart the game and delete it from the menu"), 6f); return; }
            var dir = Path.Combine(RoomsRoot, room);
            if (!Directory.Exists(dir)) return;
            var trash = Path.Combine(RoomsRoot, "_удалённые"); Directory.CreateDirectory(trash);
            var to = Path.Combine(trash, room + "_" + DateTime.Now.ToString("yyyy-MM-dd_HH-mm-ss"));
            Directory.Move(dir, to);
            Plugin.L.LogInfo($"ROOM: аптека «{room}» удалена (лежит в {to})");
            Say(Loc.L($"Аптека «{room}» удалена", $"Pharmacy \"{room}\" deleted"), 4f);
        }
        catch (Exception e) { Plugin.L.LogError("ROOM delete: " + e.Message); Say(Loc.L("Не получилось удалить аптеку: ", "Couldn't delete the pharmacy: ") + e.Message, 6f); }
    }

    // Один раз: старая «общая аптека на моём сейве» (папка _mysave) переезжает в аптеку «Общая аптека».
    // Личный сейв копируется туда вместе с навыками гостей и счётом краж; сам личный сейв остаётся личным.
    public const string MigratedRoom = "Общая аптека";
    static bool _migrated;
    public static void MigrateMySave()
    {
        if (_migrated) return;
        _migrated = true;
        try
        {
            var old = Path.Combine(RoomsRoot, "_mysave");
            var room = Path.Combine(RoomsRoot, MigratedRoom);
            if (!Directory.Exists(old)) { Plugin.L.LogInfo("MIGRATE: старой общей игры на личном сейве (_mysave) нет — переносить нечего"); return; }
            if (Directory.Exists(room)) { Plugin.L.LogInfo($"MIGRATE: аптека «{MigratedRoom}» уже есть — перенос не нужен"); return; }
            int n = CopySaveFiles(PersonalSaveDir(), Path.Combine(room, "save"));
            if (n == 0) { Plugin.L.LogWarning("MIGRATE: личный сейв не нашёлся — перенос отложен"); try { Directory.Delete(room, true); } catch { } _migrated = false; return; }
            foreach (var f in Directory.GetFiles(old)) try { File.Copy(f, Path.Combine(room, Path.GetFileName(f)), true); } catch { }
            EnsureRoomId(MigratedRoom);
            Directory.Move(old, Path.Combine(RoomsRoot, "_mysave_перенесено"));
            Plugin.L.LogInfo($"MIGRATE: общий сейв перенесён в аптеку «{MigratedRoom}» ({n} файлов сейва + навыки гостей и кражи)");
            Say(Loc.L($"Общая игра на твоём сейве теперь отдельная аптека «{MigratedRoom}». Твой личный сейв — только твой.", $"Your old co-op game is now a separate pharmacy \"{MigratedRoom}\". Your own save is yours alone."), 10f);
        }
        catch (Exception e) { Plugin.L.LogError("MIGRATE: " + e.Message); }
    }

    public static string[] ListRooms()
    {
        try
        {
            if (!Directory.Exists(RoomsRoot)) return Array.Empty<string>();
            var dirs = new DirectoryInfo(RoomsRoot).GetDirectories();
            Array.Sort(dirs, (a, b) => b.LastWriteTimeUtc.CompareTo(a.LastWriteTimeUtc));
            var res = new System.Collections.Generic.List<string>();
            foreach (var d in dirs) if (!d.Name.StartsWith("_")) res.Add(d.Name);
            return res.ToArray();
        }
        catch { return Array.Empty<string>(); }
    }

    public static string SafeRoomName(string n)
    {
        n = (n ?? "").Trim();
        foreach (var c in Path.GetInvalidFileNameChars()) n = n.Replace(c, '_');
        return n.Length == 0 ? "Аптека" : (n.Length > 40 ? n.Substring(0, 40) : n);
    }

    // Гость: сейвы читаются/пишутся в гостевую папку ВЕСЬ этот запуск игры (личный сейв в безопасности)
    public static bool RedirectSaves;
    // после перезапуска «в гости» — сам нажать «Продолжить», когда хост на связи
    public static bool AutoJoin;
    public static float AutoJoinSince;
    public static string Banner; public static float BannerUntil;
    public static void Say(string text, float seconds = 6f) { Banner = text; BannerUntil = Time.unscaledTime + seconds; Plugin.L.LogInfo("COOP: " + text); }

    static string FlagPath => Path.Combine(BepInEx.Paths.BepInExRootPath, "potioncoop_join.txt");

    // Вход в чужую аптеку = перезапуск игры: так игра с самого старта читает сейв хоста целиком
    public static void RelaunchAsGuest(ulong hostId, string hostName) => Relaunch($"guest\n{hostId}\n{hostName}\n{DateTime.UtcNow.Ticks}", "зайти в аптеку " + hostName);

    // Хост открывает отдельную аптеку: тоже через перезапуск, чтобы игра с нуля читала её сейв
    // Пустая новая аптека: метка «новая». По файлам судить нельзя — игра при старте сама создаёт сейв по умолчанию
    public static void MarkFresh(string room)
    {
        try { var d = Path.Combine(RoomsRoot, room); Directory.CreateDirectory(d); File.WriteAllText(Path.Combine(d, "new.flag"), DateTime.UtcNow.ToString("o")); } catch { }
    }
    public static bool TakeFresh()
    {
        try { var f = Path.Combine(RoomDir, "new.flag"); if (!File.Exists(f)) return false; File.Delete(f); return true; } catch { return false; }
    }

    public static void RelaunchAsHost(string room)
    {
        Directory.CreateDirectory(Path.Combine(RoomsRoot, room, "save"));
        Directory.SetLastWriteTimeUtc(Path.Combine(RoomsRoot, room), DateTime.UtcNow);
        Relaunch($"host\n{room}\n-\n{DateTime.UtcNow.Ticks}", "открыть аптеку «" + room + "»");
    }

    // Вернуться в свою обычную игру: этот запуск читает чужие сейвы, поэтому перезапуск без подмены
    public static void RelaunchSolo(bool thenContinue) => Relaunch($"solo\n{(thenContinue ? "continue" : "-")}\n-\n{DateTime.UtcNow.Ticks}", "вернуться в личную игру");

    static void Relaunch(string flag, string why)
    {
        File.WriteAllText(FlagPath, flag);
        var psi = new ProcessStartInfo("cmd.exe", "/c timeout /t 4 /nobreak >nul & start \"\" \"steam://rungameid/5189500\"")
        { CreateNoWindow = true, UseShellExecute = false, WindowStyle = ProcessWindowStyle.Hidden };
        Process.Start(psi);
        Plugin.L.LogInfo("COOP: перезапускаю игру, чтобы " + why);
        Application.Quit();
    }

    // Вызывается при загрузке мода: мы перезапустились, чтобы зайти к хосту?
    public static void CheckJoinFlag()
    {
        try
        {
            if (!File.Exists(FlagPath)) return;
            var lines = File.ReadAllLines(FlagPath);
            File.Delete(FlagPath);
            if (lines.Length < 4) return;
            var age = TimeSpan.FromTicks(DateTime.UtcNow.Ticks - long.Parse(lines[3]));
            if (age.TotalMinutes > 3) return;
            if (lines[0] == "guest")
            {
                Role = Role.Guest; HostId = ulong.Parse(lines[1]); HostName = lines[2];
                SaveDir = GuestDir; RedirectSaves = true; AutoJoin = true;
                Plugin.L.LogInfo($"COOP: запуск в гостях у {HostName} — сейвы берутся из {GuestDir}");
            }
            else if (lines[0] == "host")
            {
                RoomName = lines[1];
                RoomId = EnsureRoomId(RoomName);
                SaveDir = Path.Combine(RoomsRoot, RoomName, "save");
                Directory.CreateDirectory(SaveDir);
                RedirectSaves = true; AutoHost = true;
                Plugin.L.LogInfo($"COOP: запуск хостом аптеки «{RoomName}» ({RoomId}) — сейвы в {SaveDir}");
            }
            else if (lines[0] == "solo")
            {
                AutoContinue = lines[1] == "continue";
                Plugin.L.LogInfo("COOP: запуск в личную игру" + (AutoContinue ? " — сам нажму «Продолжить»" : ""));
            }
        }
        catch (Exception e) { Plugin.L.LogError("COOP flag: " + e.Message); }
    }

    public static bool InLobby => State == eGameState.Lobby;

    // ---- сейв хоста: упаковать / распаковать ----

    public static string HostSaveFolder()
    {
        if (RedirectSaves && Role == Role.Host && SaveDir != null) return SaveDir;
        try { return AccountSaveFolder.FolderPath; }
        catch (Exception e) { Plugin.L.LogError("Не нашёл папку сейва: " + e.Message); return null; }
    }

    // ---- страховка: копия ЛИЧНОГО сейва при каждом запуске (последние 10, одинаковые не дублируем) ----
    public static string BackupRoot => Path.Combine(Application.persistentDataPath, "PotionCoop_backup");
    static bool _backedUp;
    public static void BackupPersonalSave()
    {
        if (_backedUp) return;
        _backedUp = true;
        try
        {
            string dir;
            try { dir = AccountSaveFolder.FolderPath; } catch { dir = null; }
            if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir)) { Plugin.L.LogWarning("BACKUP: не нашёл личный сейв"); return; }
            var files = Directory.GetFiles(dir);
            if (files.Length == 0) return;
            string Hash(string d)
            {
                using var sha = System.Security.Cryptography.SHA256.Create();
                var sb = new StringBuilder();
                foreach (var f in Directory.GetFiles(d).OrderBy(Path.GetFileName))
                {
                    if (Path.GetFileName(f) == "_from.txt") continue;
                    try { sb.Append(Path.GetFileName(f)).Append(Convert.ToHexString(sha.ComputeHash(File.ReadAllBytes(f)))); } catch { }
                }
                return sb.ToString();
            }
            Directory.CreateDirectory(BackupRoot);
            var old = new DirectoryInfo(BackupRoot).GetDirectories().OrderByDescending(d => d.Name).ToList();
            var now = Hash(dir);
            if (old.Count > 0 && Hash(old[0].FullName) == now) { Plugin.L.LogInfo("BACKUP: личный сейв не менялся, копия уже есть"); return; }
            var to = Path.Combine(BackupRoot, DateTime.Now.ToString("yyyy-MM-dd_HH-mm-ss"));
            Directory.CreateDirectory(to);
            foreach (var f in files) try { File.Copy(f, Path.Combine(to, Path.GetFileName(f)), true); } catch { }
            File.WriteAllText(Path.Combine(to, "_from.txt"), dir);
            foreach (var d in old.Skip(9)) try { d.Delete(true); } catch { }
            Plugin.L.LogInfo($"BACKUP: копия личного сейва -> {to}");
        }
        catch (Exception e) { Plugin.L.LogError("BACKUP: " + e.Message); }
    }

    public static byte[] PackHostSave()
    {
        var dir = HostSaveFolder();
        if (dir == null || !Directory.Exists(dir)) return null;
        using var ms = new MemoryStream();
        using var w = new BinaryWriter(ms, Encoding.UTF8);
        var files = new List<string>();
        foreach (var f in Directory.GetFiles(dir))
        {
            var name = Path.GetFileName(f);
            if (name.StartsWith(".") || name.EndsWith(".vdf") || name.EndsWith(".tmp")) continue;
            files.Add(f);
        }
        w.Write(files.Count);
        foreach (var f in files)
        {
            byte[] data;
            try { data = File.ReadAllBytes(f); }
            catch (IOException) { continue; } // игра как раз пишет файл — пропустим, он придёт при следующем входе
            w.Write(Path.GetFileName(f));
            w.Write(data.Length);
            w.Write(data);
        }
        Plugin.L.LogInfo($"COOP: упаковал сейв хоста: {files.Count} файлов, {ms.Length} байт из {dir}");
        return ms.ToArray();
    }

    public static int UnpackToGuestDir(byte[] b, int offset)
    {
        var dir = GuestDir;
        if (Directory.Exists(dir)) Directory.Delete(dir, true);
        Directory.CreateDirectory(dir);
        using var ms = new MemoryStream(b, offset, b.Length - offset);
        using var r = new BinaryReader(ms, Encoding.UTF8);
        int n = r.ReadInt32(), written = 0;
        for (int i = 0; i < n && ms.Position < ms.Length; i++)
        {
            var name = Path.GetFileName(r.ReadString()); // только имя, без путей
            int len = r.ReadInt32();
            var data = r.ReadBytes(len);
            File.WriteAllBytes(Path.Combine(dir, name), data);
            written++;
        }
        Plugin.L.LogInfo($"COOP: сейв хоста разложен в {dir} ({written} файлов)");
        return written;
    }

    // ---- запустить «Продолжить» так же, как если бы игрок нажал кнопку ----

    public static void PressContinue()
    {
        if (ContinueStarter != null)
        {
            Plugin.L.LogInfo("COOP: жму «Продолжить»");
            ContinueStarter.AdvancedButton_OnUp();
        }
        else if (Machine != null)
        {
            Plugin.L.LogInfo("COOP: кнопки «Продолжить» нет — вхожу в ContinueGame напрямую");
            Machine.Enter(eGameState.ContinueGame);
        }
        else Plugin.L.LogError("COOP: не могу запустить игру — не нашёл ни кнопку, ни машину состояний");
    }
}

static class CoopPatches
{
    // Следим за состоянием игры (меню / загрузка / игра)
    [HarmonyPostfix]
    [HarmonyPatch(typeof(GameStateMachine), nameof(GameStateMachine.Enter), new[] { typeof(eGameState) })]
    static void Enter_Post(GameStateMachine __instance, eGameState id)
    {
        Coop.Machine = __instance;
        Coop.State = id;
        if (id == eGameState.Lobby) { Coop.BackupPersonalSave(); Coop.MigrateMySave(); Loc.Detect(); }
        if (id == eGameState.Playing) LabScout.Report();
        NetDriver.I?.OnGameState(id);
        if (id == eGameState.Playing && Coop.Role != Role.Solo)
            try { if (Sync.NeedsReindex()) Sync.Reindex(); else Sync.Resume(); } catch (Exception e) { Plugin.L.LogError("SYNC Reindex: " + e.Message); }
    }

    // Личная игра всегда личная: если этот запуск читает сейв аптеки (был гостем/хостом),
    // «Продолжить» и «Новая игра» не пускаем в чужой сейв — перезапускаем игру в личную.
    [HarmonyPrefix]
    [HarmonyPatch(typeof(GameStateMachine), nameof(GameStateMachine.Enter), new[] { typeof(eGameState) })]
    static bool Enter_Pre(eGameState id)
    {
        try
        {
            if ((id == eGameState.ContinueGame || id == eGameState.NewGame) && Coop.RedirectSaves && Coop.Role == Role.Solo && !Coop.AutoJoin && !Coop.AutoHost)
            {
                bool cont = id == eGameState.ContinueGame;
                if (NetDriver.I != null)
                    NetDriver.I.AskRestart(Loc.L("Игра перезапустится. Вернуться в свою игру?", "The game will restart. Back to your own game?"),
                        () => { MenuButtons.CloseScreen(); Coop.RelaunchSolo(cont); }, null);
                else Coop.RelaunchSolo(cont);
                return false;
            }
        }
        catch (Exception e) { Plugin.L.LogError("COOP guard: " + e.Message); }
        return true;
    }

    // Запоминаем настоящую кнопку «Продолжить» в меню
    [HarmonyPostfix]
    [HarmonyPatch(typeof(AdvancedButtonContinueStarter), nameof(AdvancedButtonContinueStarter.OnEnable))]
    static void ContinueEnable_Post(AdvancedButtonContinueStarter __instance) => Coop.ContinueStarter = __instance;

    // У гостя все файлы сейва читаются и пишутся в отдельную папку — его собственный сейв не трогаем
    static int _logged;
    static void Redirect(ref string __result, string what)
    {
        if (!Coop.RedirectSaves || string.IsNullOrEmpty(__result)) return;
        // настройки (язык, графика, звук, управление) — общие для всех аптек: их не подменяем,
        // иначе в новой аптеке игра стартует «с завода» — на английском и с другими настройками
        var fn = Path.GetFileName(__result);
        if (fn.IndexOf("Settings", StringComparison.OrdinalIgnoreCase) >= 0) return;
        // общая статистика игрока (и отметка «первый запуск») — тоже личная, у аптек своей нет
        if (fn.IndexOf("GlobalProgress", StringComparison.OrdinalIgnoreCase) >= 0) return;
        var to = Path.Combine(Coop.SaveDir ?? Coop.GuestDir, Path.GetFileName(__result));
        if (_logged++ < 40) Plugin.L.LogInfo($"COOP: {what} {Path.GetFileName(__result)} -> общая аптека");
        __result = to;
    }

    [HarmonyPostfix]
    [HarmonyPatch(typeof(SaveLoadService), nameof(SaveLoadService.KeyToFilePath))]
    static void KeyToFilePath_Post(ref string __result) => Redirect(ref __result, "сейв");

    [HarmonyPostfix]
    [HarmonyPatch(typeof(SaveLoadService), nameof(SaveLoadService.TempFilePath))]
    static void TempFilePath_Post(ref string __result) => Redirect(ref __result, "temp");

    [HarmonyPostfix]
    [HarmonyPatch(typeof(SaveLoadService), nameof(SaveLoadService.BackupFilePath))]
    static void BackupFilePath_Post(ref string __result) => Redirect(ref __result, "backup");
}
