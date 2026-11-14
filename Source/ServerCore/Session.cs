using System;
using System.Collections.Generic;
using System.Linq;
using RWMLWC.Net;
using RWMLWC.Shared;
using RWMLWC.Shared.Protocol;

namespace RWMLWC.Server;

/// <summary>Настройки сессии.</summary>
public sealed class SessionOptions
{
    /// <summary>Тик мира на старте сессии (пока задаётся вручную; из сейва — T-044).</summary>
    public int StartTick;

    /// <summary>Зерно сессии для <c>Welcome.SessionSeed</c>.</summary>
    public uint SessionSeed;

    /// <summary>Сколько ждать <c>Hello</c> после подключения.</summary>
    public TimeSpan HelloTimeout = TimeSpan.FromSeconds(10);

    public int MaxPlayers = 8;

    /// <summary>Хранилище сейвов (T-044); null — сервер копии мира не хранит.</summary>
    public SaveStore? Store;

    /// <summary>Предел размера одного собираемого снимка мира.</summary>
    public long MaxSnapshotBytes = 512L * 1024 * 1024;

    /// <summary>
    /// Выделенный сервер (T-058): мира в процессе нет ни у кого постоянно. Когда в сессии никого нет,
    /// часы встают на тик мира, с которым придёт первый игрок: копия на сервере или его сейв, если он новее.
    /// Игроку без мира, который входит в пустую сессию, сервер сам отдаёт сохранённую копию.
    /// </summary>
    public bool Dedicated;

    /// <summary>
    /// Автосохранение (T-065): раз в столько времени сервер просит отдающего прислать мир и сохраняет копию.
    /// null — выключено (сессия в игре хоста: у хоста свои сейвы).
    /// </summary>
    public TimeSpan? AutosaveInterval;

    /// <summary>
    /// Насколько часы сервера могут уйти вперёд от самого медленного игрока с миром (T-066). Где игрок, сервер знает
    /// по его хешам (раз в 60 тиков). 0 — без ограничения. Меньше отставание — быстрее исполняются команды,
    /// лечится расхождение и входит новичок.
    /// </summary>
    public int MaxLeadTicks;

    /// <summary>Сколько ждать хеши остальных игроков, чтобы выбрать отдающего из большинства при расхождении.</summary>
    public TimeSpan MajorityWait = TimeSpan.FromSeconds(2);
}

/// <summary>
/// Сессия сервера (T-013): рукопожатие, общие часы, правило меньшей скорости, общий порядок команд.
/// Однопоточная и без таймеров: хост вызывает <see cref="Pump"/> и <see cref="Advance"/> из одного цикла,
/// тесты — вручную. Поэтому поведение воспроизводимо.
/// </summary>
public sealed class Session
{
    /// <summary>
    /// Тиков в секунду на скорости 1; скорости 2..4 — базовые множители игры (<c>TickManager.TickRateMultiplier</c>): 3, 6, 15.
    /// Игра сама меняет множитель по обстановке: бой — принудительно 1x (<c>ForcedNormalSpeed</c>),
    /// «ничего не происходит» — 12x вместо 6x. Сервер этого не видит; учитывать это — дело клиента (T-014).
    /// </summary>
    public const int TicksPerSecondAtSpeed1 = 60;

    private static readonly int[] SpeedMultiplier = { 0, 1, 3, 6, 15 };

    /// <summary>Вид команды «общий снимок мира» (<see cref="ProtocolIds.RequestWorldSnapshotKind"/>).</summary>
    public const int RequestWorldSnapshotKind = ProtocolIds.RequestWorldSnapshotKind;

    /// <summary>Номер «игрока» самого сервера (чат из консоли, команды сервера).</summary>
    public const int ServerPlayerId = ProtocolIds.ServerPlayerId;

    private readonly SessionOptions _options;
    private readonly List<Pending> _pending = new();
    private readonly List<Player> _players = new();
    // Хеши по (карта, тик): игрок → Total. Нужны все, чтобы при расхождении найти большинство (T-066).
    private readonly Dictionary<(int map, int tick), Dictionary<int, ulong>> _hashes = new();
    private readonly Action<string> _log;
    // Права по картам (T-031). Номер игрока постоянен в сессии (возврат по имени), поэтому храним номера.
    private readonly SortedDictionary<int, MapRights> _maps = new();

    private sealed class MapRights
    {
        public int Owner;
        public readonly SortedDictionary<int, PermissionLevel> Grants = new();
    }
    private int _nextPlayerId = 1;
    private long _nextSeq = 1;
    private double _tickAccumulator;
    private byte _lastSentSpeed = byte.MaxValue;

    public Session(SessionOptions options, Action<string>? log = null)
    {
        _options = options;
        _log = log ?? (_ => { });
        TickUpTo = options.StartTick;
        SaveCopy? latest = options.Store?.Latest;
        if (latest != null)
        {
            SaveWorldId = latest.WorldId;
            SaveTick = latest.Tick;
            WorldId = latest.WorldId;
        }
    }

    /// <summary>Мир сессии: из копии сервера или от первого игрока со своим сейвом; 0 — неизвестен.</summary>
    public ulong WorldId { get; private set; }

    /// <summary>Мир последней копии на сервере; 0 — копии нет.</summary>
    public ulong SaveWorldId { get; private set; }

