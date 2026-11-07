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

    static int Main(string[] args)
    {
        Console.OutputEncoding = Encoding.UTF8;
        Console.Title = "Общая аптека — установка";
        try { return Run(); }
        catch (Exception e)
        {
            Red("\nЧто-то пошло не так:\n" + e.Message);
            Console.WriteLine("\nСделай скриншот этого окна и приложи к сообщению о проблеме.");
            Pause();
            return 1;
        }
    }

    static int Run()
    {
        Title("ОБЩАЯ АПТЕКА — кооп-мод для A Place for Every Potion");

        // 1. ищем игру
        string game = FindGame();
        if (game == null)
        {
            Yellow("Не нашёл игру автоматически.");
            Console.WriteLine("Открой Steam → правой кнопкой по игре → Управление → Просмотреть локальные файлы,");
            Console.WriteLine("скопируй путь из адресной строки проводника и вставь сюда, потом нажми Enter:");
            Console.Write("> ");
            game = (Console.ReadLine() ?? "").Trim().Trim('"');
            if (!File.Exists(Path.Combine(game, GameExe))) { Red("В этой папке нет игры. Проверь путь."); Pause(); return 2; }
        }
        Green("Игра найдена: " + game);

        // 2. что уже стоит
        bool hasLoader = File.Exists(Path.Combine(game, "winhttp.dll")) && Directory.Exists(Path.Combine(game, "BepInEx", "core"));
        bool hasMod = File.Exists(Path.Combine(game, "BepInEx", "plugins", "PotionCoop.dll"));
        Console.WriteLine();
        Console.WriteLine($"  Загрузчик модов: {(hasLoader ? "уже стоит" : "нет")}");
        Console.WriteLine($"  Мод «Общая аптека»: {(hasMod ? "уже стоит" : "нет")}");
        Console.WriteLine();
        Console.WriteLine("  1 — Установить / обновить (по умолчанию)");
        if (hasLoader || hasMod) Console.WriteLine("  2 — Удалить мод и загрузчик, вернуть игру как была");
        var backups = FindBackups();
        if (backups.Count > 0) Console.WriteLine($"  3 — Вернуть личный сейв из копии (копий: {backups.Count})");
        if (hasLoader) Console.WriteLine("  4 — Собрать логи (zip на рабочий стол)");
        Console.Write("\nНажми Enter или введи номер: ");
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
                            ?? throw new Exception("внутри установщика нет файлов мода (payload.zip)");
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
        Green($"Готово! Новых файлов: {added}, обновлено: {updated}, без изменений: {same}.");
        CheckGameVersion(game, zip);
        Console.WriteLine();
        Console.WriteLine("Теперь просто запускай игру из Steam.");
        Console.WriteLine("При запуске появится чёрное окно консоли — это загрузчик модов, так и должно быть, не закрывай его.");
        Console.WriteLine("В главном меню появятся кнопки «Создать общую аптеку» и «Зайти в аптеку …».");
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
            Yellow("Внимание: у тебя игра другой версии, чем та, под которую собран мод.");
            Console.WriteLine("Мод в игре сам выключится и покажет предупреждение — ничего не сломается.");
            Console.WriteLine("Обновите игру в Steam у всех игроков; если мод отключился — дождитесь новой версии мода.");
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
        if (n == 0) { File.Delete(to); Yellow("Логов не нашёл — игру с модом ещё не запускали?"); return; }
        HandOver(to);
        Green($"Готово! Логи собраны: {Path.GetFileName(to)} (на рабочем столе).");
        Console.WriteLine("Файл уже скопирован — вставь его Ctrl+V туда, куда пишешь о проблеме.");
        Console.WriteLine("Если не вставилось — перетащи файл из открывшейся папки в чат.");
        Console.WriteLine("Важно: лог перезаписывается при запуске игры — собирай логи до того, как снова зайдёшь в игру.");
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
        Green("Мод и загрузчик удалены. Игра снова как была. Сохранения не трогали.");
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
        Title("Какую копию вернуть? (сверху — самые свежие)");
        int n = Math.Min(backups.Count, 10);
        for (int i = 0; i < n; i++)
        {
            var d = backups[i];
            string when = DateTime.TryParseExact(d.Name.Substring(0, Math.Min(19, d.Name.Length)), "yyyy-MM-dd_HH-mm-ss", null,
                              System.Globalization.DateTimeStyles.None, out var t) ? t.ToString("dd.MM.yyyy HH:mm") : d.Name;
            string note = d.Name.Contains("до-восстановления") ? "  (сейв перед прошлым восстановлением)" : "";
            Console.WriteLine($"  {i + 1} — {when}{note}");
        }
        Console.Write("\nВведи номер (Enter — отмена): ");
        if (!int.TryParse((Console.ReadLine() ?? "").Trim(), out var k) || k < 1 || k > n) { Yellow("Отменил, ничего не трогал."); return; }

        var src = backups[k - 1];
        var target = File.ReadAllText(Path.Combine(src.FullName, "_from.txt")).Trim();
        if (string.IsNullOrEmpty(target)) { Red("В копии не записано, куда её возвращать."); return; }
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
        Green($"\nГотово! Вернул сейв из копии ({copied} файлов).");
        Console.WriteLine("Сейв, который был до этого, тоже сохранил — он в списке с пометкой «перед прошлым восстановлением».");
        Console.WriteLine("Если Steam при запуске спросит, какой сейв оставить, — выбирай тот, что на компьютере (локальный).");
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
            Yellow("\nИгра сейчас запущена — закрой её и нажми Enter.");
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
    static void Pause() { Console.WriteLine("\nНажми Enter, чтобы закрыть."); Console.ReadLine(); }
}
