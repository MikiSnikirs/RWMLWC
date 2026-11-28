namespace Multiplayer.Common;

/// <summary>
/// Точка, которую ищет Multiplayer API (MP.cs: сборка «Multiplayer», поле Instance). Отдаёт реализацию RWMLWC.
/// Включать вместе с настоящим RimWorld Multiplayer нельзя — как и сам RWMLWC.
/// </summary>
public static class MultiplayerAPIBridge
{
    public static readonly global::Multiplayer.API.IAPI Instance = Create();

    private static global::Multiplayer.API.IAPI Create()
    {
        global::Multiplayer.Client.AsyncTimeShimPatches.Install();
        return RWMLWC.Client.Input.MpApi.MpApiImpl.Create();
    }
}

/// <summary>Отпечаток описаний для сверки при входе (MultiplayerData.localDefInfos).</summary>
public class DefInfo
{
    public int count;
    public int hash;
}