    /// <summary>Тик последней копии на сервере; 0 — копии нет.</summary>
    public int SaveTick { get; private set; }

    // Сборка снимков мира из кусков: (игрок, номер снимка) → куски.
    private readonly Dictionary<(int player, int snapshot), SnapshotAssembly> _assemblies = new();

    private sealed class SnapshotAssembly
    {
        public required int Tick;
        public required byte[]?[] Chunks;
        public long Bytes;
        public int Received;
    }

    /// <summary>
    /// Клиентам разрешено дотикать до этого тика включительно. Растёт только вперёд; исключение —
    /// пустая выделенная сессия, где часы встают на тик мира следующего игрока (T-058).
    /// Команда, принятая сейчас, исполняется после тика <see cref="TickUpTo"/>.
    /// </summary>
    public int TickUpTo { get; private set; }

    /// <summary>Общая скорость: 0 — пауза, 1..4.</summary>
    public byte Speed { get; private set; }

    public IReadOnlyList<Player> Players => _players;

    /// <summary>Обнаружено расхождение хешей карты между игроками (T-034 будет на это реагировать).</summary>
    public event Action<MapHashMessage, int, MapHashMessage, int>? Desync;

    public sealed class Player
    {
        internal Player(int id, string name, IConnection connection)
        {
            Id = id;
            Name = name;
            Connection = connection;
        }

        public int Id { get; }
        public string Name { get; }
        public IConnection Connection { get; internal set; }
        public bool Online => Connection.IsOpen;

        /// <summary>Скорость, выбранная игроком. Новый игрок держит паузу, пока не пришлёт <c>SpeedRequest</c>.</summary>
        public byte RequestedSpeed { get; internal set; }

        /// <summary>Мир и тик локальной копии сейва, о которых игрок сообщил в <c>Hello</c>.</summary>
        public ulong SaveWorldId { get; internal set; }
        public int SaveTick { get; internal set; }

        /// <summary>Когда (часы сессии, мс) игрок писал в чат в последнем окне — для ограничения частоты.</summary>
        internal Queue<double> ChatTimes { get; } = new();

        /// <summary>
        /// У игрока загружен мир сессии: вошёл со своим сейвом на тике сервера или подтвердил снимок
        /// (<c>SnapshotApplied</c>). Только такой может отдавать мир другим.
        /// </summary>
        public bool HasWorld { get; internal set; }

        /// <summary>До какого тика игрок точно дошёл: Welcome с миром, SnapshotApplied, последний присланный хеш.</summary>
        public int LastTick { get; internal set; }

        /// <summary>Цвет 0xRRGGBB из палитры (v5); -1 — не выбран. Сохраняется при переподключении, как имя.</summary>
        public int Color { get; internal set; } = -1;

        /// <summary>Когда (часы сессии, мс) пересылались курсоры игрока за последнюю секунду — для ограничения частоты.</summary>
        internal Queue<double> CursorTimes { get; } = new();
    }

    private sealed class Pending
    {
        public required IConnection Connection;
        public TimeSpan Waited;
    }

    /// <summary>Новое подключение: ждём от него <c>Hello</c>.</summary>
    public void Accept(IConnection connection) => _pending.Add(new Pending { Connection = connection });

    /// <summary>Обработать всё, что пришло. Вызывать часто (каждые ~10 мс).</summary>
    public void Pump()
    {
        for (int i = _pending.Count - 1; i >= 0; i--)
        {
            Pending p = _pending[i];
            if (!p.Connection.IsOpen) { _pending.RemoveAt(i); continue; }
            if (!p.Connection.TryReceive(out Message m)) continue;
            _pending.RemoveAt(i);
            if (m is HelloMessage hello) Handshake(p.Connection, hello);
            else p.Connection.Close("первым сообщением ожидался Hello, пришёл " + m.Type);
        }

        // Игроки обрабатываются по возрастанию номера, сообщения каждого — по порядку прихода.
        foreach (Player player in _players)
        {
            while (player.Online && player.Connection.TryReceive(out Message m))
                Handle(player, m);
        }

        BroadcastPlayersIfChanged();
        TryResolveResync();
        UpdateSpeed();
    }

    // Часы сессии (сумма elapsed из Advance) — для пинга и ограничения чата.
    private double _clockMs;
    private double _lastPingMs;
    private double _lastPlayersRefreshMs;
    private string _lastPings = "";

    // Состав, разосланный последним: номер и «в сети» каждого игрока. Уход замечается здесь же, в Pump.
    private string _lastRoster = "";

    private bool BroadcastPlayersIfChanged()
    {
        string roster = string.Join(",", _players.Select(p => (p.Online ? p.Id.ToString() : "-" + p.Id) + ":" + p.Color));
        if (roster == _lastRoster) return false;
        _lastRoster = roster;
        Broadcast(BuildPlayers());
        return true;
    }

    private PlayersMessage BuildPlayers()
    {
        var msg = new PlayersMessage();
        foreach (Player p in _players)
            msg.Players.Add(new PlayerInfo(p.Id, p.Name, p.Online, p.Online ? p.Connection.RoundTripMs : -1, p.Color));
        return msg;
    }

