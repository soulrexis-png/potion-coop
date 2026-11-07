using System;
using System.Collections.Generic;
using Il2CppInterop.Runtime;
using UnityEngine;
using CodeBase.Components.UI;

namespace PotionCoop;

// Настоящие кнопки в главном меню игры: копии «Продолжить» с нашим текстом.
public static class MenuButtons
{
    // ОДНА кнопка, чтобы меню не вылезало за экран: «Создать общую аптеку» или «Зайти в аптеку X»
    static GameObject _btn;
    static ulong _joinTarget;
    static string _label;
    static bool _failed;
    public static bool Ok => _btn != null;

    static Action _open;
    public static void Tick(Action onOpen, Action<ulong> onJoin, ulong firstHostId, string firstHostName, bool busy)
    {
        _open = onOpen;
        if (_failed || !Coop.InLobby) return;
        var starter = Coop.ContinueStarter;
        if (starter == null) return;           // нет кнопки «Продолжить» (нет сейва) — остаётся запасная панель
        try
        {
            if (_btn == null)
            {
                _label = null;
                Loc.Detect();   // язык меню — по родной кнопке «Продолжить»
                _btn = Clone(starter.gameObject, "Общие аптеки", () => _open?.Invoke());
                Plugin.L.LogInfo("MENU: кнопка общей аптеки добавлена в меню");
            }
            _joinTarget = firstHostId;
            string label = busy ? "Захожу в аптеку…" : firstHostId != 0 ? $"Общие аптеки  ·  {firstHostName} ждёт" : "Общие аптеки";
            if (label != _label) { _label = label; SetText(_btn, label); }
        }
        catch (Exception e)
        {
            _failed = true;
            Plugin.L.LogError("MENU: не смог сделать кнопку, остаётся запасная панель: " + e);
        }
    }

    static GameObject Clone(GameObject src, string label, Action onClick)
    {
        // копируем выключенным, чтобы скрипты оригинала не успели проснуться на копии
        bool wasActive = src.activeSelf;
        src.SetActive(false);
        var go = UnityEngine.Object.Instantiate(src, src.transform.parent);
        src.SetActive(wasActive);
        go.name = "PotionCoop_" + label;

        foreach (var mb in go.GetComponentsInChildren<MonoBehaviour>(true))
        {
            if (mb == null) continue;
            var n = mb.GetIl2CppType().Name;
            if (n == "AdvancedButtonContinueStarter" || n == "ContinueNumeralAppender" || n == "ContinueButtonVisibilityUpdater" || n.Contains("Locali"))
                UnityEngine.Object.DestroyImmediate(mb);
        }

        var btn = go.GetComponentInChildren<AdvancedButton>(true);
        var srcBtn = src.GetComponentInChildren<AdvancedButton>(true);
        if (btn == null) throw new Exception("в копии нет AdvancedButton");
        if (srcBtn != null) btn._inputService = srcBtn._inputService;
        btn.add_OnUp(DelegateSupport.ConvertDelegate<Il2CppSystem.Action>(onClick));

        // «прописываем» копию в системе зависимостей игры — тогда оживут подсветка и звуки, как у родной кнопки
        try
        {
            foreach (var scope in UnityEngine.Object.FindObjectsByType<VContainer.Unity.LifetimeScope>(FindObjectsSortMode.None))
            {
                if (scope == null || scope.gameObject.scene != src.scene || scope.Container == null) continue;
                VContainer.Unity.ObjectResolverUnityExtensions.InjectGameObject(scope.Container, go);
                Plugin.L.LogInfo($"MENU: кнопка подключена к {scope.name}");
                break;
            }
        }
        catch (Exception e) { Plugin.L.LogWarning("MENU: не смог подключить подсветку: " + e.Message); }

        go.transform.SetSiblingIndex(src.transform.GetSiblingIndex() + 1);
        SetText(go, label);
        go.SetActive(true);
        return go;
    }

    static void SetText(GameObject go, string t)
    {
        t = Loc.T(t);
        foreach (var tx in go.GetComponentsInChildren<TMPro.TMP_Text>(true)) tx.text = t;
    }

    public static void Reset() { _btn = null; _joinTarget = 0; _screen.Clear(); _hidden.Clear(); _nameBtn = null; }

    // ---------- Экран «Общие аптеки» ----------
    // Как «Настройки» в игре: прячем кнопки меню и на их месте показываем свои — копии родной кнопки (они точно жмутся мышью).
    // Сюда же потом лягут турнир и настройки коопа.
    static readonly List<GameObject> _screen = new(), _hidden = new();
    static GameObject _nameBtn;
    static string _nameShown, _hostsKey;
    public static bool ScreenOpen => _screen.Count > 0;

    public class Entry { public string Label; public Action Click; public bool IsName; }

    public static void OpenScreen(List<Entry> entries, string hostsKey)
    {
        if (_btn == null) return;
        try
        {
            CloseScreen(false);
            _hostsKey = hostsKey;
            var parent = _btn.transform.parent;
            for (int i = 0; i < parent.childCount; i++)
            {
                var c = parent.GetChild(i).gameObject;
                if (c.activeSelf) _hidden.Add(c);
            }
            foreach (var c in _hidden) c.SetActive(false);
            var tpl = Coop.ContinueStarter != null ? Coop.ContinueStarter.gameObject : _btn;
            foreach (var e in entries)
            {
                var act = e.Click;
                var go = Clone(tpl, e.Label, () => act?.Invoke());
                go.transform.SetParent(parent, false);
                go.transform.SetAsLastSibling();
                _screen.Add(go);
                if (e.IsName) { _nameBtn = go; _nameShown = e.Label; }
            }
            Plugin.L.LogInfo($"MENU: открыт экран общих аптек ({entries.Count} кнопок)");
        }
        catch (Exception ex) { Plugin.L.LogError("MENU: экран общих аптек: " + ex); CloseScreen(); }
    }

    public static void CloseScreen(bool log = true)
    {
        foreach (var go in _screen) if (go != null) UnityEngine.Object.Destroy(go);
        _screen.Clear(); _nameBtn = null; _nameShown = null;
        foreach (var c in _hidden) if (c != null) c.SetActive(true);
        _hidden.Clear();
        if (log) Plugin.L.LogInfo("MENU: экран общих аптек закрыт");
    }

    public static void SetNameLabel(string label)
    {
        if (_nameBtn == null || label == _nameShown) return;
        _nameShown = label; SetText(_nameBtn, label);
    }
    public static bool HostsChanged(string key) => ScreenOpen && key != _hostsKey;
    public static bool HasNameButton => _nameBtn != null;
}
