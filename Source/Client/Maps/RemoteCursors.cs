using System.Collections.Generic;
using HarmonyLib;
using RimWorld;
using RimWorld.Planet;
using RWMLWC.Client.SessionView;
using RWMLWC.Client.Sim;
using RWMLWC.Shared.Protocol;
using UnityEngine;
using Verse;

namespace RWMLWC.Client.Maps;

/// <summary>
/// Курсоры других игроков (T-050, docs/ARCHITECTURE.md, раздел 14). Только интерфейс: на симуляцию не влияет.
///  - свой курсор раз в кадр отдаётся в SessionUi.SendCursor (там ограничение частоты, протокол v5 агента B);
///  - чужие (SessionUi.Cursors) рисуются на открытой карте стрелкой курсора игры в цвете игрока с именем под ней;
///    курсор, не обновлявшийся 3 секунды, не рисуется.
/// </summary>
public static class RemoteCursors
{
    private const float HideAfterSeconds = 3f;

    /// <summary>Тест: вместо мыши отправлять эту точку карты (сценарий cursors). null — настоящая мышь.</summary>
    public static Vector3? FakeCursor;

    /// <summary>Сколько чужих курсоров нарисовано в последнем кадре (для сводки теста).</summary>
    public static int DrawnLastFrame { get; private set; }

    /// <summary>Сколько подписей с именем нарисовано в последнем проходе интерфейса.</summary>
    public static int LabelsLastFrame { get; private set; }

    /// <summary>Цвет игрока; пока он не выбран — цвет палитры по номеру игрока (серый плохо виден на земле).</summary>
    public static Color ColorOf(int playerId)
    {
        int rgb = SessionUi.ColorOf(playerId);
        if (rgb == PlayerColors.None)
        {
            rgb = Squads.Palette[(playerId - 1 + Squads.Palette.Length) % Squads.Palette.Length];
        }
        return Squads.ToColor(rgb);
    }

    internal static void SendOwn()
    {
        if (!LockstepClient.InSession)
        {
            return;
        }
        Map? map = Find.CurrentMap;
        if (map == null || WorldRendererUtility.WorldSelected)
        {
            SessionUi.SendCursor(CursorMessage.NoCursor, 0f, 0f);
            return;
        }
        Vector3 at = FakeCursor ?? UI.MouseMapPosition();
        SessionUi.SendCursor(map.uniqueID, at.x, at.z);
    }

    private static IEnumerable<(int playerId, Vector3 at)> Visible()
    {
        Map? map = Find.CurrentMap;
        if (map == null || WorldRendererUtility.WorldSelected)
        {
            yield break;
        }
        float now = Time.realtimeSinceStartup;
        foreach (KeyValuePair<int, SessionUi.RemoteCursor> pair in SessionUi.Cursors)
        {
            if (pair.Key == LockstepClient.PlayerId || pair.Value.MapId != map.uniqueID || now - pair.Value.ReceivedAt > HideAfterSeconds)
            {
                continue;
            }
            yield return (pair.Key, new Vector3(pair.Value.X, AltitudeLayer.MetaOverlays.AltitudeFor(), pair.Value.Z));
        }
    }

    // Сглаживание: чем больше, тем быстрее стрелка догоняет полученную точку (в секунду).
    private const float SmoothPerSecond = 14f;
    private static readonly Dictionary<int, Vector3> shown = new Dictionary<int, Vector3>();

    // Тот же курсор, что у самой игры (Verse.CustomCursor): стрелка, острие в точке (3, 3) текстуры.
    private static Texture2D? cursorTex;
    private static readonly Vector2 Hotspot = new Vector2(3f, 3f);

    /// <summary>Чужие курсоры — стрелкой игры в цвете игрока, под ней имя на тёмной подложке. В интерфейсе, на перерисовке.</summary>
    internal static void Draw()
    {
        cursorTex ??= ContentFinder<Texture2D>.Get("UI/Cursors/CursorCustom");
        int drawn = 0;
        float dt = Mathf.Min(Time.unscaledDeltaTime, 0.1f);
        foreach ((int playerId, Vector3 target) in Visible())
        {
            // Положение приходит не чаще 10 раз в секунду: стрелка плавно догоняет последнюю точку,
            // на большом скачке (другая карта, телепорт камеры) — сразу встаёт на место.
            Vector3 at = target;
            if (shown.TryGetValue(playerId, out Vector3 was) && (was - target).sqrMagnitude < 30f * 30f)
            {
                at = Vector3.Lerp(was, target, 1f - Mathf.Exp(-dt * SmoothPerSecond));
            }
            shown[playerId] = at;
            Vector2 screen = Find.Camera.WorldToScreenPoint(new Vector3(at.x, 0f, at.z)) / Prefs.UIScale;
            screen.y = UI.screenHeight - screen.y;
            Color color = ColorOf(playerId);
            float size = cursorTex.width / Prefs.UIScale;
            Rect rect = new Rect(screen.x - Hotspot.x / Prefs.UIScale, screen.y - Hotspot.y / Prefs.UIScale, size, cursorTex.height / Prefs.UIScale);
            // Белый курсор игры, окрашенный в цвет игрока; тень — чтобы читался на светлой земле.
            GUI.color = new Color(0f, 0f, 0f, 0.5f);
            GUI.DrawTexture(new Rect(rect.x + 1f, rect.y + 1f, rect.width, rect.height), cursorTex);
            GUI.color = color;
            GUI.DrawTexture(rect, cursorTex);
            GUI.color = Color.white;
            GenMapUI.DrawThingLabel(new Vector2(screen.x + size * 0.5f, rect.yMax + 2f), SessionUi.NameOf(playerId), color);
            drawn++;
        }
        DrawnLastFrame = drawn;
        LabelsLastFrame = drawn;
    }
}

[HarmonyPatch(typeof(MapInterface), nameof(MapInterface.MapInterfaceUpdate))]
internal static class Patch_MapInterface_RemoteCursors
{
    private static void Postfix()
    {
        RemoteCursors.SendOwn();
    }
}

[HarmonyPatch(typeof(MapInterface), nameof(MapInterface.MapInterfaceOnGUI_BeforeMainTabs))]
internal static class Patch_MapInterface_RemoteCursorLabels
{
    private static void Postfix()
    {
        if (Event.current.type == EventType.Repaint)
        {
            RemoteCursors.Draw();
        }
    }
}