    /// <summary>Продвинуть общие часы на прошедшее реальное время и разослать разрешение тикать.</summary>
    public void Advance(TimeSpan elapsed)
    {
        for (int i = _pending.Count - 1; i >= 0; i--)
        {
            _pending[i].Waited += elapsed;
            if (_pending[i].Waited > _options.HelloTimeout)
            {
                _pending[i].Connection.Close("нет Hello за " + _options.HelloTimeout.TotalSeconds + " с");
                _pending.RemoveAt(i);
            }
        }

        _clockMs += elapsed.TotalMilliseconds;
        TryResolveResync();
        Autosave();
        if (_clockMs - _lastPingMs >= ProtocolLimits.PingIntervalMs)
        {
            _lastPingMs = _clockMs;
            foreach (Player p in _players)
                if (p.Online) p.Connection.SendPing();
        }
        if (_clockMs - _lastPlayersRefreshMs >= ProtocolLimits.PlayersRefreshMs)
        {
            _lastPlayersRefreshMs = _clockMs;
            string pings = string.Join(",", _players.Select(p => p.Online ? p.Connection.RoundTripMs : -1));
            if (pings != _lastPings)
            {
                _lastPings = pings;
                Broadcast(BuildPlayers());
            }
        }

        UpdateSpeed();
        int before = TickUpTo;
        if (Speed > 0)
        {
            _tickAccumulator += elapsed.TotalSeconds * TicksPerSecondAtSpeed1 * SpeedMultiplier[Speed];
            int whole = (int)Math.Floor(_tickAccumulator);
            _tickAccumulator -= whole;
            int target = TickUpTo + whole;
            // Не уходить вперёд от самого медленного игрока с миром больше чем на MaxLeadTicks (T-066).
            if (_options.MaxLeadTicks > 0)
            {
                int slowest = int.MaxValue;
                foreach (Player p in _players)
                    if (p.Online && p.HasWorld && p.LastTick < slowest) slowest = p.LastTick;
                if (slowest != int.MaxValue && target > slowest + _options.MaxLeadTicks)
                {
                    target = Math.Max(TickUpTo, slowest + _options.MaxLeadTicks);
                    _tickAccumulator = 0; // не копим «долг»: после отставания часы не рвутся вперёд
                }
            }
            TickUpTo = target;
        }
        if (TickUpTo != before || Speed != _lastSentSpeed)
        {
            _lastSentSpeed = Speed;
            Broadcast(new TickMessage { TickUpTo = TickUpTo, Speed = Speed });
        }
    }

    private void Handshake(IConnection connection, HelloMessage hello)
    {
        string? reject = null;
        RejectReason reason = RejectReason.Other;
        Player? first = _players.FirstOrDefault();
        if (!ProtocolInfo.IsCompatible(hello.ProtocolVersion))
            (reason, reject) = (RejectReason.ProtocolMismatch, $"версия протокола клиента {hello.ProtocolVersion}, сервера {ProtocolInfo.ProtocolVersion}");
        else if (hello.ModId != ProtocolInfo.ModId)
            (reason, reject) = (RejectReason.ModMismatch, "чужой мод: " + hello.ModId);
        else if (string.IsNullOrWhiteSpace(hello.PlayerName))
            (reason, reject) = (RejectReason.Other, "пустое имя игрока");
        else if (_gameVersion != null && hello.GameVersion != _gameVersion)
            (reason, reject) = (RejectReason.GameVersionMismatch, $"версия игры {hello.GameVersion}, в сессии {_gameVersion}");
        else if (_modListHash != null && hello.ModListHash != _modListHash)
            (reason, reject) = (RejectReason.ModListMismatch, "список модов отличается от сессии");

        Player? existing = _players.FirstOrDefault(p => p.Name == hello.PlayerName);
        if (reject == null && existing != null && existing.Online)
            (reason, reject) = (RejectReason.NameTaken, "игрок с таким именем уже в сессии");
        if (reject == null && existing == null && _players.Count(p => p.Online) >= _options.MaxPlayers)
            (reason, reject) = (RejectReason.ServerFull, "сервер заполнен");

        if (reject != null)
        {
            _log($"отказ {connection.Remote} ({hello.PlayerName}): {reject}");
            connection.Send(new RejectMessage { Reason = reason, Detail = reject });
            connection.Close("отказ: " + reject);
            return;
        }

        // Выделенный сервер, сессия пуста: выбрать тик мира до Welcome (до создания игрока — он ещё не в сети).
        if (_options.Dedicated && !_players.Any(p => p.Online))
        {
            string? why = AlignEmptySession(hello);
            if (why != null)
            {
                _log($"отказ {connection.Remote} ({hello.PlayerName}): {why}");
                connection.Send(new RejectMessage { Reason = RejectReason.Other, Detail = why });
                connection.Close("отказ: " + why);
                return;
            }
        }

        _gameVersion ??= hello.GameVersion;
        _modListHash ??= hello.ModListHash;

        Player player;
        if (existing != null)
        {
            // Возврат того же игрока: тот же номер. Догон пропущенного — T-045.
            existing.Connection = connection;
            existing.RequestedSpeed = 0;
            player = existing;
            _log($"вернулся игрок {player.Id} «{player.Name}» ({connection.Remote})");
        }
        else
        {
            player = new Player(_nextPlayerId++, hello.PlayerName, connection);
            _players.Add(player);
            _log($"вошёл игрок {player.Id} «{player.Name}» ({connection.Remote})");
        }

        player.SaveWorldId = hello.SaveWorldId;
        player.SaveTick = hello.SaveTick;
        // Мир есть, если сейв на тике сервера (иначе клиент сейчас попросит снимок).
        player.HasWorld = hello.SaveWorldId != 0 && hello.SaveTick == TickUpTo;
        player.LastTick = TickUpTo;
        if (player.HasWorld && WorldId == 0) WorldId = hello.SaveWorldId;
        connection.Send(new WelcomeMessage
        {
            PlayerId = player.Id,
            ServerTick = TickUpTo,
            SessionSeed = _options.SessionSeed,
            SaveWorldId = SaveWorldId,
            SaveTick = SaveTick,
        });
        connection.Send(BuildPermissions());
        // Состав: всем в сети, включая нового (он идёт сразу после Welcome и Permissions). Если игрок вернулся
        // раньше, чем сервер заметил его уход, состав не изменился — тогда только ему.
        if (!BroadcastPlayersIfChanged()) connection.Send(BuildPlayers());
        CheckSaveVersion(player);
        // Новому игроку — текущее разрешение, остальным — новая скорость (новый игрок держит паузу).
        connection.Send(new TickMessage { TickUpTo = TickUpTo, Speed = Speed });
        UpdateSpeed();
    }

