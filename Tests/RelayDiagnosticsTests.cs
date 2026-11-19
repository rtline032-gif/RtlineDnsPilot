using System.Buffers.Binary;
using System.IO.Pipes;
using System.Net;
using System.Net.Sockets;
using System.Security.Principal;

namespace NetPilot.Tests;

public static class RelayDiagnosticsTests
{
    public static async Task RunAsync(Action<string,bool,string> report)
    {
        async Task Check(string name, Func<Task> body)
        {
            try { await body(); report(name,true,""); }
            catch(Exception ex) { report(name,false,ex.ToString()); }
        }
        await Check("relay metadata retains bounded negative results without names packets or persistent writes", () =>
        {
            var now=new DateTimeOffset(2026,11,19,10,0,0,TimeSpan.Zero);
            var store=new RelayDiagnosticsStore(()=>now);
            var profile=new ChannelProfile {Id=Guid.NewGuid().ToString("N"),Mode=DnsMode.Doh,Servers=["192.0.2.53"],DohUrl="https://private-resolver.invalid/dns-query"};
            for(int i=0;i<100;i++)
            {
                var trace=store.Begin(DnsWire.Query("private-finance.invalid",65),"UDP",AddressFamily.InterNetworkV6);
                trace.Profile(profile); trace.Response(Reply(DnsWire.Query("private-finance.invalid",65),i==0?3:0)); trace.Finish(true); trace.Finish(true);
            }
            var snapshot=store.Snapshot(); snapshot.ServiceVersion=AppVersion.Current;
            RelayDiagnostics.Validate(snapshot);
            Require(snapshot.Received==100 && snapshot.Completed==100 && snapshot.InFlight==0 && snapshot.ResponsesQueued==100
                && snapshot.NegativeOrError==1 && snapshot.Requests.Count==33 && snapshot.Requests[0].ReturnedCode==3,"Bounded queues lost a negative response or counted completion twice.");
            Require(snapshot.Requests.All(r=>r.QueryType==65 && r.LocalFamily=="IPv6" && r.ClientTransport=="UDP"),"QTYPE and local transport family were conflated.");
            var text=JsonData.Encode(snapshot)+RelayDiagnostics.Format(snapshot);
            Require(!text.Contains("private-finance",StringComparison.Ordinal) && !text.Contains("private-resolver",StringComparison.Ordinal)
                && !text.Contains("dns-query",StringComparison.Ordinal),"Query names or DoH URL entered retained metadata.");
            snapshot.Requests[0].Stage="changed";
            Require(store.Snapshot().Requests[0].Stage!="changed","Caller mutated retained metadata.");
            now=now.AddSeconds(301);
            Require(store.Snapshot().Requests.Count==0 && store.Snapshot().Completed==100,"Expired rows survived or session counters were lost.");
            return Task.CompletedTask;
        });
        await Check("relay metadata bounds both completion queues independently and preserves ordered profile evidence", () =>
        {
            var store=new RelayDiagnosticsStore();
            var query=DnsWire.Query("bound.invalid");
            for(int i=0;i<200;i++)
            {
                var trace=store.Begin(query,"UDP",AddressFamily.InterNetwork); trace.Response(Reply(query,i%2==0?2:0)); trace.Finish(true);
            }
            var snapshot=store.Snapshot();
            Require(snapshot.Requests.Count<=64 && snapshot.Requests.Select(r=>r.RequestId).Distinct().Count()==snapshot.Requests.Count
                && snapshot.NegativeOrError==100,"Retained request count is unbounded or contains duplicates.");
            var a=new ChannelProfile {Servers=["192.0.2.1","192.0.2.2"]};
            var b=JsonData.Clone(a); Array.Reverse(b.Servers);
            Require(RelayDiagnostics.Context(a)!=RelayDiagnostics.Context(b),"Diagnostic evidence hid upstream priority.");
            Require(RelayDiagnostics.Error(new IOException("secret-name.invalid",new SocketException(11001)))=="SOCKET_11001"
                && RelayDiagnostics.Error(new OperationCanceledException("secret-name.invalid"),true)=="CANCELLED","Error metadata exposed messages or invented a native code.");
            return Task.CompletedTask;
        });
        await Check("UDP relay records actual QTYPE server port and RCODE without its query name", async () => await Wire(false,1,0,false));
        await Check("TCP relay preserves a valid NXDOMAIN and records AAAA independently of IPv4 transport", async () => await Wire(true,28,3,false));
        await Check("observed UDP truncation uses unchanged TCP fallback and records its final stage", async () => await Wire(false,65,0,true));
        await Check("a rejected upstream packet retains its validation stage before local SERVFAIL", async () => await Wire(false,28,0,false,malformed:true));
        await Check("no active relay profile emits SERVFAIL with a distinct selection reason", async () =>
        {
            var store=new RelayDiagnosticsStore(); using var transport=new DnsTransport();
            await using var relay=new DnsRelay(transport,()=>null,store);
            using var deadline=new CancellationTokenSource(TimeSpan.FromSeconds(6)); int port=FreePort();
            await relay.StartAsync(port,ipv6:false,deadline.Token);
            using var client=new UdpClient(); var query=DnsWire.Query("missing.invalid",28);
            await client.SendAsync(query,new IPEndPoint(IPAddress.Loopback,port),deadline.Token);
            var reply=await client.ReceiveAsync(deadline.Token);
            Require(DnsWire.ResponseCode(reply.Buffer)==2,"Missing selection did not preserve SERVFAIL.");
            await Until(()=>store.Snapshot().Completed==1,deadline.Token);
            var row=store.Snapshot().Requests.Single();
            Require(row.ProfileId is null && row.UpstreamAttempts.Count==0 && row.Stage=="selection"
                && row.ErrorCode=="NO_ACTIVE_PROFILE" && row.ResponseQueued && row.ReturnedCode==2,"Selection failure was mislabeled as upstream success or delivery proof.");
        });
        await Check("a failing diagnostic observer cannot change the upstream DNS answer", async () =>
        {
            using var upstream=new UdpClient(new IPEndPoint(IPAddress.Loopback,0));
            using var deadline=new CancellationTokenSource(TimeSpan.FromSeconds(6));
            var serving=Task.Run(async () =>
            {
                var received=await upstream.ReceiveAsync(deadline.Token);
                await upstream.SendAsync(Reply(received.Buffer,3),received.RemoteEndPoint,deadline.Token);
            });
            using var transport=new DnsTransport();
            var result=await transport.ExchangeObservedAsync(new ChannelProfile {Servers=["127.0.0.1"],DnsPort=((IPEndPoint)upstream.Client.LocalEndPoint!).Port},
                DnsWire.Query("negative.invalid"),deadline.Token,_=>throw new InvalidOperationException("Do not alter DNS."));
            Require(DnsWire.ResponseCode(result)==3,"Observer changed a valid negative answer."); await serving;
        });
        await Check("relay diagnostic RPC reads memory only and omits ordinary status", async () =>
        {
            var config=AppConfig.Defaults(); config.MonitoringPaused=true; foreach(var p in config.Profiles)p.Enabled=false;
            var directory=Path.Combine(AppContext.BaseDirectory,"test-data","relay-rpc-"+Guid.NewGuid().ToString("N"));
            int probes=0;
            await using var engine=new MonitorEngine(config,directory,false,(_,_,_,_)=>{probes++;throw new Exception("A read must not probe.");});
            using var stop=new CancellationTokenSource(TimeSpan.FromSeconds(6)); string pipeName="NetPilot.Relay.Test."+Guid.NewGuid().ToString("N");
            var server=new ServiceRpc(engine,WindowsIdentity.GetCurrent().User!.Value,pipeName).RunAsync(stop.Token);
            try
            {
                using var pipe=new NamedPipeClientStream(".",pipeName,PipeDirection.InOut,PipeOptions.Asynchronous);
                await pipe.ConnectAsync(2000,stop.Token);
                await PipeMessages.WriteAsync(pipe,JsonData.Encode(new RpcRequest {Command="relay-diagnostics"}),stop.Token);
                var reply=JsonData.Decode<RpcReply>(await PipeMessages.ReadAsync(pipe,4*1024*1024,stop.Token));
                Require(reply.Ok && reply.Status is null && reply.RelayDiagnostics is {Requests.Count:0,Received:0},"Read-only RPC returned unrelated status or lost its payload.");
                RelayDiagnostics.Validate(reply.RelayDiagnostics!);
                Require(probes==0 && !engine.GetStatus().SystemDnsManaged && Directory.GetFiles(directory,"*relay*",SearchOption.AllDirectories).Length==0,"Reading diagnostics probed, managed DNS or persisted metadata.");
            }
            finally {stop.Cancel(); try {await server;}catch(OperationCanceledException) { }}
        });
    }

