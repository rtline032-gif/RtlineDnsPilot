using System.Runtime.InteropServices;

namespace NetPilot;

public sealed class MonitorEngine : IAsyncDisposable
{
    private readonly object _state = new();
    private readonly SemaphoreSlim _operations = new(1,1);
    private readonly string _directory;
    private readonly bool _serviceMode;
    private readonly DnsTransport _transport = new();
    private readonly ProfileProbe _probe;
    private readonly Func<ChannelProfile,string,TimeSpan,CancellationToken,Task<ProbeResult>> _check;
    private readonly string _chartPath;
    private ChartHistoryArchive _chart = new();
    private bool _chartWriteBlocked;
    private string? _chartWarning;
    private string? _chartWriteWarning;
    private readonly DnsSessionManager? _dns;
    private DnsRelay? _relay;
    private CancellationTokenSource? _cancel;
    private CancellationTokenSource? _roundCancel;
    private Task? _loop;
    private AppConfig _config;
    private List<ProfileStats> _stats = [];
    private List<LogEntry> _events = [];
    private string? _active;
    private string? _error;
    private DateTimeOffset _switched = DateTimeOffset.MinValue;
    private bool _allUnavailable;
    private bool _monitoring;
    private bool _paused;
    private string _selectionReason="Ожидание первой проверки выбранных сервисов.";
    private int _allDownRounds;
    private int _probeRequested;

    public MonitorEngine(AppConfig config,string dataDirectory,bool serviceMode=false)
    {
        config.Validate(); _config=JsonData.Clone(config); _directory=dataDirectory; _serviceMode=serviceMode;
        _paused=config.MonitoringPaused;
        Directory.CreateDirectory(_directory);
        if (!serviceMode) _config.ManagementRequested=false;
        _probe=new ProfileProbe(_transport);
        _check=_probe.CheckAsync;
        _chartPath=Path.Combine(_directory,"chart-history.json");
        if(serviceMode) _dns=new DnsSessionManager(_directory);
        _stats=AtomicStore.Read<List<ProfileStats>>(Path.Combine(_directory,"statistics.json")) ?? [];
        _events=AtomicStore.Read<List<LogEntry>>(Path.Combine(_directory,"events.json")) ?? [];
        string? legacyKey=null;
        foreach(var s in _stats)
        {
            s.ConsecutiveFailures=0; s.ConsecutiveSuccesses=0;
            // Old releases stored one endpoint per configuration and removed its
            // samples when that endpoint changed. Preserve that known context.
            foreach(var sample in s.Samples.Where(s=>s.MonitoringKey.Length==0))
            {
                if(legacyKey is null)
                {
                    var legacy=JsonData.Clone(_config); legacy.MonitorChatGpt=true;legacy.MonitorGemini=false;legacy.MonitorTailscale=false;
                    legacy.AdditionalTargets=[];legacyKey=MonitoringTargets.Key(legacy);
                }
                sample.MonitoringKey=legacyKey;
            }
        }
        LoadChartHistory();
    }

    internal MonitorEngine(AppConfig config,string dataDirectory,bool serviceMode,
        Func<ChannelProfile,string,TimeSpan,CancellationToken,Task<ProbeResult>> check) : this(config,dataDirectory,serviceMode)
    { _check=check??throw new ArgumentNullException(nameof(check)); }

    private void LoadChartHistory()
    {
        if(File.Exists(_chartPath))
        {
            try
            {
                _chart=AtomicStore.Read<ChartHistoryArchive>(_chartPath)??throw new InvalidDataException("Пустой архив графиков.");
                _chart.NormalizeLoaded(DateTimeOffset.UtcNow);
            }
            catch(Exception ex)
            {
                _chart=new ChartHistoryArchive();_chartWriteBlocked=true;
                _chartWarning="Архив графиков не прочитан и оставлен без изменений. Новые измерения доступны только в этом сеансе. "+ex.Message;
            }
            return;
        }
        try { _chart.Seed(_config,_stats,DateTimeOffset.UtcNow); }
        catch(Exception ex) { _chartWarning="Не удалось перенести прежние измерения в графики. "+ex.Message; }
        SaveChartHistoryLocked();
    }

    private void SaveChartHistoryLocked()
    {
        if(_chartWriteBlocked) return;
        try { AtomicStore.Write(_chartPath,_chart);_chartWriteWarning=null; }
        catch(Exception ex) { _chartWriteWarning="Архив графиков не сохранён; новые измерения пока доступны в памяти. "+ex.Message; }
    }

