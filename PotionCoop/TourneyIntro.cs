using System;
using UnityEngine;
using UnityEngine.UI;

namespace PotionCoop;

// Соревнование: без вводного письма и обучения. Уроки помечаем пройденными в прогрессе,
// а если игра всё равно открыла письмо или окно урока — закрываем их сами.
public static class TourneyIntro
{
    static float _until;   // сколько ещё следим за окнами обучения после старта уровня
    static bool _done;     // уроки уже помечены пройденными в этой аптеке
    public static void Reset() => _done = false;

    public static void OnState(eGameState id)
    {
        // сервис сейвов трогаем только до загрузки уровня (NewGame); дальше — только свежий прогресс
        if (!_done) _done = SaveSwitch.SkipOnboarding(allowService: id == eGameState.NewGame);
        _until = Time.unscaledTime + 20f;
    }

    // из Update
    public static void Tick()
    {
        if (!Tourney.On || Time.unscaledTime > _until) return;
        if (Time.frameCount % 10 != 0) return;
        try
        {
            // вводное письмо: жмём его кнопку «закрыть», как игрок
            var letter = GameObject.Find("IntroLetterWindow(Clone)");
            if (letter != null && letter.activeInHierarchy)
            {
                foreach (var b in letter.GetComponentsInChildren<Button>(true))
                    if (b.gameObject.name.Contains("Dismiss")) { b.onClick.Invoke(); Plugin.L.LogInfo("TOURNEY: вводное письмо закрыто"); break; }
            }
            // окна уроков (первое наливание, первая полка) — прячем
            foreach (var n in new[] { "OnboardingFirstPourWindow(Clone)", "OnboardingFirstShelveWindow(Clone)" })
            {
                var w = GameObject.Find(n);
                if (w != null && w.activeSelf) { w.SetActive(false); Plugin.L.LogInfo($"TOURNEY: урок «{n}» скрыт"); }
            }
        }
        catch (Exception e) { Plugin.L.LogWarning("TOURNEY intro: " + e.Message); _until = 0; }
    }
}
