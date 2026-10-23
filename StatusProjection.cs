namespace NetPilot;

/// <summary>
/// A bounded UI/RPC view. The engine retains its full samples for selection and
/// persistence; status consumers receive all numeric points and current details.
/// </summary>
public static class StatusProjection
{
    public static List<ProfileStats> Statistics(IReadOnlyList<ProfileStats> source)
    {
        var result = new List<ProfileStats>(source.Count);
        foreach (var statistics in source)
        {
            var projected = new ProfileStats
            {
                ProfileId = statistics.ProfileId, TotalChecks = statistics.TotalChecks, TotalSuccesses = statistics.TotalSuccesses,
                ConsecutiveFailures = statistics.ConsecutiveFailures, ConsecutiveSuccesses = statistics.ConsecutiveSuccesses,
                Samples = new List<ProbeResult>(statistics.Samples.Count)
            };
            for (var index = 0; index < statistics.Samples.Count; index++)
            {
                var sample = statistics.Samples[index];
                bool latest = index == statistics.Samples.Count - 1;
                projected.Samples.Add(new ProbeResult
                {
                    MonitoringKey = sample.MonitoringKey, ProfileId = sample.ProfileId, At = sample.At,
                    Success = sample.Success, DnsMs = sample.DnsMs, HttpsMs = sample.HttpsMs, HttpStatus = sample.HttpStatus,
                    Stage = Clip(sample.Stage, 64), Detail = latest ? Clip(sample.Detail, 160) : "",
                    CountryCode = latest ? sample.CountryCode : null,
                    CountrySource = latest ? sample.CountrySource : ConnectionCountrySource.None,
                    ServiceDetailsOmitted = !latest || sample.ServiceDetailsOmitted,
                    Services = latest ? sample.Services.Select(service => new ServiceProbeResult
                    {
                        ServiceId = service.ServiceId, At = service.At, Success = service.Success,
                        DnsMs = service.DnsMs, HttpsMs = service.HttpsMs, HttpStatus = service.HttpStatus,
                        Stage = Clip(service.Stage, 64), Detail = Clip(service.Detail, 128),
                        CountryCode = service.CountryCode, CountrySource = service.CountrySource
                    }).ToList() : []
                });
            }
            result.Add(projected);
        }
        return result;
    }

    public static List<LogEntry> Events(IReadOnlyList<LogEntry> source) => source.Select(entry => new LogEntry
    { At = entry.At, Kind = entry.Kind, Message = Clip(entry.Message, 128) }).ToList();

    private static string Clip(string text, int maximum)
    {
        if (text.Length <= maximum) return text;
        // Avoid producing invalid UTF16 when a diagnostic ends at an emoji.
        if (maximum > 0 && char.IsHighSurrogate(text[maximum - 1])) maximum--;
        return text[..maximum];
    }
}
