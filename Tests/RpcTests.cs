using System.IO.Pipes;
using System.Security.Principal;

namespace NetPilot.Tests;

public static class RpcTests
{
    public static async Task RunAsync(Action<string,bool,string> report)
    {
        var dir=Path.Combine(AppContext.BaseDirectory,"test-data","rpc-"+Guid.NewGuid().ToString("N"));
        var config=AppConfig.Defaults(); foreach(var p in config.Profiles) p.Enabled=false;
        await using var engine=new MonitorEngine(config,dir);
        string name="NetPilot.Test."+Guid.NewGuid().ToString("N");
        using var lifetime=new CancellationTokenSource(TimeSpan.FromSeconds(8));
        var server=new ServiceRpc(engine,WindowsIdentity.GetCurrent().User!.Value,name).RunAsync(lifetime.Token);
        try
        {
            using var client=new NamedPipeClientStream(".",name,PipeDirection.InOut,PipeOptions.Asynchronous);
            await client.ConnectAsync(2000,lifetime.Token);
            await PipeMessages.WriteAsync(client,JsonData.Encode(new RpcRequest()),lifetime.Token);
            // A real scheduler may resume the reader after the server's buffered write.
            await Task.Delay(400,lifetime.Token);
            var reply=JsonData.Decode<RpcReply>(await PipeMessages.ReadAsync(client,4*1024*1024,lifetime.Token));
            if(!reply.Ok||reply.Status==null||reply.Status.Config.Profiles.Count!=3) throw new Exception("Incomplete status reply.");
            report("RPC buffered reply survives a delayed client reader",true,"");
        }
        catch(Exception ex) { report("RPC buffered reply survives a delayed client reader",false,ex.ToString()); }
        finally { lifetime.Cancel(); try {await server;} catch(OperationCanceledException) {} }

        string reconnectName="NetPilot.Test."+Guid.NewGuid().ToString("N");
        using var reconnectLifetime=new CancellationTokenSource(TimeSpan.FromSeconds(8));
        var reconnectServer=new ServiceRpc(engine,WindowsIdentity.GetCurrent().User!.Value,reconnectName).RunAsync(reconnectLifetime.Token);
        try
        {
            // A window may close or time out before it sends a complete request.
            using(var abandoned=new NamedPipeClientStream(".",reconnectName,PipeDirection.InOut,PipeOptions.Asynchronous))
                await abandoned.ConnectAsync(2000,reconnectLifetime.Token);
            for(int i=0;i<2;i++)
            {
                using var client=new NamedPipeClientStream(".",reconnectName,PipeDirection.InOut,PipeOptions.Asynchronous);
                await client.ConnectAsync(2000,reconnectLifetime.Token);
                await PipeMessages.WriteAsync(client,JsonData.Encode(new RpcRequest()),reconnectLifetime.Token);
                var reply=JsonData.Decode<RpcReply>(await PipeMessages.ReadAsync(client,4*1024*1024,reconnectLifetime.Token));
                if(!reply.Ok||reply.Status is null) throw new Exception("Status unavailable after an abandoned connection.");
            }
            report("RPC accepts subsequent requests after a client abandons its connection",true,"");
        }
        catch(Exception ex) { report("RPC accepts subsequent requests after a client abandons its connection",false,ex.ToString()); }
        finally { reconnectLifetime.Cancel(); try {await reconnectServer;} catch(OperationCanceledException) {} }

        string controlName="NetPilot.Test."+Guid.NewGuid().ToString("N");
        using var controlLifetime=new CancellationTokenSource(TimeSpan.FromSeconds(8));
        await engine.StartAsync();
        var controlServer=new ServiceRpc(engine,WindowsIdentity.GetCurrent().User!.Value,controlName).RunAsync(controlLifetime.Token);
        try
        {
            async Task<AppStatus> Command(string command)
            {
                using var client=new NamedPipeClientStream(".",controlName,PipeDirection.InOut,PipeOptions.Asynchronous);
                await client.ConnectAsync(2000,controlLifetime.Token);
                await PipeMessages.WriteAsync(client,JsonData.Encode(new RpcRequest {Command=command}),controlLifetime.Token);
                var reply=JsonData.Decode<RpcReply>(await PipeMessages.ReadAsync(client,4*1024*1024,controlLifetime.Token));
                if(!reply.Ok||reply.Status is null)throw new Exception(reply.Error??"No control reply.");
                return reply.Status;
            }
            if((await Command("pause")).Monitoring)throw new Exception("Service kept checking after pause.");
            if((await Command("probe")).Monitoring)throw new Exception("One-off check resumed a paused service.");
            if(!(await Command("resume")).Monitoring)throw new Exception("Service did not resume checks.");
            report("RPC pauses and resumes service checks and accepts one-off probes while paused",true,"");
        }
        catch(Exception ex){report("RPC pauses and resumes service checks and accepts one-off probes while paused",false,ex.ToString());}
        finally {controlLifetime.Cancel();try{await controlServer;}catch(OperationCanceledException){}}
    }
}
