using System;
using System.Runtime.InteropServices;
using HarmonyLib;
using RimWorld;
using RWMLWC.Client.Sim;
using RWMLWC.Shared.Protocol;
using UnityEngine;
using Verse;
using Verse.Sound;

namespace RWMLWC.Client.Absence;

/// <summary>
/// Простой и тревога (T-040, КОНТРАКТ v6).
/// <list type="bullet">
/// <item>Нет ввода (мышь, клавиатура) дольше <c>idle_minutes</c> из <c>RWMLWC/ui.txt</c> (по умолчанию 5) — игрок отошёл:
/// клиент сообщает серверу, и его выбор скорости больше не держит остальных. Любой ввод — вернулся.</item>
/// <item>Письмо «крупная угроза» в сессии — клиент сообщает серверу. Если хозяин карты отошёл, сервер ставит общую
/// паузу и рассылает тревогу: позванному — звук и мигание окна в панели задач, всем — сообщение и отсчёт.</item>
/// </list>
/// </summary>
public static class IdleAlarm
{
    private static float _lastActivity;
    private static Vector3 _lastMouse;
    private static bool _sentIdle;
    private static bool _wasInSession;
    private static float? _idleSeconds;

    /// <summary>Текущая тревога: до какого момента (реальное время) и о чём; 0 — нет.</summary>
    public static float AlarmUntil { get; private set; }
    public static string AlarmText { get; private set; } = "";

    public static bool IsIdle => _sentIdle;

    private static float IdleSeconds
    {
        get
        {
            if (_idleSeconds == null)
            {
                _idleSeconds = 300f;
                try
                {
                    // Автопрогон: ключ idleminutes= в метке (чтобы проверять простой без ожидания минут).
                    string arm = System.IO.Path.Combine(GenFilePaths.SaveDataFolderPath, "RWMLWC", "arm-determinism-run.txt");
                    if (System.IO.File.Exists(arm))
                        foreach (string l in System.IO.File.ReadAllLines(arm))
                            if (l.StartsWith("idleminutes=", StringComparison.Ordinal)
                                && double.TryParse(l.Substring(12), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out double m) && m > 0)
                            {
                                _idleSeconds = (float)(m * 60);
                                return _idleSeconds.Value;
                            }
                    string path = System.IO.Path.Combine(GenFilePaths.SaveDataFolderPath, "RWMLWC", "ui.txt");
                    if (System.IO.File.Exists(path))
                        _idleSeconds = (float)RWMLWC.Server.SettingsFile.LoadOrCreate(path, "").GetDouble("idle_minutes", 5, 0.1, 24 * 60) * 60f;
                }
                catch (Exception)
                {
                }
            }
            return _idleSeconds.Value;
        }
    }

    /// <summary>Раз в кадр (из отрисовки интерфейса карты).</summary>
    public static void Frame()
    {
        float now = Time.realtimeSinceStartup;
        Vector3 mouse = UnityEngine.Input.mousePosition;
        if (UnityEngine.Input.anyKey || (mouse - _lastMouse).sqrMagnitude > 4f || Math.Abs(UnityEngine.Input.mouseScrollDelta.y) > 0.01f)
            _lastActivity = now;
        _lastMouse = mouse;

        bool inSession = LockstepClient.InSession;
        if (inSession && !_wasInSession)
        {
            _sentIdle = false; // новая сессия: сервер считает нас на месте
            _lastActivity = now;
        }
        _wasInSession = inSession;
        if (!inSession) return;

        bool idle = now - _lastActivity > IdleSeconds;
        if (idle != _sentIdle && LockstepClient.TrySend(new PlayerStateMessage { Idle = idle }))
            _sentIdle = idle;
    }

    /// <summary>Сообщение тревоги от сервера.</summary>
    public static void OnAlarm(AlarmMessage alarm)
    {
        if (alarm.Seconds <= 0)
        {
            AlarmUntil = 0;
            AlarmText = "";
            Messages.Message("RWMLWC: тревога снята — " + alarm.Text, MessageTypeDefOf.NeutralEvent, false);
            return;
        }
        AlarmUntil = Time.realtimeSinceStartup + alarm.Seconds;
        AlarmText = alarm.Text;
        string who = alarm.PlayerId == 0 ? "отошедших игроков" : RWMLWC.Client.SessionView.SessionUi.NameOf(alarm.PlayerId);
        Messages.Message($"RWMLWC: ТРЕВОГА — {alarm.Text}. Игра на паузе до {alarm.Seconds} с, ждём {who}.", MessageTypeDefOf.ThreatBig, false);
        bool meCalled = alarm.PlayerId == LockstepClient.PlayerId || (alarm.PlayerId == 0 && _sentIdle);
        if (!meCalled) return;
        try
        {
            LetterDefOf.ThreatBig.arriveSound?.PlayOneShotOnCamera();
        }
        catch (Exception)
        {
        }
        FlashWindow();
    }

    /// <summary>Строка для строки состояния: «ТРЕВОГА 37 с» или пусто.</summary>
    public static string StatusSuffix()
    {
        float left = AlarmUntil - Time.realtimeSinceStartup;
        if (left > 0) return $" · ТРЕВОГА {Mathf.CeilToInt(left)} с";
        return _sentIdle ? " · вы отошли" : "";
    }

    // ---- мигание окна в панели задач (Windows) ----
    [StructLayout(LayoutKind.Sequential)]
    private struct FLASHWINFO
    {
        public uint cbSize;
        public IntPtr hwnd;
        public uint dwFlags;
        public uint uCount;
        public uint dwTimeout;
    }

    [DllImport("user32.dll")]
    private static extern bool FlashWindowEx(ref FLASHWINFO pwfi);

    private static void FlashWindow()
    {
        try
        {
            if (Environment.OSVersion.Platform != PlatformID.Win32NT) return;
            IntPtr hwnd = System.Diagnostics.Process.GetCurrentProcess().MainWindowHandle;
            if (hwnd == IntPtr.Zero) return;
            var info = new FLASHWINFO
            {
                cbSize = (uint)Marshal.SizeOf<FLASHWINFO>(),
                hwnd = hwnd,
                dwFlags = 3 | 12, // окно и кнопка; пока окно не станет активным
                uCount = 0,
                dwTimeout = 0,
            };
            FlashWindowEx(ref info);
        }
        catch (Exception)
        {
        }
    }
}

/// <summary>Письмо «крупная угроза» в сессии — сообщить серверу (он решит, нужна ли тревога).</summary>
[HarmonyPatch(typeof(LetterStack), nameof(LetterStack.ReceiveLetter), new[] { typeof(Letter), typeof(string), typeof(int), typeof(bool) })]
internal static class Patch_LetterStack_ReceiveLetter_Alarm
{
    private static void Postfix(Letter let, int delayTicks)
    {
        try
        {
            if (delayTicks > 0 || let == null || let.def != LetterDefOf.ThreatBig || !LockstepClient.InSession) return;
            Map? map = let.lookTargets != null && let.lookTargets.IsValid ? let.lookTargets.PrimaryTarget.Map : null;
            map ??= Find.AnyPlayerHomeMap;
            if (map == null) return;
            LockstepClient.TrySend(new AlarmMessage { MapId = map.uniqueID, Text = let.Label });
        }
        catch (Exception)
        {
        }
    }
}
