using System.Text.Json.Nodes;
using System.Net;
using System.Net.Http.Headers;
using System.Text;

namespace NetPilot.Tests;

public static class CountryTests
{
    public static async Task RunAsync(Action<string,bool,string> report)
    {
        async Task Check(string name,Func<Task> action)
        { try {await action();report(name,true,"");} catch(Exception ex) {report(name,false,ex.Message);} }
        static void Require(bool value,string reason) {if(!value) throw new InvalidOperationException(reason);}
        static ProbeResult WithCountry(string id,string country) => JsonData.Decode<ProbeResult>(
            "{\"ProfileId\":\""+id+"\",\"Success\":true,\"HttpStatus\":200,\"Stage\":\"http\",\"CountryCode\":\""+country+"\",\"CountrySource\":\"SiteTrace\"}");

        await Check("per-object monitoring preserves each site's own country without copying it to another site",async ()=>
        {
            var config=AppConfig.Defaults();config.MonitorGemini=true;config.Profiles=config.Profiles.Take(1).ToList();
            var results=await MonitoringTargets.CheckAsync(config,(profile,url,timeout,token)=>
                Task.FromResult(WithCountry(profile.Id,url==MonitoringTargets.GeminiUrl?"DE":"NL")),CancellationToken.None);
            var json=JsonNode.Parse(JsonData.Encode(results.Single()))!;
            var sites=json["Services"]!.AsArray();
            Require(sites.Single(s=>s!["ServiceId"]!.GetValue<string>()==MonitoringTargets.ChatGpt)!["CountryCode"]?.GetValue<string>()=="NL","ChatGPT's measured country was lost.");
            Require(sites.Single(s=>s!["ServiceId"]!.GetValue<string>()==MonitoringTargets.Gemini)!["CountryCode"]?.GetValue<string>()=="DE","Gemini's independent country was lost or replaced.");
            Require(json["CountryCode"] is null,"A multi-site aggregate invented one shared country.");
        });
        await Check("current country evidence survives JSON and the bounded status projection",()=>
        {
            var raw=WithCountry(Guid.NewGuid().ToString("N"),"NL");
            var service=JsonData.Decode<ServiceProbeResult>("{\"ServiceId\":\"chatgpt\",\"Success\":true,\"CountryCode\":\"NL\",\"CountrySource\":\"SiteTrace\"}");
            raw.Services=[service];
            var projected=StatusProjection.Statistics([new ProfileStats {ProfileId=raw.ProfileId,Samples=[raw]}]);
            var json=JsonNode.Parse(JsonData.Encode(projected[0].Last!))!;
            Require(json["CountryCode"]?.GetValue<string>()=="NL" && json["Services"]![0]!["CountryCode"]?.GetValue<string>()=="NL","Country was dropped in the RPC view.");
            Require(json["Services"]![0]!["CountrySource"]?.GetValue<string>()=="SiteTrace","The country source was dropped.");
            return Task.CompletedTask;
        });
        await Check("legacy measurements never invent a country",()=>
        {
            var raw=JsonData.Decode<ProbeResult>("{\"Success\":true,\"HttpStatus\":200}");
            var legacy=MonitoringTargets.Service(raw,MonitoringTargets.ChatGpt)!;
            Require(JsonNode.Parse(JsonData.Encode(legacy))!["CountryCode"] is null,"Legacy HTTP success was converted into a country.");
            return Task.CompletedTask;
        });

        var uri=new Uri(MonitoringTargets.DefaultChatGptUrl);
        static HttpResponseMessage Response(string text,string contentType="text/plain") => new(HttpStatusCode.OK) {Content=new StringContent(text,Encoding.UTF8,contentType)};
        const string trace="h=chatgpt.com\nvisit_scheme=https\nip=203.0.113.17\nloc=NL\ncolo=FRA\n";
        await Check("country comes from the checked site's trace, not its CDN data center or client IP",async ()=>
        {
            using var response=Response(trace);
            var evidence=await ConnectionCountry.ReadAsync(uri,response,CancellationToken.None);
            Require(evidence is {Code:"NL",Source:ConnectionCountrySource.SiteTrace},"The site's explicit visitor country was not recognized.");
            var persisted=JsonData.Encode(evidence);
            Require(!persisted.Contains("203.0.113.17")&&!persisted.Contains("FRA"),"Raw trace or client IP leaked into country metadata.");
        });
        await Check("trace country requires HTTPS, exact trace path and the checked host",async ()=>
        {
            foreach(var request in new[] {new Uri("http://chatgpt.com/cdn-cgi/trace"),new Uri("https://chatgpt.com/"),new Uri("https://other.example/cdn-cgi/trace")})
            {using var response=Response(trace);Require(await ConnectionCountry.ReadAsync(request,response,CancellationToken.None) is null,"Foreign or unrelated trace was trusted.");}
        });
        await Check("trace rejects missing, duplicate, invalid and contradictory location fields",async ()=>
        {
            foreach(var body in new[] {trace.Replace("loc=NL\n",""),trace+"loc=DE\n",trace+"h=chatgpt.com\n",trace.Replace("visit_scheme=https","visit_scheme=http"),
                trace.Replace("loc=NL","loc=XX"),trace.Replace("loc=NL","loc=T1"),trace.Replace("loc=NL","loc=ZZ"),trace.Replace("loc=NL","loc=Netherlands"),trace+"broken line\n"})
            {using var response=Response(body);Require(await ConnectionCountry.ReadAsync(uri,response,CancellationToken.None) is null,"Malformed or unknown location was accepted.");}
        });
        await Check("site language, HTML, arbitrary JSON country and request-only Cloudflare headers never imply visitor country",async ()=>
        {
            foreach(var contentType in new[] {"text/html","application/json"})
            {using var response=Response(trace,contentType);response.Headers.TryAddWithoutValidation("CF-IPCountry","DE");Require(await ConnectionCountry.ReadAsync(uri,response,CancellationToken.None) is null,"Unrelated page metadata was treated as a client country.");}
            using var page=Response("{\"country\":\"US\",\"language\":\"en-US\"}","application/json");
            Require(await ConnectionCountry.ReadAsync(new Uri(MonitoringTargets.GeminiUrl),page,CancellationToken.None) is null,"Gemini inherited an arbitrary country field.");
        });
        await Check("custom site can explicitly report its client country in X-Client-Country",async ()=>
        {
            using var response=Response("{}","application/json");response.Headers.TryAddWithoutValidation("X-Client-Country"," de ");
            Require(await ConnectionCountry.ReadAsync(new Uri("https://service.example/health"),response,CancellationToken.None) is {Code:"DE",Source:ConnectionCountrySource.ClientCountryHeader},"Explicit site country header was ignored.");
        });
        await Check("country metadata is useful on a refused site without changing HTTP status",async ()=>
        {
            using var response=Response("Access denied","text/html");response.StatusCode=HttpStatusCode.Forbidden;response.Headers.TryAddWithoutValidation("X-Client-Country","RU");
            Require(await ConnectionCountry.ReadAsync(uri,response,CancellationToken.None) is {Code:"RU"} && response.StatusCode==HttpStatusCode.Forbidden,"Country extraction changed a site's refusal or lost its country.");
        });
        await Check("multiple or conflicting explicit country sources stay unknown",async ()=>
        {
            using var duplicate=Response(trace);duplicate.Headers.TryAddWithoutValidation("X-Client-Country",new[] {"NL","DE"});
            Require(await ConnectionCountry.ReadAsync(uri,duplicate,CancellationToken.None) is null,"Ambiguous response headers were accepted.");
            using var conflict=Response(trace);conflict.Headers.TryAddWithoutValidation("X-Client-Country","DE");
            Require(await ConnectionCountry.ReadAsync(uri,conflict,CancellationToken.None) is null,"Conflicting same-site sources were hidden.");
        });
        await Check("a response redirected to another origin cannot report this site's country",async ()=>
        {
            using var response=Response(trace);response.Headers.TryAddWithoutValidation("X-Client-Country","NL");
            response.RequestMessage=new HttpRequestMessage(HttpMethod.Get,"https://other.example/cdn-cgi/trace");
            Require(await ConnectionCountry.ReadAsync(uri,response,CancellationToken.None) is null,"Another origin's country was attributed to this site.");
        });
        await Check("oversized and compressed trace bodies remain unknown",async ()=>
        {
            using var oversized=Response(trace+new string('x',ConnectionCountry.MaxBodyBytes));
            Require(await ConnectionCountry.ReadAsync(uri,oversized,CancellationToken.None) is null,"Oversized trace body was consumed.");
            using var compressed=Response(trace);compressed.Content.Headers.ContentEncoding.Add("gzip");
            Require(await ConnectionCountry.ReadAsync(uri,compressed,CancellationToken.None) is null,"Compressed bytes were parsed as a plain trace.");
        });
        await Check("an unknown-length trace cannot make optional metadata read unbounded",async ()=>
        {
            using var stream=new CountingStream(Encoding.UTF8.GetBytes(trace+new string('x',ConnectionCountry.MaxBodyBytes*2)));
            using var response=new HttpResponseMessage(HttpStatusCode.OK) {Content=new UnknownLengthContent(stream)};
            Require(await ConnectionCountry.ReadAsync(uri,response,CancellationToken.None) is null && stream.ReadBytes<=ConnectionCountry.MaxBodyBytes,"Trace reader exceeded its body budget or accepted a truncated body.");
        });
        await Check("slow optional country reads finish within their own deadline and preserve HTTP reachability",async ()=>
        {
            using var response=new HttpResponseMessage(HttpStatusCode.OK) {Content=new UnknownLengthContent(new SlowStream())};
            var pending=ConnectionCountry.ReadAsync(uri,response,CancellationToken.None);
            var done=await Task.WhenAny(pending,Task.Delay(TimeSpan.FromSeconds(2)));
            Require(done==pending && await pending is null && response.IsSuccessStatusCode,"Optional metadata blocked the probe or changed reachability.");
        });
        await Check("user cancellation interrupts the optional country read",async ()=>
        {
            using var cancellation=new CancellationTokenSource();
            using var response=new HttpResponseMessage(HttpStatusCode.OK) {Content=new UnknownLengthContent(new SlowStream())};
            var pending=ConnectionCountry.ReadAsync(uri,response,cancellation.Token);cancellation.Cancel();
            try {await pending;throw new InvalidOperationException("Cancelled country reader completed normally.");}
            catch(OperationCanceledException) when(cancellation.IsCancellationRequested) { }
        });
        await Check("body transport failure and invalid UTF8 leave country unknown",async ()=>
        {
            using var broken=new HttpResponseMessage(HttpStatusCode.OK) {Content=new UnknownLengthContent(new BrokenStream())};
            Require(await ConnectionCountry.ReadAsync(uri,broken,CancellationToken.None) is null && broken.IsSuccessStatusCode,"Optional body failure changed HTTP availability.");
            using var invalid=new HttpResponseMessage(HttpStatusCode.OK) {Content=new ByteArrayContent([255,254,255])};invalid.Content.Headers.ContentType=new MediaTypeHeaderValue("text/plain");
            Require(await ConnectionCountry.ReadAsync(uri,invalid,CancellationToken.None) is null,"Invalid text was accepted as country evidence.");
        });
        await Check("display keeps an ISO code and never turns a missing or invalid value into a country",()=>
        {
            Require(ConnectionCountry.Display("NL").Contains("NL") && ConnectionCountry.Display(null)=="Не определена" && ConnectionCountry.Display("ZZ")=="Не определена","Invalid country display.");
            return Task.CompletedTask;
        });
    }

