using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using CodeBase.Services.PersistentDataService;
using CodeBase.Services.DataMediatorService;
using HarmonyLib;

namespace PotionCoop;

// Переключение сейва прямо в главном меню, без перезапуска игры.
// Игра читает прогресс игрока (PlayerProgress) один раз при запуске и держит в памяти, а склянки
// (PotionsPersistentData_N) — кэширует. Поэтому: сохраняем текущее, меняем папку сейвов,
// переписываем прогресс в памяти значениями из новой папки и выкидываем кэш склянок —
// «Продолжить» / «Новая игра» загрузят лабораторию уже из новой папки.
// Подход подсмотрен у PotionCoop от csermet (MIT). Если что-то не вышло — вызывающий откатывается на перезапуск.
public static class SaveSwitch
{
    public static bool Enabled => Plugin.NoRestart.Value;
    public static SkillsDataMediator Skills;   // ловим, когда игра считает очки навыков

    static PersistentDataService Svc => Sync.PersistentSvc?.TryCast<PersistentDataService>();

    static PlayerProgress FindProgress(PersistentDataService svc)
    {
        // прогресс игрока лежит в обычных данных (_data), склянки — в «помеченных» (_taggedData)
        foreach (var kv in svc._data)
            if (kv.Key != null && kv.Key.Name == "PlayerProgress" && kv.Value != null)
                return kv.Value.TryCast<PlayerProgress>();
        foreach (var kv in svc._taggedData)
            if (kv.Key.Item1 != null && kv.Key.Item1.Name == "PlayerProgress" && kv.Value != null)
                return kv.Value.TryCast<PlayerProgress>();
        return null;
    }

    // соревнование: обучение не нужно — помечаем все уроки пройденными (вводное письмо, первое наливание, первая полка, доска навыков)
    // прогресс берём только «свежий» (Xp.Progress — тот, к которому игра сама сейчас обращается) или из сервиса в меню.
    // После старта уровня старая ссылка на сервис сейвов может указывать на уже освобождённую память — обращение к ней роняло игру целиком.
    public static bool SkipOnboarding(bool allowService)
    {
        try
        {
            PlayerProgress p = null;
            if (allowService) { var svc = Svc; if (svc != null) p = FindProgress(svc); }   // до загрузки уровня — так уже работало
            else p = Xp.Progress;
            if (p == null) return false;
            int all = (int)eOnboarding.FirstPour | (int)eOnboarding.FirstShelve | (int)eOnboarding.SkillBoardOpened | (int)eOnboarding.IntroLetterRead;
            if ((p.OnboardingFlags.Value & all) == all) return true;
            p.OnboardingFlags.Value = p.OnboardingFlags.Value | all;
            Plugin.L.LogInfo("TOURNEY: обучение пропущено (уроки помечены пройденными)");
            return true;
        }
        catch (Exception e) { Plugin.L.LogWarning("TOURNEY обучение: " + e.Message); return false; }
    }

    // шаг — сразу в potioncoop_diag.txt: если игра упадёт целиком, последний записанный шаг и есть виновник
    static void Mark(string step)
    {
        try { File.AppendAllText(Path.Combine(BepInEx.Paths.BepInExRootPath, "potioncoop_diag.txt"), $"[{DateTime.Now:HH:mm:ss.fff}] SWITCH шаг: {step}\n"); } catch { }
    }

    // дописать текущие склянки туда, куда сейв сейчас смотрит. Прогресс игрока игра сама пишет при выходе в меню;
    // своего «сохранить» у сервиса без generic-вызова нет (а generic роняет игру) — поэтому только склянки
    public static void SaveCurrent()
    {
        try { Sync.FlushSave(); } catch (Exception e) { Plugin.L.LogWarning("SWITCH save: " + e.Message); }
    }

