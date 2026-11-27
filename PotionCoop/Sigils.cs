using System;
using System.Collections.Generic;
using CodeBase.Components.UI;
using UnityEngine;
using UnityEngine.UI;
using TMPro;

namespace PotionCoop;

// Эмблемы подвидов на табло турнира — те же кружки, что игра рисует внизу экрана при наведении на флакон.
// Игра хранит эмблемы в массиве текстур (PotionSigilConfig) и рисует их своим материалом на RawImage
// поверх цветного кружка. Мы берём у её «карточки зелья» (PotionCardPainter) кружок, обводку, материал
// и массив — и собираем такие же кружки на своём холсте поверх экрана.
public static class Sigils
{
    static bool _tried, _ok;
    static Sprite _circle, _outline;
    static Material _sigilMat;
    static CodeBase.Services.ConfigsService.PotionSigilConfig _cfg;
    static int _arrayId, _indexId;
    static Canvas _canvas;
    static TMP_FontAsset _font;
    static Material _fontMat;
    class Txt { public GameObject Go; public TextMeshProUGUI T; public float Seen; }
    static readonly Dictionary<string, Txt> _texts = new();
    public static bool FontReady => Init() && _font != null;

    class Chip { public GameObject Go; public Image Back; public RawImage Sigil; public Material Mat; public float Seen; }
    static readonly Dictionary<int, Chip> _chips = new();   // ключ: семейство*100 + подвид

    public static bool Ready => Init();

    // Координаты приходят от табло (IMGUI, пиксели от левого верхнего угла). Холст привязываем к ближайшему краю
    // экрана — левому, центру или правому, — чтобы всё стояло на месте при любом размере окна и масштабе холста.
    static void Place(RectTransform rt, float x, float y, bool forceCenter = false)
    {
        float w = Mathf.Max(1, Screen.width);
        float ax = forceCenter ? 0.5f : (x < w * 0.33f ? 0f : x > w * 0.66f ? 1f : 0.5f);
        rt.anchorMin = rt.anchorMax = new Vector2(ax, 1);
        rt.anchoredPosition = new Vector2(x - ax * w, -y);
    }

    static bool Init()
    {
        if (_ok) return true;
        if (_tried && Time.frameCount % 120 != 0) return false;   // не нашли — пробуем раз в ~2 с
        _tried = true;
        try
        {
            PotionCardPainter painter = null;
            foreach (var o in Resources.FindObjectsOfTypeAll(Il2CppInterop.Runtime.Il2CppType.Of<PotionCardPainter>()))
            { painter = o.TryCast<PotionCardPainter>(); if (painter != null && painter._sigilMaterial != null) break; }
            if (painter == null || painter._sigilMaterial == null || painter._sigilConfig == null) return false;
            _sigilMat = painter._sigilMaterial;
            _cfg = painter._sigilConfig;
            _arrayId = PotionCardPainter.SigilArrayId;
            _indexId = PotionCardPainter.SigilIndexId;
            if (painter._chip != null)
            {
                _circle = painter._chip.sprite;
                foreach (var img in painter._chip.GetComponentsInChildren<Image>(true))
                    if (img != painter._chip && img.sprite != null && img.sprite.name.Contains("sigil-chip")) { _outline = img.sprite; break; }
            }
            // шрифт — тот, которым игра пишет название зелья в той же карточке
            foreach (var t in painter.GetComponentsInChildren<TMP_Text>(true))
                if (t.font != null) { _font = t.font; _fontMat = t.fontSharedMaterial; Plugin.L.LogInfo($"SIGIL: шрифт игры {t.font.name}"); break; }
            var cgo = new GameObject("PotionCoop_SigilCanvas");
            UnityEngine.Object.DontDestroyOnLoad(cgo);
            _canvas = cgo.AddComponent<Canvas>();
            _canvas.renderMode = RenderMode.ScreenSpaceOverlay;
            _canvas.sortingOrder = 500;
            _ok = true;
            Plugin.L.LogInfo($"SIGIL: эмблемы игры найдены (кружок {(_circle != null ? _circle.name : "-")}, обводка {(_outline != null ? _outline.name : "-")})");
        }
        catch (Exception e) { Plugin.L.LogWarning("SIGIL: " + e.Message); }
        return _ok;
    }

