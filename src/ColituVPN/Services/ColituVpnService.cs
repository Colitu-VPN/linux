using System.Diagnostics;
using Process = System.Diagnostics.Process;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Net;
using System.Net.Http;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using ServiceLib.Handler.Builder;
using ServiceLib.Handler.Fmt;
using ServiceLib.Services.CoreConfig;
using ServiceLib.Handler.SysProxy;
using ServiceLib.Helper;
using ServiceLib.Services;

namespace v2rayN.Desktop.Services;

public sealed class ColituVpnService
{
    public static ColituVpnService Instance { get; } = new();

    private const string ColituSubId = "colitu-api";
    private const string ColituRoutingRemarks = "Colitu VPN Protection";
    internal const string LanDirectRuleId = "colitu-lan-direct";
    private readonly ColituApiClient _api = ColituApiClient.Instance;
    private readonly Config _config = AppManager.Instance.Config;
    private readonly JsonSerializerOptions _jsonOptions = new() { PropertyNameCaseInsensitive = true };
    private readonly SemaphoreSlim _connectionLock = new(1, 1);
    private bool _coreReady;
    private ColituVpnSession _session = new();
    private ESysProxyType? _restoreSysProxyType;
    private string? _lastCoreMessage;
    private volatile bool _credentialsRefused;

    private List<ColituVpnServer> _lastServers = [];
    private Timer? _watchdog;
    private readonly ColituKillSwitch _killSwitch = new();
    /// <summary>When the last traffic check of this session ran (Environment.TickCount64).</summary>
    private long _lastTrafficCheckAt;
    private int _probeFailures;
    /// <summary>The last automatic transport switch after a mid-session Hysteria2 stall.</summary>
    private DateTimeOffset? _lastTransportSwitch;
    private bool _autoReconnecting;
    private bool _userDisconnected;
    private CancellationTokenSource? _connectCts;
    private bool _otherVpnWarned;
    /// <summary>
    /// Adaptive Connect memory per network: last-good server and transport, stalled transports,
    /// penalized servers (all expire). Saved with the state; written by the watchdog (thread pool)
    /// and read while a connect orders the servers and transports.
    /// </summary>
    private readonly ColituAdaptiveMemory _adaptive = new();
    /// <summary>Last ping of each server (failed ones too), with the network it was taken on.</summary>
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, ColituPingSample> _pings = new(StringComparer.OrdinalIgnoreCase);
    /// <summary>Network key of the current connect / tunnel: stall marks and penalties are filed under it.</summary>
    private string _networkKey = ColituAdaptiveConnect.NetworkKey("other", null);
    private string? _linkKind;
    private long _linkKindAt;
    /// <summary>The spare path the next core config gets behind the primary; null for none.</summary>
    private volatile ColituSparePlan? _sparePlan;
    /// <summary>Automatic mode: the next ranked server's transports, fetched next to the primary's settings.</summary>
    private ColituSpareServer? _spareServer;
    /// <summary>The running core's loopback check inbound that reaches the primary only; null without a spare.</summary>
    private volatile ColituVerifyInbound? _verifyInbound;
    /// <summary>The running core's second check inbound, to the spare alone; null without a spare.</summary>
    private volatile ColituVerifyInbound? _spareVerifyInbound;
    /// <summary>The tunnel as a whole (<see cref="ColituWarmSpare.CheckInboundTag"/>), set at every core start.</summary>
    private volatile ColituVerifyInbound? _checkInbound;
    /// <summary>The profile whose config gets the check inbound (not the TUN front of an Xray tunnel).</summary>
    private volatile string? _checkIndexId;
    /// <summary>Adaptive Connect 2.0 mid-session watcher (every transport; spare-aware).</summary>
    private readonly ColituTunnelWatch _watch = new();
    private readonly ColituSpareHealth _spareHealth = new();
    private long _lastSpareProbeAt;
    /// <summary>A replacement for a dead spare, waiting for a quiet moment to reload the core.</summary>
    private ColituSparePlan? _pendingSpare;
    /// <summary>When the pending spare swap was first held back (0: not held back); reset by a swap or a new spare target.</summary>
    private long _spareSwapDeferredSince;
    private string? _spareSwapDeferKey;
    /// <summary>Bytes on the physical adapter, sampled every watchdog second (for "the tunnel is quiet").</summary>
    private readonly Queue<(long Tick, long Bytes)> _trafficSamples = new();
    /// <summary>The primary server's transports of the current connect (the same-server spare picks from them).</summary>
    private List<(ColituConfigCandidate Candidate, ProfileItem Profile)> _currentProfiles = [];
    /// <summary>Transports that failed during the current connect (they go last when marks are ignored).</summary>
    private readonly HashSet<string> _failedThisConnect = new(StringComparer.OrdinalIgnoreCase);
    /// <summary>The last settings fetch of this connect failed on every API base at the network level (the cached settings, if any, were used).</summary>
    private volatile bool _apiUnreachable;
    /// <summary>The stall marks covered (almost) every transport in this round and are ignored.</summary>
    private bool _marksIgnored;

    /// <summary>A manual server where every transport failed: the error offers "Try the fastest server".</summary>
    public bool OfferFastestServer { get; private set; }
    private string? _activeTransport;
    /// <summary>Names and addresses of the current VPN server; the only hosts kept readable in core log lines.</summary>
    private readonly HashSet<string> _serverHosts = new(StringComparer.OrdinalIgnoreCase);
    private int _watchdogBusy;
    /// <summary>The status saved by the previous run; Connected here means it did not exit cleanly.</summary>
    private ColituVpnStatus _statusAtLastExit = ColituVpnStatus.Disconnected;

    /// <summary>The transport the current tunnel runs on (hysteria2, vless-reality, …); for support diagnostics.</summary>
    public string? ConnectedProtocol => Status == ColituVpnStatus.Connected ? _activeTransport : null;

    private ColituVpnService()
    {
        LoadState();
        // The warm spare goes into the generated core config before the core starts.
        CoreConfigHandler.ClientConfigPostProcessor = PostProcessConfig;
        StartWatchdog();
    }

    public event Action<ColituVpnStatus>? StatusChanged;

    public ColituVpnStatus Status { get; private set; } = ColituVpnStatus.Disconnected;
    /// <summary>The location the user chose; null means "best server".</summary>
    public ColituVpnServer? SelectedServer { get; private set; }
    public string? LastError { get; private set; }
    public DateTimeOffset? ConnectedAt { get; private set; }
    public ColituVpnPreferences Preferences => _session.Preferences.Normalize();

    /// <summary>
    /// Proxy mode is the default (TUN asks for the sudo password), so Colitu offers TUN once:
    /// on the first start, and once to users who already ran an older version. The offer
    /// never changes the mode by itself; only the user's answer does.
    /// </summary>
    public bool ShouldOfferTun => !_session.TunOfferAnswered && !Preferences.IsTunMode && ColituKillSwitch.SudoAvailable;

    public void MarkTunOfferAnswered()
    {
        _session = _session with { TunOfferAnswered = true };
        SaveState();
    }

