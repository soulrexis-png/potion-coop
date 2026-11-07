using System;
using System.Collections.Generic;
using System.Linq;
using HarmonyLib;
using UnityEngine;
using UnityEngine.InputSystem;
using CodeBase.Services.UI.Mediator;

namespace PotionCoop;

// Инструкция к моду — в родном окне вступительного письма игры (бумага, сургуч), только со своим текстом.
// Открывается из колеса эмоций («Письмо»), закрывается кнопкой письма, Enter, Пробелом или Esc.
// Если окно игры открыть не удалось — показываем свою «бумажку» поверх экрана.
public static class Letter
{
    public static IUIService Ui;   // берём, когда игра собирает кнопку «Продолжить» (см. патч ниже)
    static CodeBase.Components.UI.UIWindowFacade _win;
    static bool _fallback;
    static CursorLockMode _lock; static bool _cursor;
    static CodeBase.Components.Hero.HeroLook _look;
    public static bool Open => _win != null || _fallback;

    static string Title => Loc.Ru ? "Общая аптека" : "Shared pharmacy";
    static string Body => Loc.Ru
        ? "Дорогой аптекарь!\n\n" +
          "Ты не один в этой лаборатории. Всё, что ты берёшь, переливаешь и ставишь, видят остальные — и наоборот.\n\n" +
          "• T (держать) — колесо эмоций, отпусти на нужной\n" +
          "• Колёсико мыши или F2 — «я тут»: всем видно, где ты\n" +
          "• Из чужих рук склянку можно отобрать. Кражи считаются\n" +
          "• «Шаг назад» не вернёт склянку, которую уже тронул другой\n" +
          "• Ушёл — твои склянки ждут на крючке, там, где ты появился\n" +
          "• F9 — манекен, чтобы проверить что-то одному\n" +
          "• F11 — собрать логи, если что-то сломалось\n\n" +
          "Иногда случается всякое:\n" +
          "• Землетрясение — склянки с пола и недособранных полок разлетаются\n" +
          "• Хрупкие склянки — брошенная может разбиться, зелье разольётся по соседним\n" +
          "• Дырявый карман — из рук выпадает одна-две склянки\n" +
          "• Переутомление — ходишь медленно, пока не погладишь кота\n" +
          "Хост: F8 — выключить все модификаторы у всех, если что-то пошло не так (ещё раз — включить)\n\n" +
          "Удачной смены!"
        : "Dear apothecary,\n\n" +
          "You are not alone in this lab. Everything you take, pour and shelve, the others see too — and the other way round.\n\n" +
          "• T (hold) — emote wheel, release on the one you want\n" +
          "• Middle mouse or F2 — \"I'm here\": everyone sees where you are\n" +
          "• You can snatch a potion out of someone's hands. Steals are counted\n" +
          "• Undo won't bring back a potion someone else has touched\n" +
          "• Leave — and your potions wait on a hook where you appeared\n" +
          "• F9 — a dummy to try things alone\n" +
          "• F11 — collect logs if something breaks\n\n" +
          "Now and then things happen:\n" +
          "• Earthquake — potions on the floor and on unfinished shelves go flying\n" +
          "• Fragile flasks — a thrown flask may break and spill into its neighbours\n" +
          "• Leaky pocket — one or two potions slip out of your hands\n" +
          "• Exhaustion — you move slowly until you pet the cat\n" +
          "Host: F8 — turn all modifiers off for everyone if something goes wrong (again — back on)\n\n" +
          "Have a good shift!";

    public static void Toggle() { if (Open) Close(); else Show(); }

    public static void Show()
    {
        if (Open) return;
        _lock = Cursor.lockState; _cursor = Cursor.visible;
        _look = UnityEngine.Object.FindFirstObjectByType<CodeBase.Components.Hero.HeroLook>();
        if (_look != null) _look.enabled = false;
        Cursor.lockState = CursorLockMode.None; Cursor.visible = true;
        try
        {
            if (Ui == null) throw new Exception("нет сервиса окон игры");
            _win = Ui.OpenWindowSync(eUIWindow.IntroLetter, false, false);
            if (_win == null) throw new Exception("окно письма не открылось");
            Fill(_win.gameObject);
            Plugin.L.LogInfo("LETTER: открыл письмо-инструкцию");
        }
        catch (Exception e)
        {
            Plugin.L.LogWarning("LETTER: родное письмо не вышло (" + e.Message + ") — показываю своё");
            _win = null; _fallback = true;
        }
    }

