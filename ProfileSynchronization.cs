namespace NetPilot;

internal static class ProfileSynchronization
{
    public static AppConfig ForConnection(AppConfig local, AppConfig service, bool explicitDraft)
    {
        local.Validate(); service.Validate();
        if (explicitDraft)
        {
            // Preserve an explicit edit, including deletions. Reuse service identities
            // for equivalent channels so existing measurements remain associated.
            var desired = JsonData.Clone(local);
            foreach (var profile in desired.Profiles)
            {
                var known = service.Profiles.FirstOrDefault(p => Guid.Parse(p.Id) == Guid.Parse(profile.Id))
                    ?? service.Profiles.FirstOrDefault(p => AppConfig.SameEffectiveConfiguration(p, profile));
                if (known is null) continue;
                var oldId = profile.Id;
                profile.Id = known.Id;
                if (desired.PinnedProfileId == oldId) desired.PinnedProfileId = known.Id;
                if (desired.ManualProfileId == oldId) desired.ManualProfileId = known.Id;
            }
            desired.Validate();
            return desired;
        }

        // Old releases kept independent local/service databases. Merge missing local
        // channels once, coalescing defaults whose IDs were generated independently.
        var merged = JsonData.Clone(service);
        foreach (var profile in local.Profiles)
            if (!merged.Profiles.Any(p => Guid.Parse(p.Id) == Guid.Parse(profile.Id) || AppConfig.SameEffectiveConfiguration(p, profile)))
                merged.Profiles.Add(JsonData.Clone(profile));
        if (!service.ManagementRequested && string.IsNullOrEmpty(service.AdapterId) && !string.IsNullOrEmpty(local.AdapterId))
            merged.AdapterId = local.AdapterId;
        merged.Validate();
        return merged;
    }

    public static bool SameConfig(AppConfig first, AppConfig second)
    {
        var a = JsonData.Clone(first); var b = JsonData.Clone(second);
        // Management is service-owned; a local file never authorizes a DNS mutation.
        a.ManagementRequested = b.ManagementRequested = false;
        a.MonitoringPaused=b.MonitoringPaused=false;
        return JsonData.Encode(a) == JsonData.Encode(b);
    }

    public static AppStatus WithDraft(AppStatus service, AppConfig desired)
    {
        var result = JsonData.Clone(service);
        result.Config = JsonData.Clone(desired);
        result.Config.ManagementRequested = service.Config.ManagementRequested;
        result.Config.MonitoringPaused=service.Config.MonitoringPaused;
        bool sameTargets=MonitoringTargets.Key(service.Config)==MonitoringTargets.Key(desired);
        bool Unchanged(string id) => desired.Profiles.Any(p => p.Id == id && service.Config.Profiles.Any(s => s.Id == id && AppConfig.SameEffectiveConfiguration(p, s)));
        result.Statistics.RemoveAll(s => !sameTargets || !Unchanged(s.ProfileId));
        if (result.ActiveProfileId is { } active && !Unchanged(active)) result.ActiveProfileId = null;
        var enabled = desired.Profiles.Where(p => p.Enabled).ToArray();
        result.AllUnavailable = service.AllUnavailable && enabled.Length > 0 && enabled.All(p => result.Statistics.Any(s => s.ProfileId == p.Id && s.Last is { Success: false }));
        return result;
    }
}