    private string? _gameVersion;
    private ulong? _modListHash;

    private void Handle(Player player, Message m)
    {
        switch (m)
        {
            case CommandMessage c:
                PermissionLevel had = LevelOf(player.Id, c.MapId);
                if (had < c.Required)
                {
                    _log($"отклонена команда игрока {player.Id} #{c.ClientSeq} на карте {c.MapId}: нужно {c.Required}, есть {had}");
                    player.Connection.Send(new CommandRejectedMessage { ClientSeq = c.ClientSeq, MapId = c.MapId, Required = c.Required, Had = had });
                    break;
                }
                // Исполняется всеми после тика TickUpTo: ни один клиент ещё не ушёл дальше него,
                // а Tick с большим номером будет разослан только после этой команды.
                if (c.Kind == ProtocolIds.SaveWorldSnapshotKind)
                {
                    _log($"игрок {player.Id}: команда сохранения мира — только от сервера, отброшена");
                    break;
                }
                if (c.Kind == RequestWorldSnapshotKind)
                {
                    OrderWorldSnapshot(player, c.ClientSeq, "просьба игрока " + player.Id);
                    break;
                }
                Broadcast(new CommandOrderedMessage
                {
                    Seq = _nextSeq++,
                    Tick = TickUpTo,
                    PlayerId = player.Id,
                    ClientSeq = c.ClientSeq,
                    MapId = c.MapId,
                    Kind = c.Kind,
                    Payload = c.Payload,
                });
                break;

            case MapClaimMessage claim:
                Claim(player, claim.MapId);
                break;

            case GrantPermissionMessage grant:
                Grant(player, grant);
                break;

            case SnapshotAppliedMessage applied:
                // Поток «снимок → все загружают → все подтвердили → тики дальше» — T-032/T-034/T-045.
                _log($"игрок {player.Id}: снимок {applied.SnapshotId} применён, тик {applied.Tick}");
                player.HasWorld = true;
                player.LastTick = Math.Max(player.LastTick, applied.Tick);
                break;

            case SpeedRequestMessage s:
                player.RequestedSpeed = Math.Min(s.Speed, (byte)4);
                UpdateSpeed();
                break;

            case MapHashMessage h:
                CompareHash(player, h);
                break;

            case ChatMessage chat:
                Chat(player, chat.Text);
                break;

            case ChooseColorMessage choose:
                ChooseColor(player, choose.Color);
                break;

            case CursorMessage cursor:
                ForwardCursor(player, cursor);
                break;

            case SnapshotChunkMessage chunk:
                // Пересылка снимка остальным; снимок автосохранения (101) — только серверу.
                if (!_saveSnapshotIds.Contains(chunk.SnapshotId))
                    foreach (Player other in _players)
                        if (other != player && other.Online) other.Connection.Send(chunk);
                if (chunk.MapId == ProtocolIds.WorldMapId && _options.Store != null)
                    CollectWorldSnapshot(player, chunk);
                break;

            default:
                player.Connection.Close("неожиданное сообщение от клиента: " + m.Type);
                break;
        }
    }

    // Тик последнего Notice Desync по каждой карте.
    private readonly Dictionary<int, int> _desyncNoticeTick = new();