    // в окне письма: самый длинный текст — тело письма, самый короткий непустой — заголовок, остальное прячем
    static void Fill(GameObject go)
    {
        foreach (var mb in go.GetComponentsInChildren<MonoBehaviour>(true))
        {
            if (mb == null) continue;
            var n = mb.GetIl2CppType().Name;
            if (n.Contains("Locali") || n == "LetterVoicePlayer") UnityEngine.Object.Destroy(mb);   // иначе вернут родной текст / зачитают его голосом
        }
        var texts = go.GetComponentsInChildren<TMPro.TMP_Text>(true).Where(t => t != null && !string.IsNullOrWhiteSpace(t.text)).OrderByDescending(t => t.text.Length).ToList();
        if (texts.Count == 0) throw new Exception("в окне письма нет текста");
        var body = texts[0];
        body.text = Body; body.enableAutoSizing = true; body.fontSizeMin = 10;
        // кнопка письма (если есть) — её подпись не трогаем
        var rest = texts.Skip(1).Where(t => t.GetComponentInParent<CodeBase.Components.UI.AdvancedButton>() == null).ToList();
        if (rest.Count > 0) { var title = rest.OrderBy(t => t.text.Length).First(); title.text = Title; rest.Remove(title); }
        foreach (var t in rest) t.text = "";
    }

    public static void Close()
    {
        try { if (_win != null && Ui != null) Ui.Close(eUIWindow.IntroLetter, false); } catch (Exception e) { Plugin.L.LogWarning("LETTER close: " + e.Message); }
        _win = null; _fallback = false;
        if (_look != null) { _look.enabled = true; _look = null; }
        Cursor.lockState = _lock; Cursor.visible = _cursor;
    }

    public static void Tick()
    {
        if (!Open) return;
        if (Coop.State != eGameState.Playing) { Close(); return; }
        // окно закрыли его собственной кнопкой
        try { if (_win != null && Ui != null && !Ui.IsOpen(eUIWindow.IntroLetter)) { _win = null; Close(); return; } } catch { }
        var kb = Keyboard.current;
        if (kb != null && (kb.enterKey.wasPressedThisFrame || kb.spaceKey.wasPressedThisFrame || kb.escapeKey.wasPressedThisFrame)) Close();
    }

    // запасная «бумажка» — если родное окно не открылось
    static GUIStyle _paper, _text, _head;
    public static void DrawFallback()
    {
        if (!_fallback) return;
        if (_paper == null)
        {
            var t = new Texture2D(2, 2) { hideFlags = HideFlags.HideAndDontSave };
            var c = new Color(0.96f, 0.91f, 0.80f, 0.98f);
            t.SetPixels(new[] { c, c, c, c }); t.Apply();
            _paper = new GUIStyle { normal = { background = t } };
            _text = new GUIStyle(GUI.skin.label) { fontSize = 17, wordWrap = true, normal = { textColor = new Color(0.25f, 0.17f, 0.1f) } };
            _head = new GUIStyle(_text) { fontSize = 26, fontStyle = FontStyle.Bold, alignment = TextAnchor.MiddleCenter };
        }
        float w = 640, h = 620, x = (Screen.width - w) / 2, y = (Screen.height - h) / 2;
        GUI.Box(new Rect(x, y, w, h), GUIContent.none, _paper);
        GUI.Label(new Rect(x, y + 20, w, 40), Title, _head);
        GUI.Label(new Rect(x + 40, y + 70, w - 80, h - 120), Body, _text);
        _text.alignment = TextAnchor.MiddleCenter;
        GUI.Label(new Rect(x, y + h - 44, w, 30), Loc.Ru ? "Enter / Пробел / Esc — закрыть" : "Enter / Space / Esc — close", _text);
        _text.alignment = TextAnchor.UpperLeft;
    }
}

static class LetterPatches
{
    // сервис окон игры приходит в кнопку «Продолжить» при сборке меню
    [HarmonyPostfix]
    [HarmonyPatch(typeof(CodeBase.Components.UI.AdvancedButtonContinueStarter), nameof(CodeBase.Components.UI.AdvancedButtonContinueStarter.Construct))]
    static void Construct_Post(IUIService uiService) { if (uiService != null) Letter.Ui = uiService; }
}
