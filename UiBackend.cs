using System.Text.Json;

namespace NetPilot;

internal interface IUiServiceConnection
{
    bool IsInstalled { get; }
    Task<RpcReply> SendAsync(RpcRequest request, CancellationToken token);
    Task InstallAsync();
    Task RecoverAsync();
}

internal sealed class WindowsUiServiceConnection : IUiServiceConnection
{
    public bool IsInstalled => ServiceClient.IsInstalled;
    public Task<RpcReply> SendAsync(RpcRequest request, CancellationToken token) => ServiceClient.SendAsync(request, token);
    public Task InstallAsync() => ServiceClient.InstallAsync();
    public Task RecoverAsync() => ServiceClient.RecoverAsync();
}

internal sealed class PendingUiConfiguration
{
    [System.Text.Json.Serialization.JsonRequired]
    public AppConfig Config { get; set; } = null!;
    public bool MergeServiceProfiles { get; set; }
}

/// <summary>Connects the unprivileged window to either the service or a probe-only engine.</summary>
public sealed class UiBackend : IAsyncDisposable
{
    private readonly string _dataDirectory;
    private readonly string _configPath;
    private readonly string _pendingPath;
    private readonly IUiServiceConnection _service;
    private readonly Func<DateTimeOffset> _clock;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private MonitorEngine? _local;
    private AppConfig _config;
    private bool _localRequested = true;
    private bool _connected;
    private bool _everConnected;
    private bool _disposed;
    private bool _pendingConfiguration;
    private bool _mergeServiceProfiles;
    private string? _loadWarning;
    private string? _syncWarning;
    private int _connectionFailures;
    private AppStatus? _lastServiceStatus;

    public AppStatus Status { get; private set; }
    internal string DataDirectory=>Path.GetFullPath(_dataDirectory);
    public bool ServiceConnected => _connected;
    public bool ServiceInstalled => _service.IsInstalled;
    public DateTimeOffset? LastServiceContact {get;private set;}
    public DateTimeOffset? NextServiceRetry {get;private set;}
    public bool ShowingLocalDiagnostics {get;private set;}
    public bool DnsStateUnknown { get; private set; }
    public bool RecoveryConfirmed { get; private set; }
    public string? ConnectionError { get; private set; }
    public bool PendingServiceConfiguration => _connected && _pendingConfiguration;
    public bool ServiceNeedsUpdate => _connected && (!Version.TryParse(Status.ServiceVersion, out var installed) || installed < Version.Parse(AppVersion.Current));
    public bool ServiceUpdateAvailable => _service.IsInstalled && (!_connected || ServiceNeedsUpdate);
    public string? ConfigurationWarning => string.Join("\r\n", new[] { _loadWarning, _syncWarning,
        ServiceNeedsUpdate ? "Требуется обновление службы. Нажмите «Обновить службу»; каналы и история сохраняются, управление DNS после обновления будет выключено." : null }.Where(s => !string.IsNullOrWhiteSpace(s)));

    public UiBackend(string dataDirectory) : this(dataDirectory, new WindowsUiServiceConnection(), includeBundledChannels: true) { }