    /// <summary>The plan-end banner was closed today (it comes back the next day).</summary>
    public bool PlanNoticeDismissedToday => _session.PlanNoticeDismissedOn == DateTime.Now.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    public void DismissPlanNoticeForToday()
    {
        _session = _session with { PlanNoticeDismissedOn = DateTime.Now.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) };
        SaveState();
    }

    /// <summary>
    /// Raised with a localization key for events the user should hear about even
    /// when they did not trigger them (automatic reconnects, dropped tunnels).
    /// </summary>
    public event Action<string>? Notice;

    /// <summary>The node the tunnel actually runs through (the panel may pick or fall back).</summary>
    public ColituVpnServer? ConnectedServer { get; private set; }

    /// <summary>"Best server": the panel chooses the recommended node.</summary>
    public bool IsAutoSelection => SelectedServer == null
        && (_session.SelectedServerId.IsNullOrEmpty() || string.Equals(_session.SelectionMode, ColituServerSelectionModes.Best, StringComparison.OrdinalIgnoreCase));

    public string? SavedServerId => IsAutoSelection ? null : SelectedServer?.Id ?? _session.SelectedServerId;

    // ── Simple / Advanced mode ─────────────────────────────────────────────
    /// <summary>Advanced mode: the expert settings are shown. Hidden settings keep their values and keep working.</summary>
    public bool AdvancedMode => Preferences.AdvancedMode;

    /// <summary>
    /// Simple mode only shows this as one line on the home screen: an expert setting changes what
    /// the connection does (split tunnelling, a rotating exit, a multihop route, or the kill switch
    /// holding the internet closed).
    /// </summary>
    public bool AdvancedSettingsActive => AdvancedSettingsOn(Preferences, RotationActive,
        SelectedServer is { IsMultihop: true }, KillSwitchEngaged && Status != ColituVpnStatus.Connected);

    /// <summary>
    /// The kill switch is on by default on desktop, so it only counts while it holds the internet
    /// closed; split tunnelling, a rotating exit and a chosen multihop route always count.
    /// </summary>
    internal static bool AdvancedSettingsOn(ColituVpnPreferences preferences, bool rotation, bool multihopSelected, bool killSwitchHolding) =>
        ColituSplitTunnel.IsActive(preferences) || rotation || multihopSelected || killSwitchHolding;

    /// <summary>
    /// Switches between Simple and Advanced mode: only what the screens show changes (no reconnect).
    /// Going Simple with a multihop route picked, the next connect uses the automatic choice; a live
    /// connection is not touched.
    /// </summary>
    public void SetAdvancedMode(bool advanced)
    {
        _session = _session with { Preferences = _session.Preferences with { AdvancedMode = advanced } };
        SaveState();
        if (!advanced && SelectedServer is { IsMultihop: true })
        {
            LogConnection("Simple mode: the multihop route is no longer the connection target; best server from the next connect");
            SetSelection(null);
        }
    }

    /// <summary>A localization key telling what the connect does right now ("Trying another server…"); null otherwise.</summary>
    public string? ConnectStage { get; private set; }

    /// <summary>
    /// The server "best server" connects to first (rank[0] of <see cref="RankedServers"/>); the
    /// location list and the home card show the same one. Null before the list is loaded.
    /// </summary>
    public ColituVpnServer? RecommendedServer => RankedServers().FirstOrDefault();

    /// <summary>The order automatic mode tries the servers in on the current network (see <see cref="ColituAdaptiveConnect.Rank"/>).</summary>
    public List<ColituVpnServer> RankedServers() =>
        ColituAdaptiveConnect.Rank(_lastServers, _pings, _adaptive, CurrentNetworkKey(), _session.ClientCountry, DateTimeOffset.UtcNow);

    /// <summary>
    /// Pings every location with a probe address (see <see cref="ColituLatency"/>) and keeps the
    /// results, failed ones too, for the automatic order. Returns the successful pings by server id.
    /// </summary>
    public async Task<Dictionary<string, int>> MeasurePingsAsync(IReadOnlyCollection<ColituVpnServer> servers)
    {
        var network = CurrentNetworkKey();
        var measured = await ColituLatency.MeasureAllAsync(servers);
        var at = DateTimeOffset.UtcNow;
        foreach (var server in servers.Where(ColituLatency.CanProbe))
        {
            _pings[server.Id!] = new ColituPingSample(measured.TryGetValue(server.Id!, out var ms) ? ms : null, at, network);
        }
        return measured;
    }

    /// <summary>"&lt;link&gt;|&lt;client_network&gt;" of the network this computer is on now; the link kind is looked up at most every 5 s.</summary>
    private string CurrentNetworkKey()
    {
        var now = Environment.TickCount64;
        if (_linkKind == null || now - Interlocked.Read(ref _linkKindAt) > 5000)
        {
            _linkKind = ColituNetwork.LinkKind();
            Interlocked.Exchange(ref _linkKindAt, now);
        }
        return ColituAdaptiveConnect.NetworkKey(_linkKind, _session.ClientNetwork);
    }

    /// <summary>Saves the panel's view of this device's network; values fetched through the VPN come back empty and never overwrite.</summary>
    private void RememberClientNetwork(ColituServersResponse servers)
    {
        var country = servers.ClientCountry.NullIfEmpty() ?? _session.ClientCountry;
        var network = ColituAdaptiveConnect.ResolveClientNetwork(servers.ClientNetwork, _session.ClientNetwork);
        var session = _session with { ClientCountry = country, ClientNetwork = network };
        if (servers.ClientNetwork.IsNotEmpty())
        {
            // Fetched with the VPN off: the token and the hints belong to this network (no hints = none blocked).
            session = session with
            {
                NetworkHintsBlocked = servers.NetworkHintsBlocked,
                NetworkHintsPreferred = servers.NetworkHintsPreferred,
                NetworkHintsNetwork = servers.ClientNetwork
            };
            if (servers.NetworkToken.IsNotEmpty())
            {
                session = session with { NetworkToken = servers.NetworkToken, NetworkTokenNetwork = servers.ClientNetwork, NetworkTokenAt = DateTimeOffset.UtcNow };
            }
        }
        if (session == _session)
        {
            return;
        }
        _session = session;
        SaveState();
    }

    /// <summary>The panel's tokens are valid for 48 h.</summary>
    private static readonly TimeSpan NetworkTokenValidFor = TimeSpan.FromHours(48);

    /// <summary>The network token for the network this PC is on (the last one seen with the VPN off), unless expired.</summary>
    private string? CurrentNetworkToken() =>
        _session.NetworkToken.IsNotEmpty() && string.Equals(_session.NetworkTokenNetwork, _session.ClientNetwork, StringComparison.Ordinal)
        && _session.NetworkTokenAt is { } at && DateTimeOffset.UtcNow - at < NetworkTokenValidFor
            ? _session.NetworkToken
            : null;

    /// <summary>Protocols the panel's hints call blocked on the current network.</summary>
    private IReadOnlyCollection<string> HintedBlockedList() =>
        string.Equals(_session.NetworkHintsNetwork, _session.ClientNetwork, StringComparison.Ordinal) ? _session.NetworkHintsBlocked ?? [] : [];

    private bool HintedBlocked(string protocol) =>
        ColituAdaptiveConnect.HintSaysBlocked(HintedBlockedList(), protocol, _adaptive, _networkKey, DateTimeOffset.UtcNow);

    /// <summary>Protocols the panel's hints call preferred on the current network (same network binding as the blocked ones).</summary>
    private IReadOnlyList<string> HintedPreferredList() =>
        string.Equals(_session.NetworkHintsNetwork, _session.ClientNetwork, StringComparison.Ordinal) ? _session.NetworkHintsPreferred ?? [] : [];

    public async Task<ColituServersResponse> GetServersAsync()
    {
        try
        {
            var servers = await _api.GetServersAsync();
            RememberClientNetwork(servers);
            // Routes are looked up by id like nodes: a saved choice may be one of them.
            _lastServers = servers.Servers.Concat(servers.Multihop).ToList();
            _multihopRoutes = servers.Multihop;
            // Never blocks the caller: the recovery set follows the server list (VPN on or off).
            _ = RefreshRecoverySetIfDueAsync();
            if (!IsAutoSelection && SelectedServer == null && _session.SelectedServerId.IsNotEmpty())
            {
                SelectedServer = _lastServers.FirstOrDefault(s => string.Equals(s.Id, _session.SelectedServerId, StringComparison.OrdinalIgnoreCase));
            }
            if (Rotation == null)
            {
                // Known before the first connect: a rotation restricts the transports to VLESS.
                _ = TryLoadRotationAsync();
            }
            return servers;
        }
        catch (ColituApiException ex) when (IsPlanError(ex))
        {
            return new ColituServersResponse { PlanRequired = true };
        }
    }

    // ── Multihop routes and the rotating exit IP ───────────────────────────
    private List<ColituVpnServer> _multihopRoutes = [];

    /// <summary>The double-VPN routes of the last server list.</summary>
    public IReadOnlyList<ColituVpnServer> MultihopRoutes => _multihopRoutes;

    /// <summary>The account's rotating-IP preference; null until it was fetched.</summary>
    public ColituRotationPreference? Rotation { get; private set; }

    /// <summary>The exit rotates on a schedule (VLESS only, so the connection avoids Hysteria2).</summary>
    public bool RotationActive => Rotation?.Active == true;

    /// <summary>The connected tunnel is a multihop route.</summary>
    public bool OnMultihopRoute => Status == ColituVpnStatus.Connected && ConnectedServer is { IsMultihop: true };

    public async Task<ColituRotationPreference> LoadRotationAsync(CancellationToken token = default)
    {
        var preference = await _api.GetRotationAsync(token);
        Rotation = preference;
        return preference;
    }

    private async Task TryLoadRotationAsync()
    {
        try
        {
            await LoadRotationAsync();
        }
        catch (Exception ex)
        {
            // An older panel has no rotation; the preference is simply unknown (off).
            LogConnection($"Rotation preference not loaded: {ex.GetType().Name}");
        }
    }

    public async Task<ColituRotationPreference> SaveRotationAsync(int intervalSeconds, IEnumerable<string> countries, CancellationToken token = default)
    {
        var preference = await _api.SetRotationAsync(intervalSeconds, countries, token);
        Rotation = preference;
        return preference;
    }

    /// <summary>
    /// Rotation status of the node this device is connected to; null when not connected to a
    /// node (a multihop route has fixed ends and does not rotate).
    /// </summary>
    public async Task<ColituRotationStatus?> GetRotationStatusAsync(CancellationToken token = default)
    {
        if (Status != ColituVpnStatus.Connected || ConnectedServer is not { IsMultihop: false, Id: { Length: > 0 } nodeId })
        {
            return null;
        }
        return await _api.GetRotationStatusAsync(nodeId, token);
    }

    public async Task<ColituStatsResponse?> GetStatsAsync()
    {
        try
        {
            return await _api.GetStatsAsync();
        }
        catch (ColituApiException ex) when (IsPlanError(ex))
        {
            return null;
        }
    }

    /// <summary>Connects to <paramref name="server"/>, or to the panel's recommended node when null.</summary>
    public async Task ConnectAsync(ColituVpnServer? server)
    {
        await _connectionLock.WaitAsync();
        try
        {
            _userDisconnected = false;
            await ConnectCoreAsync(server);
        }
        finally
        {
            _connectionLock.Release();
        }
    }

    /// <summary>Connects with the saved choice (a specific location or the best server).</summary>
    public Task ConnectSavedAsync() => ConnectAsync(IsAutoSelection ? null : SelectedServer ?? FindServer(_session.SelectedServerId));

    private ColituVpnServer? FindServer(string? id)
    {
        return id.IsNullOrEmpty()
            ? null
            : _lastServers.FirstOrDefault(s => string.Equals(s.Id, id, StringComparison.OrdinalIgnoreCase));
    }

    /// <param name="keepPreference">
    /// An automatic transport switch to the node already in use: the device's preferred node on the
    /// panel (the user's choice, possibly "best server") is left as it is.
    /// </param>
    private async Task ConnectCoreAsync(ColituVpnServer? server, bool keepPreference = false)
    {
        if (server is { Available: false })
        {
            throw new ColituConnectException(Loc.I["err.unreachable"]);
        }

        EnsureSudoForTun();
        await EnsureCoreReadyAsync();
        _connectCts?.Cancel();
        var attempt = _connectCts = new CancellationTokenSource();
        var token = attempt.Token;
        ConnectStage = null;
        _sparePlan = null;
        _spareServer = null;
        _pendingSpare = null;
        ResetSpareSwapDeferral();
        _failedThisConnect.Clear();
        _apiUnreachable = false;
        OfferFastestServer = false;
        SetStatus(ColituVpnStatus.Connecting);
        LastError = null;
        _lastCoreMessage = null;
        _credentialsRefused = false;
        SaveState();
        // "Best server": this app ranks the servers itself and moves on to the next one when one
        // carries no traffic on this network. A server the user picked is never switched.
        var automatic = server == null;
        var connectWatch = Stopwatch.StartNew();

        try
        {
            LogConnection($"Connecting to {(server == null ? "best server" : $"server id={server.Id}, name={server.Name}")}");
            _linkKind = null;
            _networkKey = CurrentNetworkKey();
            if (Preferences.IsTunMode && !_otherVpnWarned && ColituNetwork.CompetingVpnAdapter() is { } other)
            {
                _otherVpnWarned = true;
                LogConnection($"Another VPN adapter holds a default route: {other}");
                Notice?.Invoke("warn.otherVpn");
            }

            var ranked = automatic ? RankedServers() : [];
            if (automatic)
            {
                LogConnection(ranked.Count == 0
                    ? "Automatic order: no server list yet; the panel chooses"
                    : $"Automatic order: {string.Join(" > ", ranked.Take(5).Select(item => item.Id))}");
            }
            // Servers that carried nothing during this connect: not tried again, and excluded when
            // the panel chooses.
            var failed = new List<string>();
            ColituVpnServer? connected = null;
            // Parallel connect: only the spare on the next server carried traffic, so it leads the next round.
            ColituSpareServerWins? swap = null;
            // Adaptive Connect 3.0: once set, the recovery set's servers replace the panel (API
            // unreachable, no usable cache); the last server failure is what the connect ends with.
            ColituRecoverySet? recovery = null;
            Exception? lastServerFailure = null;
            for (var round = 1; ; round++)
            {
                var wasSwap = swap != null;
                var viaRecovery = recovery != null && !wasSwap;
                var recoveryEnvelope = viaRecovery ? recovery!.NextServer(failed) : null;
                if (viaRecovery && recoveryEnvelope == null)
                {
                    LogConnection($"Recovery set: all {recovery!.Configs.Count} server(s) tried, giving up");
                    throw lastServerFailure ?? new ColituConnectException(Loc.I["err.noServers"]);
                }
                var target = swap != null
                    ? FindServer(swap.Spare.ServerId) ?? swap.Spare.Config.Server ?? new ColituVpnServer { Id = swap.Spare.ServerId }
                    : viaRecovery
                    ? null
                    : automatic
                    ? ranked.FirstOrDefault(item => !failed.Contains(item.Id!, StringComparer.OrdinalIgnoreCase))
                    : server;
                if (round > 1)
                {
                    ConnectStage = "connect.tryingOther";
                    StatusChanged?.Invoke(Status);
                    LogConnection($"Trying another server ({(viaRecovery ? $"recovery set, id={recoveryEnvelope!.Server?.Id}" : target == null ? "the panel chooses" : $"id={target.Id}")}, {connectWatch.ElapsedMilliseconds} ms into the connect)");
                }
                var configTask = swap != null ? Task.FromResult(swap.Spare.Config)
                    : viaRecovery ? FetchRecoveryConfigAsync(recoveryEnvelope!, token)
                    : FetchConfigAsync(target, token, keepPreference || round > 1, automatic, failed);
                // Started after the primary's request reset the panel connections; never awaited.
                var spareTask = viaRecovery ? StartRecoverySpare(recovery!, recoveryEnvelope!, failed, token) : StartSpareFetch(automatic, ranked, target, failed, token);
                ColituVpnConfigResponse config;
                try
                {
                    config = await configTask;
                }
                catch (Exception ex) when (viaRecovery && !token.IsCancellationRequested)
                {
                    // Its settings could not be turned into a connection (e.g. a rotating exit needs VLESS).
                    var skipped = recoveryEnvelope!.Server?.Id;
                    LogConnection($"Recovery server {skipped} unusable ({ex.GetType().Name}: {ex.Message}); trying the next one");
                    failed.Add(skipped!);
                    lastServerFailure = ex;
                    continue;
                }
                catch (Exception ex) when (!viaRecovery && !wasSwap && !token.IsCancellationRequested
                    && ColituAdaptiveConnect3.ShouldUseRecovery(automatic, _apiUnreachable && ColituRecoverySet.IsApiUnreachable(ex), cacheUsable: false))
                {
                    // Every API base failed at the network level and nothing cached is left to try.
                    recovery = BeginRecovery();
                    if (recovery == null)
                    {
                        throw;
                    }
                    lastServerFailure = ex;
                    continue;
                }
                var forcedFirst = swap?.Protocol;
                swap = null;
                token.ThrowIfCancellationRequested();
                if (config.ServerId.IsNotEmpty() && target?.Id is { } requested && !string.Equals(config.ServerId, requested, StringComparison.OrdinalIgnoreCase))
                {
                    // The panel falls back to another healthy node when the preferred one is unavailable.
                    LogConnection($"Panel selected fallback node {config.ServerId} instead of {requested}");
                }

                connected = FindServer(config.ServerId) ?? config.Server ?? target;
                var connectedId = config.ServerId ?? connected?.Id;
                try
                {
                    var profiles = await ImportConfigAsync(config, connected);
                    if (forcedFirst != null && profiles.FindIndex(item => item.Candidate.Protocol == forcedFirst) is > 0 and var first)
                    {
                        // It just carried traffic as the spare: it leads.
                        var winner = profiles[first];
                        profiles.RemoveAt(first);
                        profiles.Insert(0, winner);
                    }
                    _currentProfiles = profiles;
                    await PrepareConnectionModeAsync(config.Server?.CountryCode ?? connected?.CountryCode);
                    // Whatever arrived by now (or was cached); the connect never waits for the spare.
                    _spareServer = TakeSpareServer(spareTask, connectedId);
                    // While the kill switch blocks (tunnel lost, or rules kept from a crashed run), let
                    // this attempt's server (resolved with the config) through before the core dials it.
                    if (_killSwitch.IsEngaged && KillSwitchApplies)
                    {
                        await EngageKillSwitchAsync("allow the new server before connecting");
                    }
                    // Automatic mode: about 20 s per server and 45 s for the whole connect.
                    TimeSpan? serverBudget = automatic ? ServerBudget(connectWatch.Elapsed) : null;
                    try
                    {
                        await StartFirstWorkingProfileAsync(profiles, config, connected, token, serverBudget);
                    }
                    catch (Exception ex) when (_credentialsRefused && ex is not (OperationCanceledException or ColituSpareServerWins))
                    {
                        // The panel creates this device's credentials for a server the first time it is
                        // chosen; the server applies them some seconds later and refuses us until then.
                        LogConnection("The server refused the credentials (new on this server); retrying in 15 s");
                        await StopCoreAsync();
                        await Task.Delay(TimeSpan.FromSeconds(15), token);
                        _credentialsRefused = false;
                        await StartFirstWorkingProfileAsync(profiles, config, connected, token, serverBudget);
                    }
                    break;
                }
                catch (ColituSpareServerWins wins) when (connectWatch.Elapsed < ConnectBudget)
                {
                    // Roles swap: the spare's server and transport lead the next round (one quick reload).
                    swap = wins;
                    await StopCoreAsync();
                }
                catch (Exception ex) when (automatic && IsServerFailure(ex) && connectedId.IsNotEmpty())
                {
                    // Every transport of this server failed on this network: it goes last here for
                    // 30 minutes, and the next ranked server gets its turn.
                    _adaptive.Penalize(_networkKey, connectedId, DateTimeOffset.UtcNow);
                    failed.Add(connectedId!);
                    if (target?.Id is { Length: > 0 } targetId && !failed.Contains(targetId, StringComparer.OrdinalIgnoreCase))
                    {
                        failed.Add(targetId);
                    }
                    SaveState();
                    lastServerFailure = ex;
                    if (recovery != null)
                    {
                        // The set has at most four servers; the loop ends when none is left.
                        LogConnection($"Server {connectedId} carried no traffic on this network ({ex.Message}); penalized for 30 minutes, trying the next recovery server");
                        await StopCoreAsync();
                        continue;
                    }
                    if (round >= ColituAdaptiveConnect.MaxServersPerConnect || connectWatch.Elapsed > NextServerDeadline)
                    {
                        // The settings that failed came from the cache because the panel was unreachable:
                        // the cache is spent, so the recovery set is the last resort.
                        if (ColituAdaptiveConnect3.ShouldUseRecovery(automatic, _apiUnreachable, cacheUsable: false) && BeginRecovery() is { } begun)
                        {
                            recovery = begun;
                            await StopCoreAsync();
                            continue;
                        }
                        LogConnection($"Server {connectedId} carried no traffic ({ex.Message}); {round} server(s) tried in {connectWatch.ElapsedMilliseconds} ms, giving up");
                        throw;
                    }
                    LogConnection($"Server {connectedId} carried no traffic on this network ({ex.Message}); penalized for 30 minutes, trying the next one");
                    await StopCoreAsync();
                }
            }

            ConnectedServer = connected;
            ConnectedAt = DateTimeOffset.Now;
            ConnectStage = null;
            if (recovery != null)
            {
                LogConnection($"Connected through the recovery set (server {connected?.Id}); the server list, settings and recovery set are refreshed through the tunnel");
                _ = RefreshAfterRecoveryAsync();
            }
            if (connected is { IsMultihop: false, Id.Length: > 0 })
            {
                var now = DateTimeOffset.UtcNow;
                _adaptive.RememberGoodServer(_networkKey, connected.Id, now);
                _adaptive.RememberGoodTransport(_networkKey, connected.Id, _activeTransport, now);
            }
            _probeFailures = 0;
            _watch.Reset();
            _spareHealth.Reset();
            // First traffic check 5 s after the connect; the spare's first probe after its own interval.
            _lastTrafficCheckAt = _lastSpareProbeAt = Environment.TickCount64;
            await ApplyKillSwitchForConnectedTunnelAsync();
            KillSwitchRecoveryPending = false;
            SetStatus(ColituVpnStatus.Connected);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            ConnectStage = null;
            LogConnection("Connection attempt cancelled");
            await StopCoreAsync();
            await SysProxyHandler.UpdateSysProxy(_config, true);
            await RestoreProxyPreferenceAsync();
            await RestoreRoutingPreferenceAsync();
            ConnectedServer = null;
            ConnectedAt = null;
            SetStatus(ColituVpnStatus.Disconnected);
        }
        catch (Exception ex)
        {
            ConnectStage = null;
            // A server the user picked where every transport failed: offer the fastest server (one tap).
            OfferFastestServer = !automatic && server is not null && IsServerFailure(ex);
            LastError = FriendlyConnectionError(ex);
            LogConnection($"Connection failed: {ex}");
            await StopCoreAsync();
            await SysProxyHandler.UpdateSysProxy(_config, true);
            await RestoreProxyPreferenceAsync();
            await RestoreRoutingPreferenceAsync();
            ConnectedServer = null;
            ConnectedAt = null;
            SetStatus(ColituVpnStatus.Error);
            if (ex is ColituApiException api && IsPlanError(api))
            {
                throw new ColituPlanRequiredException();
            }
            if (ex is ColituApiException { ErrorCode: ColituDevicePause.ErrorCode } paused)
            {
                // The plan allows fewer devices: the kill switch must not keep the user
                // offline on the paused screen, so the rules go (when this run can remove them).
                await ReleaseKillSwitchAsync("this device is paused (device limit)");
                throw new ColituDevicePausedException(paused.DevicePause ?? new ColituDevicePause());
            }
            if (ex is ColituSudoRequiredException)
            {
                throw;
            }
            throw new ColituConnectException(LastError, ex);
        }
    }

    // ── Warm spare: a second path inside the running core ──────────────────
    /// <summary>The spare's settings may take this long; after that the connect goes on without them.</summary>
    private static readonly TimeSpan SpareFetchBudget = TimeSpan.FromSeconds(2);

    /// <summary>
    /// Automatic mode: fetches the settings of the next ranked server (not failed, not penalized
    /// on this network) for the warm spare, next to the primary's. Null when there is no such
    /// server or the setting is off.
    /// </summary>
    private ColituSpareFetch? StartSpareFetch(bool automatic, IReadOnlyList<ColituVpnServer> ranked, ColituVpnServer? target, IReadOnlyCollection<string> failed, CancellationToken token)
    {
        if (!automatic || !Preferences.WarmSpareEnabled)
        {
            return null;
        }
        var now = DateTimeOffset.UtcNow;
        var spare = ranked.FirstOrDefault(item => item.Id is { Length: > 0 } id
            && !string.Equals(id, target?.Id, StringComparison.OrdinalIgnoreCase)
            && !failed.Contains(id, StringComparer.OrdinalIgnoreCase)
            && !_adaptive.IsPenalized(_networkKey, id, now));
        return spare == null ? null : new ColituSpareFetch(spare.Id!, FetchSpareServerAsync(spare, token));
    }

    private async Task<ColituSpareServer?> FetchSpareServerAsync(ColituVpnServer server, CancellationToken token)
    {
        var cacheKey = SpareCacheKey(server.Id!);
        ColituVpnConfigResponse? config = null;
        try
        {
            using var budget = CancellationTokenSource.CreateLinkedTokenSource(token);
            budget.CancelAfter(SpareFetchBudget);
            config = await _api.GetConfigAsync(null, budget.Token, node: server.Id);
            if (config != null)
            {
                SaveCachedConfig(cacheKey, config);
            }
        }
        catch (Exception ex) when (!token.IsCancellationRequested)
        {
            LogConnection($"Warm spare settings for server {server.Id} not fetched in {SpareFetchBudget.TotalSeconds:0} s ({ex.GetType().Name})");
        }
        return ToSpareServer(config ?? LoadCachedConfig(cacheKey), server.Id!, cached: config == null);
    }

    /// <summary>The spare server's transports as profiles (never stored in the profile list); null when unusable.</summary>
    private ColituSpareServer? ToSpareServer(ColituVpnConfigResponse? config, string serverId, bool cached = true)
    {
        if (config == null || config.Server?.IsMultihop == true
            || (config.ServerId.IsNotEmpty() && !string.Equals(config.ServerId, serverId, StringComparison.OrdinalIgnoreCase)))
        {
            return null;
        }
        if (RotationActive)
        {
            // A rotating exit runs on VLESS only, the spare too.
            config = ColituApiClient.RestrictToVless(config);
            if (config == null)
            {
                return null;
            }
        }
        var items = config.Candidates
            .Select(candidate => (candidate.Protocol, Item: FmtHandler.ResolveConfig(candidate.ShareLink, out _)))
            .Where(item => item.Item != null && item.Protocol.IsNotEmpty())
            .Select(item => (item.Protocol, Item: item.Item!))
            .ToList();
        foreach (var (_, item) in items)
        {
            // The kill switch lets the known server addresses through (cached settings were never pinned here).
            if (IPAddress.TryParse(item.Address, out var address))
            {
                ColituNetwork.RememberServerAddress(address);
            }
        }
        return items.Count == 0 ? null : new ColituSpareServer(serverId, items, config, cached);
    }

    /// <summary>The spare server if its settings are already here (or cached); the connect never waits for them.</summary>
    private ColituSpareServer? TakeSpareServer(ColituSpareFetch? fetch, string? primaryId)
    {
        if (fetch == null)
        {
            return null;
        }
        ColituSpareServer? spare;
        if (fetch.Task.IsCompleted)
        {
            spare = fetch.Task.IsCompletedSuccessfully ? fetch.Task.Result : null;
        }
        else
        {
            LogConnection("Warm spare settings not here yet; connecting with the cached ones, if any");
            spare = ToSpareServer(LoadCachedConfig(SpareCacheKey(fetch.ServerId)), fetch.ServerId, cached: true);
        }
        return spare != null && !string.Equals(spare.ServerId, primaryId, StringComparison.OrdinalIgnoreCase) ? spare : null;
    }

    private static string SpareCacheKey(string serverId) => serverId;

    /// <summary>
    /// The spare behind <paramref name="primaryProtocol"/> (Adaptive Connect 2.0). Automatic mode: a
    /// transport proven on this network on the next ranked server (the primary's own first; same
    /// family allowed), only if nothing is proven the other family. Otherwise (manual server, or
    /// nothing fits there) the same server: the other family, Shadowsocks last, never the primary's
    /// own transport. Stalled transports, penalized servers, multihop routes and what failed as a
    /// spare in this connect (<paramref name="excluded"/>, "server|transport") never; hinted-blocked
    /// ones only when nothing else is left. Only transports the primary's core can run.
    /// </summary>
    private ColituSparePlan? PlanSpare(string primaryProtocol, ProfileItem primary, List<(ColituConfigCandidate Candidate, ProfileItem Profile)> profiles,
        ColituVpnConfigResponse config, ColituVpnServer? server, ISet<string>? excluded = null)
    {
        if (!SpareAllowed(Preferences, server, config))
        {
            LogConnection(Preferences.WarmSpareEnabled ? "warm spare none: multihop route" : "warm spare none: turned off");
            return null;
        }
        var singBox = ColituWarmSpare.IsUdp(primaryProtocol);
        var serverId = config.ServerId ?? server?.Id;
        var now = DateTimeOffset.UtcNow;
        var proven = _adaptive.ProvenOnNetwork(_networkKey, now);
        bool Excluded(string? id, string protocol) => excluded?.Contains(SpareKey(id, protocol)) == true;
        if (_spareServer is { } other && !string.Equals(other.ServerId, serverId, StringComparison.OrdinalIgnoreCase)
            && !_adaptive.IsPenalized(_networkKey, other.ServerId, now)
            && ColituWarmSpare.ChooseOnNextServer(primaryProtocol, other.Items.Select(item => item.Protocol), singBox,
                protocol => proven.Contains(protocol, StringComparer.OrdinalIgnoreCase),
                protocol => SpareStalled(other.ServerId, protocol) || Excluded(other.ServerId, protocol), HintedBlocked) is { } choice)
        {
            var plan = new ColituSparePlan(primary.IndexId, other.Items.First(item => item.Protocol == choice.Protocol).Item, choice.Protocol, other.ServerId, choice.Reason, other.ServerId);
            LogSpareAttached(plan, primaryProtocol, proven, other.Items.Select(item => item.Protocol), other.Cached ? "cached" : "fetched");
            return plan;
        }
        if (ColituWarmSpare.ChooseOnSameServer(primaryProtocol, profiles.Select(item => item.Candidate.Protocol), singBox,
                protocol => SpareStalled(serverId, protocol), HintedBlocked, protocol => Excluded(serverId, protocol)) is { } same)
        {
            var plan = new ColituSparePlan(primary.IndexId, profiles.First(item => item.Candidate.Protocol == same.Protocol).Profile, same.Protocol, serverId, same.Reason);
            LogSpareAttached(plan, primaryProtocol, proven, profiles.Select(item => item.Candidate.Protocol), "fetched");
            return plan;
        }
        LogConnection($"warm spare none: nothing fits behind {primaryProtocol} (proven here [{string.Join(", ", proven)}])");
        return null;
    }

    private void LogSpareAttached(ColituSparePlan plan, string primaryProtocol, IEnumerable<string> proven, IEnumerable<string> offered, string source)
    {
        var stalled = offered.Distinct(StringComparer.OrdinalIgnoreCase).Where(protocol => _adaptive.IsStalled(_networkKey, plan.ServerId, protocol, DateTimeOffset.UtcNow));
        LogConnection($"warm spare attached: {plan.NextServerId ?? "same server"}/{plan.Protocol} (primary {primaryProtocol}; spare reason: {plan.Reason}; proven here [{string.Join(", ", proven)}]; stalled [{string.Join(", ", stalled)}]; profile {source})");
    }

    /// <summary>Stall marks for the spare choice; ignored in a round where they cover (almost) every transport.</summary>
    private bool SpareStalled(string? serverId, string protocol) =>
        !_marksIgnored && _adaptive.IsStalled(_networkKey, serverId, protocol, DateTimeOffset.UtcNow);

    private static string SpareKey(string? serverId, string protocol) => $"{serverId ?? "-"}|{protocol}";

    private static string Describe(ColituSparePlan plan) => $"{plan.NextServerId ?? "same server"}/{plan.Protocol}";

    /// <summary>No spare with the setting off or on a multihop route (its ends are fixed).</summary>
    internal static bool SpareAllowed(ColituVpnPreferences preferences, ColituVpnServer? server, ColituVpnConfigResponse config) =>
        preferences.WarmSpareEnabled && server is not { IsMultihop: true } && config.Server is not { IsMultihop: true };

    /// <summary>
    /// <see cref="CoreConfigHandler.ClientConfigPostProcessor"/>: the warm spare, then the tunnel
    /// check inbound, to the main core's config.
    /// </summary>
    private string PostProcessConfig(CoreConfigContext context, string json)
    {
        json = ApplyWarmSpare(context, json);
        if (_checkIndexId == null || !string.Equals(context.Node?.IndexId, _checkIndexId, StringComparison.Ordinal))
        {
            return json;
        }
        try
        {
            var check = ColituWarmSpare.NewVerifyInbound();
            if (ColituWarmSpare.AddCheckInbound(json, context.RunCoreType == ECoreType.sing_box, check) is { } added)
            {
                _checkInbound = check;
                return added;
            }
            LogConnection("Tunnel check inbound could not be added; checks use the local proxy port");
        }
        catch (Exception ex)
        {
            Logging.SaveLog("ColituVpnService.AddCheckInbound", ex);
        }
        return json;
    }

    /// <summary>Where a check of the whole tunnel goes: the check inbound, else the local proxy port (routing rules apply there).</summary>
    private (int Port, NetworkCredential? Credentials) TunnelCheckTarget() => _checkInbound is { } check
        ? (check.Port, new NetworkCredential(check.User, check.Password))
        : (AppManager.Instance.GetLocalPort(EInboundProtocol.socks), null);

    /// <summary>
    /// Adds the planned spare to the main
    /// core's config (the TUN front of an Xray tunnel and other configs stay as generated). Any
    /// problem leaves the config without a spare.
    /// </summary>
    private string ApplyWarmSpare(CoreConfigContext context, string json)
    {
        var plan = _sparePlan;
        if (plan == null || !string.Equals(context.Node?.IndexId, plan.PrimaryIndexId, StringComparison.Ordinal))
        {
            return json;
        }
        try
        {
            var singBox = context.RunCoreType == ECoreType.sing_box;
            if (!ColituWarmSpare.RunsOn(plan.Protocol, singBox))
            {
                LogConnection($"Warm spare {plan.Protocol} does not run in {context.RunCoreType}; none this time");
                return json;
            }
            var spareContext = context with { Node = plan.Spare };
            var generated = singBox
                ? new CoreConfigSingboxService(spareContext).GenerateClientConfigContent()
                : new CoreConfigV2rayService(spareContext).GenerateClientConfigContent();
            var outbound = generated.Success && generated.Data?.ToString() is { } spareJson ? ColituWarmSpare.FindOutbound(spareJson) : null;
            // The connect check reaches the primary through this inbound, never the spare.
            var verify = ColituWarmSpare.NewVerifyInbound();
            // And the spare alone, for the parallel connect and the spare health probe.
            var spareVerify = ColituWarmSpare.NewVerifyInbound();
            var merged = outbound == null ? null
                : singBox ? ColituWarmSpare.ApplySingbox(json, outbound, verify: verify, spareVerify: spareVerify)
                : ColituWarmSpare.ApplyXray(json, outbound, verify: verify, spareVerify: spareVerify);
            if (merged == null)
            {
                LogConnection($"Warm spare {plan.Protocol} could not be added to the {context.RunCoreType} config; none this time");
                return json;
            }
            var problems = singBox ? ColituWarmSpare.SingboxProblems(merged) : ColituWarmSpare.XrayProblems(merged);
            if (problems.Count > 0)
            {
                LogConnection($"Warm spare config failed its tag check ({string.Join("; ", problems.Take(3))}); none this time");
                return json;
            }
            _verifyInbound = verify;
            _spareVerifyInbound = spareVerify;
            foreach (var host in new[] { plan.Spare.Address, plan.Spare.Sni })
            {
                if (host.IsNotEmpty()) _serverHosts.Add(host);
            }
            LogConnection($"Warm spare: {plan.Protocol} on server {plan.ServerId} behind the primary ({context.RunCoreType}, probe every {(singBox ? ColituWarmSpare.SingboxProbeInterval : ColituWarmSpare.XrayProbeInterval).TotalSeconds:0} s)");
            return merged;
        }
        catch (Exception ex)
        {
            Logging.SaveLog("ColituVpnService.ApplyWarmSpare", ex);
            return json;
        }
    }

    // ── Recovery set (Adaptive Connect 3.0) ────────────────────────────────
    // GET /client/recovery: up to four config envelopes, fetched in the background next to the
    // server list and stored (encrypted, they carry the device's server credentials) next to the
    // config cache. Used only by an automatic connect that cannot reach the panel on any API base
    // and has no usable cached settings. The nodes still check the device credential: it grants
    // nothing new.
    private int _recoveryRefreshing;

    private static string RecoverySetPath() => ColituHardening.UserConfigPath("colitu-recovery.bin");

    /// <summary>The stored set (also an expired one, so that the caller deletes it); null when none or unreadable.</summary>
    private static ColituRecoverySet? LoadRecoverySet()
    {
        try
        {
            if (!File.Exists(RecoverySetPath())) return null;
            return ColituRecoverySet.Parse(System.Text.Encoding.UTF8.GetString(ColituHardening.UnprotectBytes(File.ReadAllBytes(RecoverySetPath()))));
        }
        catch
        {
            return null;
        }
    }

    private static void SaveRecoveryJson(string json)
    {
        try
        {
            ColituHardening.WritePrivateFile(RecoverySetPath(), ColituHardening.ProtectBytes(System.Text.Encoding.UTF8.GetBytes(json)));
        }
        catch (Exception ex)
        {
            Logging.SaveLog("ColituVpnService.SaveRecoveryJson", ex);
        }
    }

    /// <summary>Sign-out, account deletion, 401/403 on a fetch, or a set past its <c>recovery_until</c>.</summary>
    internal static void DeleteRecoverySet()
    {
        try
        {
            if (File.Exists(RecoverySetPath())) File.Delete(RecoverySetPath());
        }
        catch
        {
            // Best effort.
        }
    }

    /// <summary>
    /// Fetches the set when none is stored or it is 24 h old (at most one attempt per 6 h). Never
    /// blocks start-up or a connect; works with the VPN on or off. 401/403 delete the stored set,
    /// network errors and 5xx keep it.
    /// </summary>
    private async Task RefreshRecoverySetIfDueAsync()
    {
        if (!ColituAdaptiveConnect3.RecoveryFetchAllowed() || Interlocked.Exchange(ref _recoveryRefreshing, 1) == 1)
        {
            return;
        }
        try
        {
            if (!ColituAuthService.Instance.HasSession)
            {
                return;
            }
            var user = ColituAuthService.Instance.CurrentUser?.Id ?? "-";
            var now = DateTimeOffset.UtcNow;
            var lastAttempt = string.Equals(_session.RecoveryAttemptUser, user, StringComparison.Ordinal) ? _session.RecoveryAttemptAt : null;
            if (!ColituRecoverySet.RefreshDue(LoadRecoverySet()?.GeneratedAt, lastAttempt, now))
            {
                return;
            }
            _session = _session with { RecoveryAttemptAt = now, RecoveryAttemptUser = user };
            SaveState();
            try
            {
                var json = await _api.GetRecoveryJsonAsync(_session.ClientCountry);
                if (ColituRecoverySet.Parse(json) is { } fresh)
                {
                    SaveRecoveryJson(json);
                    LogConnection($"Recovery set stored: {fresh.Configs.Count} server(s), until {fresh.RecoveryUntil:O}");
                }
                else
                {
                    LogConnection("Recovery set answer not usable; the stored one is kept");
                }
            }
            catch (ColituApiException ex) when (ColituRecoverySet.DeletesSetOnFetchFailure(ex.StatusCode, ex.ErrorCode))
            {
                DeleteRecoverySet();
                LogConnection($"Recovery set refused ({(int)ex.StatusCode}); the stored one is deleted");
            }
            catch (Exception ex)
            {
                LogConnection($"Recovery set not fetched ({ex.GetType().Name}); the stored one is kept");
            }
        }
        catch (Exception ex)
        {
            Logging.SaveLog("ColituVpnService.RefreshRecoverySetIfDueAsync", ex);
        }
        finally
        {
            Interlocked.Exchange(ref _recoveryRefreshing, 0);
        }
    }

    /// <summary>Connected through the recovery set: the panel is reachable through the tunnel now, refresh everything from it.</summary>
    private async Task RefreshAfterRecoveryAsync()
    {
        await Task.Delay(TimeSpan.FromSeconds(3));
        await RefreshServerListQuietlyAsync();
    }

    /// <summary>
    /// The stored set for a connect that cannot use the panel or the cache: null (give up as before)
    /// when there is none, or it is past <c>recovery_until</c> (then it is deleted).
    /// </summary>
    private ColituRecoverySet? BeginRecovery()
    {
        if (!ColituAdaptiveConnect3.RecoveryFetchAllowed())
        {
            return null;
        }
        var set = LoadRecoverySet();
        if (set == null)
        {
            LogConnection("API unreachable and no usable cache; no recovery set stored");
            return null;
        }
        if (set.IsExpired(DateTimeOffset.UtcNow))
        {
            DeleteRecoverySet();
            LogConnection($"API unreachable and no usable cache; the recovery set is past {set.RecoveryUntil:O} and was deleted");
            return null;
        }
        LogConnection($"API unreachable and no usable cache: recovery set, {set.Configs.Count} servers (until {set.RecoveryUntil:O})");
        return set;
    }

    /// <summary>One envelope of the set as connection settings (pinned addresses, its own offline grace ignored).</summary>
    private async Task<ColituVpnConfigResponse> FetchRecoveryConfigAsync(ColituConfigEnvelopeDto envelope, CancellationToken token)
    {
        var config = await _api.GetRecoveryConfigAsync(envelope, token) ?? throw new ColituConnectException(Loc.I["err.noServers"]);
        foreach (var candidate in config.Candidates)
        {
            // The kill switch lets the known server addresses through, exactly as for a cached config's server.
            if (FmtHandler.ResolveConfig(candidate.ShareLink, out _) is { } item && IPAddress.TryParse(item.Address, out var address))
            {
                ColituNetwork.RememberServerAddress(address);
            }
        }
        return RestrictToVlessIfNeeded(config, null);
    }

    /// <summary>The warm spare behind a recovery server: the next server of the set that did not fail and is not penalized here.</summary>
    private ColituSpareFetch? StartRecoverySpare(ColituRecoverySet recovery, ColituConfigEnvelopeDto current, IReadOnlyCollection<string> failed, CancellationToken token)
    {
        if (!Preferences.WarmSpareEnabled)
        {
            return null;
        }
        var now = DateTimeOffset.UtcNow;
        var skip = failed
            .Append(current.Server?.Id ?? "")
            .Concat(recovery.Configs.Select(item => item.Server?.Id ?? "").Where(id => _adaptive.IsPenalized(_networkKey, id, now)))
            .ToList();
        if (recovery.NextServer(skip) is not { Server.Id.Length: > 0 } next)
        {
            return null;
        }
        return new ColituSpareFetch(next.Server.Id, SpareFromRecoveryAsync(next, next.Server.Id, token));
    }

    private async Task<ColituSpareServer?> SpareFromRecoveryAsync(ColituConfigEnvelopeDto envelope, string serverId, CancellationToken token)
    {
        try
        {
            return ToSpareServer(await _api.GetRecoveryConfigAsync(envelope, token), serverId, cached: true);
        }
        catch (Exception ex) when (!token.IsCancellationRequested)
        {
            LogConnection($"Warm spare settings for recovery server {serverId} not built ({ex.GetType().Name})");
            return null;
        }
    }

    // ── Speed budget of an automatic connect ───────────────────────────────
    /// <summary>One transport: core start plus traffic check.</summary>
    internal static readonly TimeSpan TransportBudget = TimeSpan.FromSeconds(8);
    internal static readonly TimeSpan PerServerBudget = TimeSpan.FromSeconds(20);
    internal static readonly TimeSpan ConnectBudget = TimeSpan.FromSeconds(45);
    /// <summary>No further server is started after this (it would end past <see cref="ConnectBudget"/>).</summary>
    private static readonly TimeSpan NextServerDeadline = ConnectBudget - PerServerBudget;

    /// <summary>Time for the next server: at most 20 s, at least one transport, within the 45 s of the connect.</summary>
    internal static TimeSpan ServerBudget(TimeSpan elapsed)
    {
        var left = ConnectBudget - elapsed;
        return left > PerServerBudget ? PerServerBudget : left < TransportBudget ? TransportBudget : left;
    }

    /// <summary>
    /// A failure of the server on this network (no transport carried traffic, its settings did not
    /// start), not one of the panel, the account, the sudo password, or a device that is offline.
    /// </summary>
    internal static bool IsServerFailure(Exception ex) =>
        ex is not (OperationCanceledException or ColituApiException or ColituPlanRequiredException or ColituDevicePausedException or ColituSpareServerWins or ColituSudoRequiredException)
        && ex is not ColituConnectException { Offline: true };

    /// <summary>
    /// Asks the panel for fresh connection settings. When the panel cannot be
    /// reached (offline, blocked, down) the last settings received for the same
    /// choice are reused until their offline grace period ends.
    /// In <paramref name="automatic"/> mode <paramref name="server"/> is the ranked node, asked for
    /// with <c>node=</c> (the stored preference stays "best server"); without one the panel
    /// chooses and skips the servers in <paramref name="exclude"/>.
    /// </summary>
    private async Task<ColituVpnConfigResponse> FetchConfigAsync(ColituVpnServer? server, CancellationToken token, bool keepPreference = false,
        bool automatic = false, IReadOnlyCollection<string>? exclude = null)
    {
        var cacheKey = server?.Id ?? "auto";
        var watch = Stopwatch.StartNew();
        // The previous tunnel may just have stopped: never reuse a connection opened over its route.
        ResetDirectConnections();
        try
        {
            // A route is not a node: it is never stored as this device's preferred node.
            if (!keepPreference && server is not { IsMultihop: true })
            {
                await _api.SetPreferredServerAsync(automatic ? null : server?.Id, token);
            }
            if (Rotation == null && server is not { IsMultihop: true })
            {
                // Whether the exit rotates decides the transports; do not connect blind if it is quickly known.
                using var rotationBudget = CancellationTokenSource.CreateLinkedTokenSource(token);
                rotationBudget.CancelAfter(TimeSpan.FromSeconds(3));
                try
                {
                    await LoadRotationAsync(rotationBudget.Token);
                }
                catch (Exception ex) when (!token.IsCancellationRequested)
                {
                    LogConnection($"Rotation preference not loaded: {ex.GetType().Name}");
                }
            }
            var preferenceMs = watch.ElapsedMilliseconds;
            var config = await _api.GetConfigAsync(server, token,
                    node: automatic ? server?.Id : null,
                    exclude: automatic && server == null ? exclude : null)
                ?? throw new ColituConnectException(Loc.I["err.noServers"]);
            LogConnection($"Panel answered in {watch.ElapsedMilliseconds} ms (preference {preferenceMs} ms, config {watch.ElapsedMilliseconds - preferenceMs} ms)");
            ColituAuthService.Instance.SetDevicePause(null);
            _apiUnreachable = false;
            SaveCachedConfig(cacheKey, config);
            if (automatic && server != null)
            {
                // "Best server" with the panel unreachable later falls back to the last one that answered.
                SaveCachedConfig("auto", config);
            }
            return RestrictToVlessIfNeeded(config, server);
        }
        catch (ColituApiException ex) when (ex.ErrorCode is "MULTIHOP_ROUTE_NOT_FOUND")
        {
            // The route was removed or renamed: learn the current list for the next try.
            LogConnection("Multihop route no longer offered by the panel");
            _ = RefreshServerListQuietlyAsync();
            throw new ColituConnectException(Loc.I["multihop.gone"], ex);
        }
        catch (Exception ex) when (!token.IsCancellationRequested && IsNetworkFailure(ex) && CachedFallback(cacheKey, automatic, exclude) is { } cached)
        {
            _apiUnreachable = ColituRecoverySet.IsApiUnreachable(ex);
            LogConnection($"Panel unreachable after {watch.ElapsedMilliseconds} ms ({ex.GetType().Name}); using cached connection settings for {cached.ServerId ?? cacheKey}");
            return RestrictToVlessIfNeeded(cached, server);
        }
        catch (Exception ex) when (!token.IsCancellationRequested && ColituRecoverySet.IsApiUnreachable(ex))
        {
            // Nothing cached to fall back on: the recovery set may take over (see ConnectCoreAsync).
            _apiUnreachable = true;
            throw;
        }
    }

    /// <summary>The cached settings for this choice; in automatic mode also the last "best server" ones, unless that server failed during this connect.</summary>
    private ColituVpnConfigResponse? CachedFallback(string cacheKey, bool automatic, IReadOnlyCollection<string>? exclude)
    {
        if (LoadCachedConfig(cacheKey) is { } cached)
        {
            return cached;
        }
        return automatic && cacheKey != "auto" && LoadCachedConfig("auto") is { } auto
            && (exclude == null || auto.ServerId == null || !exclude.Contains(auto.ServerId, StringComparer.OrdinalIgnoreCase))
            ? auto
            : null;
    }

    /// <summary>
    /// A multihop route and a rotating exit run on VLESS only: the other transports (Hysteria2, Trojan,
    /// Shadowsocks) are dropped from the candidates, and a node without VLESS cannot be used for them.
    /// </summary>
    private ColituVpnConfigResponse RestrictToVlessIfNeeded(ColituVpnConfigResponse config, ColituVpnServer? server)
    {
        if (server is not { IsMultihop: true } && !RotationActive)
        {
            return config;
        }
        var restricted = ColituApiClient.RestrictToVless(config);
        if (restricted == null)
        {
            throw new ColituConnectException(Loc.I[server is { IsMultihop: true } ? "multihop.needsVless" : "rotation.needsVless"]);
        }
        if (restricted != config)
        {
            LogConnection($"VLESS only ({(server is { IsMultihop: true } ? "multihop route" : "rotating IP")}): {config.Candidates.Count - restricted.Candidates.Count} other transport(s) skipped");
        }
        return restricted;
    }

    private async Task RefreshServerListQuietlyAsync()
    {
        try
        {
            await GetServersAsync();
        }
        catch (Exception ex)
        {
            LogConnection($"Server list not refreshed: {ex.GetType().Name}");
        }
    }

    /// <summary>
    /// Keep-alive connections to the panel and the DNS resolvers die when the tunnel goes up or
    /// down (their route changes underneath them); reusing one hangs until its timeout.
    /// </summary>
    private static void ResetDirectConnections()
    {
        ColituAuthService.Instance.ResetConnections();
        ColituNetwork.ResetConnections();
    }

    public async Task DisconnectAsync()
    {
        _userDisconnected = true;
        _connectCts?.Cancel();
        await _connectionLock.WaitAsync();
        try
        {
            await DisconnectCoreAsync();
        }
        finally
        {
            _connectionLock.Release();
        }
    }

    private async Task DisconnectCoreAsync()
    {
        await EnsureCoreReadyAsync();
        await StopCoreAsync();
        var proxyResult = await SysProxyHandler.UpdateSysProxy(_config, true);
        LogConnection($"Disconnect proxy restore result={proxyResult}");
        await RestoreProxyPreferenceAsync();
        await RestoreRoutingPreferenceAsync();
        ConnectedAt = null;
        ConnectedServer = null;
        await ReleaseKillSwitchAsync("disconnected by the user");
        SetStatus(ColituVpnStatus.Disconnected);
    }

    /// <summary>Moves an active connection to <paramref name="server"/> (null = best server).</summary>
    public async Task SwitchServerAsync(ColituVpnServer? server)
    {
        _connectCts?.Cancel();
        await _connectionLock.WaitAsync();
        try
        {
            SetSelection(server);
            SetStatus(ColituVpnStatus.Reconnecting);
            await StopCoreAsync();
            await ConnectCoreAsync(server);
        }
        finally
        {
            _connectionLock.Release();
        }
    }

    public async Task ReconnectAsync()
    {
        _connectCts?.Cancel();
        await _connectionLock.WaitAsync();
        try
        {
            SetStatus(ColituVpnStatus.Reconnecting);
            await StopCoreAsync();
            await ConnectCoreAsync(IsAutoSelection ? null : SelectedServer ?? FindServer(_session.SelectedServerId));
        }
        finally
        {
            _connectionLock.Release();
        }
    }

    public async Task<bool> TryAutoConnectAsync()
    {
        if (!Preferences.AutoConnectEnabled || GetStatus() is ColituVpnStatus.Connected or ColituVpnStatus.Connecting or ColituVpnStatus.Reconnecting
            || ColituAuthService.Instance.DevicePause != null)
        {
            return false;
        }

        var waited = Stopwatch.StartNew();
        while (!NetworkInterface.GetIsNetworkAvailable() || !ColituNetwork.HasDefaultRoute())
        {
            if (waited.Elapsed > TimeSpan.FromSeconds(45) || _userDisconnected)
            {
                LogConnection("Auto-connect skipped: no network");
                return false;
            }
            await Task.Delay(1000);
        }

        for (var round = 1; round <= 2; round++)
        {
            try
            {
                await ConnectSavedAsync();
                return Status == ColituVpnStatus.Connected;
            }
            catch (Exception ex)
            {
                LogConnection($"Auto-connect attempt {round} failed: {ex.Message}");
                if (_userDisconnected || round == 2 || ex is ColituDevicePausedException or ColituPlanRequiredException)
                {
                    return false;
                }
                await Task.Delay(TimeSpan.FromSeconds(8));
                if (_userDisconnected || Status is ColituVpnStatus.Connected or ColituVpnStatus.Connecting)
                {
                    return false;
                }
            }
        }
        return false;
    }

    public ColituVpnStatus GetStatus() => Status;

    public TimeSpan GetConnectionDuration()
    {
        return Status == ColituVpnStatus.Connected && ConnectedAt != null
            ? DateTimeOffset.Now - ConnectedAt.Value
            : TimeSpan.Zero;
    }

    public ColituVpnServer? GetSelectedServer() => SelectedServer;

    public string? GetLastError() => LastError;

    public void SelectServer(ColituVpnServer server) => SetSelection(server);

    public void SelectAuto() => SetSelection(null);

    private void SetSelection(ColituVpnServer? server)
    {
        SelectedServer = server;
        _session = _session with
        {
            SelectedServerId = server?.Id,
            SelectionMode = server == null ? ColituServerSelectionModes.Best : ColituServerSelectionModes.Manual
        };
        SaveState();
    }

    public async Task UpdatePreferencesAsync(ColituVpnPreferences preferences)
    {
        _session = _session with { Preferences = preferences.Normalize() };
        SaveState();

        if (!_session.Preferences.KillSwitchEnabled)
        {
            await ReleaseKillSwitchAsync("kill switch turned off");
        }
        else if (Status == ColituVpnStatus.Connected)
        {
            await ApplyKillSwitchForConnectedTunnelAsync();
        }

        if (_coreReady && Status is not (ColituVpnStatus.Connected or ColituVpnStatus.Connecting or ColituVpnStatus.Reconnecting))
        {
            await ApplyRuntimePreferencesAsync(_session.Preferences);
        }
    }

    /// <summary>Starts or stops launching Colitu when the user signs in (XDG autostart entry).</summary>
    public async Task SetLaunchAtStartupAsync(bool enabled)
    {
        _config.GuiItem.AutoRun = enabled;
        await ConfigHandler.SaveConfig(_config);
        await AutoStartupHandler.UpdateTask(_config);
    }

    public bool LaunchAtStartup => _config.GuiItem.AutoRun;

    /// <summary>
    /// A crash or forced shutdown while connected leaves the desktop pointing at the
    /// dead local proxy, which cuts the internet. Undo that before anything else.
    /// </summary>
    public async Task RecoverFromPreviousRunAsync()
    {
        try
        {
            // Kill switch rules outlive a crash on purpose (fail closed). Take them over:
            // they keep blocking until the user reconnects (which replaces them) or turns
            // protection off (which needs the sudo password to remove them).
            if (ColituKillSwitch.HasLeftoverRules())
            {
                _killSwitch.Adopt();
                KillSwitchRecoveryPending = true;
                LogConnection(KillSwitchApplies
                    ? "Kill switch rules of the previous run are still in place (unclean exit); keeping them until the user reconnects or turns protection off"
                    : "Kill switch rules of the previous run are still in place, but the kill switch is now off; they are removed once the sudo password is entered");
            }

            await EnsureCoreReadyAsync();
            var wasActive = _statusAtLastExit is ColituVpnStatus.Connected or ColituVpnStatus.Connecting or ColituVpnStatus.Reconnecting;
            if (!wasActive && _config.SystemProxyItem.SysProxyType != ESysProxyType.ForcedChange)
            {
                return;
            }

            LogConnection($"Recovering after unclean exit (saved status={_session.Status}, proxy={_config.SystemProxyItem.SysProxyType})");
            KillOrphanCoreProcesses();
            await SysProxyHandler.UpdateSysProxy(_config, true);
            if (_config.SystemProxyItem.SysProxyType == ESysProxyType.ForcedChange)
            {
                _config.SystemProxyItem.SysProxyType = ESysProxyType.ForcedClear;
                await ConfigHandler.SaveConfig(_config);
            }
            await RestoreRoutingPreferenceAsync();
            ConnectedAt = null;
            SetStatus(ColituVpnStatus.Disconnected);
        }
        catch (Exception ex)
        {
            Logging.SaveLog("ColituVpnService.RecoverFromPreviousRunAsync", ex);
        }
    }

    /// <summary>
    /// True from a start that found the kill switch rules of a crashed run until the user
    /// reconnects or turns protection off. The internet is blocked meanwhile.
    /// </summary>
    public bool KillSwitchRecoveryPending { get; private set; }

    /// <summary>
    /// The crashed run's VPN cores may still run as root (TUN mode starts them through sudo)
    /// and hold the TUN routes. Stops them; needs the sudo password. Best effort.
    /// </summary>
    public async Task StopOrphanRootCoresAsync()
    {
        var password = AppManager.Instance.LinuxSudoPwd;
        if (password.IsNullOrEmpty() || !OperatingSystem.IsLinux())
        {
            return;
        }
        try
        {
            var (code, error) = await ColituKillSwitch.RunAsRootAsync(ColituKillSwitch.BuildStopOrphanCoresScript(Utils.GetBinPath(""), Utils.GetBaseDirectory("bin")), password);
            LogConnection(code == 0 ? "Stopped VPN cores left running as root by the previous run" : $"Stopping orphan root cores failed ({code}): {error.Trim()}");
        }
        catch (Exception ex)
        {
            Logging.SaveLog("ColituVpnService.StopOrphanRootCoresAsync", ex);
        }
    }

    /// <summary>
    /// "Turn off protection" after a crash: removes the rules the crashed run left (and its
    /// root cores). Needs the sudo password; false when the rules are still in place.
    /// </summary>
    public async Task<bool> RemoveLeftoverKillSwitchAsync()
    {
        if (Status is not (ColituVpnStatus.Connected or ColituVpnStatus.Connecting or ColituVpnStatus.Reconnecting))
        {
            await StopOrphanRootCoresAsync();
        }
        await _connectionLock.WaitAsync();
        try
        {
            _userDisconnected = true;
            var released = await ReleaseKillSwitchAsync("turned off after an unclean exit");
            SetStatus(ColituVpnStatus.Disconnected);
            return released;
        }
        finally
        {
            _connectionLock.Release();
        }
    }

    /// <summary>
    /// The session is ending (SIGTERM/SIGHUP on logout or shutdown): stop the core and
    /// hand the system proxy back, otherwise the next login starts with the desktop
    /// pointing at a dead local proxy until Colitu runs again. Blocks for at most a few seconds.
    /// </summary>
    public void CleanupForSessionEnd()
    {
        try
        {
            // Without this the watchdog sees the stopped core as a dropped tunnel and starts
            // reconnecting (proxy, TUN) while the session is ending.
            _userDisconnected = true;
            _watchdog?.Dispose();
            _watchdog = null;
            _connectCts?.Cancel();
            SetStatus(ColituVpnStatus.Disconnected);
            Task.Run(async () =>
            {
                await StopCoreAsync();
                await SysProxyHandler.UpdateSysProxy(_config, true);
                await RestoreProxyPreferenceAsync();
            }).Wait(TimeSpan.FromSeconds(5));
            _killSwitch.Release();
            LogConnection("Session ending: core stopped and system proxy restored");
        }
        catch (Exception ex)
        {
            Logging.SaveLog("ColituVpnService.CleanupForSessionEnd", ex);
        }
    }

    /// <summary>Stops the tunnel on sign-out and forgets cached connection settings.</summary>
    public async Task ForgetAccountAsync()
    {
        if (Status is ColituVpnStatus.Connected or ColituVpnStatus.Connecting or ColituVpnStatus.Reconnecting)
        {
            await DisconnectAsync();
        }
        try
        {
            if (File.Exists(ConfigCachePath())) File.Delete(ConfigCachePath());
        }
        catch
        {
            // Best effort.
        }
        DeleteRecoverySet();
        _session = _session with { RecoveryAttemptAt = null, RecoveryAttemptUser = null };
        try
        {
            // The imported profiles hold this account's server credentials in plain text
            // (v2rayN database); the encrypted cache above is pointless if they stay.
            await ConfigHandler.RemoveServersViaSubid(_config, ColituSubId, false);
        }
        catch (Exception ex)
        {
            Logging.SaveLog("ColituVpnService.ForgetAccount profiles", ex);
        }
        AppManager.Instance.LinuxSudoPwd = "";
        _lastServers = [];
        _multihopRoutes = [];
        _pings.Clear();
        Rotation = null;
        SelectedServer = null;
        ConnectedServer = null;
    }

    // ── Health watch: restart a dropped tunnel ─────────────────────────────
    private void StartWatchdog()
    {
        // Suspend and resume show up as a stalled tunnel; the traffic probe below catches them.
        // Every second: cheap (are the cores running?); the traffic checks keep their own pace.
        _watchdog = new Timer(_ => _ = WatchdogTickAsync(), null, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1));
        NetworkChange.NetworkAddressChanged += (_, _) =>
        {
            // Marks from now on belong to the network the computer is on now.
            _linkKind = null;
            if (Status == ColituVpnStatus.Connected)
            {
                _networkKey = CurrentNetworkKey();
                _ = VerifyAfterResumeAsync();
            }
        };
    }

    private async Task WatchdogTickAsync()
    {
        // The timer fires every second while a probe can take up to 14 s: one tick at a time,
        // or two overlapping ticks would both start an automatic reconnect.
        if (Interlocked.Exchange(ref _watchdogBusy, 1) == 1)
        {
            return;
        }
        try
        {
            await WatchdogCheckAsync();
        }
        catch (Exception ex)
        {
            Logging.SaveLog("ColituVpnService.Watchdog", ex);
        }
        finally
        {
            Interlocked.Exchange(ref _watchdogBusy, 0);
        }
    }

    private async Task WatchdogCheckAsync()
    {
        if (Status != ColituVpnStatus.Connected || _connectionLock.CurrentCount == 0 || _autoReconnecting)
        {
            return;
        }
        if (!CoreManager.Instance.IsCoreRunning)
        {
            LogConnection("VPN core stopped unexpectedly; reconnecting");
            await OnTunnelLostAsync();
            return;
        }
        // In TUN mode with Xray the adapter belongs to a separate root sing-box. If only that
        // one dies, the local SOCKS probe below still passes while apps bypass the tunnel.
        if (CoreManager.Instance.PreCoreExited)
        {
            LogConnection("TUN core stopped unexpectedly; reconnecting");
            await OnTunnelLostAsync();
            return;
        }

        await WatchTrafficAsync(Environment.TickCount64);
    }

    private bool _offlineLogged;

    private bool CanWatch() => Status == ColituVpnStatus.Connected && _connectionLock.CurrentCount > 0 && !_autoReconnecting && !_userDisconnected;

    private Task OnWatchHealthyAsync(long now)
    {
        _probeFailures = 0;
        _offlineLogged = false;
        return Task.CompletedTask;
    }

    private Task OnWatchOfflineAsync()
    {
        if (!_offlineLogged)
        {
            _offlineLogged = true;
            LogConnection("No internet outside the tunnel either; keeping the tunnel and waiting for the connection to return");
        }
        return Task.CompletedTask;
    }

    /// <summary>3 misses, no spare: Hysteria2 switches transport; a TCP transport is marked (10 minutes) and the tunnel reconnects.</summary>
    private async Task OnWatchPrimaryDeadAsync(string detail)
    {
        if (_activeTransport == "hysteria2")
        {
            await OnHysteriaStalledAsync(detail);
            return;
        }
        if (_activeTransport != null)
        {
            // The device is online (the watcher checked): 10 minutes, 6 h once another transport carries traffic here.
            var shortMark = _adaptive.MarkMidSessionStall(_networkKey, ConnectedServer?.Id, _activeTransport, DateTimeOffset.UtcNow, provisional: true);
            SaveState();
            LogConnection(shortMark
                ? $"Transport {_activeTransport} stalled; proven here: short penalty, it goes last on this server and network for 90 s"
                : $"Transport {_activeTransport} stalled; it goes last on this server and network for 10 minutes (6 h once another transport carries traffic here)");
        }
        LogConnection($"Tunnel carries no traffic ({detail}); reconnecting");
        await OnTunnelLostAsync();
    }

    private async Task OnWatchReconnectAsync(string detail)
    {
        LogConnection(_watch.SpareDeadUnreplaced
            ? $"The primary misses behind a dead spare ({detail}); reconnecting"
            : $"Both paths carry no traffic ({detail}); reconnecting");
        await OnTunnelLostAsync();
    }

    /// <summary>The spare server's addresses were remembered with its settings: re-apply the kill switch rules.</summary>
    private async Task AllowSpareServerAsync(ColituSpareServer spare)
    {
        if (_killSwitch.IsEngaged && KillSwitchApplies)
        {
            await EngageKillSwitchAsync($"allow the new spare server {spare.ServerId}");
        }
    }

    /// <summary>Reloads the core with the same primary and the new spare; true when traffic flows again.</summary>
    private async Task<bool> ReloadCoreForSpareAsync(string reason)
    {
        if (!await _connectionLock.WaitAsync(0))
        {
            return true;
        }
        _autoReconnecting = true;
        try
        {
            if (Status != ColituVpnStatus.Connected || _userDisconnected)
            {
                return true;
            }
            LogConnection($"Reloading the VPN core ({reason})");
            _lastCoreMessage = null;
            await StopCoreAsync();
            ResetDirectConnections();
            await ReloadCoreAsync();
            var probe = await VerifyConnectionActiveAsync(ConnectedServer, 2);
            return probe.Success;
        }
        catch (Exception ex)
        {
            LogConnection($"VPN core reload failed: {ex.Message}");
            return false;
        }
        finally
        {
            _autoReconnecting = false;
            _connectionLock.Release();
        }
    }


    // ── Adaptive Connect 2.0: mid-session watcher, spare probe, spare swap ──
    private static readonly TimeSpan WatchProbeTimeout = TimeSpan.FromSeconds(4);

    /// <summary>
    /// Every 5 s for the first 90 s after the connect, then every 30 s (every transport). With a spare
    /// the primary alone (colitu-verify) and the normal path are checked at once; see <see cref="ColituTunnelWatch"/>.
    /// Between the rounds the spare's own health is probed and a replacement swapped in when quiet.
    /// </summary>
    private async Task WatchTrafficAsync(long now)
    {
        SampleTraffic(now);
        var sinceConnect = ConnectedAt is { } at ? DateTimeOffset.Now - at : TimeSpan.Zero;
        if (now - _lastTrafficCheckAt < (long)ColituTunnelWatch.Interval(sinceConnect).TotalMilliseconds)
        {
            await WatchSpareAsync(now);
            return;
        }
        _lastTrafficCheckAt = now;
        var (port, credentials) = TunnelCheckTarget();
        var verify = _verifyInbound;
        var normalTask = ProbeThroughLocalProxyAsync(port, 1, roundTimeout: WatchProbeTimeout, credentials: credentials);
        var primaryTask = verify == null ? null
            : ProbeThroughLocalProxyAsync(verify.Port, 1, roundTimeout: WatchProbeTimeout, credentials: new NetworkCredential(verify.User, verify.Password));
        var normal = await normalTask;
        var primary = primaryTask == null ? null : await primaryTask;
        if (!CanWatch())
        {
            return;
        }
        var miss = !normal.Success || primary is { Success: false };
        var online = !miss || await InternetReachableDirectAsync();
        if (!CanWatch())
        {
            return;
        }
        var action = _watch.Round(primary?.Success, normal.Success, online);
        var detail = primary is { Success: false } ? primary.Detail : normal.Detail;
        if (miss && action == ColituWatchAction.None)
        {
            LogConnection($"Tunnel check missed (primary {(primary == null ? "-" : primary.Success ? "ok" : "miss")} {_watch.PrimaryMisses}/{ColituTunnelWatch.MissesForDead}, normal {(normal.Success ? "ok" : "miss")} {_watch.NormalMisses}/{ColituTunnelWatch.MissesForDead}): {detail}");
        }
        switch (action)
        {
            case ColituWatchAction.None:
                if (normal.Success)
                {
                    await OnWatchHealthyAsync(now);
                }
                return;
            case ColituWatchAction.Offline:
                await OnWatchOfflineAsync();
                return;
            case ColituWatchAction.SpareCarries:
                OnSpareCarries();
                return;
            case ColituWatchAction.PrimaryDead:
                await OnWatchPrimaryDeadAsync(detail);
                return;
            case ColituWatchAction.Reconnect:
                await OnWatchReconnectAsync(detail);
                return;
        }
    }

    /// <summary>
    /// The primary is dead and the warm spare carries the traffic: no reconnect. The primary is marked
    /// for the next connect; the spare's server and transport become the remembered good ones (they lead it).
    /// </summary>
    private void OnSpareCarries()
    {
        var plan = _sparePlan;
        var serverId = ConnectedServer?.Id;
        var now = DateTimeOffset.UtcNow;
        if (plan == null)
        {
            return;
        }
        var shortMark = _adaptive.MarkMidSessionStall(_networkKey, serverId, _activeTransport, now, provisional: true);
        LogConnection($"{serverId}/{_activeTransport} is dead, the warm spare {plan.ServerId}/{plan.Protocol} carries the traffic; it leads the next connect{(shortMark ? " (proven here: short penalty)" : "")}");
        if (plan.ServerId is { Length: > 0 } spareServer)
        {
            _adaptive.RememberGoodServer(_networkKey, spareServer, now);
            _adaptive.RememberGoodTransport(_networkKey, spareServer, plan.Protocol, now);
        }
        SaveState();
    }

    /// <summary>
    /// Spare health probe while the primary is healthy (60 s for a UDP spare, 180 s for a TCP one);
    /// 2 misses while online: a replacement by the spare rules, swapped in when the tunnel is quiet.
    /// </summary>
    private async Task WatchSpareAsync(long now)
    {
        if (_pendingSpare != null)
        {
            await TrySwapSpareAsync(now);
            return;
        }
        var plan = _sparePlan;
        var spareVerify = _spareVerifyInbound;
        if (plan == null || spareVerify == null || _watch.PrimaryMisses > 0 || _watch.PrimaryDeclaredDead || _watch.SpareDeadUnreplaced
            || now - _lastSpareProbeAt < (long)ColituSpareHealth.Interval(plan.Protocol).TotalMilliseconds)
        {
            return;
        }
        _lastSpareProbeAt = now;
        var probe = await ProbeThroughLocalProxyAsync(spareVerify.Port, 1, roundTimeout: TimeSpan.FromSeconds(5), credentials: new NetworkCredential(spareVerify.User, spareVerify.Password));
        if (!CanWatch() || !ReferenceEquals(plan, _sparePlan))
        {
            return;
        }
        var online = probe.Success || await InternetReachableDirectAsync();
        LogConnection(probe.Success ? $"spare probe ok: {Describe(plan)} in {probe.LatencyMs} ms" : $"spare probe miss: {Describe(plan)} ({probe.Detail})");
        if (!_spareHealth.Probe(probe.Success, online))
        {
            return;
        }
        var replacement = await FindReplacementSpareAsync(plan);
        if (replacement == null)
        {
            _watch.SpareDeadUnreplaced = true;
            LogConnection($"spare replaced: nothing can replace {Describe(plan)} (dead); keeping it, 2 primary misses reconnect");
            return;
        }
        _pendingSpare = replacement;
        ResetSpareSwapDeferral();
        await TrySwapSpareAsync(now);
    }

    /// <summary>A dead spare's replacement: another ranked server first (not the dead spare's), then the primary's own server.</summary>
    private async Task<ColituSparePlan?> FindReplacementSpareAsync(ColituSparePlan dead)
    {
        var primary = await ConfigHandler.GetDefaultServer(_config);
        var primaryProtocol = _activeTransport;
        var serverId = ConnectedServer?.Id;
        if (primary == null || primaryProtocol == null)
        {
            return null;
        }
        var excluded = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { SpareKey(dead.ServerId, dead.Protocol) };
        var config = new ColituVpnConfigResponse { ServerId = serverId, Server = ConnectedServer };
        if (IsAutoSelection)
        {
            var now = DateTimeOffset.UtcNow;
            foreach (var candidate in RankedServers().Where(item => item.Id is { Length: > 0 } id
                && !string.Equals(id, serverId, StringComparison.OrdinalIgnoreCase)
                && !string.Equals(id, dead.NextServerId, StringComparison.OrdinalIgnoreCase)
                && !_adaptive.IsPenalized(_networkKey, id, now)).Take(2))
            {
                if (await FetchSpareServerAsync(candidate, CancellationToken.None) is not { } fetched)
                {
                    continue;
                }
                _spareServer = fetched;
                if (PlanSpare(primaryProtocol, primary, _currentProfiles, config, ConnectedServer, excluded) is { NextServerId: not null } plan)
                {
                    return plan;
                }
            }
        }
        _spareServer = null;
        return PlanSpare(primaryProtocol, primary, _currentProfiles, config, ConnectedServer, excluded);
    }

    private void ResetSpareSwapDeferral()
    {
        _spareSwapDeferredSince = 0;
        _spareSwapDeferKey = null;
    }

    /// <summary>
    /// Swaps the pending spare in by reloading the core: at once when the tunnel carried under 10 KB in
    /// the last 10 s (or the traffic is unknown), after 2 minutes of deferral also under 32 KB
    /// (<see cref="ColituSpareHealth.SwapDeferral"/>). The log says why only when the reason changes.
    /// </summary>
    private async Task TrySwapSpareAsync(long now)
    {
        var next = _pendingSpare;
        if (next == null)
        {
            return;
        }
        var bytes = TrafficInWindow();
        var deferredFor = _spareSwapDeferredSince == 0 ? TimeSpan.Zero : TimeSpan.FromMilliseconds(now - _spareSwapDeferredSince);
        var decision = ColituSpareHealth.SwapDeferral(bytes < 0 ? null : bytes, deferredFor);
        if (!decision.Now)
        {
            if (_spareSwapDeferredSince == 0)
            {
                _spareSwapDeferredSince = now;
            }
            if (_spareSwapDeferKey != decision.Key)
            {
                _spareSwapDeferKey = decision.Key;
                LogConnection($"spare swap deferred: {decision.Reason}");
            }
            return;
        }
        if (decision.Key == "light")
        {
            LogConnection($"spare swap: {decision.Reason}");
        }
        ResetSpareSwapDeferral();
        var old = _sparePlan;
        _pendingSpare = null;
        if (next.NextServerId != null && _spareServer is { } spareServer)
        {
            await AllowSpareServerAsync(spareServer);
        }
        _sparePlan = next;
        _spareHealth.Reset();
        LogConnection($"spare replaced: {(old == null ? "none" : Describe(old))} → {Describe(next)} ({next.Reason}; the old one missed {ColituSpareHealth.MissesForDead} probes)");
        if (!await ReloadCoreForSpareAsync("spare replaced") && !_userDisconnected)
        {
            await OnTunnelLostAsync();
        }
    }

    private void SampleTraffic(long now)
    {
        var bytes = ColituNetwork.PhysicalBytes();
        if (bytes < 0)
        {
            return;
        }
        _trafficSamples.Enqueue((now, bytes));
        while (_trafficSamples.Count > 0 && now - _trafficSamples.Peek().Tick > 15_000)
        {
            _trafficSamples.Dequeue();
        }
    }

    /// <summary>Bytes over the physical adapter in the last 10 s; -1 when unknown (counts as quiet).</summary>
    private long TrafficInWindow()
    {
        var samples = _trafficSamples.ToArray();
        if (samples.Length < 2)
        {
            return -1;
        }
        var latest = samples[^1];
        var start = samples.LastOrDefault(sample => latest.Tick - sample.Tick >= (long)ColituSpareHealth.QuietWindow.TotalMilliseconds, samples[0]);
        return Math.Max(0, latest.Bytes - start.Bytes);
    }

    private const long TrafficCheckIntervalMs = 10_000;
    private const int HysteriaStallChecks = ColituTunnelWatch.MissesForDead;
    private static readonly TimeSpan TransportSwitchMinInterval = TimeSpan.FromSeconds(60);

    /// <summary>
    /// Hysteria2 stopped carrying traffic in the middle of a session (Russian mobile networks
    /// throttle long-lived UDP flows). With the internet still there, the transport is demoted on
    /// this server and network for 6 hours and the same server is reconnected with the next transport,
    /// silently (one log line, no notice), at most once a minute. Without internet the usual
    /// reconnect runs (no demotion: the transport is not to blame).
    /// </summary>
    private async Task OnHysteriaStalledAsync(string detail)
    {
        if (!await InternetReachableDirectAsync())
        {
            LogConnection($"Tunnel carries no traffic ({detail}) and nothing answers outside it either; reconnecting");
            await OnTunnelLostAsync();
            return;
        }
        if (Status != ColituVpnStatus.Connected || _autoReconnecting || _userDisconnected
            || !ShouldSwitchTransport(_lastTransportSwitch, DateTimeOffset.UtcNow))
        {
            // Switched less than a minute ago: the verdict stays (one miss short), so the switch
            // happens as soon as the gap ends instead of after three more misses.
            _watch.Defer(ColituWatchAction.PrimaryDead);
            return;
        }
        _lastTransportSwitch = DateTimeOffset.UtcNow;
        var server = ConnectedServer;
        var shortMark = _adaptive.MarkMidSessionStall(_networkKey, server?.Id, "hysteria2", DateTimeOffset.UtcNow, provisional: false);
        SaveState();
        LogConnection($"Hysteria2 stalled mid-session ({HysteriaStallChecks} checks, last: {detail}); demoted on this server and network for {(shortMark ? "90 s (proven here: short penalty)" : "6 hours")}, switching to the next transport");
        await SwitchTransportAsync(server);
    }

    /// <summary>At most one automatic transport switch per <see cref="TransportSwitchMinInterval"/>.</summary>
    internal static bool ShouldSwitchTransport(DateTimeOffset? lastSwitch, DateTimeOffset now) =>
        lastSwitch == null || now - lastSwitch.Value >= TransportSwitchMinInterval;

    /// <summary>A Russian and two foreign addresses that accept TCP on 443 (the kill switch lets the last two through).</summary>
    private static readonly IPAddress[] DirectReferences = [IPAddress.Parse("77.88.8.8"), IPAddress.Parse("1.1.1.1"), IPAddress.Parse("8.8.8.8")];

    /// <summary>
    /// Whether the internet answers outside the tunnel (TCP 443, bound to the physical interface).
    /// True when that can't be checked (no physical interface found).
    /// </summary>
    private static async Task<bool> InternetReachableDirectAsync()
    {
        if (!ColituNetwork.HasDefaultRoute())
        {
            return false;
        }
        if (ColituNetwork.PhysicalInterfaceName() is not { } device)
        {
            return true;
        }
        var checks = DirectReferences.Select(address => ColituLatency.ConnectMsAsync(address, 443, device)).ToList();
        while (checks.Count > 0)
        {
            var done = await Task.WhenAny(checks);
            if (await done != null)
            {
                return true;
            }
            checks.Remove(done);
        }
        return false;
    }

    /// <summary>
    /// Reconnects to <paramref name="server"/> (the node the tunnel ran through) without a notice;
    /// the stall mark puts the demoted transport last. When that fails, the usual automatic
    /// reconnect takes over (with its notices: the tunnel is down then).
    /// </summary>
    private async Task SwitchTransportAsync(ColituVpnServer? server)
    {
        // "Best server" ranks again: the server that just worked on this network comes first
        // (unless it is penalized), and a failure moves on to the next one.
        var target = IsAutoSelection ? null
            : server is { IsMultihop: false, Id.Length: > 0 } ? server
            : SelectedServer ?? FindServer(_session.SelectedServerId);
        var failed = false;
        _autoReconnecting = true;
        try
        {
            if (KillSwitchApplies && !_killSwitch.IsEngaged)
            {
                // Before the core goes down: nothing may leave outside the tunnel meanwhile.
                await EngageKillSwitchAsync("switching transport");
            }
            await _connectionLock.WaitAsync();
            try
            {
                if (_userDisconnected)
                {
                    return;
                }
                SetStatus(ColituVpnStatus.Reconnecting);
                await StopCoreAsync();
                await ConnectCoreAsync(target, keepPreference: true);
                failed = Status != ColituVpnStatus.Connected;
            }
            finally
            {
                _connectionLock.Release();
            }
        }
        catch (Exception ex)
        {
            failed = true;
            LogConnection($"Transport switch did not connect: {ex.Message}");
        }
        finally
        {
            _autoReconnecting = false;
        }
        if (failed && !_userDisconnected)
        {
            await OnTunnelLostAsync();
        }
    }

    private async Task OnTunnelLostAsync()
    {
        if (KillSwitchApplies && await EngageKillSwitchAsync("tunnel lost"))
        {
            Notice?.Invoke("info.killSwitch");
        }
        await AutoReconnectAsync();
    }

    private async Task VerifyAfterResumeAsync()
    {
        await Task.Delay(TimeSpan.FromSeconds(4));
        if (Status != ColituVpnStatus.Connected || _autoReconnecting)
        {
            return;
        }
        var (checkPort, checkCredentials) = TunnelCheckTarget();
        var probe = await ProbeThroughLocalProxyAsync(checkPort, credentials: checkCredentials);
        if (!probe.Success)
        {
            LogConnection($"Tunnel check after resume failed: {probe.Detail}");
            await OnTunnelLostAsync();
        }
    }

    private async Task AutoReconnectAsync()
    {
        _autoReconnecting = true;
        try
        {
            for (var attempt = 1; attempt <= 3 && !_userDisconnected; attempt++)
            {
                try
                {
                    await ReconnectAsync();
                    Notice?.Invoke("info.reconnected");
                    return;
                }
                catch (ColituPlanRequiredException)
                {
                    Notice?.Invoke("err.noPlan");
                    return;
                }
                catch (ColituDevicePausedException)
                {
                    // The paused screen takes over; no background retries against the limit.
                    return;
                }
                catch (Exception ex)
                {
                    LogConnection($"Automatic reconnect attempt {attempt} failed: {ex.Message}");
                    await Task.Delay(TimeSpan.FromSeconds(attempt * 3));
                }
            }

            if (_userDisconnected)
            {
                return;
            }

            LastError = Loc.I["err.reconnectFailed"];
            SetStatus(ColituVpnStatus.Error);
            Notice?.Invoke("err.reconnectFailed");

            // With the kill switch holding the connection closed, keep retrying in
            // the background so the internet returns as soon as the VPN can. "Best server" moves
            // on each time: the servers that failed are penalized on this network and rank last
            // (the oldest penalty first), so the retries walk down the list instead of hitting
            // the same node every 20 s. A server the user picked stays the target.
            while (!_userDisconnected && _killSwitch.IsEngaged && Status == ColituVpnStatus.Error && ColituAuthService.Instance.DevicePause == null)
            {
                await Task.Delay(TimeSpan.FromSeconds(20));
                if (_userDisconnected || !_killSwitch.IsEngaged || Status != ColituVpnStatus.Error || _connectionLock.CurrentCount == 0)
                {
                    continue;
                }
                try
                {
                    await ReconnectAsync();
                    Notice?.Invoke("info.reconnected");
                    return;
                }
                catch (ColituDevicePausedException)
                {
                    return;
                }
                catch (Exception ex)
                {
                    LogConnection($"Background reconnect failed: {ex.Message}");
                    SetStatus(ColituVpnStatus.Error);
                }
            }
        }
        finally
        {
            _autoReconnecting = false;
        }
    }

    // ── Kill switch ────────────────────────────────────────────────────────
    /// <summary>True while the kill switch is blocking traffic outside the VPN.</summary>
    public bool KillSwitchEngaged => _killSwitch.IsEngaged;

    /// <summary>
    /// On Linux the switch is a set of nftables rules installed with the sudo
    /// password, which only TUN mode asks for; in TUN mode it stays on for the
    /// whole session and closes any gap if the tunnel drops.
    /// </summary>
    public bool KillSwitchApplies => Preferences is { KillSwitchEnabled: true, IsTunMode: true };

    private async Task ApplyKillSwitchForConnectedTunnelAsync()
    {
        if (KillSwitchApplies)
        {
            await EngageKillSwitchAsync("TUN session");
        }
        else
        {
            await ReleaseKillSwitchAsync("tunnel is up");
        }
    }

    private async Task<bool> EngageKillSwitchAsync(string reason)
    {
        try
        {
            // Re-applied on every engage so a new server or a moved panel address is let through.
            var allowed = new List<IPAddress>(ColituNetwork.KnownAddresses());
            // The panel is dialled by its pinned addresses (the rules drop DNS to public upstream
            // resolvers); refreshed here, the last good ones stay when the lookup fails.
            // Every API base and list URL of the signed endpoint list: failover must never hit the kill switch.
            var apiHosts = ColituAuthService.Instance.PinnedUrls()
                .Select(url => Uri.TryCreate(url, UriKind.Absolute, out var uri) ? uri.Host : null)
                .OfType<string>().Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            await ColituPinnedHosts.RefreshAsync(apiHosts);
            foreach (var host in apiHosts.Concat(ColituNetwork.DohEndpoints.Select(url => new Uri(url).Host)))
            {
                allowed.AddRange(ColituPinnedHosts.AddressesOf(host));
                if (await ColituNetwork.ResolveServerAsync(host) is { } address)
                {
                    allowed.Add(address);
                }
            }
            allowed.AddRange(new[] { "1.1.1.1", "1.0.0.1", "8.8.8.8", "8.8.4.4" }.Select(IPAddress.Parse));
            await _killSwitch.EngageAsync(allowed, ColituSplitTunnel.KillSwitchBypassNetworks(Preferences));
            LogConnection($"Kill switch engaged ({reason}), {allowed.Distinct().Count()} addresses allowed");
            return true;
        }
        catch (Exception ex)
        {
            LogConnection($"Kill switch could not be engaged: {ex.Message}");
            Notice?.Invoke("err.killSwitch");
            return false;
        }
    }

    /// <summary>False when the rules are still in place (no sudo password yet, or nft failed).</summary>
    private async Task<bool> ReleaseKillSwitchAsync(string reason)
    {
        if (!_killSwitch.IsEngaged)
        {
            return true;
        }
        if (!await _killSwitch.ReleaseAsync())
        {
            LogConnection($"Kill switch could not be released ({reason}); it keeps blocking");
            return false;
        }
        KillSwitchRecoveryPending = false;
        LogConnection($"Kill switch released ({reason})");
        return true;
    }

    /// <summary>True when the kill switch blocks and removing it needs the sudo password first.</summary>
    public bool KillSwitchNeedsPasswordToRelease => _killSwitch.IsEngaged && AppManager.Instance.LinuxSudoPwd.IsNullOrEmpty();

    // ── Cached connection settings (encrypted: they carry credentials) ─────
    private void SaveCachedConfig(string key, ColituVpnConfigResponse config)
    {
        try
        {
            var cache = ReadConfigCache();
            cache[key] = config;
            var json = JsonSerializer.SerializeToUtf8Bytes(cache, _jsonOptions);
            ColituHardening.WritePrivateFile(ConfigCachePath(), ColituHardening.ProtectBytes(json));
        }
        catch (Exception ex)
        {
            Logging.SaveLog("ColituVpnService.SaveCachedConfig", ex);
        }
    }

    private ColituVpnConfigResponse? LoadCachedConfig(string key)
    {
        var cache = ReadConfigCache();
        return cache.TryGetValue(key, out var config)
            && config.OfflineGraceUntil is { } grace && grace > DateTimeOffset.UtcNow
            && config.Candidates.Count > 0
            ? config
            : null;
    }

    private Dictionary<string, ColituVpnConfigResponse> ReadConfigCache()
    {
        try
        {
            if (!File.Exists(ConfigCachePath())) return [];
            var bytes = ColituHardening.UnprotectBytes(File.ReadAllBytes(ConfigCachePath()));
            return JsonSerializer.Deserialize<Dictionary<string, ColituVpnConfigResponse>>(bytes, _jsonOptions) ?? [];
        }
        catch
        {
            return [];
        }
    }

    private static string ConfigCachePath() => ColituHardening.UserConfigPath("colitu-config-cache.bin");

    internal static bool IsPlanError(ColituApiException ex) => ex.ErrorCode is "ENTITLEMENT_INACTIVE" or "ENTITLEMENT_EXPIRED";

    internal static bool IsNetworkFailure(Exception ex)
    {
        return ex switch
        {
            HttpRequestException or TaskCanceledException or TimeoutException => true,
            ColituApiException api => (int)api.StatusCode >= 500 || api.ErrorCode is "REFRESH_FAILED",
            _ => ex.InnerException != null && IsNetworkFailure(ex.InnerException)
        };
    }

    private async Task EnsureCoreReadyAsync()
    {
        if (_coreReady) return;
        await ConfigHandler.InitBuiltinDNS(_config);
        await ConfigHandler.InitBuiltinFullConfigTemplate(_config);
        await ProfileExManager.Instance.Init();
        await CoreManager.Instance.Init(_config, UpdateCoreMessageAsync);
        // v2rayN's built-in resolvers are Chinese; use global ones for the bootstrap of the encrypted DNS.
        _config.SimpleDNSItem ??= new SimpleDNSItem();
        _config.SimpleDNSItem.DirectDNS = "1.1.1.1,8.8.8.8";
        _config.SimpleDNSItem.BootstrapDNS = "1.1.1.1,8.8.8.8";
        // No hosts overrides: they only add DNS rules that sing-box 1.14 rejects without extra evaluate steps.
        _config.SimpleDNSItem.AddCommonHosts = false;
        _config.SimpleDNSItem.UseSystemHosts = false;
        _config.SimpleDNSItem.Hosts = "";
        // Info level makes the cores print every DNS lookup and connection (the user's browsing);
        // warnings and errors still show why the tunnel failed. No access log either.
        _config.CoreBasicItem.Loglevel = "warning";
        _config.CoreBasicItem.LogEnabled = false;
        // The local proxy has no password: it stays on 127.0.0.1, whatever an old or edited
        // settings file says (v2rayN's "allow LAN" would open it to everyone on the network).
        foreach (var inbound in _config.Inbound ?? [])
        {
            inbound.AllowLANConn = false;
        }
        // Hysteria2 is not an Xray protocol; it runs on the bundled sing-box core.
        _config.CoreTypeItem = [new CoreTypeItem { ConfigType = EConfigType.Hysteria2, CoreType = ECoreType.sing_box }];
        // Port hopping (mport on the panel's hysteria2:// links): a new UDP port every 30 s, so a
        // mobile network that throttles one long-lived UDP flow never sees one. Both cores ignore
        // values under 5 s; a stale value from an old settings file must not slow or break the hops.
        _config.HysteriaItem ??= new HysteriaItem();
        _config.HysteriaItem.HopInterval = Global.Hysteria2DefaultHopInt;
        _coreReady = true;
    }

    /// <summary>
    /// Imports every transport offered by the panel for the selected location
    /// (primary first) and returns the matching v2rayN profiles in that order.
    /// </summary>
    private static readonly string[] ImportSchemes = ["vless://", "trojan://", "hysteria2://", "ss://"];

    /// <summary>True when every non-empty line of the config is one of the share-link schemes the app builds.</summary>
    internal static bool IsLinksOnly(string? rawConfig)
        => !string.IsNullOrWhiteSpace(rawConfig)
           && rawConfig.Split('\n').Select(line => line.Trim()).Where(line => line.Length > 0)
               .All(line => ImportSchemes.Any(scheme => line.StartsWith(scheme, StringComparison.OrdinalIgnoreCase)));

    private async Task<List<(ColituConfigCandidate Candidate, ProfileItem Profile)>> ImportConfigAsync(ColituVpnConfigResponse config, ColituVpnServer? server)
    {
        var candidates = config.Candidates;
        LogConnection($"Importing config for serverId={config.ServerId ?? server?.Id}, revision={config.Revision}, candidates={string.Join(",", candidates.Select(item => item.Protocol))}");
        if (candidates.Count == 0 || string.IsNullOrWhiteSpace(config.RawConfig))
        {
            throw new InvalidOperationException("Server config is not ready.");
        }

        // RawConfig is built locally from share links. Anything else must never reach v2rayN's
        // importer, whose fallbacks register unrecognised text as a full core config.
        if (!IsLinksOnly(config.RawConfig))
        {
            throw new InvalidOperationException("Server config could not be imported.");
        }
        var imported = await ConfigHandler.AddBatchServers(_config, config.RawConfig, ColituSubId, true);
        if (imported <= 0) throw new InvalidOperationException("Server config could not be imported.");

        var profiles = await AppManager.Instance.ProfileItems(ColituSubId) ?? [];
        var used = new HashSet<string>(StringComparer.Ordinal);
        var ordered = new List<(ColituConfigCandidate Candidate, ProfileItem Profile)>();
        foreach (var candidate in candidates)
        {
            var type = candidate.Protocol switch
            {
                "hysteria2" => EConfigType.Hysteria2,
                "vless-reality" or "vless-xhttp" => EConfigType.VLESS,
                "trojan" => EConfigType.Trojan,
                "shadowsocks" => EConfigType.Shadowsocks,
                _ => (EConfigType?)null
            };
            // Both VLESS transports import as VLESS profiles; the network tells them apart.
            var xhttp = candidate.Protocol == "vless-xhttp";
            var profile = profiles.FirstOrDefault(item => type != null && item.ConfigType == type && !used.Contains(item.IndexId)
                && (type != EConfigType.VLESS || string.Equals(item.Network, "xhttp", StringComparison.OrdinalIgnoreCase) == xhttp));
            if (profile == null) continue;
            used.Add(profile.IndexId);
            ordered.Add((candidate, profile));
        }

        if (ordered.Count == 0)
        {
            throw new InvalidOperationException("Imported server was not found in v2rayN profiles.");
        }

        var serverId = config.ServerId ?? server?.Id;
        var lastGood = _adaptive.LastGoodTransport(_networkKey, serverId, DateTimeOffset.UtcNow);
        var offered = ordered.Select(item => item.Candidate.Protocol).ToList();
        bool Stalled(string protocol) => _adaptive.IsStalled(_networkKey, serverId, protocol, DateTimeOffset.UtcNow);
        _marksIgnored = ColituTransportOrder.MarksCoverAlmostAll(offered, Stalled);
        // Adaptive Connect 3.0: no memory of this network (no last good transport for this server),
        // so what worked for most devices here goes first, and the latency probe is skipped. Own
        // experience (a last good transport) and a round with ignored marks keep the usual order.
        if (ColituAdaptiveConnect3.HintedStartActive(_marksIgnored)
            && ColituNetworkHintsPolicy.HintedStart(ordered, item => item.Candidate.Protocol, HintedPreferredList(),
                protocol => Stalled(protocol) || HintedBlocked(protocol), lastGood,
                rest => rest.OrderBy(item => TransportRank(item.Candidate.Protocol, 0, Stalled(item.Candidate.Protocol),
                    hintedBlocked: HintedBlocked(item.Candidate.Protocol)))) is { } hinted)
        {
            LogConnection($"no memory of this network, starting with the hinted [{string.Join(", ", hinted.Select(item => item.Candidate.Protocol))}]");
            LogConnection($"Transport order: {string.Join(" > ", hinted.Select(item => item.Candidate.Protocol))} (hinted, no probe)");
            return hinted;
        }
        var latency = await MeasureTransportLatencyAsync(ordered);
        if (_marksIgnored)
        {
            var proven = _adaptive.ProvenOnNetwork(_networkKey, DateTimeOffset.UtcNow);
            LogConnection($"stall marks cover (almost) every transport ({string.Join(", ", offered.Where(Stalled))}): a network problem, ignored for this round");
            ordered = ordered
                .OrderBy(item => ColituTransportOrder.IgnoredMarksTier(item.Candidate.Protocol,
                    protocol => string.Equals(lastGood, protocol, StringComparison.OrdinalIgnoreCase) || proven.Contains(protocol, StringComparer.OrdinalIgnoreCase),
                    Stalled, _failedThisConnect.Contains))
                .ThenBy(item => TransportRank(item.Candidate.Protocol, latency.GetValueOrDefault(item.Candidate.Protocol, -1), false, false, HintedBlocked(item.Candidate.Protocol)))
                .ThenBy(item => latency.GetValueOrDefault(item.Candidate.Protocol, int.MaxValue))
                .ToList();
        }
        else
        {
            ordered = ordered
                .OrderBy(item => TransportRank(item.Candidate.Protocol, latency.GetValueOrDefault(item.Candidate.Protocol, -1), serverId, lastGood))
                .ThenBy(item => latency.GetValueOrDefault(item.Candidate.Protocol, int.MaxValue))
                .ToList();
        }
        LogConnection($"Transport order: {string.Join(" > ", ordered.Select(item => $"{item.Candidate.Protocol}({LatencyText(latency, item.Candidate.Protocol)})"))}");
        return ordered;
    }

    private static string LatencyText(Dictionary<string, int> latency, string protocol)
    {
        if (!latency.TryGetValue(protocol, out var ms)) return "udp";
        return ms < 0 ? "unreachable" : $"{ms} ms";
    }

    /// <summary>
    /// TCP connect time to each TCP transport's endpoint, two attempts in parallel, best of two.
    /// Hysteria2 is UDP and has no cheap handshake, so it is not measured. -1 = no answer.
    /// </summary>
    private async Task<Dictionary<string, int>> MeasureTransportLatencyAsync(List<(ColituConfigCandidate Candidate, ProfileItem Profile)> profiles)
    {
        var result = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var tasks = profiles
            .Where(item => item.Profile.ConfigType != EConfigType.Hysteria2)
            .Select(async item =>
            {
                var attempts = await Task.WhenAll(TcpConnectMsAsync(item.Profile.Address, item.Profile.Port), TcpConnectMsAsync(item.Profile.Address, item.Profile.Port));
                var reachable = attempts.Where(ms => ms >= 0).ToList();
                return (item.Candidate.Protocol, Ms: reachable.Count > 0 ? reachable.Min() : -1);
            })
            .ToList();
        foreach (var (protocol, ms) in await Task.WhenAll(tasks))
        {
            result[protocol] = ms;
        }
        return result;
    }

    private static async Task<int> TcpConnectMsAsync(string address, int port)
    {
        try
        {
            using var client = new TcpClient();
            using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(2500));
            var watch = System.Diagnostics.Stopwatch.StartNew();
            await client.ConnectAsync(address, port, cts.Token);
            return (int)watch.ElapsedMilliseconds;
        }
        catch
        {
            return -1;
        }
    }

    /// <summary>
    /// Reality with the vision flow is the fastest and most robust transport on
    /// this core, plain Shadowsocks the least; a transport that stalled recently
    /// (on this server, or on this network as a whole) goes to the back of the line for a while.
    /// The one that last carried traffic on this server and network goes first while reachable.
    /// </summary>
    private int TransportRank(string protocol, int latencyMs, string? serverId, string? lastGood = null) =>
        TransportRank(protocol, latencyMs, _adaptive.IsStalled(_networkKey, serverId, protocol, DateTimeOffset.UtcNow),
            string.Equals(lastGood, protocol, StringComparison.OrdinalIgnoreCase), HintedBlocked(protocol));

    /// <param name="hintedBlocked">The network hints call it blocked on this network (and it has not
    /// worked here in the last 24 h): it goes behind every other transport.</param>
    internal static int TransportRank(string protocol, int latencyMs, bool stalledRecently, bool lastGood = false, bool hintedBlocked = false)
    {
        if (lastGood && !stalledRecently && (latencyMs >= 0 || protocol == "hysteria2"))
        {
            return -1;
        }
        // Hysteria2 (QUIC) keeps working on lossy links where TCP transports stall, so it leads;
        // the TCP transports then follow in measured-latency order (ThenBy), unreachable ones last.
        var rank = protocol switch
        {
            "hysteria2" => 0,
            _ => 1
        };
        if (latencyMs < 0 && protocol != "hysteria2")
        {
            rank += 5;
        }
        if (stalledRecently)
        {
            rank += 10;
        }
        if (hintedBlocked)
        {
            rank += 20;
        }
        return rank;
    }

    /// <summary>
    /// Starts the core with each candidate transport until traffic flows through
    /// one of them, then reports the observations back to the panel. With a
    /// <paramref name="serverBudget"/> (automatic mode) no further transport is started once
    /// another one would not fit in it: the next server gets the time instead.
    /// </summary>
    private async Task StartFirstWorkingProfileAsync(List<(ColituConfigCandidate Candidate, ProfileItem Profile)> profiles, ColituVpnConfigResponse config, ColituVpnServer? server, CancellationToken token, TimeSpan? serverBudget = null)
    {
        var observations = new List<ColituProtocolObservation>();
        var watch = Stopwatch.StartNew();
        // Spares that failed together with their primary in this connect ("server|transport"): not reused.
        var excludedSpares = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            for (var index = 0; index < profiles.Count; index++)
            {
                token.ThrowIfCancellationRequested();
                var (candidate, profile) = profiles[index];
                if (index > 0 && serverBudget is { } budget && watch.Elapsed + TransportBudget > budget)
                {
                    LogConnection($"Server time budget spent ({watch.ElapsedMilliseconds} ms of {budget.TotalSeconds:0} s); {profiles.Count - index} transport(s) not tried");
                    throw new ColituConnectException(Loc.I["err.unreachable"]);
                }
                var isLast = index == profiles.Count - 1;
                foreach (var host in new[] { profile.Address, profile.Sni })
                {
                    if (host.IsNotEmpty()) _serverHosts.Add(host);
                }
                if (await ConfigHandler.SetDefaultServerIndex(_config, profile.IndexId) != 0)
                {
                    throw new InvalidOperationException("Imported server could not be selected as default.");
                }
                LogConnection($"Trying transport {candidate.Protocol}: index={profile.IndexId}, address={profile.Address}:{profile.Port}");

                try
                {
                    _lastCoreMessage = null;
                    _sparePlan = PlanSpare(candidate.Protocol, profile, profiles, config, server, excludedSpares);
                    await ReloadCoreAsync(token);
                    // Hysteria2's first QUIC handshake right after the TUN adapter appears sometimes
                    // needs longer than one round; it gets a second one unless the server refused us.
                    // Speed budget: start plus check stay within about 8 s per pair (2 x 4 s, or 6 s).
                    var rounds = isLast || candidate.Protocol == "hysteria2" ? 2 : 1;
                    var roundTimeout = TimeSpan.FromSeconds(rounds == 2 ? 4 : 6);
                    var pairWatch = Stopwatch.StartNew();
                    var plan = _sparePlan;
                    var spareVerify = plan != null ? _spareVerifyInbound : null;
                    // Parallel connect: the primary alone (colitu-verify) and the spare alone
                    // (colitu-verify-spare) at the same time, in one core start.
                    var primaryTask = VerifyConnectionActiveAsync(server, rounds, token, roundTimeout, primaryOnly: true);
                    var spareTask = spareVerify == null ? null
                        : ProbeThroughLocalProxyAsync(spareVerify.Port, rounds, token, roundTimeout: roundTimeout, credentials: new NetworkCredential(spareVerify.User, spareVerify.Password));
                    var probe = await primaryTask;
                    var spareProbe = spareTask == null ? null : await spareTask;
                    var graceOk = false;
                    if (!probe.Success && spareProbe is { Success: true } && _verifyInbound is { } verify)
                    {
                        // Only the spare answered: the primary still gets 1.5 s.
                        graceOk = (await ProbeThroughLocalProxyAsync(verify.Port, 1, token, roundTimeout: ColituParallelConnect.PrimaryGrace,
                            credentials: new NetworkCredential(verify.User, verify.Password))).Success;
                    }
                    var outcome = ColituParallelConnect.Decide(probe.Success, spareProbe?.Success, graceOk);
                    if (plan != null && spareProbe != null)
                    {
                        var winner = outcome switch
                        {
                            ColituParallelOutcome.PrimaryWins => candidate.Protocol,
                            ColituParallelOutcome.SwapRoles => Describe(plan),
                            _ => "none"
                        };
                        LogConnection($"parallel round: {candidate.Protocol} vs {Describe(plan)} → winner {winner} in {pairWatch.ElapsedMilliseconds} ms");
                    }
                    observations.Add(new ColituProtocolObservation { Protocol = candidate.Protocol, Reachable = probe.Success || graceOk, LatencyMs = probe.LatencyMs });
                    if (outcome == ColituParallelOutcome.PrimaryWins)
                    {
                        _activeTransport = candidate.Protocol;
                        return;
                    }
                    // Without internet every transport fails: say so instead of blaming (and marking)
                    // each one in turn. Checked directly, outside the tunnel.
                    if (!_credentialsRefused && spareProbe is not { Success: true } && !await InternetReachableDirectAsync())
                    {
                        observations.Clear();
                        LogConnection("No internet outside the tunnel either; not trying the other transports");
                        throw new ColituConnectException(Loc.I["warn.noInternet"], offline: true);
                    }
                    // A transport that carried nothing goes last on the next attempts too (UDP blocked on
                    // this network): 10 minutes, 6 h once another transport carries traffic here. Refused
                    // credentials are a server still applying new ones, not a property of the transport.
                    if (!_credentialsRefused)
                    {
                        _adaptive.MarkStalled(_networkKey, config.ServerId ?? server?.Id, candidate.Protocol, DateTimeOffset.UtcNow, provisional: true);
                        _failedThisConnect.Add(candidate.Protocol);
                    }
                    if (outcome == ColituParallelOutcome.SwapRoles && plan != null)
                    {
                        if (plan.NextServerId != null && _spareServer is { } spareServer)
                        {
                            // The spare's server and transport take the lead (one quick reload, new spare).
                            throw new ColituSpareServerWins(spareServer, plan.Protocol);
                        }
                        // Same server: the spare's transport is the next primary; a new spare is picked
                        // (neither the failed transport, now marked, nor the new primary).
                        var next = profiles.FindIndex(index + 1, item => item.Candidate.Protocol == plan.Protocol);
                        if (next > index + 1)
                        {
                            var promoted = profiles[next];
                            profiles.RemoveAt(next);
                            profiles.Insert(index + 1, promoted);
                        }
                        isLast = index == profiles.Count - 1;
                    }
                    else if (outcome == ColituParallelOutcome.BothFailed && plan != null)
                    {
                        excludedSpares.Add(SpareKey(plan.ServerId, plan.Protocol));
                    }
                    if (isLast)
                    {
                        // No transport carried traffic: report it instead of claiming a connection.
                        throw new ColituConnectException(Loc.I["err.unreachable"]);
                    }
                }
                catch (Exception ex) when (!isLast && ex is not (OperationCanceledException or ColituConnectException { Offline: true } or ColituSpareServerWins))
                {
                    observations.Add(new ColituProtocolObservation { Protocol = candidate.Protocol, Reachable = false });
                    LogConnection($"Transport {candidate.Protocol} failed: {ex.Message}");
                }

                await StopCoreAsync();
            }
        }
        finally
        {
            // Observations are per node; a route's id is not one.
            if (config.Server?.IsMultihop != true && server?.IsMultihop != true)
            {
                _ = _api.ReportProtocolObservationsAsync(config.ServerId ?? server?.Id, observations, CurrentNetworkToken());
            }
        }
    }

    private async Task PrepareConnectionModeAsync(string? serverCountry)
    {
        EnsureSudoForTun();
        var preferences = _session.Preferences.Normalize();
        await ApplyRuntimePreferencesAsync(preferences);
        await EnsureColituRoutingAsync(preferences, serverCountry);

        if (!_config.TunModeItem.EnableTun && _config.SystemProxyItem.SysProxyType != ESysProxyType.ForcedChange)
        {
            _restoreSysProxyType ??= _config.SystemProxyItem.SysProxyType;
            _config.SystemProxyItem.SysProxyType = ESysProxyType.ForcedChange;
            if (_config.SystemProxyItem.SystemProxyAdvancedProtocol.IsNullOrEmpty())
            {
                _config.SystemProxyItem.SystemProxyAdvancedProtocol = string.Empty;
            }
            await ConfigHandler.SaveConfig(_config);
            LogConnection($"System proxy mode forced for Colitu connection. Previous={_restoreSysProxyType}");
        }
    }

    private async Task ApplyRuntimePreferencesAsync(ColituVpnPreferences preferences)
    {
        preferences = preferences.Normalize();
        var tun = preferences.IsTunMode;
        _config.TunModeItem.EnableTun = tun;
        _config.TunModeItem.AutoRoute = tun;
        // Strict route keeps the system resolver and other traffic from leaving next to the
        // tunnel. The kill switch's nftables rules work at another layer and do not conflict.
        _config.TunModeItem.StrictRoute = tun;

        // Domain rules (split tunneling) need the name of each connection: sniff it.
        if ((preferences.DnsLeakProtectionEnabled || preferences.SplitTunnelDomains is { Count: > 0 }) && _config.Inbound.Count > 0)
        {
            _config.Inbound.First().SniffingEnabled = true;
        }

        // Proxy mode, sites that bypass the VPN: the desktop connects to them directly too.
        if (OperatingSystem.IsLinux())
        {
            _config.SystemProxyItem.SystemProxyExceptions = ColituSplitTunnel.BuildProxyExceptions(Global.SystemProxyExceptionsLinux, preferences);
        }

        // Ad blocking: lookups go to Colitu's AdGuard Home servers (DoH through the tunnel), which
        // answer 0.0.0.0 for ad and tracker domains. In TUN mode that already stops the apps; in
        // proxy mode the browser hands the domain to the core, so the core resolves it first
        // (IPIfNonMatch) and the 0.0.0.0 rule in BuildColituRoutingRules drops the connection.
        // IPIfNonMatch is also what sends a domain the Russian rules do not name out directly
        // when its address is in Russia (as on iOS and Android).
        _config.SimpleDNSItem ??= new SimpleDNSItem();
        _config.SimpleDNSItem.RemoteDNS = (preferences.AdBlockEnabled && AdBlockAvailable)
            ? string.Join(",", ColituAdBlockDohServers)
            : Global.DomainRemoteDNSAddress.First();
        _config.RoutingBasicItem.DomainStrategy = Global.IPIfNonMatch;

        await ConfigHandler.SaveConfig(_config);
    }

    /// <summary>
    /// Colitu's ad-blocking DNS servers, tried in order. They are Colitu's own nodes, so they are
    /// not in the public source: the package scripts embed them as assembly metadata.
    /// Builds without them have no ad blocking (the switch is hidden).
    /// </summary>
    internal static readonly string[] ColituAdBlockDohServers = ParseAdBlockDohServers(
        typeof(ColituVpnService).Assembly.GetCustomAttributes(typeof(System.Reflection.AssemblyMetadataAttribute), false)
            .OfType<System.Reflection.AssemblyMetadataAttribute>()
            .FirstOrDefault(attribute => attribute.Key == "ColituAdBlockDoh")?.Value);

    internal static bool AdBlockAvailable => ColituAdBlockDohServers.Length > 0;

    internal static string[] ParseAdBlockDohServers(string? value) =>
        (value ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(url => url.StartsWith("https://", StringComparison.OrdinalIgnoreCase) && Uri.TryCreate(url, UriKind.Absolute, out _))
            .ToArray();

    /// <summary>Whether a node (by its probe host) runs one of <see cref="ColituAdBlockDohServers"/>; the server list tags it.</summary>
    internal static bool HostsAdBlockDns(string? host) =>
        !string.IsNullOrWhiteSpace(host)
        && ColituAdBlockDohServers.Any(url => string.Equals(new Uri(url).Host, host.Trim(), StringComparison.OrdinalIgnoreCase));

    private async Task EnsureColituRoutingAsync(ColituVpnPreferences preferences, string? serverCountry)
    {
        preferences = preferences.Normalize();
        await ConfigHandler.InitBuiltinRouting(_config);
        var items = await AppManager.Instance.RoutingItems() ?? [];
        var activeRouting = items.FirstOrDefault(item => item.IsActive);
        var routing = items.FirstOrDefault(item => string.Equals(item.Remarks, ColituRoutingRemarks, StringComparison.OrdinalIgnoreCase));
        var rules = BuildColituRoutingRules(preferences, serverCountry);

        routing ??= new RoutingItem
        {
            Id = Guid.NewGuid().ToString("N"),
            Remarks = ColituRoutingRemarks,
            Url = string.Empty,
            Sort = items.Count + 1,
            Enabled = true
        };

        routing.RuleSet = JsonSerializer.Serialize(rules, _jsonOptions);
        routing.RuleNum = rules.Count;
        routing.DomainStrategy = _config.RoutingBasicItem.DomainStrategy;
        routing.DomainStrategy4Singbox = _config.RoutingBasicItem.DomainStrategy4Singbox;
        await SQLiteHelper.Instance.ReplaceAsync(routing);

        if (activeRouting != null
            && activeRouting.Id != routing.Id
            && _session.Preferences.PreviousRoutingId.IsNullOrEmpty())
        {
            _session = _session with { Preferences = _session.Preferences with { PreviousRoutingId = activeRouting.Id } };
            SaveState();
        }

        await ConfigHandler.SetDefaultRouting(_config, routing);
        LogConnection(RussianSitesDirect(serverCountry, preferences.PrivacyModeEnabled)
            ? "Routing profile applied: DNS protection, Russian sites direct"
            : preferences.PrivacyModeEnabled
                ? "Routing profile applied: DNS protection, privacy mode (all traffic through the VPN)"
                : "Routing profile applied: DNS protection, Russian sites through the Russian server");
    }

    /// <summary>
    /// Russian sites skip the tunnel unless the server itself is in Russia: someone abroad who
    /// picks the Moscow server wants exactly those sites to see a Russian address.
    /// Privacy mode sends everything through the tunnel, Russian sites included.
    /// </summary>
    internal static bool RussianSitesDirect(string? serverCountry, bool privacyMode = false) =>
        !privacyMode && !string.Equals(serverCountry?.Trim(), "RU", StringComparison.OrdinalIgnoreCase);

    internal static List<RulesItem> BuildColituRoutingRules(ColituVpnPreferences preferences, string? serverCountry = null)
    {
        preferences = preferences.Normalize();
        // "Only selected apps and sites use the VPN": everything else is direct anyway, and a
        // selected app's Russian sites must stay in the tunnel the user asked for.
        var ruDirect = RussianSitesDirect(serverCountry, preferences.PrivacyModeEnabled) && !ColituSplitTunnel.IsIncludeActive(preferences);
        var rules = new List<RulesItem>();

        rules.Add(new RulesItem
        {
            Id = "colitu-dns-protection",
            Remarks = "DNS leak protection",
            OutboundTag = Global.ProxyTag,
            Port = "53",
            Network = "tcp,udp",
            Enabled = preferences.DnsLeakProtectionEnabled
        });

        rules.Add(new RulesItem
        {
            Id = "colitu-ad-block",
            Remarks = "Ad blocking: domains Colitu DNS answers with 0.0.0.0",
            OutboundTag = Global.BlockTag,
            Ip = ["0.0.0.0/32", "::/128"],
            Enabled = (preferences.AdBlockEnabled && AdBlockAvailable)
        });

        // The local network (printers, NAS, another PC, the router page) never goes to the server,
        // which cannot reach it: in TUN mode the adapter took it and the connection hung. After the
        // DNS rule, so a resolver on the LAN still gets no names. The kill switch always lets it out.
        rules.Add(new RulesItem
        {
            Id = LanDirectRuleId,
            Remarks = "Local network direct",
            OutboundTag = Global.DirectTag,
            Ip = ["geoip:private"],
            Enabled = true
        });

        // Russian sites and apps (banks, Gosuslugi, Wildberries, ...) refuse connections from a
        // foreign IP ("turn off your VPN"), so they go out directly, as on iOS and Android; through
        // a Russian server they already arrive from a Russian address and stay in the tunnel.
        // Xray reads these from bin/geo*.dat (XRAY_LOCATION_ASSET), sing-box from bin/srss/*.srs
        // (shipped by scripts/package-linux.sh; GitHub, where sing-box would fetch them, is blocked in Russia).
        // The user's split tunneling comes before Colitu's own regional rules.
        rules.AddRange(ColituSplitTunnel.BuildRules(preferences));

        rules.Add(new RulesItem
        {
            Id = "colitu-ru-direct-domain",
            Remarks = "Russian sites direct",
            OutboundTag = Global.DirectTag,
            Domain = ["geosite:category-ru"],
            Enabled = ruDirect
        });

        rules.Add(new RulesItem
        {
            Id = "colitu-ru-direct-ip",
            Remarks = "Russian IPs direct",
            OutboundTag = Global.DirectTag,
            Ip = ["geoip:ru"],
            Enabled = ruDirect
        });

        if (ColituSplitTunnel.BuildCatchAllRule(preferences) is { } rest)
        {
            rules.Add(rest);
        }

        return rules;
    }

    /// <summary>TUN mode runs the core as root through sudo: the password must be known first.</summary>
    private void EnsureSudoForTun()
    {
        if (Preferences.IsTunMode && AppManager.Instance.LinuxSudoPwd.IsNullOrEmpty())
        {
            throw new ColituSudoRequiredException();
        }
    }

    /// <summary>True when connecting now would ask for the sudo password first.</summary>
    public bool NeedsSudoPassword => Preferences.IsTunMode && AppManager.Instance.LinuxSudoPwd.IsNullOrEmpty();

    private async Task RestoreProxyPreferenceAsync()
    {
        if (_restoreSysProxyType == null) return;
        _config.SystemProxyItem.SysProxyType = _restoreSysProxyType.Value;
        _restoreSysProxyType = null;
        await ConfigHandler.SaveConfig(_config);
    }

    private async Task RestoreRoutingPreferenceAsync()
    {
        var previousRoutingId = _session.Preferences.PreviousRoutingId;
        if (previousRoutingId.IsNullOrEmpty()) return;

        var previous = await AppManager.Instance.GetRoutingItem(previousRoutingId);
        if (previous != null)
        {
            await ConfigHandler.SetDefaultRouting(_config, previous);
        }

        _session = _session with { Preferences = _session.Preferences with { PreviousRoutingId = null } };
        SaveState();
    }

    private async Task ReloadCoreAsync(CancellationToken token = default)
    {
        var profileItem = await ConfigHandler.GetDefaultServer(_config);
        if (profileItem == null)
        {
            throw new InvalidOperationException("No v2rayN profile is selected.");
        }

        // Send through the physical interface, never through another VPN's tunnel.
        _config.CoreBasicItem.BindInterface = ColituNetwork.PhysicalInterfaceName();
        LogConnection($"Building core config for profile={profileItem.IndexId}, type={profileItem.ConfigType}, outbound interface={_config.CoreBasicItem.BindInterface ?? "auto"}");
        var allResult = await CoreConfigContextBuilder.BuildAll(_config, profileItem);
        if (!allResult.Success)
        {
            var errors = allResult.CombinedValidatorResult.Errors;
            throw new InvalidOperationException(errors.Count > 0 ? string.Join(Environment.NewLine, errors) : "Core config validation failed.");
        }

        var mainContext = allResult.MainResult.Context;
        var coreInfo = CoreInfoManager.Instance.GetCoreInfo(mainContext.RunCoreType);
        var coreExe = CoreInfoManager.Instance.GetCoreExecFile(coreInfo, out var coreMsg);
        var configPath = Utils.GetBinConfigPath(Global.CoreConfigFileName);
        LogConnection($"Core config path={configPath}");
        LogConnection(coreExe.IsNotEmpty()
            ? $"Core exe path={coreExe}"
            : $"Core exe missing: {coreMsg}");

        if (coreExe.IsNullOrEmpty())
        {
            throw new InvalidOperationException(coreMsg.IsNotEmpty() ? coreMsg : "Xray/sing-box core executable was not found.");
        }

        await EnsureLocalPortsFreeAsync();
        // Set again by the warm spare when this config gets one.
        _verifyInbound = null;
        _spareVerifyInbound = null;
        _checkInbound = null;
        _checkIndexId = profileItem.IndexId;
        await CoreManager.Instance.LoadCore(mainContext, allResult.PreSocksResult?.Context);

        // The core needs a moment to load its geo data, longer on the first run
        // while antivirus scans the binary: wait for the local port, not a fixed delay.
        var socksPort = AppManager.Instance.GetLocalPort(EInboundProtocol.socks);
        var started = Stopwatch.StartNew();
        var listening = false;
        while (started.Elapsed < TimeSpan.FromSeconds(12))
        {
            await Task.Delay(250, token);
            if (!CoreManager.Instance.IsCoreRunning)
            {
                throw new InvalidOperationException(_lastCoreMessage.IsNotEmpty()
                    ? $"VPN core failed to start: {_lastCoreMessage}"
                    : $"VPN core failed to start. Check core executable and config path: {coreExe}, {configPath}");
            }
            if (await IsLocalPortOpenAsync(Global.Loopback, socksPort))
            {
                listening = true;
                break;
            }
        }
        LogConnection($"Core ready check: listening={listening} after {started.ElapsedMilliseconds} ms");
        if (!listening)
        {
            throw new InvalidOperationException($"VPN core started but local SOCKS port {socksPort} is not listening.");
        }

        if (_config.TunModeItem.EnableTun)
        {
            // TUN captures all traffic at the adapter level; the system proxy
            // must stay off or apps would double-route through the local proxy.
            await SysProxyHandler.UpdateSysProxy(_config, true);
            LogConnection($"TUN mode active, system proxy disabled. socks={Global.Loopback}:{socksPort}");
            return;
        }

        var proxyResult = await SysProxyHandler.UpdateSysProxy(_config, false);
        LogConnection($"System proxy result={proxyResult}, mode={_config.SystemProxyItem.SysProxyType}, socks={Global.Loopback}:{socksPort}, tun={_config.TunModeItem.EnableTun}");
        if (!proxyResult)
        {
            throw new InvalidOperationException("System proxy could not be applied.");
        }
    }

    /// <summary>
    /// A previous core process (crashed app, stuck switch) can keep the local
    /// inbound port bound, which makes the next start fail with
    /// "address already in use". Stop our core, kill orphans, and wait for the
    /// port to be released before starting a new core.
    /// </summary>
    private async Task EnsureLocalPortsFreeAsync()
    {
        var socksPort = AppManager.Instance.GetLocalPort(EInboundProtocol.socks);
        if (!await IsLocalPortOpenAsync(Global.Loopback, socksPort))
        {
            return;
        }

        LogConnection($"Local port {socksPort} is already in use; stopping previous VPN core.");
        await StopCoreAsync();

        for (var attempt = 0; attempt < 10; attempt++)
        {
            await Task.Delay(400);
            if (!await IsLocalPortOpenAsync(Global.Loopback, socksPort))
            {
                return;
            }
            if (attempt == 3)
            {
                KillOrphanCoreProcesses();
            }
        }

        throw new InvalidOperationException(
            $"Local port {socksPort} is still in use. Close other VPN/proxy apps (or another Colitu instance) and try again.");
    }

    private void KillOrphanCoreProcesses()
    {
        var binPath = Utils.GetBinPath("");
        // Cores started through sudo belong to root and are stopped by CoreManager; these are user cores.
        foreach (var name in new[] { "xray", "sing-box", "v2ray", "mihomo" })
        {
            foreach (var process in Process.GetProcessesByName(name))
            {
                try
                {
                    var path = process.MainModule?.FileName ?? "";
                    if (path.IsNotEmpty() && path.StartsWith(binPath, StringComparison.OrdinalIgnoreCase))
                    {
                        LogConnection($"Killing orphan core process {name} (pid {process.Id})");
                        process.Kill(true);
                        process.WaitForExit(2000);
                    }
                }
                catch
                {
                    // Best effort; the port re-check reports if the conflict persists.
                }
                finally
                {
                    process.Dispose();
                }
            }
        }
    }

    /// <param name="primaryOnly">
    /// The connect check: with a warm spare in the config, probe through the <c>colitu-verify</c>
    /// inbound, which reaches the primary outbound only. Otherwise (and for recovery checks) the
    /// <c>colitu-check</c> inbound, the tunnel as a whole; never the routing rules.
    /// </param>
    private async Task<ColituTrafficProbeResult> VerifyConnectionActiveAsync(ColituVpnServer? server, int rounds = 2, CancellationToken token = default, TimeSpan? roundTimeout = null, bool primaryOnly = false)
    {
        if (!CoreManager.Instance.IsCoreRunning)
        {
            throw new InvalidOperationException("VPN core stopped before the connection could be verified.");
        }

        var socksPort = AppManager.Instance.GetLocalPort(EInboundProtocol.socks);
        if (!await IsLocalPortOpenAsync(Global.Loopback, socksPort))
        {
            throw new InvalidOperationException($"VPN local proxy port {socksPort} is not listening.");
        }

        // The probe goes through the local SOCKS port, not the adapter, so both run at once.
        var tunWatch = Stopwatch.StartNew();
        var tunReady = Task.FromResult(true);
        if (_config.TunModeItem.EnableTun)
        {
            LogConnection($"TUN mode enabled. autoRoute={_config.TunModeItem.AutoRoute}, strictRoute={_config.TunModeItem.StrictRoute}, stack={_config.TunModeItem.Stack}");
            tunReady = WaitForTunInterfaceAsync();
        }

        var verify = primaryOnly ? _verifyInbound : null;
        if (verify != null)
        {
            LogConnection($"Checking the primary alone (warm spare in the config), loopback port {verify.Port}");
        }
        var (port, credentials) = verify != null ? (verify.Port, new NetworkCredential(verify.User, verify.Password)) : TunnelCheckTarget();
        // The server refusing our credentials will not change in a second round.
        var probe = await ProbeThroughLocalProxyAsync(port, rounds, token,
            () => _lastCoreMessage?.Contains("authentication failed", StringComparison.OrdinalIgnoreCase) == true, roundTimeout, credentials);
        if (!await tunReady)
        {
            throw new InvalidOperationException("VPN tunnel adapter or route was not activated.");
        }
        if (_config.TunModeItem.EnableTun)
        {
            LogConnection($"TUN adapter up within {tunWatch.ElapsedMilliseconds} ms");
        }
        LogConnection($"Traffic verification result={probe.Success}, detail={probe.Detail}, latency={probe.LatencyMs} ms, serverId={server?.Id}");
        if (!probe.Success)
        {
            LogConnection("Traffic probe failed after the VPN core became ready.");
        }
        return probe;
    }

    /// <summary>Generic connectivity checks, never the Colitu API; the first 2xx answer through the tunnel counts.</summary>
    internal static readonly string[] TrafficProbeUrls =
    [
        "https://cp.cloudflare.com/generate_204",
        "https://www.gstatic.com/generate_204",
        "http://www.msftconnecttest.com/connecttest.txt"
    ];

    /// <summary>A traffic check answer that proves the tunnel carries traffic: 2xx (generate_204 answers 204).</summary>
    internal static bool IsTrafficProbeSuccess(int statusCode) => statusCode is >= 200 and < 300;

    /// <summary>
    /// Fetches small "connectivity check" pages (<see cref="TrafficProbeUrls"/>) through the tunnel,
    /// all at once, and succeeds on the first 2xx answer. Two rounds of at most seven seconds (or <paramref name="roundTimeout"/>).
    /// </summary>
    private static async Task<ColituTrafficProbeResult> ProbeThroughLocalProxyAsync(int port, int rounds = 2, CancellationToken token = default, Func<bool>? giveUp = null, TimeSpan? roundTimeout = null,
        NetworkCredential? credentials = null)
    {
        var timeout = roundTimeout ?? TimeSpan.FromSeconds(7);
        var handler = new SocketsHttpHandler
        {
            Proxy = new WebProxy($"{Global.Socks5Protocol}{Global.Loopback}:{port}") { Credentials = credentials },
            UseProxy = true,
            ConnectTimeout = timeout < TimeSpan.FromSeconds(5) ? timeout : TimeSpan.FromSeconds(5)
        };

        using var client = new HttpClient(handler) { Timeout = timeout };
        var probeUrls = TrafficProbeUrls;
        var errors = new System.Collections.Concurrent.ConcurrentQueue<string>();

        for (var round = 1; round <= rounds; round++)
        {
            using var budget = CancellationTokenSource.CreateLinkedTokenSource(token);
            budget.CancelAfter(timeout);
            var pending = probeUrls.Select(url => ProbeOnceAsync(client, url, errors, budget.Token)).ToList();
            while (pending.Count > 0)
            {
                var finished = await Task.WhenAny(pending);
                pending.Remove(finished);
                if (await finished is { } success)
                {
                    budget.Cancel();
                    return success;
                }
            }

            token.ThrowIfCancellationRequested();
            if (round < rounds)
            {
                if (giveUp?.Invoke() == true)
                {
                    break;
                }
                await Task.Delay(800, token);
            }
        }

        var detail = errors.IsEmpty ? "No probe URLs were available." : string.Join(" | ", errors.TakeLast(6));
        return new(false, detail);
    }

    private static async Task<ColituTrafficProbeResult?> ProbeOnceAsync(HttpClient client, string url, System.Collections.Concurrent.ConcurrentQueue<string> errors, CancellationToken token)
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.UserAgent.TryParseAdd($"ColituVPN/{ColituAuthService.ClientVersion}");
            var watch = Stopwatch.StartNew();
            using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token);
            if (IsTrafficProbeSuccess((int)response.StatusCode))
            {
                return new(true, $"{url} returned {(int)response.StatusCode}", (int)watch.ElapsedMilliseconds);
            }
            errors.Enqueue($"{url}: HTTP {(int)response.StatusCode}");
        }
        catch (Exception ex) when (!token.IsCancellationRequested || ex is not OperationCanceledException)
        {
            errors.Enqueue($"{url}: {ex.GetType().Name} {ex.Message}");
        }
        catch (OperationCanceledException)
        {
            errors.Enqueue($"{url}: timed out");
        }
        return null;
    }

    private static bool HasActiveTunInterface()
    {
        try
        {
            return NetworkInterface.GetAllNetworkInterfaces().Any(adapter =>
                adapter.OperationalStatus == OperationalStatus.Up &&
                ColituNetwork.IsOwnTun(adapter.Name));
        }
        catch
        {
            return false;
        }
    }

    private static async Task<bool> WaitForTunInterfaceAsync()
    {
        for (var i = 0; i < 40; i++)
        {
            if (HasActiveTunInterface()) return true;
            await Task.Delay(200);
        }

        return false;
    }

    private static async Task<bool> IsLocalPortOpenAsync(string host, int port)
    {
        try
        {
            using var client = new TcpClient();
            using var timeout = new CancellationTokenSource(TimeSpan.FromMilliseconds(1500));
            await client.ConnectAsync(host, port, timeout.Token);
            return client.Connected;
        }
        catch
        {
            return false;
        }
    }

    private async Task UpdateCoreMessageAsync(bool notify, string message)
    {
        var line = ColituLogPrivacy.SanitizeCoreLine(message, _serverHosts);
        if (line != null)
        {
            _lastCoreMessage = line;
            if (line.Contains("authentication failed", StringComparison.OrdinalIgnoreCase))
            {
                _credentialsRefused = true;
            }
            LogConnection($"core notify={notify}: {line}");
            LastError = notify && Status == ColituVpnStatus.Error ? line : LastError;
        }
        await Task.CompletedTask;
    }

    /// <summary>Stops the core and removes its generated config files, which contain the server credentials.</summary>
    private static async Task StopCoreAsync()
    {
        await CoreManager.Instance.CoreStop();
        foreach (var name in new[] { Global.CoreConfigFileName, Global.CorePreConfigFileName })
        {
            try
            {
                var path = Utils.GetBinConfigPath(name);
                if (File.Exists(path)) File.Delete(path);
            }
            catch
            {
                // Best effort; the folder is readable by its owner only.
            }
        }
    }

    private static string FirstNonEmpty(params string?[] values)
    {
        return values.FirstOrDefault(value => !string.IsNullOrWhiteSpace(value)) ?? "";
    }

    private static string FriendlyConnectionError(Exception ex)
    {
        var loc = Loc.I;
        if (ex is ColituConnectException connect) return connect.Message;
        if (ex is ColituApiException api) return IsNetworkFailure(api) ? loc["err.network"] : api.Message;
        if (IsNetworkFailure(ex)) return loc["err.network"];
        var message = ex.Message;
        if (message.Contains("core executable", StringComparison.OrdinalIgnoreCase)) return loc["err.core"];
        if (message.Contains("adapter or route", StringComparison.OrdinalIgnoreCase)) return loc["err.tun"];
        if (message.Contains("still in use", StringComparison.OrdinalIgnoreCase)) return loc["err.port"];
        if (message.Contains("System proxy could not", StringComparison.OrdinalIgnoreCase)) return loc["err.proxy"];
        if (message.Contains("Server config", StringComparison.OrdinalIgnoreCase) || message.Contains("transport supported", StringComparison.OrdinalIgnoreCase)) return loc["err.noServers"];
        return loc["err.generic"];
    }

    private static string? NormalizeCountryCode(string? countryCode, string? countryName, string? serverName)
    {
        var code = (countryCode ?? "").Trim().ToUpperInvariant();
        if (string.Equals(code, "UK", StringComparison.OrdinalIgnoreCase))
        {
            code = "GB";
        }
        if (code.Length == 2 && code.All(ch => ch is >= 'A' and <= 'Z')) return code;

        var lookup = FirstNonEmpty(countryName, serverName).Trim().ToLowerInvariant();
        if (lookup.Length == 0) return null;

        var known = new Dictionary<string, string>
        {
            ["argentina"] = "AR",
            ["australia"] = "AU",
            ["austria"] = "AT",
            ["belgium"] = "BE",
            ["brazil"] = "BR",
            ["bulgaria"] = "BG",
            ["canada"] = "CA",
            ["denmark"] = "DK",
            ["finland"] = "FI",
            ["france"] = "FR",
            ["germany"] = "DE",
            ["hong kong"] = "HK",
            ["india"] = "IN",
            ["ireland"] = "IE",
            ["italy"] = "IT",
            ["japan"] = "JP",
            ["netherlands"] = "NL",
            ["norway"] = "NO",
            ["poland"] = "PL",
            ["romania"] = "RO",
            ["singapore"] = "SG",
            ["spain"] = "ES",
            ["sweden"] = "SE",
            ["switzerland"] = "CH",
            ["turkey"] = "TR",
            ["turkiye"] = "TR",
            ["türkiye"] = "TR",
            ["united kingdom"] = "GB",
            ["uk"] = "GB",
            ["united states"] = "US",
            ["usa"] = "US"
        };

        return known.FirstOrDefault(item => lookup.Contains(item.Key, StringComparison.OrdinalIgnoreCase)).Value;
    }

    public static string? NormalizeCountryCodeForApi(string? countryCode, string? countryName, string? serverName)
    {
        return NormalizeCountryCode(countryCode, countryName, serverName);
    }

    private void LogConnection(string message)
    {
        Logging.SaveLog($"ColituVpnService | {message}");
    }

    private void SetStatus(ColituVpnStatus status)
    {
        if (status != Status && status is ColituVpnStatus.Connected or ColituVpnStatus.Disconnected or ColituVpnStatus.Error)
        {
            ResetDirectConnections();
        }
        Status = status;
        _session = _session with { Status = status, ConnectedAt = ConnectedAt };
        SaveState();
        StatusChanged?.Invoke(status);
    }

    private const int CurrentPreferencesMigration = 1;

    private void LoadState()
    {
        try
        {
            if (!File.Exists(StatePath()))
            {
                // Fresh install: privacy mode (all traffic through the VPN) is the default. A saved state
                // without the property keeps the old behaviour (the record default stays false).
                _session = _session with { PreferencesMigration = CurrentPreferencesMigration, Preferences = ColituVpnPreferences.ForNewInstall() };
                return;
            }
            _session = JsonSerializer.Deserialize<ColituVpnSession>(File.ReadAllText(StatePath()), _jsonOptions) ?? new();
            // Before SaveState below, which writes the memory back (expired entries pruned).
            _adaptive.Load(_session.AdaptiveMemory, DateTimeOffset.UtcNow);
            // SaveState below writes this run's status (Disconnected) over it.
            _statusAtLastExit = _session.Status;
            if (_session.PreferencesMigration < 1)
            {
                // 1.6.5 made Kill Switch and Auto-Connect on by default; apply once to states saved by older builds.
                _session = _session with
                {
                    Preferences = _session.Preferences with { KillSwitchEnabled = true, AutoConnectEnabled = true }
                };
            }
            _session = _session with
            {
                Preferences = _session.Preferences.Normalize(),
                PreferencesMigration = CurrentPreferencesMigration
            };
            SaveState();
        }
        catch
        {
            _session = new ColituVpnSession { PreferencesMigration = CurrentPreferencesMigration };
        }
    }

    private void SaveState()
    {
        _session = _session with
        {
            Status = Status,
            SelectedServerId = SelectedServer?.Id ?? _session.SelectedServerId,
            ConnectedAt = ConnectedAt,
            SelectionMode = _session.SelectionMode,
            Preferences = _session.Preferences.Normalize(),
            AdaptiveMemory = _adaptive.Snapshot(DateTimeOffset.UtcNow)
        };
        try
        {
            File.WriteAllText(StatePath(), JsonSerializer.Serialize(_session, _jsonOptions));
        }
        catch (Exception ex)
        {
            // A locked or unwritable file must not break connecting or status updates.
            Logging.SaveLog("ColituVpnService.SaveState", ex);
        }
    }

    private static string StatePath() => Utils.GetConfigPath("colitu-vpn-state.json");

    private sealed record ColituTrafficProbeResult(bool Success, string Detail, int? LatencyMs = null);
}