    private static async Task Wire(bool clientTcp,ushort queryType,int rcode,bool truncated,bool malformed=false)
    {
        using var deadline=new CancellationTokenSource(TimeSpan.FromSeconds(8));
        var listener=new TcpListener(IPAddress.Loopback,0); listener.Start(); int upstreamPort=((IPEndPoint)listener.LocalEndpoint).Port;
        using var upstream=new UdpClient(new IPEndPoint(IPAddress.Loopback,upstreamPort));
        var udpServer=Task.Run(async () =>
        {
            var received=await upstream.ReceiveAsync(deadline.Token); var response=Reply(received.Buffer,rcode);
            if(truncated) response[2]|=2;
            if(malformed) response[0]^=1;
            await upstream.SendAsync(response,received.RemoteEndPoint,deadline.Token);
        });
        var tcpServer=truncated?Task.Run(async () =>
        {
            using var accepted=await listener.AcceptTcpClientAsync(deadline.Token); using var stream=accepted.GetStream();
            var prefix=new byte[2]; await stream.ReadExactlyAsync(prefix,deadline.Token);
            var query=new byte[BinaryPrimitives.ReadUInt16BigEndian(prefix)]; await stream.ReadExactlyAsync(query,deadline.Token);
            var response=Reply(query,rcode); BinaryPrimitives.WriteUInt16BigEndian(prefix,(ushort)response.Length);
            await stream.WriteAsync(prefix,deadline.Token); await stream.WriteAsync(response,deadline.Token);
        }):Task.CompletedTask;
        var store=new RelayDiagnosticsStore(); using var transport=new DnsTransport();
        var profile=new ChannelProfile {Id=Guid.NewGuid().ToString("N"),Servers=["127.0.0.1"],DnsPort=upstreamPort};
        await using var relay=new DnsRelay(transport,()=>profile,store); int relayPort=FreePort();
        try
        {
            await relay.StartAsync(relayPort,ipv6:false,deadline.Token);
            var query=DnsWire.Query("do-not-retain-this-name.invalid",queryType); query[0]=0x37;query[1]=0x42;
            // An OPT record follows the question; reading QTYPE from the packet tail is incorrect.
            Array.Resize(ref query,query.Length+11); query[11]=1; int opt=query.Length-11;query[opt+2]=41;query[opt+3]=4;
            byte[] response;
            if(clientTcp)
            {
                using var client=new TcpClient(); await client.ConnectAsync(IPAddress.Loopback,relayPort,deadline.Token);
                using var stream=client.GetStream(); var prefix=new byte[2]; BinaryPrimitives.WriteUInt16BigEndian(prefix,(ushort)query.Length);
                await stream.WriteAsync(prefix,deadline.Token); await stream.WriteAsync(query,deadline.Token);
                await stream.ReadExactlyAsync(prefix,deadline.Token); response=new byte[BinaryPrimitives.ReadUInt16BigEndian(prefix)]; await stream.ReadExactlyAsync(response,deadline.Token);
            }
            else
            {
                using var client=new UdpClient(); await client.SendAsync(query,new IPEndPoint(IPAddress.Loopback,relayPort),deadline.Token);
                response=(await client.ReceiveAsync(deadline.Token)).Buffer;
            }
            var expectedCode=malformed?2:rcode;
            DnsWire.ValidateResponse(query,response); Require(DnsWire.ResponseCode(response)==expectedCode,"Observed relay changed the DNS response.");
            await Until(()=>store.Snapshot().Completed==1,deadline.Token); await Task.WhenAll(udpServer,tcpServer);
            var snapshot=store.Snapshot();snapshot.ServiceVersion=AppVersion.Current;RelayDiagnostics.Validate(snapshot);
            var row=snapshot.Requests.Single(); var attempt=row.UpstreamAttempts.Single();
            Require(row.TransactionId==0x3742 && row.QueryType==queryType && row.QueryClass==1 && row.LocalFamily=="IPv4"
                && row.ClientTransport==(clientTcp?"TCP":"UDP") && row.ResponseQueued && row.ReturnedCode==expectedCode
                && row.ProfileId==profile.Id && row.ProfileContext==RelayDiagnostics.Context(profile),"Local request metadata does not match the actual request/configuration.");
            Require(attempt.Server=="127.0.0.1" && attempt.Port==upstreamPort && attempt.ResponseCode==(malformed?(int?)null:rcode)
                && attempt.Transport==(truncated?"TCP":"UDP") && attempt.TcpFallback==truncated
                && attempt.Stage==(truncated?"tcp-validate":"udp-validate")
                && attempt.ErrorCode==(malformed?"INVALID_DNS_RESPONSE":null),"Actual upstream/fallback/negative-response evidence was lost.");
            if(malformed) Require(row.ErrorCode=="INVALID_DNS_RESPONSE" && row.Stage=="upstream","Generated SERVFAIL hid the upstream validation failure.");
            Require(!JsonData.Encode(snapshot).Contains("do-not-retain-this-name",StringComparison.Ordinal),"Actual QNAME entered retained metadata.");
        }
        finally {deadline.Cancel();listener.Stop();}
    }

    private static byte[] Reply(byte[] query,int rcode)
    {var response=DnsWire.Failure(query);response[3]=(byte)((response[3]&0xf0)|rcode);return response;}
    private static int FreePort()
    {var listener=new TcpListener(IPAddress.Loopback,0);listener.Start();int port=((IPEndPoint)listener.LocalEndpoint).Port;listener.Stop();return port;}
    private static async Task Until(Func<bool> check,CancellationToken token)
    {while(!check())await Task.Delay(10,token);}
    private static void Require(bool condition,string message)
    {if(!condition)throw new Exception(message);}
}