    // из OnGUI: «здесь нужна эмблема такого-то подвида» (координаты IMGUI — от левого верхнего угла)
    public static void Want(int fam, int sec, Color color, float x, float y, float size)
    {
        if (!Init()) return;
        int key = fam * 100 + (sec + 1);
        if (!_chips.TryGetValue(key, out var c) || c.Go == null) { c = Make(fam, sec); if (c == null) return; _chips[key] = c; }
        c.Back.color = color;
        var rt = c.Go.GetComponent<RectTransform>();
        Place(rt, x, y);
        rt.sizeDelta = new Vector2(size, size);
        c.Seen = Time.unscaledTime;
        if (!c.Go.activeSelf) c.Go.SetActive(true);
    }

    static Chip Make(int fam, int sec)
    {
        try
        {
            var go = new GameObject($"Sigil_{fam}_{sec}");
            go.transform.SetParent(_canvas.transform, false);
            var rt = go.AddComponent<RectTransform>();
            rt.anchorMin = rt.anchorMax = new Vector2(0, 1);
            rt.pivot = new Vector2(0, 1);
            var back = go.AddComponent<Image>();
            back.sprite = _circle; back.raycastTarget = false;

            if (_outline != null)
            {
                var o = new GameObject("Outline"); o.transform.SetParent(go.transform, false);
                var ort = o.AddComponent<RectTransform>(); ort.anchorMin = Vector2.zero; ort.anchorMax = Vector2.one; ort.offsetMin = ort.offsetMax = Vector2.zero;
                var oi = o.AddComponent<Image>(); oi.sprite = _outline; oi.raycastTarget = false;
            }

            var s = new GameObject("Sigil"); s.transform.SetParent(go.transform, false);
            var srt = s.AddComponent<RectTransform>(); srt.anchorMin = new Vector2(0.15f, 0.15f); srt.anchorMax = new Vector2(0.85f, 0.85f); srt.offsetMin = srt.offsetMax = Vector2.zero;
            var raw = s.AddComponent<RawImage>(); raw.raycastTarget = false;
            var mat = new Material(_sigilMat);
            int slice = _cfg.ResolveSlice((eSectionFamily)fam, Math.Max(sec, 0));
            if (_cfg.SigilArray != null) mat.SetTexture(_arrayId, _cfg.SigilArray);
            mat.SetFloat(_indexId, slice);
            mat.SetInt(_indexId, slice);
            raw.material = mat;
            Plugin.L.LogInfo($"SIGIL: {(eSectionFamily)fam} #{sec} → срез {slice}");
            return new Chip { Go = go, Back = back, Sigil = raw, Mat = mat };
        }
        catch (Exception e) { Plugin.L.LogWarning("SIGIL make: " + e.Message); _ok = false; return null; }
    }

    // из OnGUI: надпись шрифтом игры
    public static void WantText(string key, string text, Color color, float x, float y, float size, bool center = false, float width = 460)
    {
        if (!FontReady) return;
        try
        {
            if (!_texts.TryGetValue(key, out var t) || t.Go == null)
            {
                var go = new GameObject("Txt_" + key);
                go.transform.SetParent(_canvas.transform, false);
                var rt = go.AddComponent<RectTransform>();
                rt.anchorMin = rt.anchorMax = new Vector2(0, 1); rt.pivot = new Vector2(0, 1);
                rt.sizeDelta = new Vector2(460, size * 1.6f);
                var tm = go.AddComponent<TextMeshProUGUI>();
                tm.font = _font; if (_fontMat != null) tm.fontSharedMaterial = _fontMat;
                tm.raycastTarget = false;
                tm.alignment = TextAlignmentOptions.TopLeft;
                t = new Txt { Go = go, T = tm };
                _texts[key] = t;
            }
            if (t.T.text != text) t.T.text = text;
            var want = center ? TextAlignmentOptions.Top : TextAlignmentOptions.TopLeft;
            if (t.T.alignment != want) { t.T.alignment = want; t.Go.GetComponent<RectTransform>().pivot = new Vector2(center ? 0.5f : 0f, 1f); }
            t.T.color = color; t.T.fontSize = size;
            var trt = t.Go.GetComponent<RectTransform>();
            trt.sizeDelta = new Vector2(width, size * 1.6f);
            Place(trt, x, y, center);
            t.Seen = Time.unscaledTime;
            if (!t.Go.activeSelf) t.Go.SetActive(true);
        }
        catch (Exception e) { Plugin.L.LogWarning("SIGIL text: " + e.Message); _font = null; }
    }

