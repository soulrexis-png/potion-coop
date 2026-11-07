using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Win32;

// Установщик кооп-мода «Общая аптека» для A Place for Every Potion.
// Сам находит игру через Steam, ставит загрузчик модов и мод, обновляет только изменившиеся файлы.
static class Program
{
    const string GameFolder = "A Place for Every Potion";
    const string GameExe = "A Place for Every Potion.exe";
    const string ProcessName = "A Place for Every Potion";

    // язык установщика — по языку Windows: русский для ru, иначе английский
    static readonly bool Ru = System.Globalization.CultureInfo.CurrentUICulture.TwoLetterISOLanguageName == "ru";
    static string T(string ru, string en) => Ru ? ru : en;

    static int Main(string[] args)
    {
        Console.OutputEncoding = Encoding.UTF8;
        Console.Title = T("Общая аптека — установка", "PotionCoop — setup");
        try { return Run(); }
        catch (Exception e)
        {
            Red(T("\nЧто-то пошло не так:\n", "\nSomething went wrong:\n") + e.Message);
            Console.WriteLine(T("\nСделай скриншот этого окна и приложи к сообщению о проблеме.", "\nTake a screenshot of this window and attach it to your bug report."));
            Pause();
            return 1;
        }
    }

    static int Run()
    {
        Title(T("ОБЩАЯ АПТЕКА — кооп-мод для A Place for Every Potion", "POTIONCOOP — co-op mod for A Place for Every Potion"));

        // 1. ищем игру
        string game = FindGame();
        if (game == null)
        {
            Yellow(T("Не нашёл игру автоматически.", "Couldn't find the game automatically."));
            Console.WriteLine(T("Открой Steam → правой кнопкой по игре → Управление → Просмотреть локальные файлы,", "Open Steam → right-click the game → Manage → Browse local files,"));
            Console.WriteLine(T("скопируй путь из адресной строки проводника и вставь сюда, потом нажми Enter:", "copy the path from the Explorer address bar, paste it here and press Enter:"));
            Console.Write("> ");
            game = (Console.ReadLine() ?? "").Trim().Trim('"');
            if (!File.Exists(Path.Combine(game, GameExe))) { Red(T("В этой папке нет игры. Проверь путь.", "The game is not in this folder. Check the path.")); Pause(); return 2; }
        }
        Green(T("Игра найдена: ", "Game found: ") + game);

        // 2. что уже стоит
        bool hasLoader = File.Exists(Path.Combine(game, "winhttp.dll")) && Directory.Exists(Path.Combine(game, "BepInEx", "core"));
        bool hasMod = File.Exists(Path.Combine(game, "BepInEx", "plugins", "PotionCoop.dll"));
        Console.WriteLine();
        Console.WriteLine(T("  Загрузчик модов: ", "  Mod loader: ") + (hasLoader ? T("уже стоит", "installed") : T("нет", "not installed")));
        Console.WriteLine(T("  Мод «Общая аптека»: ", "  PotionCoop mod: ") + (hasMod ? T("уже стоит", "installed") : T("нет", "not installed")));
        Console.WriteLine();
        Console.WriteLine(T("  1 — Установить / обновить (по умолчанию)", "  1 — Install / update (default)"));
        if (hasLoader || hasMod) Console.WriteLine(T("  2 — Удалить мод и загрузчик, вернуть игру как была", "  2 — Remove the mod and the loader, restore the game as it was"));
        var backups = FindBackups();
        if (backups.Count > 0) Console.WriteLine(T($"  3 — Вернуть личный сейв из копии (копий: {backups.Count})", $"  3 — Restore your solo save from a backup ({backups.Count} available)"));
        if (hasLoader) Console.WriteLine(T("  4 — Собрать логи (zip на рабочий стол)", "  4 — Collect logs (zip on the Desktop)"));
        Console.Write(T("\nНажми Enter или введи номер: ", "\nPress Enter or type a number: "));
        var choice = (Console.ReadLine() ?? "").Trim();

        if (choice == "4" && hasLoader) { CollectLogs(game); Pause(); return 0; }
        WaitGameClosed();

        if (choice == "2") { Uninstall(game); Pause(); return 0; }
        if (choice == "3" && backups.Count > 0) { Restore(backups); Pause(); return 0; }
        Install(game);
        Pause();
        return 0;
    }

    // ---------- установка: распаковываем только то, что отличается ----------