public enum ColituVpnStatus
{
    Disconnected,
    Connecting,
    Connected,
    Reconnecting,
    Error
}

/// <summary>The account has no active plan; the UI sends the user to the plan page.</summary>
public sealed class ColituPlanRequiredException() : Exception(Loc.I["err.noPlan"]);

/// <summary>The panel paused this device (DEVICE_OVER_LIMIT); the UI shows the paused screen.</summary>
public sealed class ColituDevicePausedException(ColituDevicePause pause) : Exception(Loc.I["err.devicePaused"])
{
    public ColituDevicePause Pause { get; } = pause;
}

/// <summary>TUN mode needs the sudo password before the core can start; the UI asks for it and retries.</summary>
public sealed class ColituSudoRequiredException() : Exception(Loc.I["err.admin"]);

/// <summary>A connection failure whose message is already localized for the user.</summary>
public sealed class ColituConnectException(string message, Exception? inner = null, bool offline = false) : Exception(message, inner)
{
    /// <summary>The device itself had no internet: no server or transport is to blame.</summary>
    public bool Offline { get; } = offline;
}

public sealed class ColituDashboardData
{
    public ColituAccount? Account { get; init; }
    public ColituServersResponse Servers { get; init; } = new();
    public ColituStatsResponse Stats { get; init; } = new();
    public ColituVpnServer? SelectedServer { get; init; }
    public ColituVpnStatus Status { get; init; }
}

