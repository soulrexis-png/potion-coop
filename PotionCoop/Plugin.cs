using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using BepInEx.Unity.IL2CPP;
using HarmonyLib;
using CodeBase.Components.Hero;
using CodeBase.Components.Potions;
using CodeBase.Components.Slots;
using CodeBase.Components.Vats;
using CodeBase.Services.UndoService;

namespace PotionCoop;

[BepInPlugin("roma.potioncoop", "PotionCoop", "0.0.27")]
public class Plugin : BasePlugin
{
    internal static ManualLogSource L;
    internal static ConfigEntry<float> AvatarScale;
    internal static ConfigEntry<int> AvatarColor;
    internal static ConfigEntry<int> HandBonus, ApronBonus;
    internal static ConfigEntry<bool> ModFatigue;
    internal static ConfigEntry<bool> ModBreak;
    internal static ConfigEntry<bool> ModLeaky, ModQuake, ModBlind;
    internal static ConfigEntry<string> ModBlindMode;
    internal static ConfigEntry<float> ModBreakChance;
    internal static ConfigEntry<float> ModEveryMin, ModEveryMax;

    public override void Load()
    {
        L = Log;
        Coop.CheckJoinFlag();
        AvatarScale = Config.Bind("Avatar", "Size", 0.75f, "Размер аптекарей других игроков (1 = как в превью)");
        AvatarColor = Config.Bind("Avatar", "Color", -1, "Какой цвет аптекаря хочу (0–11, -1 — любой свободный). Меняется кнопкой «Мой цвет» в «Общих аптеках»");
        if (UnityEngine.Mathf.Abs(AvatarScale.Value - 0.85f) < 0.001f) AvatarScale.Value = 0.75f;   // старое значение по умолчанию → на 12% меньше
        ModBreak = Config.Bind("Modifiers", "FragileFlasks", true, "Хрупкие склянки: брошенная склянка может разбиться, зелье разольётся по соседним колбам (решает хост)");
        ModBreakChance = Config.Bind("Modifiers", "FragileChancePercent", 4f, "Шанс разбить склянку при броске, в процентах");
        ModFatigue = Config.Bind("Modifiers", "Fatigue", true, "Переутомление: часть игроков замедляется, лечится котом (решает хост)");
        ModLeaky = Config.Bind("Modifiers", "LeakyPocket", true, "Дырявый карман: у случайного игрока из рук выпадает 1–2 зелья (решает хост)");
        ModQuake = Config.Bind("Modifiers", "Earthquake", true, "Землетрясение: зелья на полу и неготовых полках разлетаются (решает хост)");
        ModBlind = Config.Bind("Modifiers", "ColorBlindSpirit", true, "Дальтоник-дух: на минуту портит цвета жидкостей у одного игрока (решает хост)");
        ModBlindMode = Config.Bind("Modifiers", "ColorBlindMode", "fade", "Как портит: fade — цвета блёкнут почти до серого; shuffle — цвета семейств перепутаны");
        ModEveryMin = Config.Bind("Modifiers", "EveryMinutesMin", 4f, "Как часто хост насылает модификатор: от (минут)");
        ModEveryMax = Config.Bind("Modifiers", "EveryMinutesMax", 8f, "...до (минут)");
        HandBonus = Config.Bind("Buffs", "HandExtra", 0, "Сколько склянок добавить к руке (0 — как в игре). Ставь одинаково у всех, иначе нечестно");
        ApronBonus = Config.Bind("Buffs", "ApronExtra", 0, "Сколько склянок добавить к фартуку (0 — как в игре)");
        if (VersionGuard.Check())
        {
            AddComponent<GameChangedBanner>();   // игра обновилась — ничего не патчим, только предупреждаем
            return;
        }
        var harmony = new Harmony("roma.potioncoop");
        harmony.PatchAll(typeof(Patches));
        harmony.PatchAll(typeof(CoopPatches));
        harmony.PatchAll(typeof(SyncPatches));
        harmony.PatchAll(typeof(ModifierPatches));
        try { harmony.PatchAll(typeof(XpPatches)); } catch (System.Exception e) { L.LogWarning("XP patches: " + e.Message); }
        try { harmony.PatchAll(typeof(UndoPatches)); } catch (System.Exception e) { L.LogWarning("UNDO patches: " + e.Message); }
        try { harmony.PatchAll(typeof(SkillCostPatches)); } catch (System.Exception e) { L.LogWarning("SKILL cost patches: " + e.Message); }
        try { harmony.PatchAll(typeof(ColorCursePatches)); } catch (System.Exception e) { L.LogWarning("COLOR patches: " + e.Message); }
        try { harmony.PatchAll(typeof(CapacityPatches)); } catch (System.Exception e) { L.LogWarning("CAPACITY patches: " + e.Message); }
        try { harmony.PatchAll(typeof(LetterPatches)); } catch (System.Exception e) { L.LogWarning("LETTER patches: " + e.Message); }
        try { harmony.PatchAll(typeof(DiagPatches)); } catch (System.Exception e) { L.LogWarning("DIAG patches: " + e.Message); }
        AddComponent<NetDriver>();
        L.LogInfo("=== PotionCoop 0.0.27 loaded ===");
    }
}

// Шаг 3: ничего не меняем, только пишем в лог, что делает игрок.
static class Patches
{
    static string P(PotionFacade p)
    {
        if (p == null) return "-";
        var c = p.Contents;
        if (c == null) return $"potion#{p.Uid}";
        string top;
        try { top = c.IsEmpty ? "empty" : c.TopFamily.ToString(); }
        catch { top = "?"; }
        return $"potion#{p.Uid}({c.Count}/{c.Capacity}, top={top})";
    }

    static string S(SlotFacade s) => s == null ? "-" : $"slot#{s.Uid}";
    static string V(VatFacade v) => v == null ? "-" : $"vat#{v.Uid}";

    // Любое завершённое действие игрока попадает в undo — ловим всё одним хуком
    [HarmonyPostfix]
    [HarmonyPatch(typeof(UndoService), nameof(UndoService.Record))]
    static void UndoRecord_Post(UndoRecord record)
    {
        try
        {
            Plugin.L.LogInfo(
                $"MOVE {record.Kind}: giver={P(record.Giver)} taker={P(record.Taker)} " +
                $"potion={P(record.Potion)} vat={V(record.Vat)} slot={S(record.Slot)} " +
                $"family={record.Family} units={record.Units} row={record.RowIndex}");
        }
        catch (System.Exception e) { Plugin.L.LogError("UndoRecord hook: " + e); }
    }

    // Взятие зелья в руку в undo не пишется — ловим отдельно
    [HarmonyPostfix]
    [HarmonyPatch(typeof(HeroPotionPicker), nameof(HeroPotionPicker.Grab))]
    static void Grab_Post(PotionFacade potion, SlotFacade fromSlot, bool __result)
    {
        try { Plugin.L.LogInfo($"GRAB ok={__result}: {P(potion)} from={S(fromSlot)}"); }
        catch (System.Exception e) { Plugin.L.LogError("Grab hook: " + e); }
    }

    [HarmonyPostfix]
    [HarmonyPatch(typeof(HeroPotionPicker), nameof(HeroPotionPicker.GrabIntoApron))]
    static void GrabApron_Post(PotionFacade potion, SlotFacade fromSlot, bool __result)
    {
        try { Plugin.L.LogInfo($"GRAB->APRON ok={__result}: {P(potion)} from={S(fromSlot)}"); }
        catch (System.Exception e) { Plugin.L.LogError("GrabIntoApron hook: " + e); }
    }
}
