using System;
using System.Collections;
using HarmonyLib;
using RimWorld;
using RimWorld.Planet;
using RWMLWC.Client.Sim;
using UnityEngine;
using Verse;

namespace RWMLWC.Client.SessionView;

/// <summary>
/// Общий снимок (вход игрока, лечение расхождения) перезагружает мир у всех. Тем, кто его не вызывал,
/// вместо экрана загрузки показывается застывший последний кадр игры и надпись «синхронизация мира»
/// (просьба владельца 15.11); после загрузки возвращаются та же карта, место камеры и вид мира.
/// Тот, кто вызвал (вошедший игрок), видит обычный экран загрузки.
/// </summary>
public static class SyncCurtain
{
    private static Texture2D? frozenFrame;
    private static string text = "";
    private static bool active;

    // Что вернуть после загрузки.
    private static int mapId = -1;
    private static Vector3 cameraPos;
    private static float cameraSize;
    private static bool worldView;
    private static bool restorePending;

    public static bool Active => active;

    private static bool shotTaken;

    /// <summary>Тестовые прогоны: снимок экрана игрой в файл данных (проверка, как это выглядит).</summary>
    internal static void TestShot(string name)
    {
        if (!RWMLWC.Client.World.CaravanSelfTest.ArmKey("caravantest=1"))
        {
            return;
        }
        try
        {
            string path = System.IO.Path.Combine(GenFilePaths.SaveDataFolderPath, "RWMLWC", name + ".png");
            ScreenCapture.CaptureScreenshot(path);
            TestAutomation.Note("screenshot: " + name);
        }
        catch (Exception)
        {
        }
    }

    /// <summary>
    /// Перед загрузкой снимка: снять кадр в конце этого кадра, запомнить вид, затем выполнить <paramref name="load"/>.
    /// <paramref name="showCurtain"/> — показывать ли застывший кадр (false — обычный экран загрузки).
    /// </summary>
    public static void BeforeReload(bool showCurtain, string reason, Action load)
    {
        Remember();
        restorePending = true;
        if (!showCurtain || Find.Root == null)
        {
            load();
            return;
        }
        text = reason;
        Find.Root.StartCoroutine(CaptureThenLoad(load));
    }

    private static IEnumerator CaptureThenLoad(Action load)
    {
        yield return new WaitForEndOfFrame();
        try
        {
            frozenFrame = ScreenCapture.CaptureScreenshotAsTexture();
            active = true;
            shotTaken = false;
            TestAutomation.Note($"curtain: shown ({text}), map {mapId}, world view {worldView}");
        }
        catch (Exception ex)
        {
            Log.Warning("[RWMLWC] кадр для экрана синхронизации не снят: " + ex.Message);
        }
        load();
    }

    private static void Remember()
    {
        try
        {
            worldView = WorldRendererUtility.WorldSelected;
            Map? map = Find.CurrentMap;
            mapId = map?.uniqueID ?? -1;
            cameraPos = Find.CameraDriver.MapPosition.ToVector3Shifted();
            cameraSize = Find.CameraDriver.ZoomRootSize;
        }
        catch (Exception)
        {
            mapId = -1;
        }
    }

    /// <summary>Первый кадр после загрузки снимка: вернуть карту, камеру и вид; убрать занавес.</summary>
    public static void AfterReload()
    {
        active = false;
        if (frozenFrame != null)
        {
            UnityEngine.Object.Destroy(frozenFrame);
            frozenFrame = null;
        }
        if (!restorePending)
        {
            return;
        }
        restorePending = false;
        try
        {
            Map? map = mapId >= 0 ? Find.Maps.Find(m => m.uniqueID == mapId) : null;
            if (map != null)
            {
                Current.Game.CurrentMap = map;
                Find.CameraDriver.SetRootPosAndSize(cameraPos, cameraSize);
            }
            TestAutomation.Note($"curtain: view restored — map {(map != null ? map.uniqueID : -1)}, world view {worldView}");
            if (worldView)
            {
                CameraJumper.TryShowWorld();
            }
        }
        catch (Exception ex)
        {
            Log.Warning("[RWMLWC] вид после синхронизации не восстановлен: " + ex.Message);
        }
    }

    /// <summary>Рисует застывший кадр и надпись вместо экрана загрузки. true — нарисовано.</summary>
    internal static bool Draw()
    {
        if (!active)
        {
            return false;
        }
        Rect screen = new Rect(0f, 0f, UI.screenWidth, UI.screenHeight);
        if (frozenFrame != null)
        {
            GUI.DrawTexture(screen, frozenFrame, ScaleMode.StretchToFill);
        }
        else
        {
            Widgets.DrawBoxSolid(screen, new Color(0f, 0f, 0f, 1f));
        }
        Rect box = new Rect((UI.screenWidth - 460f) / 2f, 40f, 460f, 64f);
        Widgets.DrawBoxSolid(box, new Color(0f, 0f, 0f, 0.75f));
        Text.Font = GameFont.Medium;
        Text.Anchor = TextAnchor.MiddleCenter;
        Widgets.Label(box, text);
        Text.Anchor = TextAnchor.UpperLeft;
        Text.Font = GameFont.Small;
        if (!shotTaken && Event.current.type == EventType.Repaint)
        {
            shotTaken = true;
            TestShot("curtain");
        }
        return true;
    }
}

/// <summary>Во время загрузки снимка — застывший кадр вместо экрана загрузки (для тех, кто её не вызывал).</summary>
[HarmonyPatch(typeof(LongEventHandler), nameof(LongEventHandler.LongEventsOnGUI))]
internal static class Patch_LongEventsOnGUI_Curtain
{
    private static bool Prefix() => !SyncCurtain.Draw();
}

/// <summary>Карта в стазисе: затемнение и надпись, чтобы было видно, почему всё замерло.</summary>
internal static class StasisBanner
{
    private static bool shot;

    public static void OnGUI()
    {
        Map? map = Find.CurrentMap;
        if (!LockstepClient.InSession || map == null || WorldRendererUtility.WorldSelected || !PerMapSim.IsFrozen(map))
        {
            return;
        }
        Widgets.DrawBoxSolid(new Rect(0f, 0f, UI.screenWidth, UI.screenHeight), new Color(0.05f, 0.1f, 0.2f, 0.35f));
        Rect box = new Rect((UI.screenWidth - 520f) / 2f, 80f, 520f, 54f);
        Widgets.DrawBoxSolid(box, new Color(0f, 0f, 0f, 0.7f));
        Text.Anchor = TextAnchor.MiddleCenter;
        if (!shot && Event.current.type == EventType.Repaint)
        {
            shot = true;
            SyncCurtain.TestShot("stasis-banner");
        }
        Widgets.Label(box, $"Колония «{map.Parent?.LabelCap}» в стазисе: её игроков нет в сети.\nВремя здесь стоит, пока кто-то из них не вернётся.");
        Text.Anchor = TextAnchor.UpperLeft;
    }
}
