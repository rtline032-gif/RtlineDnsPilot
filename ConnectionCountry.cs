using System.Globalization;
using System.Text;

namespace NetPilot;

public enum ConnectionCountrySource { None, SiteTrace, ClientCountryHeader }
public readonly record struct CountryEvidence(string Code, ConnectionCountrySource Source);

/// <summary>Only the checked site's own explicit client-country evidence; no IP geolocation.</summary>
public static class ConnectionCountry
{
    public const int MaxBodyBytes=4096;
    public const int ReadTimeoutMilliseconds=500;

    public static async Task<CountryEvidence?> ReadAsync(Uri requestUri,HttpResponseMessage response,CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if(requestUri.Scheme!=Uri.UriSchemeHttps || response.RequestMessage?.RequestUri is { } actual
            && (!string.Equals(actual.IdnHost,requestUri.IdnHost,StringComparison.OrdinalIgnoreCase) || actual.Port!=requestUri.Port || actual.Scheme!=requestUri.Scheme)) return null;
        CountryEvidence? header=null;
        if(response.Headers.TryGetValues("X-Client-Country",out var values))
        {
            var items=values.ToArray();
            if(items.Length!=1 || Normalize(items[0].Trim()) is not { } code) return null;
            header=new CountryEvidence(code,ConnectionCountrySource.ClientCountryHeader);
        }
        if(!response.IsSuccessStatusCode || requestUri.AbsolutePath!="/cdn-cgi/trace" || requestUri.Query.Length!=0
            || !string.Equals(response.Content.Headers.ContentType?.MediaType,"text/plain",StringComparison.OrdinalIgnoreCase)
            || response.Content.Headers.ContentEncoding.Count>0 || response.Content.Headers.ContentLength is > MaxBodyBytes) return header;

        using var deadline=CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(ReadTimeoutMilliseconds);
        try
        {
            using var stream=await response.Content.ReadAsStreamAsync(deadline.Token).ConfigureAwait(false);
            using var memory=new MemoryStream();
            var buffer=new byte[1024];
            bool complete=false;
            while(memory.Length<MaxBodyBytes)
            {
                var count=await stream.ReadAsync(buffer.AsMemory(0,Math.Min(buffer.Length,MaxBodyBytes-(int)memory.Length)),deadline.Token).ConfigureAwait(false);
                if(count==0) {complete=true;break;}
                memory.Write(buffer,0,count);
            }
            if(!complete && response.Content.Headers.ContentLength!=memory.Length) return header;
            var trace=ParseTrace(requestUri,new UTF8Encoding(false,true).GetString(memory.ToArray()));
            if(trace is { } value && header is { } explicitCountry && value.Code!=explicitCountry.Code) return null;
            return trace??header;
        }
        catch(OperationCanceledException) when(token.IsCancellationRequested) {throw;}
        catch(Exception ex) when(ex is IOException or HttpRequestException or OperationCanceledException or DecoderFallbackException) {return header;}
    }

    private static CountryEvidence? ParseTrace(Uri uri,string text)
    {
        var fields=new Dictionary<string,string>(StringComparer.Ordinal);
        foreach(var line in text.Split('\n'))
        {
            var item=line.TrimEnd('\r');
            if(item.Length==0) continue;
            var split=item.IndexOf('=');
            if(split<=0) return null;
            var key=item[..split];
            if(key is "h" or "visit_scheme" or "loc")
                if(!fields.TryAdd(key,item[(split+1)..])) return null;
        }
        if(!fields.TryGetValue("h",out var host) || !(string.Equals(host,uri.IdnHost,StringComparison.OrdinalIgnoreCase)
                || string.Equals(host,uri.Authority,StringComparison.OrdinalIgnoreCase))
            || !fields.TryGetValue("visit_scheme",out var scheme) || scheme!="https"
            || !fields.TryGetValue("loc",out var location) || Normalize(location) is not { } code) return null;
        return new CountryEvidence(code,ConnectionCountrySource.SiteTrace);
    }

    public static string Display(string? code)
    {
        if(Normalize(code) is not { } value) return "Не определена";
        var region=new RegionInfo(value);
        return region.DisplayName+" ("+value+")";
    }
    public static string SourceLabel(ConnectionCountrySource source) => source switch
    {
        ConnectionCountrySource.SiteTrace => "Ответ сайта /cdn-cgi/trace",
        ConnectionCountrySource.ClientCountryHeader => "Заголовок сайта X-Client-Country",
        _ => "Сайт не сообщил страну подключения"
    };
    private static string? Normalize(string? code)
    {
        if(code is null || code.Length!=2 || code.Any(c=>c is not (>= 'A' and <= 'Z') and not (>= 'a' and <= 'z'))) return null;
        var value=code.ToUpperInvariant();
        if(value=="XX") return null;
        try {return new RegionInfo(value).TwoLetterISORegionName==value?value:null;}
        catch(ArgumentException) {return null;}
    }
}