    private void CompareHash(Player player, MapHashMessage h)
    {
        if (h.Tick > player.LastTick) player.LastTick = h.Tick;
        var key = (h.MapId, h.Tick);
        if (!_hashes.TryGetValue(key, out var reports))
        {
            reports = new Dictionary<int, ulong>();
            _hashes[key] = reports;
            // Держим ограниченную историю: старые тики больше не сравниваются.
            if (_hashes.Count > 4096)
                foreach (var k in _hashes.Keys.OrderBy(k => k.tick).Take(1024).ToList()) _hashes.Remove(k);
        }
        if (reports.ContainsKey(player.Id)) return;
        // С кем сравнивать: первый присланный хеш этого тика.
        int firstId = reports.Count > 0 ? reports.Keys.First() : 0;
        ulong firstTotal = reports.Count > 0 ? reports[firstId] : 0;
        reports[player.Id] = h.Total;
        if (reports.Count < 2 || firstTotal == h.Total) return;

        _log($"РАСХОЖДЕНИЕ карта {h.MapId} тик {h.Tick}: игрок {firstId} {firstTotal:X16}, игрок {player.Id} {h.Total:X16}");
        Desync?.Invoke(new MapHashMessage { MapId = h.MapId, Tick = h.Tick, Total = firstTotal }, firstId, h, player.Id);
        // Хеш тика не позже уже заказанного общего снимка: этот снимок его и вылечит, второй не нужен.
        // (Клиенты присылают хеши с отставанием — без этой проверки за время одной перезагрузки заказывалось несколько.)
        if (h.Tick <= _lastWorldSnapshotTick || _pendingResync != null) return;
        if (!_desyncNoticeTick.TryGetValue(h.MapId, out int last) || h.Tick - last >= ProtocolLimits.DesyncNoticeIntervalTicks || h.Tick < last)
        {
            _desyncNoticeTick[h.MapId] = h.Tick;
            Broadcast(new NoticeMessage
            {
                Code = NoticeCode.Desync,
                Text = $"карта {h.MapId} тик {h.Tick}: игрок {firstId} ≠ игрок {player.Id}",
            });
            // v4: лечит сервер. Отдающего выбираем из большинства (T-066): ждём хеши остальных, но не дольше MajorityWait.
            _pendingResync = (key, _clockMs + _options.MajorityWait.TotalMilliseconds);
            TryResolveResync();
        }
    }

    private ((int map, int tick) key, double deadlineMs)? _pendingResync;

    /// <summary>
    /// Заказать лечебный снимок, когда известны хеши всех игроков с миром за тик расхождения (или вышло время).
    /// Отдающий — лучший пинг среди большинства; при равенстве групп — среди всех.
    /// </summary>
    private void TryResolveResync()
    {
        if (_pendingResync is not { } pending) return;
        if (!_hashes.TryGetValue(pending.key, out var reports)) { _pendingResync = null; return; }
        var expected = _players.Where(p => p.Online && p.HasWorld).Select(p => p.Id).ToList();
        bool all = expected.All(reports.ContainsKey);
        if (!all && _clockMs < pending.deadlineMs) return;
        _pendingResync = null;

        var groups = reports.Where(r => expected.Contains(r.Key)).GroupBy(r => r.Value).OrderByDescending(g => g.Count()).ToList();
        HashSet<int>? among = null;
        if (groups.Count > 1 && groups[0].Count() > groups[1].Count())
            among = new HashSet<int>(groups[0].Select(r => r.Key));
        string note = among == null ? "большинства нет" : $"большинство: {string.Join(", ", among.OrderBy(x => x))}";
        OrderWorldSnapshot(null, 0, $"расхождение карты {pending.key.map} на тике {pending.key.tick}; {note}", among);
    }

    /// <summary>
    /// Защита от запуска со старой копии (T-044): если у игрока копия того же мира с бо́льшим тиком,
    /// сервер, вероятно, поднят со старого сейва — предупреждаем всех и пишем в лог. Соединение не рвём.
    /// </summary>
    private void CheckSaveVersion(Player player)
    {
        if (player.SaveWorldId == 0 || SaveWorldId == 0) return;
        if (player.SaveWorldId != SaveWorldId)
        {
            string t = $"У игрока «{player.Name}» сейв другого мира ({player.SaveWorldId:X16}), на сервере — {SaveWorldId:X16}.";
            _log("ВНИМАНИЕ: " + t);
            player.Connection.Send(new NoticeMessage { Code = NoticeCode.DifferentWorld, Text = t });
            return;
        }
        if (player.SaveTick > SaveTick)
        {
            string t = $"У игрока «{player.Name}» копия сейва новее, чем на сервере (тик {player.SaveTick} против {SaveTick}). " +
                       "Похоже, сервер запущен со старой копии — продолжать с неё значит потерять прогресс.";
            _log("ВНИМАНИЕ: " + t);
            Broadcast(new NoticeMessage { Code = NoticeCode.NewerSaveOnClient, Text = t });
        }
    }

    private void CollectWorldSnapshot(Player player, SnapshotChunkMessage chunk)
    {
        var key = (player.Id, chunk.SnapshotId);
        if (!_assemblies.TryGetValue(key, out SnapshotAssembly? a))
        {
            if (chunk.ChunkCount > 65536) { _log($"игрок {player.Id}: снимок {chunk.SnapshotId} — слишком много кусков"); return; }
            a = new SnapshotAssembly { Tick = chunk.Tick, Chunks = new byte[]?[chunk.ChunkCount] };
            _assemblies[key] = a;
        }
        if (chunk.ChunkCount != a.Chunks.Length || chunk.Tick != a.Tick || a.Chunks[chunk.ChunkIndex] != null)
        {
            _log($"игрок {player.Id}: снимок {chunk.SnapshotId} — несогласованные куски, сборка сброшена");
            _assemblies.Remove(key);
            return;
        }
        a.Bytes += chunk.Data.Length;
        if (a.Bytes > _options.MaxSnapshotBytes)
        {
            _log($"игрок {player.Id}: снимок {chunk.SnapshotId} больше {_options.MaxSnapshotBytes} байт, отброшен");
            _assemblies.Remove(key);
            return;
        }
        a.Chunks[chunk.ChunkIndex] = chunk.Data;
        if (++a.Received < a.Chunks.Length) return;

        _assemblies.Remove(key);
        byte[] data = new byte[a.Bytes];
        int offset = 0;
        foreach (byte[]? part in a.Chunks)
        {
            Buffer.BlockCopy(part!, 0, data, offset, part!.Length);
            offset += part.Length;
        }
        ulong world = player.SaveWorldId != 0 ? player.SaveWorldId : WorldId;
        if (world == 0)
        {
            _log($"игрок {player.Id}: снимок мира без идентификатора мира — не сохранён");
            return;
        }
        try
        {
            SaveCopy copy = _options.Store!.Store(world, a.Tick, data);
            SaveWorldId = copy.WorldId;
            SaveTick = copy.Tick;
            _log($"сохранена копия мира {copy.WorldId:X16}, тик {copy.Tick}, {copy.Size} байт, sha256 {copy.Sha256[..16]}");
        }
        catch (Exception e)
        {
            _log($"игрок {player.Id}: копия мира не сохранена: {e.Message}");
        }
    }