    static void Install(string game)
    {
        using var payload = Assembly.GetExecutingAssembly().GetManifestResourceStream("payload.zip")
                            ?? throw new Exception(T("внутри установщика нет файлов мода (payload.zip)", "the installer has no mod files inside (payload.zip)"));
        using var zip = new ZipArchive(payload, ZipArchiveMode.Read);

        var entries = zip.Entries.Where(e => !string.IsNullOrEmpty(e.Name)).ToList();
        int added = 0, updated = 0, same = 0, i = 0;
        Console.WriteLine();
        foreach (var e in entries)
        {
            i++;
            var dest = Path.Combine(game, e.FullName.Replace('/', Path.DirectorySeparatorChar));
            if (!Path.GetFullPath(dest).StartsWith(Path.GetFullPath(game), StringComparison.OrdinalIgnoreCase))
                continue; // защита от путей вида ../

            byte[] data;
            using (var s = e.Open()) using (var ms = new MemoryStream()) { s.CopyTo(ms); data = ms.ToArray(); }

            if (File.Exists(dest))
            {
                var fi = new FileInfo(dest);
                if (fi.Length == data.Length && SameHash(dest, data)) { same++; Progress(i, entries.Count); continue; }
                updated++;
            }
            else added++;

            Directory.CreateDirectory(Path.GetDirectoryName(dest));
            File.WriteAllBytes(dest, data);
            Progress(i, entries.Count);
        }
        Console.WriteLine();
        Console.WriteLine();
        Green(T($"Готово! Новых файлов: {added}, обновлено: {updated}, без изменений: {same}.", $"Done! New files: {added}, updated: {updated}, unchanged: {same}."));
        CheckGameVersion(game, zip);
        Console.WriteLine();
        Console.WriteLine(T("Теперь просто запускай игру из Steam. Первый запуск займёт несколько минут.", "Now just start the game from Steam. The first start takes a few minutes."));
        Console.WriteLine(T("При запуске появится чёрное окно консоли — это загрузчик модов, так и должно быть, не закрывай его.", "A black console window opens together with the game — that is the mod loader, leave it open."));
        Console.WriteLine(T("В главном меню появится кнопка «Общие аптеки». В лаборатории зажми T и выбери «Письмо» — там все клавиши.", "The main menu gets a \"Shared pharmacies\" button. In the lab hold T and pick \"Letter\" for all the keys."));
    }

    // Мод собран под конкретную версию игры. Если у друга игра другой версии — предупреждаем сразу
    static void CheckGameVersion(string game, ZipArchive zip)
    {
        try
        {
            var e = zip.GetEntry("BepInEx/plugins/PotionCoop.gamehash");
            if (e == null) return;
            string want; using (var r = new StreamReader(e.Open())) want = r.ReadToEnd().Trim().ToUpperInvariant();
            string have; using (var sha = SHA256.Create()) using (var f = File.OpenRead(Path.Combine(game, "GameAssembly.dll"))) have = Convert.ToHexString(sha.ComputeHash(f));
            if (want == have) return;
            Console.WriteLine();
            Yellow(T("Внимание: у тебя игра другой версии, чем та, под которую собран мод.", "Note: your game version differs from the one the mod was built for."));
            Console.WriteLine(T("Мод в игре сам выключится и покажет предупреждение — ничего не сломается.", "The mod will switch itself off in the game and show a warning — nothing will break."));
            Console.WriteLine(T("Обновите игру в Steam у всех игроков; если мод отключился — дождитесь новой версии мода.", "Update the game in Steam for every player; if the mod switched off, wait for a new mod version."));
        }
        catch { }
    }

    // zip — в буфер обмена (в чате просто Ctrl+V) и открыть папку с выделенным файлом
    static void HandOver(string zipPath)
    {
        try
        {
            var ps = new System.Diagnostics.ProcessStartInfo("powershell.exe",
                "-NoProfile -WindowStyle Hidden -Command \"Set-Clipboard -LiteralPath '" + zipPath.Replace("'", "''") + "'\"")
            { CreateNoWindow = true, UseShellExecute = false };
            System.Diagnostics.Process.Start(ps)?.WaitForExit(5000);
        }
        catch { }
        try { System.Diagnostics.Process.Start("explorer.exe", "/select,\"" + zipPath + "\""); } catch { }
    }

