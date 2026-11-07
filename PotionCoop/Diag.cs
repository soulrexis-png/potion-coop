using System;
using System.IO;
using HarmonyLib;
using UnityEngine;
using CodeBase.Services.GameStateMachineService;

namespace PotionCoop;

// Диагностика: кто ставит паузу, выводит в меню и закрывает игру. Пишем сразу в отдельный файл,
// чтобы запись не потерялась при закрытии.
static class DiagPatches
{
    static string FilePath => Path.Combine(BepInEx.Paths.BepInExRootPath, "potioncoop_diag.txt");

    public static void Write(string what)
    {
        string stack;
        try { stack = new Il2CppSystem.Diagnostics.StackTrace().ToString(); }
        catch (Exception e) { stack = "(нет стека: " + e.Message + ")"; }
        var line = $"[{DateTime.Now:HH:mm:ss.fff}] {what}\n{stack}\n--- managed ---\n{Environment.StackTrace}\n\n";
        try { File.AppendAllText(FilePath, line); } catch { }
        Plugin.L.LogWarning("DIAG: " + what);
    }

    [HarmonyPrefix]
    [HarmonyPatch(typeof(GameStateMachine), nameof(GameStateMachine.Enter), new[] { typeof(eGameState) })]
    static void Enter_Pre(eGameState id)
    {
        if (id == eGameState.Paused || id == eGameState.LoadLobby) Write($"GameStateMachine.Enter({id}), сейчас {Coop.State}, роль {Coop.Role}");
    }

    [HarmonyPrefix]
    [HarmonyPatch(typeof(Application), nameof(Application.Quit), new Type[0])]
    static void Quit_Pre() => Write("Application.Quit()");

    [HarmonyPrefix]
    [HarmonyPatch(typeof(Application), nameof(Application.Quit), new[] { typeof(int) })]
    static void QuitCode_Pre(int exitCode) => Write($"Application.Quit({exitCode})");
}
