using UnityEngine;

namespace PotionCoop;

// Перк соперничества «Толчок» (идея Егора): за каждые N правильно поставленных склянок копится заряд.
// Полный заряд — H, глядя на игрока рядом: у него срабатывает дырявый карман (выпадают зелья из рук).
// Проверить одному: F9 — манекен, ставишь склянки, H по манекену.
public static class Push
{
    public static int Charge;
    public static int Need => Plugin.EffPushEvery;   // у гостя — значение хоста
    public static bool Ready => Charge >= Need;
    public const float Range = 5f, Cone = 40f;

    // своя склянка встала на полку
    public static void OnShelved(CodeBase.Components.Slots.SlotFacade slot)
    {
        if (!Plugin.PushOn.Value || slot == null) return;
        bool ok; try { ok = slot.IsCorrectlyFilled; } catch { return; }
        if (!ok || Ready) return;
        Charge++;
        if (Ready) { Coop.Say(Loc.L("Толчок готов! H — толкнуть игрока рядом", "Shove ready! H — shove a nearby player"), 4f); Plugin.L.LogInfo("PUSH: заряд полный"); }
    }

    static GUIStyle _st;
    public static void Draw()
    {
        if (!Plugin.PushOn.Value || Coop.State != eGameState.Playing) return;
        if (Coop.Role == Role.Solo && !NetDriver.HasDummy) return;
        string t = Ready ? Loc.L("Толчок готов — H", "Shove ready — H") : Loc.L($"Толчок: {Charge}/{Need}", $"Shove: {Charge}/{Need}");
        var c = Ready ? new Color(1f, 0.85f, 0.4f) : new Color(1f, 1f, 1f, 0.8f);
        float x = 40, y = Screen.height - 170;
        if (Sigils.FontReady) { Sigils.WantText("push", t, c, x, y, 22); return; }
        if (_st == null) _st = new GUIStyle(GUI.skin.label) { fontSize = 18, fontStyle = FontStyle.Bold };
        _st.normal.textColor = c;
        GUI.Label(new Rect(x, y, 400, 28), t, _st);
    }
}
