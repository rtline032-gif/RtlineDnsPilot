namespace NetPilot;

public sealed record SelectionDecision(string? ProfileId, string Reason, bool AllUnavailable);

public static class Selection
{
    public static SelectionDecision Choose(AppConfig config, IReadOnlyList<ProfileStats> stats, string? current, DateTimeOffset switchedAt, DateTimeOffset now)
    {
        stats=stats.Select(s=>MonitoringTargets.View(s,config)!).ToArray();
        var enabled = config.Profiles.Where(p => p.Enabled).Select(p => p.Id).ToHashSet();
        var freshness=TimeSpan.FromSeconds(MonitoringTargets.FreshnessSeconds(config));
        bool Fresh(ProfileStats s) => s.Last != null && now - s.Last.At >= TimeSpan.Zero
            && now - s.Last.At <= freshness;
        bool ConfirmedSuccesses(ProfileStats s,int count)
        {
            if(s.ConsecutiveSuccesses<count || s.Samples.Count<count || !Fresh(s)) return false;
            // Several normal rounds can span more than the latest-result freshness.
            // Check their cadence so a pause cannot revive an old success streak.
            var next=s.Last!.At;
            for(int i=s.Samples.Count-1;i>=s.Samples.Count-count;i--)
            {
                var sample=s.Samples[i];var gap=next-sample.At;
                if(!sample.Success || sample.At>now || gap<TimeSpan.Zero || gap>freshness) return false;
                next=sample.At;
            }
            return true;
        }
        bool Healthy(ProfileStats s) => enabled.Contains(s.ProfileId) && Fresh(s) && s.Last!.Success;
        bool fastest=config.AutoSelection==AutoSelectionMode.Fast;
        IOrderedEnumerable<ProfileStats> Ranked(IEnumerable<ProfileStats> source)=>fastest
            ?source.OrderBy(s=>s.MeanMs).ThenByDescending(s=>s.SuccessPercent)
            :source.OrderByDescending(s=>s.SuccessPercent).ThenBy(s=>s.MeanMs);
        var healthy = stats.Where(Healthy).ToList();
        var active = stats.FirstOrDefault(s => s.ProfileId == current && enabled.Contains(s.ProfileId));
        bool allDown = !enabled.Any() || (stats.Where(s => enabled.Contains(s.ProfileId)).Count() == enabled.Count
            && stats.Where(s => enabled.Contains(s.ProfileId)).All(s => Fresh(s) && !s.Last!.Success));
        if(MonitoringTargets.ManualIsActive(config,now) && enabled.Contains(config.ManualProfileId!))
            return new(config.ManualProfileId,config.ManualUntil is null?"Канал зафиксирован вручную постоянно":"Канал зафиксирован вручную до "+config.ManualUntil.Value.ToLocalTime().ToString("HH:mm:ss"),allDown);
        if (config.PinnedProfileId is string preferred && healthy.Any(s => s.ProfileId == preferred))
            return new(preferred, "Выбран доступный предпочтительный канал", allDown);
        var best = Ranked(healthy).FirstOrDefault();
        if (current == null || !enabled.Contains(current)) return new(best?.ProfileId, fastest
            ?"Первый измеренный доступный канал с приоритетом скорости":"Первый измеренный доступный канал с приоритетом стабильности", allDown);
        if (active == null || !Fresh(active) || active.ConsecutiveFailures >= 3)
        {
            var alternate = Ranked(healthy.Where(s => s.ProfileId != current && ConfirmedSuccesses(s,2))).FirstOrDefault();
            return new(alternate?.ProfileId ?? current, alternate == null ? "Ожидание подтверждённого резервного канала"
                :fastest?"Аварийное переключение на самый быстрый подтверждённый канал":"Аварийное переключение на самый стабильный подтверждённый канал", allDown);
        }
        if (!active.Last!.Success) return new(current,"Сбой "+active.ConsecutiveFailures+" из 3 — пока остаёмся на канале",allDown);
        if (now - switchedAt < TimeSpan.FromMinutes(5)) return new(current,"Канал доступен; сравнение "+(fastest?"скорости":"стабильности")+" после "+switchedAt.AddMinutes(5).ToLocalTime().ToString("HH:mm:ss"),allDown);
        var better = Ranked(healthy.Where(s => s.ProfileId != current && ConfirmedSuccesses(s,5)
            && (fastest?s.MeanMs<=active.MeanMs*.8
                :s.SuccessPercent > active.SuccessPercent || s.SuccessPercent == active.SuccessPercent && s.MeanMs <= active.MeanMs * .8))).FirstOrDefault();
        return new(better?.ProfileId ?? current, better == null ? "Текущий канал доступен; подтверждённого улучшения пока нет"
            : !fastest && better.SuccessPercent>active.SuccessPercent ? "Подтверждён более стабильный канал"
            : "Подтверждённый выигрыш времени отклика не менее 20%", allDown);
    }
}
