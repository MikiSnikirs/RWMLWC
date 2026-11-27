namespace Multiplayer.Common;

/// <summary>
/// Точка, которую ищет Multiplayer API (MP.cs: сборка «Multiplayer», поле Instance). Отдаёт реализацию RWMLWC.
/// Включать вместе с настоящим RimWorld Multiplayer нельзя — как и сам RWMLWC.
/// </summary>
public static class MultiplayerAPIBridge
{
    public static readonly global::Multiplayer.API.IAPI Instance = RWMLWC.Client.Input.MpApi.MpApiImpl.Create();
}
