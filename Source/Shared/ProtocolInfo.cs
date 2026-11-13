namespace RWMLWC.Shared;

/// <summary>
/// Общий контракт клиента и сервера.
/// Любое изменение сообщений протокола или форматов снимков повышает <see cref="ProtocolVersion"/>
/// и описывается в AGENT_LOG.md с пометкой КОНТРАКТ (см. AGENTS.md).
/// </summary>
public static class ProtocolInfo
{
    /// <summary>Версия протокола. 0 — протокола ещё нет (до задачи T-011).</summary>
    public const int ProtocolVersion = 0;

    /// <summary>Идентификатор мода: packageId в About.xml и id экземпляра Harmony.</summary>
    public const string ModId = "MikiSnikirs.RWMLWC";

    /// <summary>Клиент и сервер могут работать вместе только при равных версиях протокола.</summary>
    public static bool IsCompatible(int otherProtocolVersion) => otherProtocolVersion == ProtocolVersion;
}