    public HistoryReport GetHistory(HistoryQuery query)
    {
        ArgumentNullException.ThrowIfNull(query);query.Validate();
        lock(_state)
        {
            var result=_chart.Query(_config,query,DateTimeOffset.UtcNow);
            var warning=string.Join("\r\n",new[] {result.Warning,_chartWarning,_chartWriteWarning}.Where(s=>!string.IsNullOrWhiteSpace(s)));
            result.Warning=warning.Length==0?null:warning;
            return result;
        }
    }

    public DnsShareOffer GetShareCandidates()
    {
        lock(_state) return DnsSharing.Select(_config,_stats,DateTimeOffset.UtcNow);
    }

    public async Task StartAsync()
    {
        if (_monitoring) return;
        lock(_state) foreach(var s in _stats) { s.ConsecutiveFailures=0; s.ConsecutiveSuccesses=0; }
        if (_dns?.HasJournal == true)
        {
            try { await _dns.RestoreAsync(); AddEvent("recovery","Восстановлены DNS после незавершённого сеанса."); }
            catch(Exception ex) { _error=ex.Message; AddEvent("error","Восстановление требует внимания: "+ex.Message); throw; }
        }
        _cancel=new CancellationTokenSource();
        lock(_state) _monitoring=true;
        _loop=RunLoopAsync(_cancel.Token);
    }

    private async Task RunLoopAsync(CancellationToken token)
    {
        while(!token.IsCancellationRequested)
        {
            bool requested=Interlocked.Exchange(ref _probeRequested,0)!=0;
            bool paused;lock(_state) paused=_paused;
            if(paused && !requested)
            {
                try {await Task.Delay(250,token);}catch(OperationCanceledException){break;}
                continue;
            }
            try { await ProbeNowCoreAsync(requested); }
            catch(OperationCanceledException) when(token.IsCancellationRequested) { break; }
            catch(OperationCanceledException) { } // An explicit pause/configure/disable interrupted this round.
            catch(Exception ex) { lock(_state) _error=ex.Message; AddEvent("error",ex.Message); }
            int seconds; lock(_state) seconds=_config.IntervalSeconds;
            try
            {
                for(int i=0;i<seconds*2;i++)
                {
                    bool expired;lock(_state) expired=_config.ManualProfileId is not null && _config.ManualUntil<=DateTimeOffset.UtcNow;
                    lock(_state) paused=_paused;
                    if(expired || Volatile.Read(ref _probeRequested)!=0 || paused) break;
                    await Task.Delay(500,token);
                }
            }
            catch(OperationCanceledException) { break; }
        }
    }

    public Task ProbeNowAsync()=>ProbeNowCoreAsync(allowWhilePaused:true);
    private async Task ProbeNowCoreAsync(bool allowWhilePaused)
    {
        var token=_cancel?.Token ?? CancellationToken.None;
        await _operations.WaitAsync(token);
        using var round=CancellationTokenSource.CreateLinkedTokenSource(token);
        lock(_state) {_roundCancel=round;if(_paused&&!allowWhilePaused)round.Cancel();}
        try { await ProbeRoundAsync(round.Token); }
        finally {lock(_state) _roundCancel=null;_operations.Release();}
    }

    private void CancelRound()
    {
        CancellationTokenSource? round;lock(_state) round=_roundCancel;
        try {round?.Cancel();}catch(ObjectDisposedException) { }
    }
    public async Task SetMonitoringAsync(bool enabled)
    {
        lock(_state) {_paused=!enabled;_config.MonitoringPaused=!enabled;}
        if(enabled)
        {
            Interlocked.Exchange(ref _probeRequested,1);
            await StartAsync();
        }
        else {Interlocked.Exchange(ref _probeRequested,0);CancelRound();}
        SaveConfig();
        AddEvent("monitor",enabled?"Проверки продолжены.":"Проверки приостановлены. Текущий DNS и работа посредника сохранены.");
    }
    public async Task RequestProbeAsync()
    {
        Interlocked.Exchange(ref _probeRequested,1);
        if(!_monitoring) await StartAsync();
    }