    internal UiBackend(string dataDirectory, IUiServiceConnection service, bool includeBundledChannels = false,Func<DateTimeOffset>? clock=null)
    {
        _service = service;
        _clock=clock??(()=>DateTimeOffset.UtcNow);
        _dataDirectory = dataDirectory;
        Directory.CreateDirectory(dataDirectory);
        _configPath = Path.Combine(dataDirectory, "ui-config.json");
        _pendingPath = Path.Combine(dataDirectory, "ui-config.pending.json");
        try
        {
            _config = AtomicStore.Read<AppConfig>(_configPath) ?? AppConfig.Defaults();
            _config.Validate();
        }
        catch (Exception ex)
        {
            _config = AppConfig.Defaults();
            _loadWarning = "Не удалось прочитать настройки. Загружены начальные каналы. " + ex.Message;
        }
        try
        {
            if (AtomicStore.Read<PendingUiConfiguration>(_pendingPath) is { } pending)
            {
                if (pending.Config is null) throw new InvalidDataException("Нет ожидающей конфигурации.");
                pending.Config.Validate();
                _config = pending.Config;
                _pendingConfiguration = true;
                _mergeServiceProfiles = pending.MergeServiceProfiles;
            }
        }
        catch (Exception ex)
        {
            _loadWarning = (_loadWarning is null ? "" : _loadWarning + "\r\n") + "Не удалось прочитать ожидающие применения настройки. Основная локальная база сохранена. " + ex.Message;
        }
        // A local settings file is never authorization to mutate Windows DNS.
        _config.ManagementRequested = false;
        // Persist default identities on first launch too: statistics refer to these IDs.
        // An existing corrupt file remains untouched for recovery.
        if (!File.Exists(_configPath) && _loadWarning is null) AtomicStore.Write(_configPath, _config);
        Status = new AppStatus { Config = JsonData.Clone(_config), State = "Наблюдение ещё не запускалось" };
        if (includeBundledChannels && _loadWarning is null)
        {
            try
            {
                var marker = Path.Combine(dataDirectory, "bundled-channels.json");
                if (AtomicStore.Read<string>(marker) != BundledChannels.Revision)
                {
                    var expanded = BundledChannels.AddMissing(_config, out int added);
                    if (added > 0) StageConfiguration(expanded, mergeServiceProfiles: !_pendingConfiguration || _mergeServiceProfiles);
                    AtomicStore.Write(marker, BundledChannels.Revision);
                }
            }
            catch (Exception ex)
            { _loadWarning = "Не удалось дополнить каналы из поставки. Исходные каналы сохранены. " + ex.Message; }
        }
    }

    public async Task<AppStatus> ReconnectAsync()
    {
        await _gate.WaitAsync();
        try {NextServiceRetry=null;}finally{_gate.Release();}
        return await RefreshAsync();
    }

    public async Task<HistoryReport> ReadHistoryAsync(HistoryQuery query,CancellationToken token=default)
    {
        ArgumentNullException.ThrowIfNull(query);
        var requested=JsonData.Clone(query);requested.Validate();
        using var timeout=CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(TimeSpan.FromSeconds(3));
        bool entered=false;
        try
        {
            await _gate.WaitAsync(timeout.Token);entered=true;
            ObjectDisposedException.ThrowIf(_disposed,this);
            if(_connected)
            {
                if(!Version.TryParse(Status.ServiceVersion,out var version)||version<new Version(0,1,5))
                    throw new InvalidOperationException("Для графиков по времени нажмите «Обновить службу». Старая служба не передаёт этот архив.");
                if(_pendingConfiguration && (_lastServiceStatus is null || !SameHistoryContext(_lastServiceStatus.Config,_config)))
                    throw new InvalidOperationException("Каналы или объекты проверок изменены локально. Сначала нажмите «Применить настройки»: история службы относится к прежним настройкам.");
                var reply=await _service.SendAsync(new RpcRequest {Command="history",History=requested},timeout.Token);
                if(!reply.Ok) throw new InvalidOperationException(reply.Error??"Служба не вернула графики. Проверьте связь и версию службы.");
                if(reply.History is null) throw new InvalidOperationException("Служба не поддерживает архив графиков. Нажмите «Обновить службу».");
                if(reply.History.Query is null || reply.History.Channels is null
                    || reply.History.Query.PeriodMinutes!=requested.PeriodMinutes || reply.History.Query.BucketMinutes!=requested.BucketMinutes
                    || reply.History.MonitoringKey!=MonitoringTargets.Key(_config)
                    || reply.History.Channels.Any(series=>series is null || !_config.Profiles.Any(profile=>profile.Id==series.ProfileId
                        && ChartHistoryArchive.ContextKey(profile,reply.History.MonitoringKey)==series.ContextKey)))
                    throw new InvalidOperationException("Настройки или период истории службы изменились. Обновите состояние и повторите открытие графиков.");
                return JsonData.Clone(reply.History);
            }
            if(DnsStateUnknown || (!RecoveryConfirmed && (_service.IsInstalled || _everConnected)))
                throw new InvalidOperationException("Нет подтверждённой связи со службой. Её архив графиков недоступен; нажмите «Проверить связь» или «Исправить службу».");
            if(_local is null) throw new InvalidOperationException("Локальное наблюдение ещё не запущено. Дождитесь обновления состояния.");
            return _local.GetHistory(requested);
        }
        catch(OperationCanceledException) when(!token.IsCancellationRequested)
        {throw new TimeoutException("Служба не вернула графики за 3 секунды. Нажмите «Проверить связь» или повторите открытие журнала.");}
        catch(IOException ex)
        {throw new InvalidOperationException("Не удалось прочитать архив графиков службы. Нажмите «Проверить связь». "+ex.Message,ex);}
        finally {if(entered)_gate.Release();}
    }