    /// <summary>
    /// Уровень прав игрока на карте: мир и карта без владельца — полный для всех; владелец — полный;
    /// остальные — выданный владельцем уровень или «нет доступа».
    /// </summary>
    public PermissionLevel LevelOf(int playerId, int mapId)
    {
        if (mapId == ProtocolIds.WorldMapId || !_maps.TryGetValue(mapId, out MapRights? rights)) return PermissionLevel.Full;
        if (rights.Owner == playerId) return PermissionLevel.Full;
        return rights.Grants.TryGetValue(playerId, out PermissionLevel level) ? level : PermissionLevel.None;
    }

    /// <summary>Владелец карты или 0, если карта свободна.</summary>
    public int OwnerOf(int mapId) => _maps.TryGetValue(mapId, out MapRights? r) ? r.Owner : 0;

    private void Claim(Player player, int mapId)
    {
        if (mapId == ProtocolIds.WorldMapId)
        {
            _log($"игрок {player.Id}: заявка на мир как на карту — отклонена");
            return;
        }
        if (_maps.TryGetValue(mapId, out MapRights? existing))
        {
            if (existing.Owner != player.Id)
                _log($"игрок {player.Id}: карта {mapId} уже принадлежит игроку {existing.Owner} — заявка отклонена");
            player.Connection.Send(BuildPermissions());
            return;
        }
        if (_maps.Count >= ProtocolLimits.MaxMaps)
        {
            _log($"игрок {player.Id}: заявка на карту {mapId} — достигнут предел карт");
            return;
        }
        _maps[mapId] = new MapRights { Owner = player.Id };
        _log($"карта {mapId} принадлежит игроку {player.Id} «{player.Name}»");
        Broadcast(BuildPermissions());
    }

    private void Grant(Player player, GrantPermissionMessage g)
    {
        if (!_maps.TryGetValue(g.MapId, out MapRights? rights) || rights.Owner != player.Id)
        {
            _log($"игрок {player.Id}: выдача прав на карту {g.MapId} — не владелец, отклонено");
            player.Connection.Send(BuildPermissions());
            return;
        }
        if (g.PlayerId == player.Id || _players.All(p => p.Id != g.PlayerId))
        {
            _log($"игрок {player.Id}: выдача прав на карту {g.MapId} игроку {g.PlayerId} — нет такого игрока или это владелец");
            return;
        }
        if (g.Level == PermissionLevel.None) rights.Grants.Remove(g.PlayerId);
        else if (rights.Grants.Count < ProtocolLimits.MaxGrantsPerMap || rights.Grants.ContainsKey(g.PlayerId)) rights.Grants[g.PlayerId] = g.Level;
        else return;
        _log($"карта {g.MapId}: игрок {g.PlayerId} — {g.Level}");
        Broadcast(BuildPermissions());
    }

    private PermissionsMessage BuildPermissions()
    {
        var msg = new PermissionsMessage();
        foreach (var (mapId, rights) in _maps)
        {
            var m = new MapPermissions { MapId = mapId, OwnerPlayerId = rights.Owner };
            foreach (var (pid, level) in rights.Grants) m.Grants.Add(new PermissionGrant(pid, level));
            msg.Maps.Add(m);
        }
        return msg;
    }

    private void Chat(Player player, string text)
    {
        text = (text ?? "").Trim();
        if (text.Length == 0) return;
        if (text.Length > ProtocolLimits.MaxChatChars) text = text.Substring(0, ProtocolLimits.MaxChatChars);
        Queue<double> recent = player.ChatTimes;
        while (recent.Count > 0 && _clockMs - recent.Peek() >= ProtocolLimits.ChatWindowMs) recent.Dequeue();
        if (recent.Count >= ProtocolLimits.MaxChatPerWindow)
        {
            _log($"чат: игрок {player.Id} пишет слишком часто, сообщение отброшено");
            return;
        }
        recent.Enqueue(_clockMs);
        _log($"чат {player.Id} «{player.Name}»: {text}");
        Broadcast(new ChatMessage { PlayerId = player.Id, Text = text });
    }