    // dir == null — личная игра. fresh — новая аптека: прогресс с нуля. saveFirst=false — текущее уже сохранено
    public static bool To(string dir, bool fresh, bool saveFirst = true)
    {
        bool oldRedirect = Coop.RedirectSaves; string oldDir = Coop.SaveDir;
        string step = "начало"; bool applied = false;
        try
        {
            if (!Enabled) return false;
            if (!Coop.InLobby) throw new Exception("не в главном меню");
            Mark($"начинаю → {(dir ?? "личная игра")}{(fresh ? " (новая)" : "")}");
            step = "сервис сейвов"; Mark(step); var svc = Svc ?? throw new Exception(Sync.PersistentSvc == null ? "сервис сейвов ещё не пойман" : "сервис сейвов другого типа: " + Sync.PersistentSvc.Cast<Il2CppSystem.Object>().GetIl2CppType().FullName);
            step = "прогресс игрока"; Mark(step); var p = FindProgress(svc) ?? throw new Exception($"прогресс игрока не найден (данных {svc._data.Count}, склянок {svc._taggedData.Count})");
            step = "дописать текущее"; Mark(step); if (saveFirst) SaveCurrent();

            step = "файл прогресса"; Mark(step); string file = Path.Combine(dir ?? Coop.PersonalSaveDir() ?? throw new Exception("нет папки личного сейва"), "PlayerProgress");
            JsonElement? json = null;
            if (!fresh && File.Exists(file))
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(file));
                json = doc.RootElement.Clone();
            }
            else if (!fresh && dir == null) throw new Exception("нет файла личного прогресса");

            // папка сейвов — новая
            if (dir == null) { Coop.RedirectSaves = false; }
            else { Coop.SaveDir = dir; Coop.RedirectSaves = true; Directory.CreateDirectory(dir); }