    private sealed class UnknownLengthContent : HttpContent
    {
        private readonly Stream stream;
        public UnknownLengthContent(Stream stream) {this.stream=stream;Headers.ContentType=new MediaTypeHeaderValue("text/plain");}
        protected override bool TryComputeLength(out long length) {length=0;return false;}
        protected override Task SerializeToStreamAsync(Stream destination,TransportContext? context)=>stream.CopyToAsync(destination);
        protected override Task<Stream> CreateContentReadStreamAsync()=>Task.FromResult(stream);
        protected override Task<Stream> CreateContentReadStreamAsync(CancellationToken token)=>Task.FromResult(stream);
        protected override void Dispose(bool disposing) {if(disposing) stream.Dispose();base.Dispose(disposing);}
    }
    private sealed class CountingStream(byte[] bytes) : MemoryStream(bytes)
    {
        public int ReadBytes {get;private set;}
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer,CancellationToken token=default)
        {var count=await base.ReadAsync(buffer,token);ReadBytes+=count;return count;}
    }
    private class SlowStream : Stream
    {
        public override bool CanRead=>true;public override bool CanSeek=>false;public override bool CanWrite=>false;
        public override long Length=>throw new NotSupportedException();public override long Position {get=>throw new NotSupportedException();set=>throw new NotSupportedException();}
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer,CancellationToken token=default) {await Task.Delay(Timeout.Infinite,token);return 0;}
        public override int Read(byte[] buffer,int offset,int count)=>throw new NotSupportedException();
        public override void Flush()=>throw new NotSupportedException();public override long Seek(long offset,SeekOrigin origin)=>throw new NotSupportedException();
        public override void SetLength(long value)=>throw new NotSupportedException();public override void Write(byte[] buffer,int offset,int count)=>throw new NotSupportedException();
    }
    private sealed class BrokenStream : SlowStream
    {public override ValueTask<int> ReadAsync(Memory<byte> buffer,CancellationToken token=default)=>ValueTask.FromException<int>(new IOException("Synthetic body failure"));}
}