    // Логи — в zip на рабочий стол (лог перезаписывается при каждом запуске игры!)
    static void CollectLogs(string game)
    {
        var desk = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
        var to = Path.Combine(desk, $"PotionCoop_logs_{Environment.UserName}_{DateTime.Now:yyyy-MM-dd_HH-mm}.zip");
        int n = 0;
        using (var z = ZipFile.Open(to, ZipArchiveMode.Create))
        {
            foreach (var rel in new[] { @"BepInEx\LogOutput.log", @"BepInEx\potioncoop_diag.txt", @"BepInEx\config\roma.potioncoop.cfg", @"BepInEx\ErrorLog.log" })
            {
                var p = Path.Combine(game, rel);
                if (!File.Exists(p)) continue;
                using var src = new FileStream(p, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                using var dst = z.CreateEntry(Path.GetFileName(p)).Open();
                src.CopyTo(dst); n++;
            }
        }
        if (n == 0) { File.Delete(to); Yellow(T("Логов не нашёл — игру с модом ещё не запускали?", "No logs found — has the game been started with the mod yet?")); return; }
        HandOver(to);
        Green(T($"Готово! Логи собраны: {Path.GetFileName(to)} (на рабочем столе).", $"Done! Logs collected: {Path.GetFileName(to)} (on the Desktop)."));
        Console.WriteLine(T("Файл уже скопирован — вставь его Ctrl+V туда, куда пишешь о проблеме.", "The file is already copied — paste it with Ctrl+V where you report the problem."));
        Console.WriteLine(T("Если не вставилось — перетащи файл из открывшейся папки в чат.", "If it doesn't paste, drag the file from the opened folder."));
        Console.WriteLine(T("Важно: лог перезаписывается при запуске игры — собирай логи до того, как снова зайдёшь в игру.", "Important: the log is overwritten when the game starts — collect logs before you start it again."));
    }

    static bool SameHash(string path, byte[] data)
    {
        using var sha = SHA256.Create();
        byte[] a; using (var f = File.OpenRead(path)) a = sha.ComputeHash(f);
        return a.SequenceEqual(sha.ComputeHash(data));
    }

    // ---------- удаление ----------

    static void Uninstall(string game)
    {
        foreach (var f in new[] { "winhttp.dll", "doorstop_config.ini", ".doorstop_version", "changelog.txt" })
        {
            var p = Path.Combine(game, f);
            if (File.Exists(p)) File.Delete(p);
        }
        foreach (var d in new[] { "BepInEx", "dotnet" })
        {
            var p = Path.Combine(game, d);
            if (Directory.Exists(p)) Directory.Delete(p, true);
        }
        Green(T("Мод и загрузчик удалены. Игра снова как была. Сохранения не трогали.", "The mod and the loader are removed. The game is as it was. Saves were not touched."));
    }

    // ---------- копии личного сейва (их делает мод при каждом запуске игры) ----------

    // Папки PotionCoop_backup ищем в AppData\LocalLow\<студия>\<игра>
    static List<DirectoryInfo> FindBackups()
    {
        var res = new List<DirectoryInfo>();
        try
        {
            var low = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "AppData", "LocalLow");
            if (!Directory.Exists(low)) return res;
            foreach (var studio in Directory.GetDirectories(low))
            {
                string[] games;
                try { games = Directory.GetDirectories(studio); } catch { continue; }
                foreach (var g in games)
                {
                    var b = Path.Combine(g, "PotionCoop_backup");
                    if (!Directory.Exists(b)) continue;
                    foreach (var d in new DirectoryInfo(b).GetDirectories())
                        if (File.Exists(Path.Combine(d.FullName, "_from.txt"))) res.Add(d);
                }
            }
        }
        catch { }
        return res.OrderByDescending(d => d.Name).ToList();
    }