public sealed class ColituServersResponse
{
    public bool ActiveSubscription { get; set; }
    public bool PremiumAllowed { get; set; }
    public bool Unlimited { get; set; }
    public string? Tier { get; set; }
    public ColituFreeQuota? FreeQuota { get; set; }
    public List<ColituVpnServer> Servers { get; set; } = [];
    /// <summary>Multihop (double VPN) routes; empty when the panel offers none.</summary>
    public List<ColituVpnServer> Multihop { get; set; } = [];
    public ColituVpnServer? SelectedServer { get; set; }
    /// <summary>ISO-2 country of this device's IP as the panel saw it; null when unknown (or the VPN was on).</summary>
    public string? ClientCountry { get; set; }
    /// <summary>The panel's opaque key of this device's ISP network; null when unknown.</summary>
    public string? ClientNetwork { get; set; }
    /// <summary>Opaque token of that network for protocol observations; null when unknown.</summary>
    public string? NetworkToken { get; set; }
    /// <summary>Protocols the network hints call blocked on that network (empty: none).</summary>
    public List<string> NetworkHintsBlocked { get; set; } = [];
    /// <summary>Protocols that worked for most devices on that network, best first (Adaptive Connect 3.0).</summary>
    public List<string> NetworkHintsPreferred { get; set; } = [];
    /// <summary>The panel refused the list because the account has no active plan.</summary>
    public bool PlanRequired { get; set; }
}