    private async Task ProbeRoundAsync(CancellationToken token)
    {
        AppConfig config;bool expired;
        lock(_state) {expired=ExpireManualLocked(DateTimeOffset.UtcNow);config=JsonData.Clone(_config);}
        if(expired) SaveConfig();
        var results=await MonitoringTargets.CheckAsync(config,_check,token);
        token.ThrowIfCancellationRequested();
        SelectionDecision decision;
        lock(_state)
        {
            // Record the completed measurements before the selection window trims
            // older samples. Charts never feed the automatic selection decision.
            try { _chart.Record(config,results,DateTimeOffset.UtcNow); }
            catch(Exception ex) { _chartWarning="Не удалось добавить измерение в графики. "+ex.Message; }
            if(ExpireManualLocked(DateTimeOffset.UtcNow))
            {config.ManualProfileId=null;config.ManualUntil=null;SaveConfig();}
            foreach(var r in results)
            {
                var s=_stats.FirstOrDefault(x=>x.ProfileId==r.ProfileId);
                if(s==null) { s=new ProfileStats {ProfileId=r.ProfileId}; _stats.Add(s); }
                s.Add(r);
            }
            _stats.RemoveAll(s=>!config.Profiles.Any(p=>p.Id==s.ProfileId));
            decision=Selection.Choose(config,_stats,_active,_switched,DateTimeOffset.UtcNow);
            _selectionReason=decision.Reason;
            if(decision.ProfileId!=_active)
            {
                var old=_active; _active=decision.ProfileId; _switched=DateTimeOffset.UtcNow;
                AddEventLocked("switch", decision.Reason+": "+(config.Profiles.FirstOrDefault(p=>p.Id==_active)?.Name??"нет канала"));
                if(_dns?.Managed==true) DnsCache.Flush();
            }
            if(decision.AllUnavailable && !_allUnavailable) AddEventLocked("outage","Все включённые каналы не прошли проверки выбранных сервисов.");
            if(!decision.AllUnavailable && _allUnavailable && results.Any(r=>r.Success)) AddEventLocked("recovered","Доступность HTTPS восстановлена.");
            _allUnavailable=decision.AllUnavailable;
            _allDownRounds=decision.AllUnavailable?_allDownRounds+1:0;
        }
        if(_serviceMode && _config.ManagementRequested)
        {
            if(_dns!.Managed && _relay is { IsRunning:false })
            {
                await RestoreRelayAsync();
                lock(_state) _error="Локальный DNS-посредник остановился; исходный DNS восстановлен.";
                AddEvent("error",_error!);
            }
            else if(_allDownRounds>=3 && _dns.Managed && !MonitoringTargets.ManualIsActive(_config,DateTimeOffset.UtcNow))
            {
                await RestoreRelayAsync();
                AddEvent("fallback","Все каналы недоступны три раунда. Возвращён исходный DNS; продолжаем наблюдение.");
            }
            else if(!_allUnavailable && !_dns!.Managed && !_dns.HasJournal && CurrentHealthy())
            {
                try { await ActivateRelayAsync(token); lock(_state) _error=null; }
                catch(Exception ex) { lock(_state) _error=ex.Message; AddEvent("error","Не удалось применить DNS: "+ex.Message); }
            }
        }
        SaveMeasurements();
    }

    private bool CurrentHealthy()
    {
        lock(_state)
        {
            var s=MonitoringTargets.View(_stats.FirstOrDefault(x=>x.ProfileId==_active),_config);
            return s?.Last?.Success==true && DateTimeOffset.UtcNow-s.Last.At<=TimeSpan.FromSeconds(MonitoringTargets.FreshnessSeconds(_config));
        }
    }

    private ChannelProfile? CurrentProfile()
    { lock(_state) return _config.Profiles.FirstOrDefault(p=>p.Enabled&&p.Id==_active); }

    private async Task ActivateRelayAsync(CancellationToken token)
    {
        if(_dns==null || !CurrentHealthy()) throw new InvalidOperationException("Сначала нужен канал с успешной проверкой HTTPS.");
        if(!Guid.TryParse(_config.AdapterId,out _)) throw new InvalidOperationException("Выберите физический сетевой адаптер.");
        _relay=new DnsRelay(_transport,CurrentProfile);
        try
        {
            token.ThrowIfCancellationRequested();
            // Probes may be cancelled while Windows still points at us. Only explicit disposal
            // after successful DNS restoration is allowed to end the relay's lifetime.
            await _relay.StartAsync(53,System.Net.Sockets.Socket.OSSupportsIPv6,CancellationToken.None);
            token.ThrowIfCancellationRequested();
            await _dns.EnableAsync(_config.AdapterId);
            DnsCache.Flush();
            AddEvent("managed","DNS выбранного адаптера направлен в локальный посредник.");
        }
        catch
        {
            if(_dns.HasJournal) await _dns.RestoreAsync();
            if(_relay!=null) { await _relay.DisposeAsync(); _relay=null; }
            throw;
        }
    }

