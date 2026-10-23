using System.IO.Pipes;
using System.Security.Principal;
using System.Text.Json;

namespace NetPilot.Tests;

public static class SharingIntegrationTests
{
    public static async Task RunAsync(Action<string,bool,string> report)
    {
        async Task Check(string name,Func<string,Task> test)
        {
            var dir=Path.Combine(AppContext.BaseDirectory,"test-data","share-integration-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(dir);
            try{await test(dir);report(name,true,"");}catch(Exception ex){report(name,false,ex.ToString());}
        }
        static void Require(bool condition,string message){if(!condition)throw new Exception(message);}
        static AppConfig Config()=>new() {Profiles=[new ChannelProfile {Name="Private display name",Servers=["8.8.8.8"],
            Enabled=false,Note="Private local note that must not be shared"}]};
        static List<ProfileStats> Statistics(AppConfig config)
        {
            var profile=config.Profiles[0];var now=DateTimeOffset.UtcNow.AddSeconds(-5);
            var stats=new ProfileStats {ProfileId=profile.Id};
            stats.Add(MonitoringTargets.Combine(profile.Id,config,[new ServiceProbeResult {ServiceId=MonitoringTargets.ChatGpt,
                At=now,Success=true,HttpStatus=200,DnsMs=2,HttpsMs=5,Detail="Private historical diagnostic"}]));
            stats.Add(MonitoringTargets.Combine(profile.Id,config,[new ServiceProbeResult {ServiceId=MonitoringTargets.ChatGpt,
                At=now.AddSeconds(1),Success=false,HttpStatus=403,DnsMs=2,HttpsMs=5,Detail="Private current diagnostic"}]));
            return [stats];
        }
        static async Task<string> Rejection(Func<Task> action)
        {try{await action();}catch(InvalidOperationException ex){return ex.Message;}throw new Exception("Expected a clear rejection.");}

        await Check("share candidates use raw measured per-site history without a probe or private metadata",async dir=>
        {
            var config=Config();AtomicStore.Write(Path.Combine(dir,"statistics.json"),Statistics(config));
            await using var engine=new MonitorEngine(config,dir,false,(_,_,_,_)=>throw new Exception("Sharing caused a network probe."));
            var offer=engine.GetShareCandidates();
            Require(offer.Profiles.Count==1&&offer.Profiles[0].Servers.SequenceEqual(new[] {"8.8.8.8"})
                &&offer.Profiles[0].WorkedServices.SequenceEqual(new[] {MonitoringTargets.ChatGpt}),"Raw historical evidence was not used.");
            Require(DnsSharing.Select(config,engine.GetStatus().Statistics,DateTimeOffset.UtcNow).Profiles.Count==0,
                "The fixture did not distinguish raw from projected historical details.");
            var json=JsonData.Encode(offer);
            Require(!json.Contains(config.Profiles[0].Id)&&!json.Contains("Private")&&!json.Contains("ProfileId")&&!json.Contains("Note")
                &&!json.Contains("Country")&&!json.Contains("Detail"),"Machine identifiers or diagnostics escaped the local selector.");
            offer.Profiles[0].Servers[0]="1.1.1.1";
            Require(engine.GetShareCandidates().Profiles[0].Servers[0]=="8.8.8.8"&&engine.GetStatus().Statistics[0].TotalChecks==2,
                "A caller could mutate the stored profile/statistics.");
            Require(!File.Exists(Path.Combine(dir,"dns-session.json")),"A read-only candidate query created an adapter transaction.");
        });
        await Check("share-candidates RPC returns only a detached sanitized offer",async dir=>
        {
            var config=Config();AtomicStore.Write(Path.Combine(dir,"statistics.json"),Statistics(config));
            await using var engine=new MonitorEngine(config,dir);
            var name="RtlineDnsPilot.Share.Test."+Guid.NewGuid().ToString("N");
            using var lifetime=new CancellationTokenSource(TimeSpan.FromSeconds(8));
            var server=new ServiceRpc(engine,WindowsIdentity.GetCurrent().User!.Value,name).RunAsync(lifetime.Token);
            try
            {
                using var client=new NamedPipeClientStream(".",name,PipeDirection.InOut,PipeOptions.Asynchronous);
                await client.ConnectAsync(2000,lifetime.Token);
                await PipeMessages.WriteAsync(client,JsonData.Encode(new RpcRequest {Command="share-candidates"}),lifetime.Token);
                var json=await PipeMessages.ReadAsync(client,4*1024*1024,lifetime.Token);
                var reply=JsonData.Decode<RpcReply>(json);using var document=JsonDocument.Parse(json);
                Require(reply.Ok&&reply.Share?.Profiles.Count==1&&!document.RootElement.TryGetProperty("Status",out _)
                    &&!document.RootElement.TryGetProperty("History",out _)&&!json.Contains("Private")&&!json.Contains(config.Profiles[0].Id),
                    "Sharing RPC attached full status/history or private metadata.");
            }
            finally{lifetime.Cancel();try{await server;}catch(OperationCanceledException){}}
        });
        await Check("connected backend reads share candidates without sending control or upload actions",async dir=>
        {
            var config=Config();AtomicStore.Write(Path.Combine(dir,"ui-config.json"),config);
            var service=new Connection(config);await using var backend=new UiBackend(dir,service);
            await backend.RefreshAsync();var offer=await backend.ReadShareCandidatesAsync();
            Require(offer.Profiles.Count==1&&service.Commands.SequenceEqual(new[] {"status","share-candidates"}),
                "Preparing the offer sent a mutation or used a different source.");
            offer.Profiles[0].Servers[0]="1.1.1.1";
            Require(service.SharedOffer.Profiles[0].Servers[0]=="8.8.8.8","Backend returned an attached RPC candidate object.");
        });
        await Check("old services get an update instruction before share-candidates RPC",async dir=>
        {
            var config=Config();AtomicStore.Write(Path.Combine(dir,"ui-config.json"),config);
            var service=new Connection(config) {Version="0.1.4"};await using var backend=new UiBackend(dir,service);
            await backend.RefreshAsync();var error=await Rejection(()=>backend.ReadShareCandidatesAsync());
            Require(error.Contains("Обновить службу")&&service.Commands.SequenceEqual(new[] {"status"}),"An older service received an unsupported share request.");
        });
        await Check("an unknown service never substitutes local measurements for a sharing offer",async dir=>
        {
            var config=Config();AtomicStore.Write(Path.Combine(dir,"ui-config.json"),config);
            var service=new Connection(config) {Offline=true};await using var backend=new UiBackend(dir,service);
            await backend.RefreshAsync();var error=await Rejection(()=>backend.ReadShareCandidatesAsync());
            Require(error.Contains("Проверить связь")&&backend.DnsStateUnknown&&!backend.Status.Monitoring
                &&service.Commands.SequenceEqual(new[] {"status"}),"Unconfirmed local evidence was substituted or monitoring was duplicated.");
        });
        await Check("the actual local observer supplies sharing evidence without contacting a service",async dir=>
        {
            var config=Config();AtomicStore.Write(Path.Combine(dir,"ui-config.json"),config);
            var observer=Path.Combine(dir,"observer");Directory.CreateDirectory(observer);
            AtomicStore.Write(Path.Combine(observer,"statistics.json"),Statistics(config));
            var service=new Connection(config) {Installed=false};await using var backend=new UiBackend(dir,service);
            await backend.RefreshAsync();var offer=await backend.ReadShareCandidatesAsync();
            Require(offer.Profiles.Count==1&&service.Commands.Count==0&&!backend.ServiceConnected,"Local sharing used service data or a network request.");
        });
        await Check("unapplied DNS edits block sharing from the service's previous settings",async dir=>
        {
            var config=Config();AtomicStore.Write(Path.Combine(dir,"ui-config.json"),config);
            var service=new Connection(config) {RejectConfigure=true};await using var backend=new UiBackend(dir,service);
            await backend.RefreshAsync();var draft=JsonData.Clone(config);draft.Profiles[0].Servers=["1.1.1.1"];
            await Rejection(()=>backend.ConfigureAsync(draft));var error=await Rejection(()=>backend.ReadShareCandidatesAsync());
            Require(error.Contains("Применить настройки")&&!service.Commands.Contains("share-candidates"),"The previous service profile was shared under an unapplied edit.");
        });
        await Check("unapplied Tailscale selection blocks sharing from an older target context",async dir=>
        {
            var config=Config();AtomicStore.Write(Path.Combine(dir,"ui-config.json"),config);
            var service=new Connection(config) {RejectConfigure=true};await using var backend=new UiBackend(dir,service);
            await backend.RefreshAsync();var draft=JsonData.Clone(config);draft.MonitorTailscale=true;
            await Rejection(()=>backend.ConfigureAsync(draft));var error=await Rejection(()=>backend.ReadShareCandidatesAsync());
            Require(error.Contains("Применить настройки")&&!service.Commands.Contains("share-candidates"),"A target-context draft was silently ignored.");
        });
        await Check("names notes and intervals do not invalidate unchanged sharing evidence",async dir=>
        {
            var config=Config();AtomicStore.Write(Path.Combine(dir,"ui-config.json"),config);
            var service=new Connection(config) {RejectConfigure=true};await using var backend=new UiBackend(dir,service);
            await backend.RefreshAsync();var draft=JsonData.Clone(config);draft.Profiles[0].Name="Renamed";draft.Profiles[0].Note="Other note";draft.IntervalSeconds=30;
            await Rejection(()=>backend.ConfigureAsync(draft));
            Require((await backend.ReadShareCandidatesAsync()).Profiles.Count==1,"Metadata-only edits hid valid measured context.");
        });
        await Check("an unsupported share reply gives update guidance without adopting its unrelated status",async dir=>
        {
            var config=Config();AtomicStore.Write(Path.Combine(dir,"ui-config.json"),config);
            var service=new Connection(config) {MissingShare=true};await using var backend=new UiBackend(dir,service);
            await backend.RefreshAsync();var error=await Rejection(()=>backend.ReadShareCandidatesAsync());
            Require(error.Contains("Обновить службу")&&backend.Status.Config.Profiles[0].Name==config.Profiles[0].Name,
                "A missing share payload silently succeeded or changed the UI status.");
        });
        await Check("canceling a sharing read releases the backend gate without disconnecting",async dir=>
        {
            var config=Config();AtomicStore.Write(Path.Combine(dir,"ui-config.json"),config);
            var service=new Connection(config) {DelayShare=true};await using var backend=new UiBackend(dir,service);
            await backend.RefreshAsync();using var cancel=new CancellationTokenSource(TimeSpan.FromMilliseconds(40));
            bool canceled=false;try{await backend.ReadShareCandidatesAsync(cancel.Token);}catch(OperationCanceledException){canceled=true;}
            await backend.RefreshAsync();Require(canceled&&backend.ServiceConnected&&service.Commands.SequenceEqual(new[] {"status","share-candidates","status"}),
                "An optional read blocked the control gate or altered connection state.");
        });
        await Check("sharing read deadline reports a clear timeout and permits later status reads",async dir=>
        {
            var config=Config();AtomicStore.Write(Path.Combine(dir,"ui-config.json"),config);
            var service=new Connection(config) {DelayShare=true};await using var backend=new UiBackend(dir,service);
            await backend.RefreshAsync();bool timedOut=false;
            try{await backend.ReadShareCandidatesAsync();}catch(TimeoutException ex){timedOut=ex.Message.Contains("3 секунды");}
            await backend.RefreshAsync();Require(timedOut&&backend.ServiceConnected,"The sharing deadline was silent or kept the gate locked.");
        });
    }

    private sealed class Connection(AppConfig config):IUiServiceConnection
    {
        public bool Installed {get;set;}=true;
        public bool IsInstalled=>Installed;
        public bool Offline {get;set;}
        public bool RejectConfigure {get;set;}
        public bool DelayShare {get;set;}
        public bool MissingShare {get;set;}
        public string Version {get;set;}="0.1.5";
        public List<string> Commands {get;}=[];
        public DnsShareOffer SharedOffer {get;}=new() {Profiles=[new DnsShareCandidate {Mode=DnsMode.Udp,Servers=["8.8.8.8"],WorkedServices=[MonitoringTargets.ChatGpt]}]};
        public async Task<RpcReply> SendAsync(RpcRequest request,CancellationToken token)
        {
            Commands.Add(request.Command);if(Offline)throw new TimeoutException("Simulated disconnected service.");
            if(request.Command=="share-candidates")
            {
                if(DelayShare)await Task.Delay(Timeout.Infinite,token);
                return new RpcReply {Ok=true,Share=MissingShare?null:SharedOffer,Status=MissingShare?new AppStatus():null};
            }
            if(request.Command=="configure"&&RejectConfigure)throw new InvalidOperationException("Simulated unapplied settings.");
            if(request.Command!="status")throw new Exception("Unexpected mutation: "+request.Command);
            return new RpcReply {Ok=true,Status=new AppStatus {Config=JsonData.Clone(config),ServiceVersion=Version,ServiceConnected=true,Monitoring=true}};
        }
        public Task InstallAsync()=>throw new Exception("No installation in sharing tests.");
        public Task RecoverAsync()=>throw new Exception("No DNS recovery in sharing tests.");
    }
}
