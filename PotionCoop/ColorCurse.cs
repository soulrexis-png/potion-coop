using System;
using HarmonyLib;
using UnityEngine;
using CodeBase.Components.Potions;
using CodeBase.Components.Vats;
using CodeBase.Services.ConfigsService;
using CodeBase.Services.DataMediatorService;
using CodeBase.Components.Potions.Liquid;

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
    static float _nextTickLog;

    // сколько секунд осталось (0, если духа нет)
    public static float Remaining => On ? Mathf.Max(0f, _until - Time.unscaledTime) : 0f;

    public static bool Shuffle => string.Equals(Plugin.ModBlindMode.Value?.Trim(), "shuffle", StringComparison.OrdinalIgnoreCase);

    public static void Set(bool on, string why)
    {
        // повторное «наслать» при уже включённом духе таймер НЕ продлевает (раньше молча перезапускало минуту)
        if (on && On)
        {
            Plugin.L.LogInfo($"MOD: Дальтоник-дух уже включён ({why}) — таймер не продлеваю, осталось {Remaining:0} с, {DateTime.Now:HH:mm:ss}");
            return;
        }
        if (on) { _until = Time.unscaledTime + Duration; _nextTickLog = Time.unscaledTime + 20f; }
        if (On == on) return;
        On = on;
        if (on) MakeMap();
        Plugin.L.LogInfo(on ? $"MOD: Дальтоник-дух ({why}, режим {(Shuffle ? "перемешка" : "блёклые цвета")}) на {Duration:0} с, {DateTime.Now:HH:mm:ss}" : $"MOD: Дальтоник-дух ушёл ({why}), {DateTime.Now:HH:mm:ss}");
        Coop.Say(on ? (Shuffle ? Loc.L("Дальтоник-дух перепутал тебе цвета! (пройдёт сам через минуту)", "The colour-blind spirit mixed up your colours! (wears off in a minute)") : Loc.L("Дальтоник-дух забрал у тебя цвета! (пройдёт сам через минуту)", "The colour-blind spirit stole your colours! (wears off in a minute)")) : Loc.L("Цвета вернулись", "Your colours are back"), 4f);
        Repaint();
    }

    public static void Tick()
    {
        if (!On) return;
        if (Time.unscaledTime > _until) { Set(false, "время вышло"); return; }
        if (Time.unscaledTime >= _nextTickLog) { _nextTickLog = Time.unscaledTime + 20f; Plugin.L.LogInfo($"MOD: Дальтоник-дух ещё {Remaining:0} с, {DateTime.Now:HH:mm:ss}"); }
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

    // цвета жидкостей игра один раз заливает в видеокарту (LiquidFamilyPalette) — мы правим их при заливке и заливаем заново
    public static LiquidLookConfig Cfg;
    static void Repaint()
    {
        try
        {
            if (Cfg == null)
                foreach (var o in Resources.FindObjectsOfTypeAll(Il2CppInterop.Runtime.Il2CppType.Of<LiquidLookConfig>())) { Cfg = o.TryCast<LiquidLookConfig>(); if (Cfg != null) break; }
            LiquidFamilyPalette.Invalidate();
            if (Cfg != null) LiquidFamilyPalette.EnsureUploaded(Cfg);
            Plugin.L.LogInfo($"MOD: палитра жидкостей перезалита ({(On ? "дух" : "обычная")})");
        }
        catch (Exception e) { Plugin.L.LogWarning("MOD: палитра: " + e.Message); }
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

    // при заливке палитры: слот = семейство
    static bool _packLogged;
    public static void Pack(int slot, ref Color hue, ref Color dark)
    {
        if (!On || _inside) return;
        if (!_packLogged) { _packLogged = true; Plugin.L.LogInfo($"MOD: дух перекрашивает палитру (слот {slot})"); }
        if (Shuffle)
        {
            if (slot <= 0 || slot >= _map.Length || _map[slot] == slot || Cfg == null) return;
            bool cb = false;
            _inside = true;
            try { hue = Cfg.ResolveColor((eSectionFamily)_map[slot], cb); dark = Cfg.ResolveColorDark((eSectionFamily)_map[slot], cb); }
            catch { }
            finally { _inside = false; }
        }
        else { hue = Fade(hue); dark = Fade(dark); }
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
    [HarmonyPrefix]
    [HarmonyPatch(typeof(LiquidFamilyPalette), nameof(LiquidFamilyPalette.Upload))]
    static void Upload_Pre(LiquidLookConfig config) { if (config != null) ColorCurse.Cfg = config; }

    [HarmonyPrefix]
    [HarmonyPatch(typeof(LiquidFamilyPalette), nameof(LiquidFamilyPalette.Pack))]
    static void Pack_Pre(int slot, ref Color hue, ref Color dark) => ColorCurse.Pack(slot, ref hue, ref dark);

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