public sealed class ColituVpnServer
{
    public string? Id { get; set; }
    public string? LocationId { get; set; }
    public string? Name { get; set; }
    public string? DisplayName { get; set; }
    public string? City { get; set; }
    public string? Region { get; set; }
    public string? CountryCode { get; set; }
    public string? Country { get; set; }
    public bool Premium { get; set; }
    public bool Free { get; set; }
    public bool? AllowFree { get; set; }
    public bool? AllowPremium { get; set; }
    public bool Available { get; set; } = true;
    public bool Locked { get; set; }
    public string? RequiredPlan { get; set; }
    public bool Hidden { get; set; }
    public bool RetainedHiddenSelection { get; set; }
    public bool Online { get; set; }
    public int? Ping { get; set; }
    public bool IsPingLoading { get; set; }
    public bool PingFailed { get; set; }
    public bool PingEstimated { get; set; }
    public double? Load { get; set; }
    public string? Host { get; set; }
    public int? Port { get; set; }
    public string? HealthCheckedAt { get; set; }
    public string? TestUrl { get; set; }
    /// <summary>Use-case categories from the panel (streaming, gaming, privacy, speed, torrent, ai).</summary>
    public List<string> Categories { get; set; } = [];
    /// <summary>Service tags from the panel (chatgpt, netflix, youtube_adfree...); only <c>youtube_adfree</c> is shown here, unknown keys are ignored.</summary>
    public List<string> Services { get; set; } = [];
    public bool HasAdFreeYoutube => Services.Contains("youtube_adfree");
    /// <summary>A multihop route (double VPN): <see cref="Entry"/> node, then <see cref="Exit"/> node. The list item's id is the route id.</summary>
    public bool IsMultihop { get; set; }
    public string? RouteSlug { get; set; }
    public ColituRouteEndpoint? Entry { get; set; }
    public ColituRouteEndpoint? Exit { get; set; }
    public string Quality => PingQuality(Ping);
    public string PingDisplay => IsPingLoading
        ? "Checking..."
        : Ping is > 0
            ? $"{Ping.Value} ms"
            : "— ms";
    public int PingSortValue => Ping is > 0 ? Ping.Value : 9999;
    public string PingColor => Ping is > 0
        ? Ping <= 80 ? "#00E5A0" : Ping <= 200 ? "#FFB547" : "#FF6B81"
        : "#5A6896";
    /// <summary>Locations list section header; assigned when the list is built.</summary>
    public string? GroupLabel { get; set; }
    public string PlanLabel => Locked ? "LOCKED" : Load switch
    {
        <= 40 => "LOW LOAD",
        <= 70 => "MEDIUM LOAD",
        > 70 => "HIGH LOAD",
        _ => "ONLINE"
    };
    public string Location => string.Join(", ", new[] { City, Country }.Where(item => !string.IsNullOrWhiteSpace(item)));
    public string FlagCode
    {
        get
        {
            var code = (CountryCode ?? "").Trim().ToUpperInvariant();
            if (string.Equals(code, "UK", StringComparison.OrdinalIgnoreCase))
            {
                code = "GB";
            }
            return IsValidCountryCode(code) ? code.ToLowerInvariant() : "xx";
        }
    }
    public Uri FlagResourceUri => new($"pack://application:,,,/flags/{FlagCode}.svg", UriKind.Absolute);
    public bool HasLocalFlag => FlagCode != "xx";
    public string FlagEmoji => CountryFlag(FlagCode.ToUpperInvariant());
    public bool HasFlagImage => HasLocalFlag;
    public string FlagFallbackText => HasLocalFlag ? FlagEmoji : "--";
    public string? FlagSvgUrl => FlagResourceUri.ToString();
    public string? FlagImageUrl => null;

