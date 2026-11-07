using System;
using HarmonyLib;
using UnityEngine;
using CodeBase.Components.Hero;

namespace PotionCoop;

// Модификаторы сложности. Решает хост, у каждого игрока применяется своё.
public static class Modifiers
{
    public const byte Fatigue = 1, Leaky = 2, Quake = 3, Blind = 4, ClearAll = 9;
    // хост выключил модификаторы кнопкой (F8 в лаборатории) — до повторного нажатия или перезапуска
    public static bool Paused;

    // ---- Переутомление: ходишь в 3 раза медленнее, пока не погладишь кота ----
    public static bool Tired;
    public static float TiredSince;
    public const float TiredSpeed = 0.35f;

    public static void SetTired(bool on, string why)
    {
        if (Tired == on) return;
        Tired = on; TiredSince = Time.unscaledTime;
        Plugin.L.LogInfo(on ? $"MOD: переутомление ({why}) — найди кота и погладь" : $"MOD: бодрячком ({why})");
        NetDriver.I?.OnLocalModChanged(Fatigue, on);
    }

    // ---- хост: случайные события ----
    static float _nextRoll;

    public static void HostTick(System.Collections.Generic.IList<ulong> players, Action<byte, ulong, bool> send)
    {
        if (Coop.Role != Role.Host || Coop.State != eGameState.Playing || Paused) return;
        var enabled = new System.Collections.Generic.List<byte>();
        if (Plugin.ModFatigue.Value) enabled.Add(Fatigue);
        if (Plugin.ModLeaky.Value) enabled.Add(Leaky);
        if (Plugin.ModQuake.Value) enabled.Add(Quake);
        // Дальтоник-дух пока не работает (цвета не меняются) — в случайные события не берём, пока не починим
        if (enabled.Count == 0) return;
        float now = Time.unscaledTime;
        if (_nextRoll == 0) { _nextRoll = now + Next(); return; }
        if (now < _nextRoll) return;
        _nextRoll = now + Next();
        byte pick = enabled[UnityEngine.Random.Range(0, enabled.Count)];
        if (pick == Quake) { send(Quake, 0, true); Plugin.L.LogInfo("MOD: хост устроил землетрясение"); return; }
        if (pick == Blind) { var t = players[UnityEngine.Random.Range(0, players.Count)]; send(Blind, t, true); Plugin.L.LogInfo("MOD: Дальтоник-дух"); return; }
        if (pick == Leaky) { var t = players[UnityEngine.Random.Range(0, players.Count)]; send(Leaky, t, true); Plugin.L.LogInfo("MOD: дырявый карман"); return; }

        // устаёт примерно треть команды, минимум один
        int n = Mathf.Max(1, Mathf.CeilToInt(players.Count / 3f));
        var pool = new System.Collections.Generic.List<ulong>(players);
        for (int i = 0; i < n && pool.Count > 0; i++)
        {
            int k = UnityEngine.Random.Range(0, pool.Count);
            send(Fatigue, pool[k], true);
            pool.RemoveAt(k);
        }
        Plugin.L.LogInfo($"MOD: хост наслал переутомление на {n} игр.");
    }

    static float Next()
    {
        float min = Mathf.Max(0.5f, Plugin.ModEveryMin.Value), max = Mathf.Max(min, Plugin.ModEveryMax.Value);
        return UnityEngine.Random.Range(min, max) * 60f;
    }
}

static class ModifierPatches
{
    // медленный шаг
    [HarmonyPostfix]
    [HarmonyPatch(typeof(HeroMover), nameof(HeroMover.GetTargetSpeed))]
    static void Speed_Post(ref float __result)
    {
        if (Modifiers.Tired) __result *= Modifiers.TiredSpeed;
    }

    // погладил кота — прошло. Ловим и у героя, и у самого кота: игра могла встроить один из вызовов
    [HarmonyPostfix]
    [HarmonyPatch(typeof(HeroPotionPicker), nameof(HeroPotionPicker.PetCat))]
    static void PetCat_Post()
    {
        Plugin.L.LogInfo("MOD: герой гладит кота");
        if (Modifiers.Tired) Modifiers.SetTired(false, "погладил кота");
    }

    [HarmonyPostfix]
    [HarmonyPatch(typeof(CodeBase.Components.Cat.CatFacade), nameof(CodeBase.Components.Cat.CatFacade.Pet))]
    static void CatPet_Post()
    {
        Plugin.L.LogInfo("MOD: кота погладили");
        if (Modifiers.Tired) Modifiers.SetTired(false, "погладил кота");
    }
}
