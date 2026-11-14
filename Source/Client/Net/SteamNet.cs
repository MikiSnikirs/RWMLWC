using System;
using System.Runtime.InteropServices;
using System.Collections.Concurrent;
using System.Threading;
using System.Threading.Tasks;
using Steamworks;

namespace RWMLWC.Client.Net;

/// <summary>
/// Steam Networking Sockets для мода: слушающий сокет хоста, подключение к другу по SteamID,
/// фоновый поток приёма. Обратные вызовы Steam игра обрабатывает сама каждый кадр
/// (<c>SteamManager.Update → SteamAPI.RunCallbacks</c>), поэтому события состояния приходят в главный поток.
/// </summary>
public static class SteamNet
{
    /// <summary>Виртуальный порт P2P мода.</summary>
    public const int VirtualPort = 0;

    private static readonly ConcurrentDictionary<uint, SteamConnection> Connections = new();
    private static Callback<SteamNetConnectionStatusChangedCallback_t>? _statusCallback;
    private static Thread? _pollThread;
    private static HSteamListenSocket _listenSocket = HSteamListenSocket.Invalid;
    private static readonly object Gate = new();
    // Регистрация соединения и обработка его состояний не должны разойтись: иначе событие «соединено»
    // может прийти раньше, чем соединение попадёт в словарь.
    private static readonly object RegisterGate = new();

    /// <summary>Новое входящее соединение принято (главный поток).</summary>
    public static event Action<SteamConnection>? Accepted;

    public static bool Available => Verse.Steam.SteamManager.Initialized;

    public static ulong MySteamId => SteamUser.GetSteamID().m_SteamID;

    /// <summary>Подготовка: обработчик состояний, поток приёма, прогрев релея. Вызывать из главного потока.</summary>
    public static void Init()
    {
        lock (Gate)
        {
            if (_statusCallback != null) return;
            if (!Available) throw new InvalidOperationException("Steam не инициализирован");
            _statusCallback = Callback<SteamNetConnectionStatusChangedCallback_t>.Create(OnStatusChanged);
            // Буфер отправки 4 МиБ вместо 512 КиБ: снимок карты уходит несколькими кусками по 256 КиБ.
            SetGlobalInt(ESteamNetworkingConfigValue.k_ESteamNetworkingConfig_SendBufferSize, 4 * 1024 * 1024);
            // Скорость отправки по умолчанию ограничена (~256 КиБ/с) — снимок карты шёл бы секундами.
            // Верхний предел поднят; реальную скорость всё равно подбирает управление перегрузкой Steam.
            SetGlobalInt(ESteamNetworkingConfigValue.k_ESteamNetworkingConfig_SendRateMin, SendRateMinBytes);
            SetGlobalInt(ESteamNetworkingConfigValue.k_ESteamNetworkingConfig_SendRateMax, 32 * 1024 * 1024);
            SteamNetworkingUtils.InitRelayNetworkAccess();
            _pollThread = new Thread(PollLoop) { IsBackground = true, Name = "RWMLWC Steam poll" };
            _pollThread.Start();
        }
    }

    /// <summary>Хост: начать принимать подключения друзей.</summary>
    public static void Listen()
    {
        Init();
        lock (Gate)
        {
            if (_listenSocket != HSteamListenSocket.Invalid) return;
            _listenSocket = SteamNetworkingSockets.CreateListenSocketP2P(VirtualPort, 0, Array.Empty<SteamNetworkingConfigValue_t>());
            if (_listenSocket == HSteamListenSocket.Invalid)
                throw new InvalidOperationException("Steam: не удалось открыть слушающий сокет");
        }
    }

    public static void StopListening()
    {
        lock (Gate)
        {
            if (_listenSocket == HSteamListenSocket.Invalid) return;
            SteamNetworkingSockets.CloseListenSocket(_listenSocket);
            _listenSocket = HSteamListenSocket.Invalid;
        }
    }