    public void ApplyPing(int? ping, bool measured = true)
    {
        if (ping is > 0)
        {
            Ping = ping;
            PingFailed = false;
            PingEstimated = !measured;
            return;
        }

        Ping = null;
        PingFailed = !measured;
        PingEstimated = false;
    }

    private static string PingQuality(int? ping)
    {
        if (ping is null or <= 0) return "Unknown";
        if (ping <= 80) return "Excellent";
        if (ping <= 200) return "Good";
        return "High";
    }

    private static string CountryFlag(string? countryCode)
    {
        var code = (countryCode ?? "").Trim().ToUpperInvariant();
        if (!IsValidCountryCode(code)) return "??";

        var chars = code
            .SelectMany(ch => char.ConvertFromUtf32(0x1F1E6 + ch - 'A'))
            .ToArray();
        return new string(chars);
    }

    private static bool IsValidCountryCode(string? countryCode)
    {
        var code = (countryCode ?? "").Trim().ToUpperInvariant();
        return code.Length == 2 && code.All(ch => ch is >= 'A' and <= 'Z');
    }
}

public sealed class ColituVpnConfigResponse
{
    public string? ServerId { get; set; }
    public ColituVpnServer? Server { get; set; }
    public string? ConfigType { get; set; }
    public string? ProtocolType { get; set; }
    /// <summary>Share links for every candidate transport, primary first.</summary>
    public string? RawConfig { get; set; }
    public List<ColituConfigCandidate> Candidates { get; set; } = [];
    public ulong Revision { get; set; }
    public string? ExpiresAt { get; set; }
    public DateTimeOffset? OfflineGraceUntil { get; set; }
    public bool Unlimited { get; set; }
}