    private async Task RestoreRelayAsync()
    {
        // The manager also detects a journal lost DURING a managed session; HasJournal
        // alone must never turn that case into a successful no-op restoration.
        if(_dns!=null) await _dns.RestoreAsync();
        if(_relay!=null) { await _relay.DisposeAsync(); _relay=null; }
        if(_serviceMode) DnsCache.Flush();
    }

    public async Task SetManagementAsync(bool enabled)
    {
        if(!_serviceMode) throw new InvalidOperationException("Изменять системный DNS может только установленная служба.");
        CancelRound();
        await _operations.WaitAsync();
        try
        {
            if(enabled)
            {
                if(_dns!.Managed) return;
                if(_dns.HasJournal) throw new InvalidOperationException("Сначала восстановите незавершённый сеанс DNS.");
                if(!CurrentHealthy()) await ProbeRoundAsync(_cancel?.Token ?? CancellationToken.None);
                if(!_dns.Managed) await ActivateRelayAsync(_cancel?.Token ?? CancellationToken.None);
                lock(_state) { _config.ManagementRequested=true; _error=null; }
            }
            else
            {
                // Persist user intent even when restoration needs a later retry.
                lock(_state) _config.ManagementRequested=false;
                SaveConfig();
                await RestoreRelayAsync();
                lock(_state) _error=null;
                AddEvent("restored","Управление выключено; исходные DNS-настройки восстановлены.");
            }
            SaveConfig(); SaveMeasurements();
        }
        catch(Exception ex) { lock(_state) _error=ex.Message; AddEvent("error",ex.Message); throw; }
        finally { _operations.Release(); }
    }