    private static bool SameHistoryContext(AppConfig first,AppConfig second)
        => MonitoringTargets.Key(first)==MonitoringTargets.Key(second)
            && first.Profiles.Count==second.Profiles.Count
            && first.Profiles.All(profile=>second.Profiles.Any(other=>profile.Id==other.Id
                && profile.Enabled==other.Enabled && AppConfig.SameEffectiveConfiguration(profile,other)));

    public async Task<DnsShareOffer> ReadShareCandidatesAsync(CancellationToken token=default)
    {
        using var timeout=CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(TimeSpan.FromSeconds(3));
        bool entered=false;
        try
        {
            await _gate.WaitAsync(timeout.Token);entered=true;
            ObjectDisposedException.ThrowIf(_disposed,this);
            if(_connected)
            {
                if(!Version.TryParse(Status.ServiceVersion,out var version)||version<new Version(0,1,5))
                    throw new InvalidOperationException("Для подготовки DNS к отправке нажмите «Обновить службу». Старая служба не передаёт эти данные.");
                if(_pendingConfiguration && (_lastServiceStatus is null || !SameHistoryContext(_lastServiceStatus.Config,_config)))
                    throw new InvalidOperationException("Каналы или объекты проверок изменены локально. Сначала нажмите «Применить настройки»: результаты службы относятся к прежним настройкам.");
                var reply=await _service.SendAsync(new RpcRequest {Command="share-candidates"},timeout.Token);
                if(!reply.Ok) throw new InvalidOperationException(reply.Error??"Служба не вернула каналы для отправки. Проверьте связь и версию службы.");
                if(reply.Share is null) throw new InvalidOperationException("Служба не поддерживает подготовку DNS к отправке. Нажмите «Обновить службу».");
                return JsonData.Clone(reply.Share);
            }
            if(DnsStateUnknown || (!RecoveryConfirmed && (_service.IsInstalled || _everConnected)))
                throw new InvalidOperationException("Нет подтверждённой связи со службой. Её результаты недоступны; нажмите «Проверить связь» или «Исправить службу».");
            if(_local is null) throw new InvalidOperationException("Локальное наблюдение ещё не запущено. Дождитесь обновления состояния.");
            return _local.GetShareCandidates();
        }
        catch(OperationCanceledException) when(!token.IsCancellationRequested)
        {throw new TimeoutException("Служба не вернула каналы для отправки за 3 секунды. Нажмите «Проверить связь».");}
        catch(IOException ex)
        {throw new InvalidOperationException("Не удалось прочитать результаты службы для отправки DNS. Нажмите «Проверить связь». "+ex.Message,ex);}
        finally {if(entered)_gate.Release();}
    }