    /// <summary>
    /// Пустая выделенная сессия: на какой тик встают часы. Новее — сейв игрока того же мира (его мир становится
    /// миром сессии), иначе копия на сервере. Мира нет ни там, ни там — отказ. Возвращает причину отказа или null.
    /// </summary>
    private string? AlignEmptySession(HelloMessage hello)
    {
        SaveCopy? latest = _options.Store?.Latest;
        bool playerHasWorld = hello.SaveWorldId != 0;
        int tick;
        string source;
        if (playerHasWorld && (latest == null || (hello.SaveWorldId == latest.WorldId && hello.SaveTick > latest.Tick)))
        {
            tick = hello.SaveTick;
            WorldId = hello.SaveWorldId;
            source = latest == null ? "сейв игрока (на сервере мира ещё нет)" : $"сейв игрока новее копии сервера ({latest.Tick})";
        }
        else if (latest != null)
        {
            tick = latest.Tick;
            source = $"копия сервера {latest.WorldId:X16}";
        }
        else
        {
            return "на сервере ещё нет мира: первым должен войти игрок с загруженным сейвом";
        }
        if (tick != TickUpTo)
        {
            _log($"сессия пуста: часы {TickUpTo} → {tick} ({source})");
            TickUpTo = tick;
            _tickAccumulator = 0;
            _hashes.Clear();
        }
        return null;
    }

    /// <summary>
    /// Кто отдаёт мир (T-065): из игроков в сети с загруженным миром — с наименьшим пингом до сервера.
    /// Путь «отдающий → сервер → получатель» короче всего через того, кто ближе к серверу; пинг между двумя
    /// игроками напрямую сервер не знает. Неизмеренный пинг считается худшим. 0 — отдать некому.
    /// </summary>
    public int ChooseWorldSender(Player? except = null, ISet<int>? among = null)
    {
        Player? best = null;
        foreach (Player p in _players)
        {
            if (!p.Online || !p.HasWorld || p == except || (among != null && !among.Contains(p.Id))) continue;
            int rtt = p.Connection.RoundTripMs < 0 ? int.MaxValue : p.Connection.RoundTripMs;
            int bestRtt = best == null ? int.MaxValue : (best.Connection.RoundTripMs < 0 ? int.MaxValue : best.Connection.RoundTripMs);
            if (best == null || rtt < bestRtt) best = p;
        }
        return best?.Id ?? ServerPlayerId;
    }

    /// <summary>
    /// Поставить в общий порядок «общий снимок мира» с назначенным отдающим. <paramref name="requester"/> — кто просил
    /// (null — сервер сам, например при расхождении). Отдающего нет — на выделенном сервере мир отдаёт сам сервер.
    /// </summary>
    private void OrderWorldSnapshot(Player? requester, int clientSeq, string why, ISet<int>? among = null)
    {
        int sender = ChooseWorldSender(requester != null && !requester.HasWorld ? requester : null, among);
        if (sender == ServerPlayerId && requester == null)
        {
            _log($"общий снимок ({why}) не заказан: мир некому отдать");
            return;
        }
        long seq = _nextSeq++;
        _lastWorldSnapshotTick = TickUpTo;
        Broadcast(new CommandOrderedMessage
        {
            Seq = seq,
            Tick = TickUpTo,
            PlayerId = requester?.Id ?? ServerPlayerId,
            ClientSeq = clientSeq,
            MapId = ProtocolIds.WorldMapId,
            Kind = RequestWorldSnapshotKind,
            Payload = WorldSnapshotOrder.Encode(sender),
        });
        _log($"общий снимок #{seq} ({why}): отдаёт {(sender == ServerPlayerId ? "сервер" : "игрок " + sender)}");
        if (sender == ServerPlayerId && requester != null) ServeStoredWorld(requester, seq);
    }

    // Номера снимков автосохранения: их куски не пересылаются игрокам. Держим последние несколько.
    private readonly HashSet<int> _saveSnapshotIds = new();
    private readonly Queue<int> _saveSnapshotOrder = new();

    // Тик последнего заказанного общего снимка: расхождения до него уже лечатся.
    private int _lastWorldSnapshotTick = int.MinValue;

    private double _lastAutosaveMs;
    private int _lastAutosaveTick = -1;
    private int _onlineWithWorld;

    /// <summary>
    /// Автосохранение: по таймеру и когда кто-то ушёл (пока ещё есть кому отдать мир). Тик не изменился — не сохраняем.
    /// </summary>
    private void Autosave()
    {
        if (_options.AutosaveInterval == null || _options.Store == null) return;
        int withWorld = _players.Count(p => p.Online && p.HasWorld);
        bool someoneLeft = withWorld < _onlineWithWorld;
        _onlineWithWorld = withWorld;
        bool due = _clockMs - _lastAutosaveMs >= _options.AutosaveInterval.Value.TotalMilliseconds;
        if ((!due && !someoneLeft) || withWorld == 0 || TickUpTo == _lastAutosaveTick) return;
        _lastAutosaveMs = _clockMs;
        _lastAutosaveTick = TickUpTo;
        int sender = ChooseWorldSender();
        long seq = _nextSeq++;
        int snapshotId = WorldSnapshotOrder.SnapshotId(seq);
        _saveSnapshotIds.Add(snapshotId);
        _saveSnapshotOrder.Enqueue(snapshotId);
        while (_saveSnapshotOrder.Count > 8) _saveSnapshotIds.Remove(_saveSnapshotOrder.Dequeue());
        Broadcast(new CommandOrderedMessage
        {
            Seq = seq,
            Tick = TickUpTo,
            PlayerId = ServerPlayerId,
            MapId = ProtocolIds.WorldMapId,
            Kind = ProtocolIds.SaveWorldSnapshotKind,
            Payload = WorldSnapshotOrder.Encode(sender),
        });
        _log($"автосохранение #{seq} на тике {TickUpTo}{(someoneLeft ? " (игрок ушёл)" : "")}: мир отдаёт игрок {sender}");
    }