            step = "записать прогресс"; Mark(step); applied = true; Apply(p, json);
            step = "забыть кэш склянок"; Mark(step); ForgetCachedWorld(svc); Mark("готово");
            // Skills.Remirror() в меню не зовём — главный подозреваемый в падении игры (экрана навыков в меню нет);
            // навыки игра перечитает сама, когда загрузится лаборатория
            // в новую папку прогресс запишет сама игра при следующем сохранении
            Plugin.L.LogInfo($"SWITCH: сейв переключён без перезапуска → {(dir == null ? "личная игра" : dir)}{(fresh ? " (новая)" : "")}; опыт {p.ExperiencePoints.Value}, навыков {p.OwnedSkills.Count}");
            return true;
        }
        catch (Exception e)
        {
            // Прогресс в памяти уже переписан — назад папку НЕ возвращаем: иначе игра запишет чужой/пустой
            // прогресс в прежнюю папку (а это может быть личный сейв). Пусть пишет в новую.
            if (!applied) { Coop.RedirectSaves = oldRedirect; Coop.SaveDir = oldDir; }
            Plugin.L.LogWarning($"SWITCH: без перезапуска не вышло на шаге «{step}» ({e.Message}) — будет перезапуск");
            // лог игры при перезапуске затирается — пишем причину ещё и в potioncoop_diag.txt
            DiagPatches.Write($"SWITCH: не вышло на шаге «{step}»: {e}");
            return false;
        }
    }

    // ---- значения в объект, который держит игра ----
    static int Int(JsonElement? o, string name, int def = 0)
    {
        if (o == null || !o.Value.TryGetProperty(name, out var v)) return def;
        if (v.ValueKind == JsonValueKind.Number) return v.GetInt32();
        if (v.ValueKind == JsonValueKind.Object && v.TryGetProperty("_value", out var iv) && iv.ValueKind == JsonValueKind.Number) return iv.GetInt32();
        return def;
    }
    static bool Bool(JsonElement? o, string name) => o != null && o.Value.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.True;
    static List<int> List(JsonElement? o, string name)
    {
        var r = new List<int>();
        if (o == null || !o.Value.TryGetProperty(name, out var v)) return r;
        if (v.ValueKind == JsonValueKind.Object && v.TryGetProperty("_innerList", out var inner)) v = inner;
        if (v.ValueKind == JsonValueKind.Array) foreach (var x in v.EnumerateArray()) if (x.ValueKind == JsonValueKind.Number) r.Add(x.GetInt32());
        return r;
    }

    static void Apply(PlayerProgress p, JsonElement? o)
    {
        Mark("  мир лаборатории");
        p.GameStarted = Bool(o, "GameStarted");
        p.LabCompleted = Bool(o, "LabCompleted");
        p.LabSummaryShown = Bool(o, "LabSummaryShown");
        p.LabManifestHash = Int(o, "LabManifestHash");
        p.DeclinedManifestHash = Int(o, "DeclinedManifestHash");
        p.StartingExperience = Int(o, "StartingExperience");
        p.SelectedHandIndex = Int(o, "SelectedHandIndex");
        p.Misfiles = Int(o, "Misfiles");
        // обучающие подсказки — личные, их не трогаем

        Mark("  счётчики");
        p.TotalPlaytimeSeconds.Value = Int(o, "TotalPlaytimeSeconds");
        p.AchievementMoments.Value = Int(o, "AchievementMoments");
        p.PotionsFinished.Value = Int(o, "PotionsFinished");
        p.PotionsShelvedHome.Value = Int(o, "PotionsShelvedHome");
        p.Pours.Value = Int(o, "Pours");
        p.Undos.Value = Int(o, "Undos");
        p.VatPoursIn.Value = Int(o, "VatPoursIn");
        p.VatDrawsOut.Value = Int(o, "VatDrawsOut");
        p.ApronStows.Value = Int(o, "ApronStows");
        p.CatPets.Value = Int(o, "CatPets");

        Mark("  навыки");
        SetSkills(p.OwnedSkills, List(o, "OwnedSkills"));
        SetSkills(p.SwitchedOffSkills, List(o, "SwitchedOffSkills"));
        Mark("  засчитанные полки");
        SetInts(p.ScoredShelves, List(o, "ScoredShelves"));
        SetInts(p.ScoredSections, List(o, "ScoredSections"));
        Mark("  опыт");
        p.ExperiencePoints.Value = Int(o, "ExperiencePoints");
    }
    static void SetSkills(CodeBase.Common.Utilities.ReactiveList<eSkill> l, List<int> v) { l.Clear(); foreach (var x in v) l.Add((eSkill)x); }
    static void SetInts(CodeBase.Common.Utilities.ReactiveList<int> l, List<int> v) { l.Clear(); foreach (var x in v) l.Add(x); }

    // склянки из старой папки забываем — следующее чтение возьмёт новую
    static void ForgetCachedWorld(PersistentDataService svc)
    {
        // Удаление по одному ключу (ValueTuple<Type,int>) роняло игру целиком — чистим кэш склянок разом.
        // В «помеченных» данных лежат только части склянок (PotionsPersistentData_N): следующее чтение возьмёт новую папку.
        int n = svc._taggedData.Count;
        Mark($"  кэш склянок: {n} частей → очищаю");
        svc._taggedData.Clear();
        var pp = Sync.Persistence;
        if (pp != null)
        {
            Mark("  раскладка лаборатории");
            var vb = pp._validatedBuckets; if (vb != null) vb.Clear();
            pp._activeManifestResolved = false;
            pp._activeManifest = null;
        }
        Plugin.L.LogInfo($"SWITCH: забыл кэш склянок ({n} частей)");
    }
}

static class SwitchPatches
{
    [HarmonyPostfix]
    [HarmonyPatch(typeof(SkillsDataMediator), nameof(SkillsDataMediator.CalculatePointsUnspent))]
    static void Unspent_Post(SkillsDataMediator __instance) { SaveSwitch.Skills = __instance; }
}
