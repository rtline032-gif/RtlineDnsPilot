namespace NetPilot.Tests;

public static class ProbeCadenceTests
{
    public static async Task RunAsync(Action<string, bool, string> report)
    {
        async Task Check(string name, Func<Fixture, Task> test, int interval = 20, bool allTargets = false)
        {
            try
            {
                await using var fixture = new Fixture(interval, allTargets);
                await test(fixture);
                report(name, true, "");
            }
            catch (Exception ex) { report(name, false, ex.Message); }
        }

        await Check("automatic rounds defer long-failing reserves without adding measurements", async f =>
        {
            await f.SeedFailuresAsync(-180, -90, -1);
            var before = f.ReserveChecks;
            await f.AutomaticRoundAsync();
            Require(f.Calls.SequenceEqual([f.PrimaryId]), "The chronically failing reserve was probed while a working channel remained.");
            Require(f.ReserveChecks == before, "The skipped check changed the reserve statistics.");
        });
        await Check("brief failures retain the ordinary monitoring cadence", async f =>
        {
            await f.SeedFailuresAsync(-60, -30, -1);
            await f.AutomaticRoundAsync();
            Require(f.Calls.Contains(f.ReserveId), "A short outage slowed the next check.");
        });
        await Check("a recovery at the deferred deadline restores the ordinary cadence", async f =>
        {
            await f.SeedFailuresAsync(-900, -780, -660);
            f.ReserveWorks = true;
            await f.AutomaticRoundAsync();
            Require(f.Calls.Contains(f.ReserveId), "A due reserve was not probed.");
            f.Calls.Clear();
            await f.AutomaticRoundAsync();
            Require(f.Calls.Contains(f.ReserveId), "A successful delayed probe did not restore the regular cadence.");
        });
        await Check("slow automatic intervals still defer a long-failing reserve for multiple rounds", async f =>
        {
            await f.SeedFailuresAsync(-1500, -1320, -1200);
            await f.AutomaticRoundAsync();
            Require(f.Calls.SequenceEqual([f.PrimaryId]), "A long base interval eliminated the reduced cadence.");
        }, interval: 600);
        await Check("losing the last working channel triggers the deferred reserve in the same round", async f =>
        {
            await f.SeedFailuresAsync(-180, -90, -1);
            f.PrimaryWorks = false;
            f.ReserveWorks = true;
            await f.AutomaticRoundAsync();
            Require(f.Calls.SequenceEqual([f.PrimaryId, f.ReserveId]), "The last working channel failed without an immediate reserve probe.");
            Require(!f.Engine.GetStatus().AllUnavailable, "A recovered reserve was ignored in the outage decision.");
        });
        await Check("during a full outage every round checks all enabled channels", async f =>
        {
            await f.SeedFailuresAsync(-180, -90, -1);
            f.PrimaryWorks = false;
            await f.AutomaticRoundAsync();
            Require(f.Engine.GetStatus().AllUnavailable && f.Calls.Contains(f.ReserveId), "Outage detection did not check the reserve.");
            f.Calls.Clear();
            await f.AutomaticRoundAsync();
            Require(f.Calls.Contains(f.ReserveId), "A deferred reserve was skipped during a full outage.");
        });
        await Check("a completed two-wave outage counts the first wave even when the reserve takes longer", async f =>
        {
            await f.SeedFailuresAsync(-180, -90, -1);
            f.PrimaryWorks = false;
            // Nine targets per wave and 30-second timeouts can occupy six
            // batches instead of the five needed in a single uninterrupted scan.
            f.PrimaryFailureAt = DateTimeOffset.UtcNow.AddSeconds(-175);
            await f.AutomaticRoundAsync();
            Require(f.Calls.Count == 18 && f.Engine.GetStatus().AllUnavailable,
                "An early failed measurement in a completed emergency round did not count toward the outage.");
        }, interval: 10, allTargets: true);
        await Check("a manual check still probes a deferred reserve", async f =>
        {
            await f.SeedFailuresAsync(-180, -90, -1);
            await f.Engine.ProbeNowAsync();
            Require(f.Calls.Contains(f.ReserveId), "A manual check skipped the deferred reserve.");
        });
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    private sealed class Fixture : IAsyncDisposable
    {
        private readonly DateTimeOffset _now = DateTimeOffset.UtcNow;
        private DateTimeOffset? _reserveFailureAt;
        public MonitorEngine Engine { get; }
        public string PrimaryId { get; }
        public string ReserveId { get; }
        public bool PrimaryWorks { get; set; } = true;
        public bool ReserveWorks { get; set; }
        public DateTimeOffset? PrimaryFailureAt { get; set; }
        public List<string> Calls { get; } = [];
        public long ReserveChecks => Engine.GetStatus().Statistics.Single(s => s.ProfileId == ReserveId).TotalChecks;

        public Fixture(int interval, bool allTargets)
        {
            var config = AppConfig.Defaults();
            config.Profiles = config.Profiles.Take(2).ToList();
            foreach (var profile in config.Profiles) profile.Enabled = true;
            config.IntervalSeconds = interval;
            if (allTargets)
            {
                config.TimeoutSeconds = 30;
                config.MonitorGemini = true;
                config.MonitorTailscale = true;
                config.AdditionalTargets = Enumerable.Range(1, MonitoringTargets.MaxAdditionalTargets)
                    .Select(i => new MonitoringEndpoint { Name = "Site " + i, Url = $"https://site{i}.example.test/" }).ToList();
            }
            PrimaryId = config.Profiles[0].Id;
            ReserveId = config.Profiles[1].Id;
            var directory = Path.Combine(AppContext.BaseDirectory, "test-data", "cadence-" + Guid.NewGuid().ToString("N"));
            Engine = new MonitorEngine(config, directory, false, CheckAsync);
        }

        public async Task SeedFailuresAsync(params int[] secondsAgo)
        {
            foreach (int offset in secondsAgo)
            {
                _reserveFailureAt = _now.AddSeconds(offset);
                await Engine.ProbeNowAsync();
            }
            _reserveFailureAt = null;
            Calls.Clear();
        }

        public Task AutomaticRoundAsync() => Engine.ProbeNowCoreAsync(requested: false);

        private Task<ProbeResult> CheckAsync(ChannelProfile profile, string url, TimeSpan timeout, CancellationToken token)
        {
            Calls.Add(profile.Id);
            bool works = profile.Id == PrimaryId ? PrimaryWorks : ReserveWorks;
            var failedAt = profile.Id == PrimaryId ? PrimaryFailureAt : _reserveFailureAt;
            return Task.FromResult(new ProbeResult
            {
                ProfileId = profile.Id,
                At = !works && failedAt is { } failure ? failure : DateTimeOffset.UtcNow,
                Success = works,
                Stage = works ? "http" : "dns",
                HttpStatus = works ? 200 : null
            });
        }

        public ValueTask DisposeAsync() => Engine.DisposeAsync();
    }
}
