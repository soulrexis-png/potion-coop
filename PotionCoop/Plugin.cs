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

[BepInPlugin("roma.potioncoop", "PotionCoop", "0.0.61")]
public class Plugin : BasePlugin
{
    internal static ManualLogSource L;
    internal static ConfigEntry<float> AvatarScale;
    internal static ConfigEntry<int> AvatarColor;
    internal static ConfigEntry<bool> NoRestart;
    internal static ConfigEntry<int> HandBonus, ApronBonus;
    internal static ConfigEntry<bool> ModFatigue;
    internal static ConfigEntry<bool> ModBreak;
    internal static ConfigEntry<bool> ModLeaky, ModQuake, ModBlind;
    internal static ConfigEntry<string> ModBlindMode;
    internal static ConfigEntry<float> ModBreakChance;
    internal static ConfigEntry<float> ModEveryMin, ModEveryMax;
    internal static ConfigEntry<bool> PushOn;
    internal static ConfigEntry<int> PushEvery;
    internal static ConfigEntry<bool> StrictRooms;
    // Настройки, от которых зависит честность. У гостя действуют значения хоста (MsgCfg), а не его собственный конфиг.
    internal static bool HostCfg; internal static int HostHand, HostApron, HostPushEvery = 8;
    internal static bool UseHostCfg => HostCfg && Coop.Role == Role.Guest;
    internal static int EffHand => System.Math.Max(0, UseHostCfg ? HostHand : HandBonus.Value);
    internal static int EffApron => System.Math.Max(0, UseHostCfg ? HostApron : ApronBonus.Value);
    internal static int EffPushEvery => System.Math.Max(1, UseHostCfg ? HostPushEvery : PushEvery.Value);

    public override void Load()
    {
        L = Log;
        Coop.CheckJoinFlag();
        AvatarScale = Config.Bind("Avatar", "Size", 0.75f, "Размер аптекарей других игроков (1 = как в превью) / Size of other players' apothecaries (1 = preview size)");
        NoRestart = Config.Bind("Rooms", "SwitchWithoutRestart", true, "Переключать аптеки прямо в меню, без перезапуска игры (если не выйдет — перезапуск) / Switch pharmacies right in the menu without restarting the game (falls back to a restart)");
        AvatarColor = Config.Bind("Avatar", "Color", -1, "Какой цвет аптекаря хочу (0–11, -1 — любой свободный). Меняется кнопкой «Мой цвет» в «Общих аптеках» / Apothecary colour I want (0–11, -1 = any free one). Changed with \"My color\" under Shared pharmacies");
        if (UnityEngine.Mathf.Abs(AvatarScale.Value - 0.85f) < 0.001f) AvatarScale.Value = 0.75f;   // старое значение по умолчанию → на 12% меньше
        ModBreak = Config.Bind("Modifiers", "FragileFlasks", true, "Хрупкие склянки: брошенная склянка может разбиться, зелье разольётся по соседним колбам (решает хост) / Fragile flasks: a thrown flask may break and spill into neighbours (host decides)");
        ModBreakChance = Config.Bind("Modifiers", "FragileChancePercent", 4f, "Шанс разбить склянку при броске, в процентах / Chance to break a flask on a throw, percent");
        ModFatigue = Config.Bind("Modifiers", "Fatigue", true, "Переутомление: часть игроков замедляется, лечится котом (решает хост) / Exhaustion: some players slow down until they pet the cat (host decides)");
        ModLeaky = Config.Bind("Modifiers", "LeakyPocket", true, "Дырявый карман: у случайного игрока из рук выпадает 1–2 зелья (решает хост) / Leaky pocket: a random player drops 1–2 potions (host decides)");
        ModQuake = Config.Bind("Modifiers", "Earthquake", true, "Землетрясение: зелья на полу и неготовых полках разлетаются (решает хост) / Earthquake: potions on the floor and unfinished shelves go flying (host decides)");
        ModBlind = Config.Bind("Modifiers", "ColorBlindSpirit", true, "Дальтоник-дух: на минуту портит цвета жидкостей у одного игрока (решает хост) / Colour-blind spirit: spoils one player's liquid colours for a minute (host decides)");
        PushOn = Config.Bind("Perks", "Shove", true, "Толчок: за правильно поставленные склянки копится заряд, H — у игрока рядом выпадают зелья / Shove: correct shelvings charge it, H makes a nearby player drop potions");
        PushEvery = Config.Bind("Perks", "ShoveEvery", 8, "Сколько склянок нужно для заряда толчка / Correct shelvings needed to charge the shove");
        ModBlindMode = Config.Bind("Modifiers", "ColorBlindMode", "fade", "Как портит: fade — цвета блёкнут почти до серого; shuffle — цвета семейств перепутаны / How the spirit spoils colours: fade or shuffle");
        ModEveryMin = Config.Bind("Modifiers", "EveryMinutesMin", 4f, "Как часто хост насылает модификатор: от (минут) / How often the host sends a modifier: from (minutes)");
        ModEveryMax = Config.Bind("Modifiers", "EveryMinutesMax", 8f, "...до (минут) / ...to (minutes)");
        HandBonus = Config.Bind("Buffs", "HandExtra", 0, "Сколько склянок добавить к руке (0 — как в игре). Ставь одинаково у всех, иначе нечестно / Extra hand slots (0 = as in the game). Set the same for everyone");
        StrictRooms = Config.Bind("Rooms", "StrictRooms", true, "Игровые сообщения (ходы, положение, толчки, логи) — только между участниками одной аптеки. Выключать только для отладки и сразу у всех / Game messages only between members of the same pharmacy. Turn off for debugging only, for everyone at once");
        ApronBonus = Config.Bind("Buffs", "ApronExtra", 0, "Сколько склянок добавить к фартуку (0 — как в игре) / Extra apron slots (0 = as in the game)");
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
        try { CapacityPatches.Apply(harmony); } catch (System.Exception e) { L.LogWarning("CAPACITY patches: " + e.Message); }
        try { harmony.PatchAll(typeof(SwitchPatches)); } catch (System.Exception e) { L.LogWarning("SWITCH patches: " + e.Message); }
        try { harmony.PatchAll(typeof(TourneyLocPatches)); } catch (System.Exception e) { L.LogWarning("TOURNEY loc patches: " + e.Message); }
        try { harmony.PatchAll(typeof(LetterPatches)); } catch (System.Exception e) { L.LogWarning("LETTER patches: " + e.Message); }
        try { harmony.PatchAll(typeof(DiagPatches)); } catch (System.Exception e) { L.LogWarning("DIAG patches: " + e.Message); }
        AddComponent<NetDriver>();
        L.LogInfo("=== PotionCoop 0.0.61 loaded ===");
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