    static void Restore(List<DirectoryInfo> backups)
    {
        Console.WriteLine();
        Title(T("Какую копию вернуть? (сверху — самые свежие)", "Which backup to restore? (newest first)"));
        int n = Math.Min(backups.Count, 10);
        for (int i = 0; i < n; i++)
        {
            var d = backups[i];
            string when = DateTime.TryParseExact(d.Name.Substring(0, Math.Min(19, d.Name.Length)), "yyyy-MM-dd_HH-mm-ss", null,
                              System.Globalization.DateTimeStyles.None, out var t) ? t.ToString("dd.MM.yyyy HH:mm") : d.Name;
            string note = d.Name.Contains("до-восстановления") ? T("  (сейв перед прошлым восстановлением)", "  (save from before the last restore)") : "";
            Console.WriteLine($"  {i + 1} — {when}{note}");
        }
        Console.Write(T("\nВведи номер (Enter — отмена): ", "\nType a number (Enter to cancel): "));
        if (!int.TryParse((Console.ReadLine() ?? "").Trim(), out var k) || k < 1 || k > n) { Yellow(T("Отменил, ничего не трогал.", "Cancelled, nothing changed.")); return; }

        var src = backups[k - 1];
        var target = File.ReadAllText(Path.Combine(src.FullName, "_from.txt")).Trim();
        if (string.IsNullOrEmpty(target)) { Red(T("В копии не записано, куда её возвращать.", "The backup doesn't say where it belongs.")); return; }
        Directory.CreateDirectory(target);

        // сначала прячем текущий сейв — вдруг передумаешь
        var safety = Path.Combine(src.Parent.FullName, DateTime.Now.ToString("yyyy-MM-dd_HH-mm-ss") + "_до-восстановления");
        Directory.CreateDirectory(safety);
        foreach (var f in Directory.GetFiles(target)) File.Copy(f, Path.Combine(safety, Path.GetFileName(f)), true);
        File.WriteAllText(Path.Combine(safety, "_from.txt"), target);

        // текущие файлы сейва убираем (кроме служебных файлов Steam) и кладём файлы из копии
        foreach (var f in Directory.GetFiles(target))
            if (!f.EndsWith(".vdf", StringComparison.OrdinalIgnoreCase)) File.Delete(f);
        int copied = 0;
        foreach (var f in Directory.GetFiles(src.FullName))
        {
            if (Path.GetFileName(f) == "_from.txt") continue;
            File.Copy(f, Path.Combine(target, Path.GetFileName(f)), true);
            copied++;
        }
        Green(T($"\nГотово! Вернул сейв из копии ({copied} файлов).", $"\nDone! Save restored from the backup ({copied} files)."));
        Console.WriteLine(T("Сейв, который был до этого, тоже сохранил — он в списке с пометкой «перед прошлым восстановлением».", "The save you had before is kept too — it is in the list as \"from before the last restore\"."));
        Console.WriteLine(T("Если Steam при запуске спросит, какой сейв оставить, — выбирай тот, что на компьютере (локальный).", "If Steam asks which save to keep, choose the one on this computer (local)."));
    }

    // ---------- поиск игры через Steam ----------

    static string FindGame()
    {
        var roots = new List<string>();
        foreach (var (hive, key) in new[] {
                     (Registry.CurrentUser, @"Software\Valve\Steam"),
                     (Registry.LocalMachine, @"SOFTWARE\WOW6432Node\Valve\Steam"),
                     (Registry.LocalMachine, @"SOFTWARE\Valve\Steam") })
        {
            try
            {
                using var k = hive.OpenSubKey(key);
                var p = (k?.GetValue("SteamPath") ?? k?.GetValue("InstallPath")) as string;
                if (!string.IsNullOrEmpty(p)) roots.Add(p.Replace('/', '\\'));
            }
            catch { }
        }
        roots.Add(@"C:\Program Files (x86)\Steam");

        var libs = new List<string>();
        foreach (var root in roots.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            libs.Add(root);
            var vdf = Path.Combine(root, "steamapps", "libraryfolders.vdf");
            if (!File.Exists(vdf)) continue;
            foreach (Match m in Regex.Matches(File.ReadAllText(vdf), "\"path\"\\s+\"(.+?)\""))
                libs.Add(m.Groups[1].Value.Replace(@"\\", @"\"));
        }
        // запасной вариант: SteamLibrary в корне любого диска
        foreach (var d in DriveInfo.GetDrives().Where(d => d.DriveType == DriveType.Fixed))
            libs.Add(Path.Combine(d.RootDirectory.FullName, "SteamLibrary"));

        foreach (var lib in libs.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var g = Path.Combine(lib, "steamapps", "common", GameFolder);
            if (File.Exists(Path.Combine(g, GameExe))) return g;
        }
        return null;
    }

    static void WaitGameClosed()
    {
        while (Process.GetProcessesByName(ProcessName).Length > 0)
        {
            Yellow(T("\nИгра сейчас запущена — закрой её и нажми Enter.", "\nThe game is running — close it and press Enter."));
            Console.ReadLine();
        }
    }

    // ---------- вывод ----------

    static void Progress(int i, int n)
    {
        int w = 40, filled = (int)((double)i / n * w);
        Console.Write($"\r  [{new string('#', filled)}{new string('.', w - filled)}] {i}/{n}");
    }
    static void Title(string t) { Console.ForegroundColor = ConsoleColor.Yellow; Console.WriteLine("\n  " + t + "\n"); Console.ResetColor(); }
    static void Green(string t) { Console.ForegroundColor = ConsoleColor.Green; Console.WriteLine(t); Console.ResetColor(); }
    static void Yellow(string t) { Console.ForegroundColor = ConsoleColor.Yellow; Console.WriteLine(t); Console.ResetColor(); }
    static void Red(string t) { Console.ForegroundColor = ConsoleColor.Red; Console.WriteLine(t); Console.ResetColor(); }
    static void Pause() { Console.WriteLine(T("\nНажми Enter, чтобы закрыть.", "\nPress Enter to close.")); Console.ReadLine(); }
}
