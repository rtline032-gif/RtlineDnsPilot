namespace NetPilot;

public sealed record ProfileImportResult(AppConfig Config, int Added, int Duplicates, int Renamed);

/// <summary>Adds imported profiles without replacing existing channels or selection settings.</summary>
public static class ProfileImport
{
    internal static void ValidateInput(IReadOnlyList<ChannelProfile>? profiles)
    {
        if (profiles is null || profiles.Count is < 1 or > 32)
            throw new ArgumentException("В файле должно быть от 1 до 32 каналов.");
        foreach (var profile in profiles)
        {
            if (profile is null) throw new ArgumentException("В списке каналов есть пустая запись.");
            profile.Validate();
        }
    }

    public static ProfileImportResult Merge(AppConfig current, IReadOnlyList<ChannelProfile> imported)
    {
        current.Validate();
        ValidateInput(imported);
        var result = JsonData.Clone(current);
        int added = 0, duplicates = 0, renamed = 0;
        foreach (var source in imported)
        {
            if (result.Profiles.Any(p => AppConfig.SameEffectiveConfiguration(p, source)))
            {
                duplicates++;
                continue;
            }
            var profile = JsonData.Clone(source);
            if (result.Profiles.Any(p => Guid.Parse(p.Id) == Guid.Parse(profile.Id)))
                profile.Id = Guid.NewGuid().ToString("N");
            var originalName = profile.Name.Trim();
            profile.Name = originalName;
            for (int suffix = 2; result.Profiles.Any(p => StringComparer.OrdinalIgnoreCase.Equals(p.Name.Trim(), profile.Name)); suffix++)
            {
                var ending = " (импорт " + suffix + ")";
                profile.Name = originalName[..Math.Min(originalName.Length, 100 - ending.Length)] + ending;
            }
            if (profile.Name != originalName) renamed++;
            result.Profiles.Add(profile);
            added++;
        }
        if (result.Profiles.Count > 32)
            throw new ArgumentException($"После добавления получилось бы {result.Profiles.Count} каналов; поддерживается не больше 32. Существующий список не изменён. Освободите место или уменьшите файл импорта.");
        result.Validate();
        return new(result, added, duplicates, renamed);
    }
}
