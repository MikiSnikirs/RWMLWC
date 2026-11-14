using System;
using System.Collections.Generic;
using HarmonyLib;
using RimWorld;
using RWMLWC.Client.Sim;
using Steamworks;
using Verse;

namespace RWMLWC.Client.Net;

/// <summary>
/// Пункты сетевой игры в меню игры (без режима разработчика):
/// главное меню — «Подключиться к другу (Steam)»; меню паузы — «Открыть сессию для друзей»,
/// «Остановить сессию» у хоста, «Отключиться» у гостя.
/// </summary>
internal static class SessionMenu
{
    internal static void AddOptions(List<ListableOption> list)
    {
        int at = list.FindIndex(o => o.label == "Options".Translate());
        if (at < 0) at = list.Count;
        foreach (ListableOption o in Options()) list.Insert(at++, o);
    }

    private static IEnumerable<ListableOption> Options()
    {
        LockstepClient.State st = LockstepClient.CurrentState;
        if (Current.ProgramState == ProgramState.Entry)
        {
            if (st == LockstepClient.State.Off)
                yield return new ListableOption("Подключиться к другу (Steam)", () => Find.WindowStack.Add(new Dialog_SteamJoin()));
            yield break;
        }
        if (Current.ProgramState != ProgramState.Playing) yield break;

        if (HostedSession.Running)
            yield return new ListableOption("Остановить сессию", () => Confirm("Остановить сессию? Друзья будут отключены.", HostedSession.StopAndLeave));
        else if (st != LockstepClient.State.Off)
            yield return new ListableOption("Отключиться от сессии", () => Confirm("Отключиться от сессии? Игра останется открытой, но дальше пойдёт без друга.", () => LockstepClient.Disconnect("игрок отключился")));
        else
            yield return new ListableOption("Открыть сессию для друзей", HostedSession.HostAndJoin);
    }

    private static void Confirm(string text, Action action)
    {
        Find.WindowStack.Add(Dialog_MessageBox.CreateConfirmation(text, action, destructive: true, null, WindowLayer.Super));
    }
}

// Список пунктов главного меню и меню паузы рисует OptionListingUtility.DrawOptionListing.
// Меню отличаем по пункту «Настройки» (второй список той же функции — ссылки на сайт).
[HarmonyPatch(typeof(OptionListingUtility), nameof(OptionListingUtility.DrawOptionListing))]
internal static class Patch_OptionListing_SessionMenu
{
    private static void Prefix(List<ListableOption> optList)
    {
        if (optList == null || !optList.Exists(o => o.label == "Options".Translate())) return;
        SteamInvite.Init();
        SessionMenu.AddOptions(optList);
    }
}

/// <summary>
/// Приглашение через Steam: хост объявляет «можно присоединиться» (rich presence <c>connect</c>),
/// у друга в списке друзей появляется «Присоединиться к игре». Нажатие приходит либо событием
/// <see cref="GameRichPresenceJoinRequested_t"/> (игра запущена), либо аргументом командной строки (игра запускается).
/// </summary>
internal static class SteamInvite
{
    private const string Prefix = "+rwmlwc_join";
    private static Callback<GameRichPresenceJoinRequested_t>? _joinRequested;
    private static bool _commandLineChecked;
    private static ulong _pendingHost;

    /// <summary>Вызывать из главного потока (из меню — каждый кадр, дёшево).</summary>
    internal static void Init()
    {
        if (!SteamNet.Available) return;
        _joinRequested ??= Callback<GameRichPresenceJoinRequested_t>.Create(OnJoinRequested);
        if (!_commandLineChecked)
        {
            _commandLineChecked = true;
            ulong host = Parse(string.Join(" ", Environment.GetCommandLineArgs()));
            if (host != 0)
            {
                Log.Message("[RWMLWC] запуск по приглашению Steam, хост " + host);
                _pendingHost = host;
            }
        }
        if (_pendingHost != 0 && Current.ProgramState == ProgramState.Entry && LockstepClient.CurrentState == LockstepClient.State.Off)
        {
            ulong h = _pendingHost;
            _pendingHost = 0;
            Find.WindowStack.Add(new Dialog_SteamJoin(h, autoStart: true));
        }
    }

    internal static void AdvertiseHost()
    {
        if (!SteamNet.Available) return;
        SteamFriends.SetRichPresence("connect", Prefix + " " + SteamNet.MySteamId);
        SteamFriends.SetRichPresence("status", "Сетевая игра RWMLWC");
    }

    internal static void Clear()
    {
        if (SteamNet.Available) SteamFriends.ClearRichPresence();
    }

    private static void OnJoinRequested(GameRichPresenceJoinRequested_t e)
    {
        ulong host = Parse(e.m_rgchConnect);
        if (host == 0) return;
        Log.Message("[RWMLWC] приглашение Steam: присоединиться к " + host);
        if (Current.ProgramState == ProgramState.Entry)
        {
            _pendingHost = host;
            Init();
            return;
        }
        // В загруженной игре мир от хоста придёт только из главного меню.
        Find.WindowStack.Add(Dialog_MessageBox.CreateConfirmation(
            "Присоединиться к игре друга? Текущая игра будет закрыта без сохранения.",
            () => { _pendingHost = host; GenScene.GoToMainMenu(); }, destructive: true, null, WindowLayer.Super));
    }

    private static ulong Parse(string? text)
    {
        if (string.IsNullOrEmpty(text)) return 0;
        int i = text!.IndexOf(Prefix, StringComparison.Ordinal);
        if (i < 0) return 0;
        string rest = text.Substring(i + Prefix.Length).Trim();
        int space = rest.IndexOf(' ');
        if (space >= 0) rest = rest.Substring(0, space);
        return SteamJoin.TryParseSteamId(rest, out ulong id) ? id : 0;
    }
}