    /// <summary>
    /// Выделенный сервер: игрок просит мир, а отдать его некому (он в сети один) — отдаём копию сервера.
    /// Куски — с номером, который ждёт клиент (<c>Seq &amp; 0x7FFFFFFF</c> его просьбы), и тиком копии.
    /// </summary>
    private void ServeStoredWorld(Player player, long requestSeq)
    {
        if (!_options.Dedicated) return;
        SaveCopy? latest = _options.Store?.Latest;
        if (latest == null) return;
        if (latest.Tick != TickUpTo)
        {
            _log($"мир для игрока {player.Id} не отдан: копия на тике {latest.Tick}, часы на {TickUpTo}");
            return;
        }
        byte[] data;
        try
        {
            data = _options.Store!.Read(latest);
        }
        catch (Exception e)
        {
            _log($"мир для игрока {player.Id} не прочитан: {e.Message}");
            return;
        }
        int id = WorldSnapshotOrder.SnapshotId(requestSeq);
        int size = ProtocolLimits.SnapshotChunkBytes;
        int count = Math.Max(1, (data.Length + size - 1) / size);
        for (int i = 0; i < count; i++)
        {
            int offset = i * size;
            int length = Math.Min(size, data.Length - offset);
            byte[] part = new byte[length];
            Buffer.BlockCopy(data, offset, part, 0, length);
            player.Connection.Send(new SnapshotChunkMessage
            {
                SnapshotId = id,
                MapId = ProtocolIds.WorldMapId,
                Tick = latest.Tick,
                ChunkIndex = i,
                ChunkCount = count,
                Data = part,
            });
        }
        _log($"игроку {player.Id} отдан мир с сервера: тик {latest.Tick}, {data.Length} байт, {count} кусков");
    }

    /// <summary>Цвет игрока (v5): только из палитры и не занятый другим игроком этой сессии (в сети или нет).</summary>
    private void ChooseColor(Player player, int color)
    {
        if (!PlayerColors.IsValid(color))
        {
            _log($"игрок {player.Id}: цвет {color:X6} не из палитры — отклонён");
            return;
        }
        Player? owner = _players.FirstOrDefault(p => p != player && p.Color == color);
        if (owner != null)
        {
            player.Connection.Send(new NoticeMessage { Code = NoticeCode.ColorTaken, Text = $"Цвет занят игроком «{owner.Name}»" });
            return;
        }
        player.Color = color;
        _log($"игрок {player.Id} «{player.Name}»: цвет {color:X6}");
        BroadcastPlayersIfChanged();
    }

    /// <summary>Курсор (v5): переслать остальным в сети, не чаще MaxCursorsPerSecond от игрока. В общий порядок не входит.</summary>
    private void ForwardCursor(Player player, CursorMessage cursor)
    {
        Queue<double> recent = player.CursorTimes;
        while (recent.Count > 0 && _clockMs - recent.Peek() >= 1000) recent.Dequeue();
        if (recent.Count >= ProtocolLimits.MaxCursorsPerSecond) return;
        recent.Enqueue(_clockMs);
        var forward = new CursorMessage { PlayerId = player.Id, MapId = cursor.MapId, X = cursor.X, Z = cursor.Z };
        foreach (Player p in _players)
            if (p != player && p.Online) p.Connection.Send(forward);
    }

    /// <summary>Сообщение в чат от сервера (консоль администратора).</summary>
    public void ServerChat(string text)
    {
        text = (text ?? "").Trim();
        if (text.Length == 0) return;
        if (text.Length > ProtocolLimits.MaxChatChars) text = text.Substring(0, ProtocolLimits.MaxChatChars);
        _log("чат сервера: " + text);
        Broadcast(new ChatMessage { PlayerId = ServerPlayerId, Text = text });
    }

    /// <summary>Отключить игрока (консоль администратора). Номер и права сохраняются — он может вернуться.</summary>
    public bool Kick(int playerId, string reason)
    {
        Player? p = _players.FirstOrDefault(x => x.Id == playerId && x.Online);
        if (p == null) return false;
        _log($"игрок {p.Id} «{p.Name}» отключён: {reason}");
        p.Connection.Close("отключён сервером: " + reason);
        return true;
    }

    /// <summary>Правило меньшей скорости: игра идёт на минимальной из скоростей игроков в сети; пауза любого — пауза всех.</summary>
    private void UpdateSpeed()
    {
        byte speed = 0;
        bool any = false;
        foreach (Player p in _players)
        {
            if (!p.Online) continue;
            speed = any ? Math.Min(speed, p.RequestedSpeed) : p.RequestedSpeed;
            any = true;
        }
        Speed = any ? speed : (byte)0;
        if (Speed == 0) _tickAccumulator = 0;
    }

    private void Broadcast(Message m)
    {
        foreach (Player p in _players)
            if (p.Online) p.Connection.Send(m);
    }
}