    public async Task<AppStatus> RefreshAsync()
    {
        await _gate.WaitAsync();
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            RpcReply? reply = null;
            var installed = _service.IsInstalled;
            bool attempted=false;
            if ((installed || _connected || _everConnected) && (NextServiceRetry is null || _clock()>=NextServiceRetry))
            {
                attempted=true;
                try
                {
                    using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
                    reply = await _service.SendAsync(new RpcRequest { Command = "status" }, timeout.Token);
                    if (!reply.Ok || reply.Status is null)
                        ConnectionError = reply.Error ?? "Служба не вернула состояние.";
                }
                catch (Exception ex) { ConnectionError = ex is TimeoutException or OperationCanceledException
                    ? "Служба не ответила вовремя. Можно обновить её или отключить управление через помощник Windows."
                    : ex.Message; }
            }
            if (reply is { Ok: true, Status: not null })
            {
                await AdoptServiceAsync(reply.Status);
                return Status;
            }
            if(attempted)
            {
                _connectionFailures=Math.Min(_connectionFailures+1,4);
                NextServiceRetry=_clock().AddSeconds(new[] {2,5,10,30}[_connectionFailures-1]);
            }

            _connected = false;
            DnsStateUnknown = (installed || _everConnected) && !RecoveryConfirmed;
            await EnsureLocalAsync();
            Status = DnsStateUnknown && !ShowingLocalDiagnostics && _lastServiceStatus is not null
                ? ProfileSynchronization.WithDraft(_lastServiceStatus,_config)
                : _local!.GetStatus();
            Status.ServiceConnected = false;
            Status.SystemDnsManaged = false;
            Status.Config.ManagementRequested = false;
            if(DnsStateUnknown) {Status.Monitoring=false;Status.ActiveProfileId=null;}
            if(DnsStateUnknown && !ShowingLocalDiagnostics && _lastServiceStatus is null) Status.Statistics=[];
            if (Status.Adapters.Count == 0) Status.Adapters = AdapterCatalog.Read();
            return Status;
        }
        finally { _gate.Release(); }
    }

    public async Task ConfigureAsync(AppConfig config)
    {
        config = JsonData.Clone(config);
        config.Validate();
        await _gate.WaitAsync();
        try
        {
            // Persist the user's intention before an RPC can fail or commit without
            // delivering its reply. Refresh must never replace this draft with defaults.
            StageConfiguration(config);
            if(_connected && config.AutoSelection==AutoSelectionMode.Fast
                && (!Version.TryParse(Status.ServiceVersion,out var automaticVersion)||automaticVersion<new Version(0,1,6)))
                throw new InvalidOperationException("Режим «Самый быстрый» сохранён локально. Нажмите «Обновить службу», затем «Применить настройки»: установленная служба ещё не поддерживает выбор приоритета.");
            if(_connected && ServiceNeedsUpdate && (config.MonitorGemini || config.MonitorTailscale || !config.MonitorChatGpt || config.AdditionalTargets.Count>0 || config.ManualProfileId is not null))
                throw new InvalidOperationException("Настройки сохранены локально. Для новых проверок и ручной фиксации нажмите «Обновить службу», затем «Применить настройки».");
            if (_connected)
                await CommandAsync(new RpcRequest { Command = "configure", Config = JsonData.Clone(_config) });
            else
            {
                config.ManagementRequested = false;
                await EnsureLocalAsync();
                await _local!.ConfigureAsync(config);
                _config = config;
                Status = _local.GetStatus();
            }
            SaveConfig();
        }
        finally { _gate.Release(); }
    }

    public async Task SetMonitoringAsync(bool enabled)
    {
        await _gate.WaitAsync();
        try
        {
            if (_connected)
            {
                if(ServiceNeedsUpdate) throw new InvalidOperationException("Для паузы проверок службы нажмите «Обновить службу». После обновления пауза будет работать без прав администратора.");
                await CommandAsync(new RpcRequest {Command=enabled?"resume":"pause"});
                return;
            }
            if(DnsStateUnknown) throw new InvalidOperationException("Нет ответа фоновой службы. Её паузу подтвердить нельзя: нажмите «Проверить связь», «Исправить службу» или «Отключить управление».");
            _localRequested = enabled;
            await EnsureLocalAsync();
            await _local!.SetMonitoringAsync(enabled);
            Status = _local.GetStatus();
            _config=JsonData.Clone(Status.Config);SaveConfig();
        }
        finally { _gate.Release(); }
    }

    public async Task ProbeAsync()
    {
        await _gate.WaitAsync();
        try
        {
            if (_connected) await CommandAsync(new RpcRequest { Command = "probe" });
            else
            {
                await EnsureLocalAsync();
                if(DnsStateUnknown) ShowingLocalDiagnostics=true;
                await _local!.RequestProbeAsync();
                Status = _local.GetStatus();
                if(DnsStateUnknown) {Status.ActiveProfileId=null;Status.SystemDnsManaged=false;Status.Monitoring=false;}
            }
        }
        finally { _gate.Release(); }
    }

    public Task SelectAsync(string? profileId)
    {
        if(DnsStateUnknown) throw new InvalidOperationException("Нет подтверждённой связи со службой. Сначала нажмите «Проверить связь» или «Исправить службу», затем меняйте выбор канала.");
        var config = JsonData.Clone(Status.Config);
        config.PinnedProfileId = profileId;
        config.ManualProfileId=null;config.ManualUntil=null;
        return ConfigureAsync(config);
    }

    public Task SelectAutomaticAsync(AutoSelectionMode mode)
    {
        if(!Enum.IsDefined(mode)) throw new ArgumentException("Выберите режим автовыбора: самый стабильный или самый быстрый.",nameof(mode));
        if(DnsStateUnknown) throw new InvalidOperationException("Нет подтверждённой связи со службой. Сначала нажмите «Проверить связь» или «Исправить службу», затем меняйте режим автовыбора.");
        var config=JsonData.Clone(Status.Config);
        config.AutoSelection=mode;config.PinnedProfileId=null;config.ManualProfileId=null;config.ManualUntil=null;
        return ConfigureAsync(config);
    }

    public Task FixChannelAsync(string profileId,int? minutes)
    {
        if(DnsStateUnknown) throw new InvalidOperationException("Зафиксировать системный канал без ответа службы нельзя. Нажмите «Проверить связь» или «Исправить службу».");
        if(minutes is <1 or >1440) throw new ArgumentException("Время фиксации: от 1 до 1440 минут.");
        var config=JsonData.Clone(Status.Config);
        config.PinnedProfileId=null;config.ManualProfileId=profileId;
        config.ManualUntil=minutes is { } value?DateTimeOffset.UtcNow.AddMinutes(value):null;
        config.Validate();
        return ConfigureAsync(config);
    }

    public async Task ResetAutomaticDnsAsync(string adapterId)
    {
        if(!Guid.TryParse(adapterId,out _)) throw new ArgumentException("Выберите сетевой адаптер.");
        await _gate.WaitAsync();
        try
        {
            await ServiceClient.ResetAutomaticAsync(adapterId);
            RecoveryConfirmed=true;DnsStateUnknown=false;_connected=false;
            _config.ManagementRequested=false;_config.AdapterId=adapterId;
            SaveConfig();
        }
        finally {_gate.Release();}
        await RefreshAsync();
    }

    // The caller must display the concrete adapter and obtain a direct click confirmation first.
    public async Task EnableManagementAsync(AppConfig config)
    {
        config = JsonData.Clone(config);
        config.ManagementRequested = false;
        config.Validate();
        if (string.IsNullOrWhiteSpace(config.AdapterId) || !config.Profiles.Any(p => p.Enabled))
            throw new InvalidOperationException("Выберите сетевой адаптер и включите хотя бы один канал.");
        await _gate.WaitAsync();
        try
        {
            if (Status.SystemDnsManaged) return;
            StageConfiguration(config);
            if (!_connected || ServiceNeedsUpdate)
            {
                await _service.InstallAsync();
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
                var reply = await _service.SendAsync(new RpcRequest { Command = "status" }, timeout.Token);
                if (!reply.Ok || reply.Status is null)
                    throw new InvalidOperationException(reply.Error ?? "Служба установлена, но не отвечает. Управление DNS не подтверждено.");
                await AdoptServiceAsync(reply.Status);
            }
            // An already-running service may have an active transaction. Keep its authoritative state.
            if (Status.SystemDnsManaged) return;
            await CommandAsync(new RpcRequest { Command = "configure", Config = JsonData.Clone(_config) });
            await CommandAsync(new RpcRequest { Command = "enable" });
            SaveConfig();
        }
        finally { _gate.Release(); }
    }

    // A separate explicit action allows fixing the service even when an old RPC
    // server cannot return status. This never sends configure or enable.
    public async Task UpdateServiceAsync()
    {
        await _gate.WaitAsync();
        try
        {
            if (!_service.IsInstalled) throw new InvalidOperationException("Служба ещё не установлена. Используйте «Включить управление DNS».");
            await _service.InstallAsync();
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            var reply = await _service.SendAsync(new RpcRequest { Command = "status" }, timeout.Token);
            if (!reply.Ok || reply.Status is null) throw new InvalidOperationException(reply.Error ?? "После обновления служба не вернула состояние. Состояние DNS нужно проверить.");
            await AdoptServiceAsync(reply.Status);
            // A retry from an already current package may need no binary replacement.
            // Honor this action's promise to finish in observation mode in that case too.
            if (Status.SystemDnsManaged || Status.DnsRecoveryPending || Status.Config.ManagementRequested)
                await CommandAsync(new RpcRequest { Command = "disable" });
        }
        finally { _gate.Release(); }
    }

    public async Task RestoreAsync()
    {
        bool recoveredOffline = false;
        await _gate.WaitAsync();
        try
        {
            if (!_connected && DnsStateUnknown && _service.IsInstalled)
            {
                // The caller separately confirms this elevated offline recovery.
                await _service.RecoverAsync();
                RecoveryConfirmed = true;
                DnsStateUnknown = false;
                ConnectionError = null;
                _config.ManagementRequested = false;
                recoveredOffline = true;
            }
            else await CommandAsync(new RpcRequest { Command = "disable" });
            SaveConfig();
        }
        finally { _gate.Release(); }
        if (recoveredOffline) await RefreshAsync();
    }

    private async Task CommandAsync(RpcRequest request)
    {
        if (!_connected) throw new InvalidOperationException("Нет соединения со службой. Состояние DNS нужно получить от службы.");
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(145));
        var reply = await _service.SendAsync(request, timeout.Token);
        if (reply.Status is not null) await AdoptServiceAsync(reply.Status);
        if (!reply.Ok) throw new InvalidOperationException(reply.Error ?? "Служба отклонила действие.");
        if (reply.Status is null) throw new InvalidOperationException("Служба не вернула состояние после действия.");
    }

    private async Task AdoptServiceAsync(AppStatus status)
    {
        status.Config.Validate();
        if (!_everConnected)
        {
            try
            {
                var desired = ProfileSynchronization.ForConnection(_config, status.Config, _pendingConfiguration && !_mergeServiceProfiles);
                if (_pendingConfiguration || !ProfileSynchronization.SameConfig(desired, status.Config)) StageConfiguration(desired);
            }
            catch (ArgumentException ex)
            {
                // Conflicting names/settings require an explicit user choice. Keep
                // the local database and preserve the service snapshot for recovery.
                StageConfiguration(_config);
                _syncWarning = "Списки каналов конфликтуют: " + ex.Message + " Локальный список сохранён; снимок службы — service-config.last-seen.json. Проверьте список перед применением.";
            }
        }
        if (_pendingConfiguration && ProfileSynchronization.SameConfig(_config, status.Config))
        {
            _config = JsonData.Clone(status.Config);
            SaveConfig();
            File.Delete(_pendingPath);
            _pendingConfiguration = false;
            _syncWarning = null;
        }
        else if (_pendingConfiguration)
        {
            WriteIfChanged(Path.Combine(_dataDirectory, "service-config.last-seen.json"), status.Config);
            _syncWarning ??= "Каналы сохранены локально. Служба использует другой список; нажмите «Применить настройки» на вкладке «Проверка и сеть» или включите управление DNS.";
        }
        if (_local is not null)
        {
            await _local.StopAsync();
            await _local.DisposeAsync();
            _local = null;
        }
        _connected = _everConnected = true;
        LastServiceContact=_clock();NextServiceRetry=null;_connectionFailures=0;
        _lastServiceStatus=JsonData.Clone(status);
        ShowingLocalDiagnostics=false;
        RecoveryConfirmed = false;
        DnsStateUnknown = false;
        ConnectionError = null;
        Status = _pendingConfiguration ? ProfileSynchronization.WithDraft(status, _config) : status;
        Status.ServiceConnected = true;
        if (!_pendingConfiguration) _config = JsonData.Clone(status.Config);
        SaveConfig();
        if (Status.Adapters.Count == 0) Status.Adapters = AdapterCatalog.Read();
    }

    private async Task EnsureLocalAsync()
    {
        bool allowBackground=!DnsStateUnknown;
        if (_local is not null)
        {
            if(!allowBackground && _local.GetStatus().Monitoring) await _local.SetMonitoringAsync(false);
            else if(allowBackground && _localRequested && !_config.MonitoringPaused && !_local.GetStatus().Monitoring)
                await _local.SetMonitoringAsync(true);
            return;
        }
        var config = JsonData.Clone(_config);
        config.ManagementRequested = false;
        if(!allowBackground) config.MonitoringPaused=true;
        var directory = Path.Combine(_dataDirectory, "observer");
        Directory.CreateDirectory(directory);
        _local = new MonitorEngine(config, directory, serviceMode: false);
        if (_localRequested && allowBackground) await _local.StartAsync();
    }

    private void SaveConfig()
    {
        var saved = JsonData.Clone(_config);
        saved.ManagementRequested = false;
        WriteIfChanged(_configPath, saved);
    }

    private void StageConfiguration(AppConfig config, bool mergeServiceProfiles = false)
    {
        var saved = JsonData.Clone(config);
        saved.ManagementRequested = false;
        WriteIfChanged(_pendingPath, new PendingUiConfiguration { Config = saved, MergeServiceProfiles = mergeServiceProfiles });
        _config = saved;
        _pendingConfiguration = true;
        _mergeServiceProfiles = mergeServiceProfiles;
        SaveConfig();
        if (Status is not null)
        {
            Status = ProfileSynchronization.WithDraft(Status, saved);
            if (_connected) _syncWarning = "Каналы сохранены локально; применение службой ещё не подтверждено. Нажмите «Применить настройки» для отправки сохранённого списка.";
        }
    }

    private static void WriteIfChanged<T>(string path, T value)
    {
        if (!File.Exists(path) || File.ReadAllText(path) != JsonData.Encode(value)) AtomicStore.Write(path, value);
    }

    public static List<ChannelProfile> ReadProfiles(string path)
    {
        var json = File.ReadAllText(path);
        using var document = JsonDocument.Parse(json);
        var profiles = document.RootElement.ValueKind == JsonValueKind.Array
            ? JsonData.Decode<List<ChannelProfile>>(json)
            : JsonData.Decode<AppConfig>(json).Profiles;
        var check = AppConfig.Defaults();
        check.Profiles = profiles;
        check.Validate();
        return profiles;
    }

    public async ValueTask DisposeAsync()
    {
        await _gate.WaitAsync();
        try
        {
            if (_disposed) return;
            _disposed = true;
            if (_local is not null)
            {
                await _local.StopAsync();
                await _local.DisposeAsync();
                _local = null;
            }
        }
        finally { _gate.Release(); }
    }
}
