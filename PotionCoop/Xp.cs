using System;
using System.Collections.Generic;
using UnityEngine;
using CodeBase.Common.Utilities;
using CodeBase.Services.PersistentDataService;
using HarmonyLib;

namespace PotionCoop;

// Общий опыт: считает ТОЛЬКО хост, по своей картине мира. Гость свой прирост больше не присылает:
// раньше каждый игрок получал опыт за одну и ту же склянку у себя и слал хосту — опыт умножался на число игроков (п. 28).
// Склянки гостей хост засчитывает сам, когда их расстановка доходит до него. Гостю хост рассылает итог.
public static class Xp
{
    static int _last = int.MinValue;
    static float _next, _nextBroadcast;

    // Прогресс игрока ловим, когда игра сама к нему обращается (без generic-вызовов: они роняли игру)
    public static PlayerProgress Progress;
    static ReactiveInt Exp() => Progress?.ExperiencePoints;

    public static void Tick(Action<bool, int> send)
    {
        if (Coop.Role == Role.Solo || Coop.State != eGameState.Playing) { _last = int.MinValue; if (Coop.Role == Role.Solo) _hostTotal = int.MinValue; return; }
        if (Time.unscaledTime < _next) return;
        _next = Time.unscaledTime + 0.5f;
        var e = Exp(); if (e == null) return;
        TickScored();
        int cur = e.Value;
        if (_last == int.MinValue) { _last = cur; if (Coop.Role == Role.Host) send(false, cur); return; }

        if (cur != _last)
        {
            int delta = cur - _last; _last = cur;
            if (Coop.Role == Role.Host) { send(false, cur); _nextBroadcast = Time.unscaledTime + 5f; Plugin.L.LogInfo($"XP: {(delta > 0 ? "+" : "")}{delta}, всего {cur}"); }
            else if (Coop.Role == Role.Guest && delta > 0) _pendingSend += delta;   // гость: прирост — хосту
        }
        if (Coop.Role == Role.Guest && _pendingSend > 0) { send(true, _pendingSend); Plugin.L.LogInfo($"XP: +{_pendingSend} → хосту"); _pendingSend = 0; }
        // хост раз в 5 секунд повторяет итог — для тех, кто зашёл позже
        if (Coop.Role == Role.Host && Time.unscaledTime >= _nextBroadcast) { _nextBroadcast = Time.unscaledTime + 5f; send(false, cur); }
    }

    // Полки/секции, за которые опыт уже дали, — общие: иначе другой игрок снимет и поставит заново и получит опыт ещё раз
    static readonly HashSet<int> _knownShelves = new(), _knownSections = new();
    static bool _scoredPrimed;
    static void TickScored()
    {
        var p = Progress; if (p == null) return;
        try
        {
            Scan(p.ScoredShelves?._innerList, _knownShelves, 0);
            Scan(p.ScoredSections?._innerList, _knownSections, 1);
            _scoredPrimed = true;
        }
        catch { }
    }
    static void Scan(Il2CppSystem.Collections.Generic.List<int> l, HashSet<int> known, byte kind)
    {
        if (l == null) return;
        for (int i = 0; i < l.Count; i++)
            if (known.Add(l[i]) && _scoredPrimed) NetDriver.I?.BroadcastScored(kind, l[i]);
    }
    public static void MarkScored(byte kind, int id)
    {
        var p = Progress; if (p == null) return;
        try
        {
            var list = kind == 0 ? p.ScoredShelves : p.ScoredSections;
            (kind == 0 ? _knownShelves : _knownSections).Add(id);
            if (list != null && !list.Contains(id)) { list.Add(id); Plugin.L.LogInfo($"XP: {(kind == 0 ? "полка" : "секция")} {id} уже засчитана другим"); }
        }
        catch (Exception e) { Plugin.L.LogWarning("XP scored: " + e.Message); }
    }

    // ---- опыт гостей ----
    // Опыт гостю начисляет его же игра — за всё, как хосту: склянка на полке, доведённое зелье, полка, секция, переворот.
    // Гость шлёт хосту только прирост, хост прибавляет его к общему и рассылает итог.
    // Дюпа нет: каждое «засчитано» (склянка, полка, секция) расходится всем, и повторно за то же самое игра опыт не даёт (п. 28).
    static int _pendingSend;   // гость: заработал, но ещё не отправил
    public static void AddFromGuest(int delta, string who)
    {
        if (Coop.Role != Role.Host || delta <= 0 || delta > 500) return;   // защита от мусора
        var e = Exp(); if (e == null) return;
        e.Value = e.Value + delta;
        Plugin.L.LogInfo($"XP: {who} заработал +{delta}");
    }

    static int _hostTotal = int.MinValue;
    public static void SetTotal(int total)
    {
        _hostTotal = total;
        var e = Exp(); if (e == null) return;
        // то, что гость заработал после прошлой проверки и ещё не отправил, не теряем: оставляем сверху и отправим следующим тактом
        if (_last != int.MinValue && e.Value > _last) _pendingSend += e.Value - _last;
        int show = total + _pendingSend;
        if (e.Value != show) e.Value = show;
        _last = show;
    }
}

static class XpPatches
{
    [HarmonyPostfix]
    [HarmonyPatch(typeof(PlayerProgress), nameof(PlayerProgress.EarnedExperience), MethodType.Getter)]
    static void Earned_Post(PlayerProgress __instance) => Xp.Progress = __instance;

    [HarmonyPostfix]
    [HarmonyPatch(typeof(PlayerProgress), nameof(PlayerProgress.BankEverySkillPoint))]
    static void Bank_Post(PlayerProgress __instance) => Xp.Progress = __instance;

    [HarmonyPostfix]
    [HarmonyPatch(typeof(PlayerProgress), nameof(PlayerProgress.PlaythroughQuarters), MethodType.Getter)]
    static void Quarters_Post(PlayerProgress __instance) => Xp.Progress = __instance;
}
