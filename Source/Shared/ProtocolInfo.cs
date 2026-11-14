namespace RWMLWC.Shared;

/// <summary>
/// Общий контракт клиента и сервера.
/// Любое изменение сообщений протокола или форматов снимков повышает <see cref="ProtocolVersion"/>
/// и описывается в AGENT_LOG.md с пометкой КОНТРАКТ (см. AGENTS.md).
/// </summary>
public static class ProtocolInfo
{
    /// <summary>
    /// Версия протокола. 1 — протокол v0 (T-011): рукопожатие, команды, тик, снимок, хеш.
    /// 2 — права на карты (T-031): <c>Command.Required</c>, <c>MapClaim</c>, <c>GrantPermission</c>, <c>Permissions</c>, <c>CommandRejected</c>;
    /// версия сейва в <c>Hello</c>/<c>Welcome</c>, <c>Notice</c> (T-044); <c>SnapshotApplied</c> (поток снимка).
    /// </summary>
    public const int ProtocolVersion = 2;

    /// <summary>Идентификатор мода: packageId в About.xml и id экземпляра Harmony.</summary>
    public const string ModId = "MikiSnikirs.RWMLWC";

    /// <summary>Клиент и сервер могут работать вместе только при равных версиях протокола.</summary>
    public static bool IsCompatible(int otherProtocolVersion) => otherProtocolVersion == ProtocolVersion;
}