    public async Task ConfigureAsync(AppConfig config)
    {
        config.Validate();
        CancelRound();
        await _operations.WaitAsync();
        try
        {
            bool desired; string oldAdapter; AppConfig previous;
            lock(_state) { desired=_config.ManagementRequested; oldAdapter=_config.AdapterId; previous=_config; }
            if(_dns is not null && (_dns.Managed||_dns.HasJournal) && !string.Equals(config.AdapterId,oldAdapter,StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("Перед сменой адаптера отключите управление DNS.");
            static string Signature(ChannelProfile p)=>JsonData.Encode(new {p.Mode,p.Servers,p.DnsPort,p.DohUrl});
            var changed=config.Profiles.Where(p=>!previous.Profiles.Any(old=>old.Id==p.Id&&Signature(old)==Signature(p))).Select(p=>p.Id).ToHashSet();
            bool probeChanged=MonitoringTargets.Key(config)!=MonitoringTargets.Key(previous);
            bool manualChanged=config.ManualProfileId!=previous.ManualProfileId || config.ManualUntil!=previous.ManualUntil;
            bool activeChanged=probeChanged || changed.Contains(_active??"") || !config.Profiles.Any(p=>p.Id==_active&&p.Enabled);
            if(_dns?.Managed==true && activeChanged) await RestoreRelayAsync();
            lock(_state)
            {
                var incoming=JsonData.Clone(config); incoming.ManagementRequested=_serviceMode&&desired;
                incoming.MonitoringPaused=_paused;
                _config=incoming;
                // Measurements from another endpoint cannot justify applying a new one.
                _stats.RemoveAll(s=>changed.Contains(s.ProfileId));
                if(probeChanged)
                {
                    foreach(var s in _stats) {s.ConsecutiveFailures=0;s.ConsecutiveSuccesses=0;}
                    _allUnavailable=false;_allDownRounds=0;
                }
                if(activeChanged) _active=null;
                if(MonitoringTargets.ManualIsActive(incoming,DateTimeOffset.UtcNow))
                {
                    _active=incoming.ManualProfileId;
                    if(manualChanged)
                    {
                        _switched=DateTimeOffset.UtcNow;
                        _selectionReason=incoming.ManualUntil is null?"Канал зафиксирован вручную постоянно.":"Канал зафиксирован вручную до "+incoming.ManualUntil.Value.ToLocalTime().ToString("HH:mm:ss")+".";
                        AddEventLocked("manual","Ручная фиксация: "+incoming.Profiles.First(p=>p.Id==_active).Name
                            +(incoming.ManualUntil is null?" — постоянно":" — до "+incoming.ManualUntil.Value.ToLocalTime().ToString("dd.MM HH:mm:ss"))+". Автопереключение при отказе выключено.");
                    }
                }
                else if(manualChanged) {_selectionReason="Ручная фиксация снята; ожидается решение автовыбора.";AddEventLocked("auto","Ручная фиксация снята; включён автоматический выбор.");}
            }
            if(manualChanged && _dns?.Managed==true) DnsCache.Flush();
            SaveConfig(); Interlocked.Exchange(ref _probeRequested,1);
        }
        finally { _operations.Release(); }
    }

    public async Task SetPreferredAsync(string? profileId)
    {
        AppConfig config; lock(_state) config=JsonData.Clone(_config);
        config.PinnedProfileId=profileId;config.ManualProfileId=null;config.ManualUntil=null;await ConfigureAsync(config);
    }

    private bool ExpireManualLocked(DateTimeOffset now)
    {
        if(_config.ManualProfileId is null || _config.ManualUntil is null || _config.ManualUntil>now) return false;
        _config.ManualProfileId=null;_config.ManualUntil=null;_config.PinnedProfileId=null;
        AddEventLocked("auto","Время ручной фиксации истекло; включён автоматический выбор.");
        return true;
    }

    public AppStatus GetStatus()
    {
        lock(_state)
        {
            var status=new AppStatus
            {
                ServiceVersion=_serviceMode?AppVersion.Current:"",ServiceConnected=_serviceMode,Monitoring=_monitoring&&!_paused,SystemDnsManaged=_dns?.Managed==true,DnsRecoveryPending=_dns is {HasJournal:true,Managed:false},
                AllUnavailable=_allUnavailable,ActiveProfileId=_active,Error=_error,Config=JsonData.Clone(_config),
                Statistics=StatusProjection.Statistics(_stats),Events=StatusProjection.Events(_events),SelectionReason=_paused?"Проверки и автоматический выбор приостановлены; текущий канал сохранён.":_selectionReason,
                State=_dns is {HasJournal:true,Managed:false}?"Восстановление DNS не завершено":_dns?.Managed==true?"Системный DNS под управлением":_config.ManagementRequested?"Ожидание канала; исходный DNS сохранён":_monitoring?"Наблюдение; DNS не изменён":"Наблюдение остановлено"
            };
            if(MonitoringTargets.ManualIsActive(_config,DateTimeOffset.UtcNow)
                && MonitoringTargets.View(_stats.FirstOrDefault(s=>s.ProfileId==_active),_config)?.Last is {Success:false})
                status.Error=string.Join("\r\n",new[] {_error,"Зафиксированный канал недоступен по выбранным проверкам. Автоматическое переключение выключено до снятия или истечения фиксации."}.Where(s=>!string.IsNullOrEmpty(s)));
            status.Adapters=AdapterCatalog.Read(); return status;
        }
    }

    private void AddEvent(string kind,string message) { lock(_state) AddEventLocked(kind,message); }
    private void AddEventLocked(string kind,string message)
    {
        if(_events.LastOrDefault() is LogEntry last && last.Kind==kind && last.Message==message && DateTimeOffset.UtcNow-last.At<TimeSpan.FromMinutes(2)) return;
        _events.Add(new LogEntry {Kind=kind,Message=message}); if(_events.Count>200) _events.RemoveRange(0,_events.Count-200);
    }
    private void SaveConfig() { lock(_state) AtomicStore.Write(Path.Combine(_directory,"config.json"),_config); }
    private void SaveMeasurements()
    { lock(_state) { AtomicStore.Write(Path.Combine(_directory,"statistics.json"),_stats); AtomicStore.Write(Path.Combine(_directory,"events.json"),_events); SaveChartHistoryLocked(); } }

    public async Task StopAsync()
    {
        _cancel?.Cancel(); if(_loop!=null) { try { await _loop; } catch(OperationCanceledException) {} }
        await _operations.WaitAsync();
        try { await RestoreRelayAsync(); lock(_state) _monitoring=false; SaveMeasurements(); }
        finally { _operations.Release(); }
        _cancel?.Dispose(); _cancel=null; _loop=null;
    }
    public async ValueTask DisposeAsync() { await StopAsync(); _transport.Dispose(); _operations.Dispose(); }
}

internal static class DnsCache
{
    [DllImport("dnsapi.dll",SetLastError=true)] [return:MarshalAs(UnmanagedType.Bool)] private static extern bool DnsFlushResolverCache();
    public static void Flush() => DnsFlushResolverCache();
}
