namespace NetPilot;

internal static class ChannelTableOrder
{
    internal static IReadOnlyList<(ChannelProfile Profile, ProfileStats? Statistics)> Build(
        AppConfig config, IReadOnlyList<ProfileStats> statistics, DateTimeOffset now)
    {
        var freshness = TimeSpan.FromSeconds(MonitoringTargets.FreshnessSeconds(config));
        return config.Profiles.Select((profile, index) =>
        {
            var view = MonitoringTargets.View(statistics.FirstOrDefault(s => s.ProfileId == profile.Id), config);
            var working = profile.Enabled && view?.Last is { Success: true } last
                && now - last.At >= TimeSpan.Zero && now - last.At <= freshness;
            return (Profile: profile, Statistics: view, Working: working, Index: index);
        })
        .OrderByDescending(row => row.Working)
        .ThenByDescending(row => row.Working ? row.Statistics!.SuccessPercent : 0)
        .ThenBy(row => row.Working ? row.Statistics!.MeanMs : 0)
        .ThenBy(row => row.Index)
        .Select(row => (row.Profile, row.Statistics)).ToArray();
    }
}