    // ---- картинки интерфейса игры по имени (стрелка-шеврон из обучения, подложка панели) ----
    static readonly Dictionary<string, Sprite> _sprites = new();
    public static Sprite FindSprite(string name)
    {
        if (_sprites.TryGetValue(name, out var sp) && sp != null) return sp;
        try
        {
            foreach (var o in Resources.FindObjectsOfTypeAll(Il2CppInterop.Runtime.Il2CppType.Of<Sprite>()))
            {
                var s2 = o.TryCast<Sprite>();
                if (s2 != null && s2.name == name) { _sprites[name] = s2; Plugin.L.LogInfo($"SIGIL: картинка «{name}» найдена"); return s2; }
            }
        }
        catch (Exception e) { Plugin.L.LogWarning("SIGIL sprite: " + e.Message); }
        _sprites[name] = null;
        return null;
    }
    class Pic { public GameObject Go; public Image I; public float Seen; }
    static readonly Dictionary<string, Pic> _pics = new();
    // x,y — центр (координаты IMGUI), angle — поворот в градусах (0 — как нарисовано)
    public static bool WantSprite(string key, string sprite, Color color, float x, float y, float w, float h, float angle = 0f, bool back = false, bool center = false)
    {
        if (!Init()) return false;
        var spr = FindSprite(sprite);
        if (spr == null) return false;
        try
        {
            if (!_pics.TryGetValue(key, out var p) || p.Go == null)
            {
                var go = new GameObject("Pic_" + key);
                go.transform.SetParent(_canvas.transform, false);
                var rt = go.AddComponent<RectTransform>();
                rt.anchorMin = rt.anchorMax = new Vector2(0, 1); rt.pivot = new Vector2(0.5f, 0.5f);
                var img = go.AddComponent<Image>(); img.sprite = spr; img.raycastTarget = false; img.type = Image.Type.Sliced;
                p = new Pic { Go = go, I = img };
                _pics[key] = p;
            }
            if (back) p.Go.transform.SetAsFirstSibling();
            p.I.color = color;
            var r = p.Go.GetComponent<RectTransform>();
            Place(r, x, y, center);
            r.sizeDelta = new Vector2(w, h);
            r.localEulerAngles = new Vector3(0, 0, angle);
            p.Seen = Time.unscaledTime;
            if (!p.Go.activeSelf) p.Go.SetActive(true);
            return true;
        }
        catch (Exception e) { Plugin.L.LogWarning("SIGIL pic: " + e.Message); return false; }
    }

    // из Update: прячем эмблемы, которые табло перестало запрашивать (табло закрылось, вышли в меню)
    public static void Tick()
    {
        float now = Time.unscaledTime;
        foreach (var t in _texts.Values)
            if (t.Go != null && t.Go.activeSelf && now - t.Seen > 0.3f) t.Go.SetActive(false);
        foreach (var p in _pics.Values)
            if (p.Go != null && p.Go.activeSelf && now - p.Seen > 0.3f) p.Go.SetActive(false);
        if (_chips.Count == 0) return;
        foreach (var c in _chips.Values)
            if (c.Go != null && c.Go.activeSelf && now - c.Seen > 0.3f) c.Go.SetActive(false);
    }
}
