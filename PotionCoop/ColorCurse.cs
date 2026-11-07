using System;
using HarmonyLib;
using UnityEngine;
using CodeBase.Components.Potions;
using CodeBase.Components.Vats;
using CodeBase.Services.ConfigsService;
using CodeBase.Services.DataMediatorService;

namespace PotionCoop;

// «Дальтоник-дух»: на минуту портит цвета жидкостей у одного игрока.
// Режим fade — цвета блёкнут почти до серого; shuffle — цвета семейств перепутаны.
public static class ColorCurse
{
    public static bool On;
    static float _until;
    static readonly int[] _map = new int[14];   // семейство → чей цвет показываем (для shuffle)
    static bool _inside;                        // защита от рекурсии
    public const float Duration = 60f;

    public static bool Shuffle => string.Equals(Plugin.ModBlindMode.Value?.Trim(), "shuffle", StringComparison.OrdinalIgnoreCase);

    public static void Set(bool on, string why)
    {
        if (on) _until = Time.unscaledTime + Duration;
        if (On == on) return;
        On = on;
        if (on) MakeMap();
        Plugin.L.LogInfo(on ? $"MOD: Дальтоник-дух ({why}, режим {(Shuffle ? "перемешка" : "блёклые цвета")})" : "MOD: Дальтоник-дух ушёл");
        Coop.Say(on ? (Shuffle ? Loc.L("Дальтоник-дух перепутал тебе цвета! (минута)", "The colour-blind spirit mixed up your colours! (one minute)") : Loc.L("Дальтоник-дух забрал у тебя цвета! (минута)", "The colour-blind spirit stole your colours! (one minute)")) : Loc.L("Цвета вернулись", "Your colours are back"), 4f);
        Repaint();
    }

    public static void Tick()
    {
        if (On && Time.unscaledTime > _until) Set(false, "время вышло");
    }

    static void MakeMap()
    {
        for (int i = 0; i < _map.Length; i++) _map[i] = i;
        // перемешиваем 1..13 так, чтобы ни одно семейство не осталось своим цветом
        for (int tries = 0; tries < 50; tries++)
        {
            for (int i = 13; i > 1; i--) { int j = UnityEngine.Random.Range(1, i + 1); (_map[i], _map[j]) = (_map[j], _map[i]); }
            bool ok = true; for (int i = 1; i < 14; i++) if (_map[i] == i) { ok = false; break; }
            if (ok) break;
        }
    }

    static void Repaint()
    {
        try
        {
            foreach (var p in UnityEngine.Object.FindObjectsByType<PotionFacade>(FindObjectsInactive.Exclude, FindObjectsSortMode.None)) try { p.RepaintLiquid(); } catch { }
            foreach (var v in UnityEngine.Object.FindObjectsByType<VatFacade>(FindObjectsInactive.Exclude, FindObjectsSortMode.None)) try { v.RepaintLiquid(); } catch { }
        }
        catch (Exception e) { Plugin.L.LogWarning("MOD: перекраска: " + e.Message); }
    }

    static Color Fade(Color c)
    {
        float l = 0.299f * c.r + 0.587f * c.g + 0.114f * c.b;
        var gray = new Color(l * 0.8f + 0.1f, l * 0.8f + 0.1f, l * 0.8f + 0.1f, c.a);
        return Color.Lerp(c, gray, 0.85f);
    }

    public static void Apply(LiquidLookConfig cfg, eSectionFamily family, bool colorBlind, bool dark, ref Color result)
    {
        if (!On || _inside) return;
        if (Shuffle)
        {
            int f = (int)family;
            if (f <= 0 || f >= _map.Length || _map[f] == f) return;
            _inside = true;
            try { result = dark ? cfg.ResolveColorDark((eSectionFamily)_map[f], colorBlind) : cfg.ResolveColor((eSectionFamily)_map[f], colorBlind); }
            finally { _inside = false; }
        }
        else result = Fade(result);
    }
}

static class ColorCursePatches
{
    [HarmonyPostfix]
    [HarmonyPatch(typeof(LiquidLookConfig), nameof(LiquidLookConfig.ResolveColor))]
    static void Color_Post(LiquidLookConfig __instance, eSectionFamily family, bool colorBlind, ref Color __result)
        => ColorCurse.Apply(__instance, family, colorBlind, false, ref __result);

    [HarmonyPostfix]
    [HarmonyPatch(typeof(LiquidLookConfig), nameof(LiquidLookConfig.ResolveColorDark))]
    static void Dark_Post(LiquidLookConfig __instance, eSectionFamily family, bool colorBlind, ref Color __result)
        => ColorCurse.Apply(__instance, family, colorBlind, true, ref __result);

    // узоры для дальтоников выдали бы цвет — пока дух рядом, их нет
    [HarmonyPostfix]
    [HarmonyPatch(typeof(FamilyPaletteDataMediator), nameof(FamilyPaletteDataMediator.UseLiquidPatterns), MethodType.Getter)]
    static void Patterns_Post(ref bool __result) { if (ColorCurse.On) __result = false; }
}