public sealed class ColituConnectResponse
{
    public bool Ok { get; set; }
    public string? Status { get; set; }
    public ColituVpnServer? SelectedServer { get; set; }
}

public sealed class ColituStatsResponse
{
    public bool Ok { get; set; }
    public ColituSubscription? Subscription { get; set; }
    public bool Unlimited { get; set; }
    public ColituUsageStats Stats { get; set; } = new();
}

public sealed class ColituUsageStats
{
    public long TotalUsedBytes { get; set; }
    public long TotalConnectedSeconds { get; set; }
    [JsonConverter(typeof(FlexibleIntJsonConverter))]
    public int ServersUsed { get; set; }
    public ColituUsageDay Today { get; set; } = new();
    public List<ColituUsageDay> Days { get; set; } = [];
}

public sealed class ColituUsageDay
{
    public string? Date { get; set; }
    public long UsedBytes { get; set; }
    public long ConnectedSeconds { get; set; }
    [JsonConverter(typeof(FlexibleIntJsonConverter))]
    public int ServersUsed { get; set; }
}

public sealed class FlexibleIntJsonConverter : JsonConverter<int>
{
    public override int Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        return reader.TokenType switch
        {
            JsonTokenType.Number => reader.TryGetInt32(out var value) ? value : 0,
            JsonTokenType.String => int.TryParse(reader.GetString(), out var value) ? value : 0,
            JsonTokenType.StartArray => CountArrayItems(ref reader),
            JsonTokenType.Null => 0,
            _ => SkipAndReturnZero(ref reader)
        };
    }

    public override void Write(Utf8JsonWriter writer, int value, JsonSerializerOptions options)
    {
        writer.WriteNumberValue(value);
    }

    private static int CountArrayItems(ref Utf8JsonReader reader)
    {
        var count = 0;
        while (reader.Read())
        {
            if (reader.TokenType == JsonTokenType.EndArray)
            {
                return count;
            }

            count += 1;
            if (reader.TokenType is JsonTokenType.StartArray or JsonTokenType.StartObject)
            {
                reader.Skip();
            }
        }

        return count;
    }

    private static int SkipAndReturnZero(ref Utf8JsonReader reader)
    {
        if (reader.TokenType is JsonTokenType.StartArray or JsonTokenType.StartObject)
        {
            reader.Skip();
        }

        return 0;
    }
}