    /// <summary>
    /// Ждёт, пока Steam получит доступ к сети релеев (без неё P2P-подключение к другу не устанавливается).
    /// Возвращает итоговое состояние для лога.
    /// </summary>
    public static async Task<string> WaitForRelayAsync(TimeSpan timeout)
    {
        Init();
        var sw = System.Diagnostics.Stopwatch.StartNew();
        while (true)
        {
            ESteamNetworkingAvailability a = SteamNetworkingUtils.GetRelayNetworkStatus(out SteamRelayNetworkStatus_t st);
            if (a == ESteamNetworkingAvailability.k_ESteamNetworkingAvailability_Current)
                return $"сеть релеев готова за {sw.ElapsedMilliseconds} мс";
            if (sw.Elapsed > timeout)
                return $"сеть релеев НЕ готова за {sw.ElapsedMilliseconds} мс: {a}, config {st.m_eAvailNetworkConfig}, relay {st.m_eAvailAnyRelay}, {st.m_debugMsg}";
            await Task.Delay(250).ConfigureAwait(false);
        }
    }

    /// <summary>Подключиться к хосту по SteamID64. Завершается, когда соединение установлено.</summary>
    public static async Task<SteamConnection> ConnectAsync(ulong hostSteamId, TimeSpan timeout, CancellationToken ct = default)
    {
        Init();
        var identity = new SteamNetworkingIdentity();
        identity.SetSteamID64(hostSteamId);
        SteamConnection connection;
        lock (RegisterGate)
        {
            HSteamNetConnection handle = SteamNetworkingSockets.ConnectP2P(ref identity, VirtualPort, 0, Array.Empty<SteamNetworkingConfigValue_t>());
            if (handle == HSteamNetConnection.Invalid)
                throw new InvalidOperationException("Steam: ConnectP2P вернул пустой дескриптор");
            connection = Register(handle, "steam:" + hostSteamId);
        }
        try
        {
            await connection.Connected.WaitAsync(timeout, ct).ConfigureAwait(false);
        }
        catch (Exception)
        {
            connection.Close("не удалось подключиться");
            throw;
        }
        return connection;
    }

    /// <summary>
    /// Пара соединений внутри одного процесса через настоящий стек Steam (без сети) — для самопроверки.
    /// </summary>
    public static (SteamConnection a, SteamConnection b) CreateLoopbackPair()
    {
        Init();
        var id1 = new SteamNetworkingIdentity();
        var id2 = new SteamNetworkingIdentity();
        id1.SetLocalHost();
        id2.SetLocalHost();
        if (!SteamNetworkingSockets.CreateSocketPair(out HSteamNetConnection h1, out HSteamNetConnection h2, true, ref id1, ref id2))
            throw new InvalidOperationException("Steam: CreateSocketPair не удался");
        SteamConnection a = Register(h1, "steam-loop:a");
        SteamConnection b = Register(h2, "steam-loop:b");
        // Пара создаётся уже соединённой.
        a.OnState(ESteamNetworkingConnectionState.k_ESteamNetworkingConnectionState_Connected, "");
        b.OnState(ESteamNetworkingConnectionState.k_ESteamNetworkingConnectionState_Connected, "");
        return (a, b);
    }

    private static SteamConnection Register(HSteamNetConnection handle, string remote)
    {
        ApplyConnectionConfig(handle);
        var c = new SteamConnection(handle, remote);
        Connections[handle.m_HSteamNetConnection] = c;
        return c;
    }

    internal static void Forget(SteamConnection c) => Connections.TryRemove(c.Handle.m_HSteamNetConnection, out _);

    private static void OnStatusChanged(SteamNetConnectionStatusChangedCallback_t e)
    {
        lock (RegisterGate) HandleStatus(e);
    }