public sealed class ColituBestServerResponse
{
    public bool Ok { get; set; }
    public ColituVpnServer? Server { get; set; }
    public int Score { get; set; }
}

public sealed class ColituPingEntry
{
    public string? ServerId { get; set; }
    public int? Ping { get; set; }
    public string? Quality { get; set; }
    public bool Online { get; set; }
    public string? MeasuredAt { get; set; }
}

public sealed class ColituPingAllResponse
{
    public bool Ok { get; set; }
    public string? RequestId { get; set; }
    public List<ColituPingEntry> Pings { get; set; } = [];
}

public sealed record ColituVpnPreferences(
    bool KillSwitchEnabled = true,
    bool DnsLeakProtectionEnabled = true,
    bool AutoConnectEnabled = true,
    bool SplitTunnelingEnabled = false,
    List<string>? SplitTunnelApps = null,
    string? PreviousRoutingId = null,
    string Language = "",
    string ConnectionMode = ColituConnectionModes.Proxy,
    bool CloseToTray = true,
    bool AdBlockEnabled = false,
    string SplitTunnelMode = ColituSplitTunnelModes.Off,
    List<string>? SplitTunnelDomains = null,
    List<string>? SplitTunnelIps = null,
    // Privacy mode: no direct-routing exceptions (Russian sites and addresses go through the tunnel too).
    // Off here so states saved before the setting existed keep their behaviour; new installs start with ForNewInstall() (on).
    bool PrivacyModeEnabled = false,
    // Warm spare: a second path inside the core takes over within seconds when the first one dies.
    bool WarmSpareEnabled = true,
    // Advanced mode shows split tunnelling, connection mode, kill switch and the other expert settings.
    // On here, so users updating from a version without the setting keep what they saw; new installs
    // start in Simple mode (ForNewInstall).
    bool AdvancedMode = true)
{
    /// <summary>
    /// Preferences of a first run (no saved state yet): privacy mode on, Simple mode. The record
    /// defaults stay as they were for saved states that predate the settings.
    /// </summary>
    public static ColituVpnPreferences ForNewInstall() => new() { PrivacyModeEnabled = true, AdvancedMode = false };

    public bool IsTunMode => string.Equals(ConnectionMode, ColituConnectionModes.Tun, StringComparison.OrdinalIgnoreCase);

    public ColituVpnPreferences Normalize()
    {
        var language = Loc.Normalize(Language);
        var connectionMode = string.Equals(ConnectionMode?.Trim(), ColituConnectionModes.Tun, StringComparison.OrdinalIgnoreCase)
            ? ColituConnectionModes.Tun
            : ColituConnectionModes.Proxy;
        var splitMode = ColituSplitTunnelModes.Normalize(SplitTunnelMode);

        return this with
        {
            // Saved lists are validated again: they end up in core configs and root's nftables.
            SplitTunnelApps = ColituSplitTunnel.CleanApps(SplitTunnelApps),
            SplitTunnelDomains = ColituSplitTunnel.CleanDomains(SplitTunnelDomains),
            SplitTunnelIps = ColituSplitTunnel.CleanNetworks(SplitTunnelIps),
            SplitTunnelMode = splitMode,
            SplitTunnelingEnabled = splitMode != ColituSplitTunnelModes.Off,
            Language = language,
            ConnectionMode = connectionMode
        };
    }

    /// <summary>Split-tunneling settings that end up in the core config (a change needs a reconnect).</summary>
    public bool SameSplitTunnel(ColituVpnPreferences other)
    {
        var a = Normalize();
        var b = other.Normalize();
        return a.SplitTunnelMode == b.SplitTunnelMode
            && a.SplitTunnelApps!.SequenceEqual(b.SplitTunnelApps!)
            && a.SplitTunnelDomains!.SequenceEqual(b.SplitTunnelDomains!)
            && a.SplitTunnelIps!.SequenceEqual(b.SplitTunnelIps!);
    }
}

/// <summary>The warm spare planned for the next core start: which primary profile it backs and with what.</summary>
internal sealed record ColituSparePlan(string PrimaryIndexId, ProfileItem Spare, string Protocol, string? ServerId, string Reason = "", string? NextServerId = null);

/// <summary>Another server's transports for the warm spare (automatic mode).</summary>
internal sealed record ColituSpareServer(string ServerId, List<(string Protocol, ProfileItem Item)> Items, ColituVpnConfigResponse Config, bool Cached);

/// <summary>Parallel connect: only the spare on the next server carried traffic; it becomes the primary.</summary>
internal sealed class ColituSpareServerWins(ColituSpareServer spare, string protocol)
    : Exception($"the warm spare {spare.ServerId}/{protocol} carried traffic, the primary did not")
{
    public ColituSpareServer Spare { get; } = spare;
    public string Protocol { get; } = protocol;
}

/// <summary>The spare server's settings on their way.</summary>
internal sealed record ColituSpareFetch(string ServerId, Task<ColituSpareServer?> Task);

public sealed record ColituVpnSession
{
    public ColituVpnStatus Status { get; init; } = ColituVpnStatus.Disconnected;
    public string? SelectedServerId { get; init; }
    public DateTimeOffset? ConnectedAt { get; init; }
    public string SelectionMode { get; init; } = ColituServerSelectionModes.Manual;
    public ColituVpnPreferences Preferences { get; init; } = new();
    /// <summary>Highest one-time preference migration applied to this saved state.</summary>
    public int PreferencesMigration { get; init; }
    /// <summary>The one-time "protect all traffic (TUN)" offer was answered (or not needed).</summary>
    public bool TunOfferAnswered { get; init; }
    /// <summary>Local date (yyyy-MM-dd) the plan-end banner was last closed.</summary>
    public string? PlanNoticeDismissedOn { get; init; }
    /// <summary>Last non-empty <c>client_country</c> of the server list (the panel sends none through the VPN).</summary>
    public string? ClientCountry { get; init; }
    /// <summary>Last non-empty <c>client_network</c> of the server list; part of the Adaptive Connect network key.</summary>
    public string? ClientNetwork { get; init; }
    /// <summary>Adaptive Connect memory (expiring, per network).</summary>
    public List<ColituAdaptiveEntry>? AdaptiveMemory { get; init; }
    /// <summary>Network hints: the last network token, the client_network it belongs to and when it came.</summary>
    public string? NetworkToken { get; init; }
    public string? NetworkTokenNetwork { get; init; }
    public DateTimeOffset? NetworkTokenAt { get; init; }
    /// <summary>Protocols blocked on <see cref="NetworkHintsNetwork"/> according to the panel.</summary>
    public List<string>? NetworkHintsBlocked { get; init; }
    /// <summary>Protocols that worked for most devices on <see cref="NetworkHintsNetwork"/>, best first (stored with the blocked ones).</summary>
    public List<string>? NetworkHintsPreferred { get; init; }
    public string? NetworkHintsNetwork { get; init; }
    /// <summary>Recovery set: the last fetch attempt (at most one per 6 h) and the account it was made for.</summary>
    public DateTimeOffset? RecoveryAttemptAt { get; init; }
    public string? RecoveryAttemptUser { get; init; }
}

public static class ColituServerSelectionModes
{
    public const string Manual = "manual";
    public const string Best = "best";
}

public static class ColituConnectionModes
{
    public const string Proxy = "proxy";
    public const string Tun = "tun";
}