    private static void HandleStatus(SteamNetConnectionStatusChangedCallback_t e)
    {
        try
        {
            ESteamNetworkingConnectionState state = e.m_info.m_eState;
            if (state == ESteamNetworkingConnectionState.k_ESteamNetworkingConnectionState_Connecting
                && e.m_info.m_hListenSocket != HSteamListenSocket.Invalid
                && e.m_info.m_hListenSocket == _listenSocket
                && !Connections.ContainsKey(e.m_hConn.m_HSteamNetConnection))
            {
                // Входящее подключение к нашему слушающему сокету.
                EResult r = SteamNetworkingSockets.AcceptConnection(e.m_hConn);
                if (r != EResult.k_EResultOK)
                {
                    SteamNetworkingSockets.CloseConnection(e.m_hConn, 0, "accept failed", false);
                    return;
                }
                SteamConnection incoming = Register(e.m_hConn, "steam:" + e.m_info.m_identityRemote.GetSteamID64());
                Accepted?.Invoke(incoming);
                return;
            }

            if (Connections.TryGetValue(e.m_hConn.m_HSteamNetConnection, out SteamConnection? c))
                c.OnState(state, e.m_info.m_szEndDebug);
        }
        catch (Exception ex)
        {
            Verse.Log.Error("[RWMLWC] Steam status callback: " + ex);
        }
    }

    private static readonly AutoResetEvent Wake = new(false);

    internal static void WakePoll() => Wake.Set();

    /// <summary>
    /// Нижний предел скорости отправки. Замер 14.11.2026 (пара соединений Steam на одной машине):
    /// Steam держит скорость на этом пределе и почти не разгоняется — при 256 КиБ/с 2 МиБ шли 5,2 с,
    /// при 4 МиБ/с — 0,33 с. Слишком высокий предел на слабом канале даст потери, поэтому 1 МиБ/с;
    /// значение уточняется на двух машинах (TST-016).
    /// </summary>
    public const int SendRateMinBytes = 1024 * 1024;

    /// <summary>Настройки скорости и буфера на конкретном соединении (глобальные на пару не действуют).</summary>
    internal static void ApplyConnectionConfig(HSteamNetConnection h)
    {
        SetInt(ESteamNetworkingConfigValue.k_ESteamNetworkingConfig_SendBufferSize, 4 * 1024 * 1024, ESteamNetworkingConfigScope.k_ESteamNetworkingConfig_Connection, (nint)h.m_HSteamNetConnection);
        SetInt(ESteamNetworkingConfigValue.k_ESteamNetworkingConfig_SendRateMin, SendRateMinBytes, ESteamNetworkingConfigScope.k_ESteamNetworkingConfig_Connection, (nint)h.m_HSteamNetConnection);
        SetInt(ESteamNetworkingConfigValue.k_ESteamNetworkingConfig_SendRateMax, 32 * 1024 * 1024, ESteamNetworkingConfigScope.k_ESteamNetworkingConfig_Connection, (nint)h.m_HSteamNetConnection);
    }

    private static void SetGlobalInt(ESteamNetworkingConfigValue key, int value) =>
        SetInt(key, value, ESteamNetworkingConfigScope.k_ESteamNetworkingConfig_Global, 0);

    private static void SetInt(ESteamNetworkingConfigValue key, int value, ESteamNetworkingConfigScope scope, nint scopeObj)
    {
        nint p = Marshal.AllocHGlobal(sizeof(int));
        try
        {
            Marshal.WriteInt32(p, value);
            if (!SteamNetworkingUtils.SetConfigValue(key, scope, scopeObj, ESteamNetworkingConfigDataType.k_ESteamNetworkingConfig_Int32, p))
                Verse.Log.Warning($"[RWMLWC] Steam: не удалось задать {key} = {value} ({scope})");
        }
        finally
        {
            Marshal.FreeHGlobal(p);
        }
    }

    private static void PollLoop()
    {
        var scratch = new nint[64];
        while (true)
        {
            int total = 0;
            try
            {
                foreach (SteamConnection c in Connections.Values)
                {
                    total += c.Flush();
                    total += c.Poll(scratch);
                }
            }
            catch (Exception ex)
            {
                Verse.Log.Warning("[RWMLWC] Steam poll: " + ex.Message);
            }
            // Нет работы — ждём не дольше 1 мс или до новой отправки.
            if (total == 0) Wake.WaitOne(1);
        }
    }
}
