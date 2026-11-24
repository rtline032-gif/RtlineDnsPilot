namespace NetPilot;

/// <summary>A public, dated catalog shipped with the reviewed application package.</summary>
public static class BundledChannels
{
    // Metadata-only corrections do not advance the effective-configuration
    // revision: accepting a new revision intentionally restores missing entries.
    public const string Revision = "2026-11-20";
    public const string PreviousRevision = "2026-11-19-r3";
    private const int OriginalDefaultCount = 3;
    private static readonly ChannelProfile[] RetiredProfiles =
    [
        new() { Id = "e3ec8bf744a8fe283a37af3eff01a5a8", Name = "Xbox DNS", Mode = DnsMode.Doh,
            Servers = ["111.88.96.54", "111.88.96.55"], DohUrl = "https://xbox-dns.ru/dns-query" },
        new() { Id = "7d944ef142cedb5394a5d34ca882a0ed", Name = "Xbox DNS (UDP/TCP)", Servers = ["111.88.96.54", "111.88.96.55"] },
        new() { Id = "67c906863ac4f756f8f42c7035ddd4a0", Name = "Bezmezhau (UDP/TCP)", Servers = ["181.215.40.49", "31.58.216.201"] },
        new() { Id = "67c906863ac4f756f8f42c7035ddd4a0", Name = "Bezmezhau (UDP/TCP)", Servers = ["143.20.64.55", "31.58.216.201"] },
        new() { Id = "9bbc21a6a4d095d4deaebcce41687df5", Name = "Comss.one (DoH)", Mode = DnsMode.Doh,
            Servers = ["195.133.25.16"], DohUrl = "https://dns.comss.one/dns-query" },
        new() { Id = "9bbc21a6a4d095d4deaebcce41687df5", Name = "Comss.one (DoH)", Mode = DnsMode.Doh,
            Servers = ["141.133.184.24", "141.133.183.77"], DohUrl = "https://dns.comss.one/dns-query" },
        new() { Id = "cb272119b9c49dbefa033fd6bb25112a", Name = "Comss.one (DoH, резерв)", Mode = DnsMode.Doh,
            Servers = ["83.220.169.155", "212.109.195.93"], DohUrl = "https://dns.comss.one/dns-query" },
        new() { Id = "cb272119b9c49dbefa033fd6bb25112a", Name = "Comss.one (DoH, резерв)", Mode = DnsMode.Doh,
            Servers = ["109.73.193.146"], DohUrl = "https://dns.comss.one/dns-query" },
        new() { Id = "11f4b140fb69f1402a081215f5b54a55", Name = "Comss.one (UDP/TCP)", Servers = ["83.220.169.155", "212.109.195.93"] },
        new() { Id = "0ae6dc27b81e635ddd6cc1f3920feaf1", Name = "GeoHide (UDP/TCP)", Servers = ["159.194.200.33", "37.230.192.51"] }
    ];

    internal static bool IsRetiredEffectiveConfiguration(ChannelProfile profile) =>
        RetiredProfiles.Any(retired => AppConfig.SameEffectiveConfiguration(profile, retired));

    internal static bool IsRetiredProfile(ChannelProfile profile) => RetiredProfiles.Any(retired =>
        Guid.Parse(profile.Id) == Guid.Parse(retired.Id)
        && StringComparer.OrdinalIgnoreCase.Equals(profile.Name.Trim(), retired.Name)
        && AppConfig.SameEffectiveConfiguration(profile, retired));

    internal static void RemoveRetiredProfiles(AppConfig config)
    {
        var retiredIds = config.Profiles.Where(IsRetiredProfile).Select(profile => profile.Id).ToHashSet();
        config.Profiles.RemoveAll(profile => retiredIds.Contains(profile.Id));
        if (config.PinnedProfileId is { } preferred && retiredIds.Contains(preferred)) config.PinnedProfileId = null;
        if (config.ManualProfileId is { } manual && retiredIds.Contains(manual))
        {
            config.ManualProfileId = null;
            config.ManualUntil = null;
        }
    }

    public static List<ChannelProfile> All()
    {
        var profiles = JsonData.Decode<List<ChannelProfile>>(Catalog);
        new AppConfig { Profiles = profiles }.Validate();
        return profiles;
    }

    // Sharing excludes the initial defaults separately, so expose every later base configuration.
    public static List<ChannelProfile> Additions() => All().Skip(OriginalDefaultCount).ToList();

    private static IEnumerable<ChannelProfile> Candidates(string? previousRevision)
    {
        if (previousRevision == Revision) return [];
        var all = All();
        // A newly accepted catalog fills every missing known configuration. This
        // also repairs lists replaced by imports in releases before 0.1.14.
        return all;
    }

    public static int PendingCount(AppConfig current, string? previousRevision = null)
    {
        current.Validate();
        return Candidates(previousRevision).Count(source => !current.Profiles.Any(p => AppConfig.SameEffectiveConfiguration(p, source)));
    }

    public static AppConfig AddMissing(AppConfig current, out int added, string? previousRevision = null)
    {
        current.Validate();
        var result = JsonData.Clone(current);
        added = 0;
        if (previousRevision == Revision) return result;
        RemoveRetiredProfiles(result);
        foreach (var source in Candidates(previousRevision))
        {
            if (result.Profiles.Any(p => AppConfig.SameEffectiveConfiguration(p, source))) continue;
            if (result.Profiles.Count == 32) break;
            var profile = JsonData.Clone(source);
            if (result.Profiles.Any(p => Guid.Parse(p.Id) == Guid.Parse(profile.Id)))
                profile.Id = Guid.NewGuid().ToString("N");
            var name = profile.Name.Trim();
            profile.Name = name;
            for (int number = 2; result.Profiles.Any(p => StringComparer.OrdinalIgnoreCase.Equals(p.Name.Trim(), profile.Name)); number++)
            {
                var suffix = " (каталог " + number + ")";
                profile.Name = name[..Math.Min(name.Length, 100 - suffix.Length)] + suffix;
            }
            result.Profiles.Add(profile);
            added++;
        }
        result.Validate();
        return result;
    }

    // The first three also appear in AppConfig.Defaults. Put the four new
    // profiles immediately after them so updates add them before older presets.
    private const string Catalog = """
[
  {
    "Id": "ac91655726d44ec18114f57640e0fda9",
    "Name": "GeoHide EU (DoH)",
    "Enabled": true,
    "Mode": "Doh",
    "Servers": [
      "5.175.161.40"
    ],
    "DnsPort": 53,
    "DohUrl": "https://eu.geohide.ru/dns-query",
    "Note": "На 24.11.2026 владелец показывает 5.175.161.40 на статусной странице, и этот IP входит в публичный A для eu.geohide.ru. Порт DoH 443. Фиксация IP в документации владельца не подтверждена. https://status.dns.geohide.ru/ru"
  },
  {
    "Id": "841faa65861c6e36c6688d2094054558",
    "Name": "Bezmezhau",
    "Enabled": true,
    "Mode": "Doh",
    "Servers": [
      "143.20.64.55",
      "31.58.216.201"
    ],
    "DnsPort": 53,
    "DohUrl": "https://dns.bezmezhau.com/dns-query",
    "Note": "Сверено 24.11.2026. Официальный DoH-профиль содержит 143.20.64.55 и 31.58.216.201. Старый IP 181.215.40.49 исключён из инструкции, но ответил в нашей контрольной пробе. Для РФ владелец ссылается на отдельные параметры в Telegram. https://bezmezhau.com/ru/setup"
  },
  {
    "Id": "45fb037af650b82daecc92c338090020",
    "Name": "DNS-AI",
    "Enabled": true,
    "Mode": "Doh",
    "Servers": [
      "194.87.196.13",
      "45.151.102.219",
      "2a00:b700:5::3:62",
      "2a12:5940:d4ef::219"
    ],
    "DnsPort": 53,
    "DohUrl": "https://dns.dns-ai.ru/dns-query",
    "Note": "Сверено 24.11.2026. Все четыре IP опубликованы для DoH через HTTPS/443. Владелец сообщает о закрытом обычном DNS на порту 53. В момент сверки монитор владельца показывал отказ DoH на обоих прямых узлах. https://dns-ai.ru/status"
  },
  {
    "Id": "25a541fefc094bf2b7b1c9a17c2c10be",
    "Name": "Перевал (DoH)",
    "Enabled": true,
    "Mode": "Doh",
    "Servers": [
      "153.80.200.170"
    ],
    "DnsPort": 53,
    "DohUrl": "https://free.pereval.top/dns-query",
    "Note": "Параметры: https://github.com/amak-tech/pereval. Публичный HTTPS ChatGPT 3/3 на другом подключении. Для обычного DNS опубликованы отдельные замеры на московской МГТС 14–16.11.2026."
  },
  {
    "Id": "0c3545c472ed42c6a12df242d493cffb",
    "Name": "Перевал (UDP/TCP)",
    "Enabled": true,
    "Mode": "Udp",
    "Servers": [
      "153.80.200.170"
    ],
    "DnsPort": 53,
    "DohUrl": "",
    "Note": "Параметры: https://github.com/amak-tech/pereval. Публичный HTTPS ChatGPT 3/3 на другом подключении. Для обычного DNS опубликованы отдельные замеры на московской МГТС 14–16.11.2026."
  },
  {
    "Id": "00155dbe394e41609d986280be98e40f",
    "Name": "GeoHide EU (5.175.161.40, UDP/TCP)",
    "Enabled": true,
    "Mode": "Udp",
    "Servers": [
      "5.175.161.40"
    ],
    "DnsPort": 53,
    "DohUrl": "",
    "Note": "Сервер GeoHide EU: https://status.dns.geohide.ru/en. Обычный DNS проверяли на московской МГТС 14–16.11.2026. Публичный HTTPS ChatGPT 2/3 на другом подключении."
  },
  {
    "Id": "af71c3a989f54aaea9fd6962f1a63728",
    "Name": "GeoHide EU (143.20.64.192, UDP/TCP)",
    "Enabled": true,
    "Mode": "Udp",
    "Servers": [
      "143.20.64.192"
    ],
    "DnsPort": 53,
    "DohUrl": "",
    "Note": "Сервер GeoHide EU: https://status.dns.geohide.ru/en. Резервный узел из замеров на московской МГТС 14–16.11.2026. Публичный HTTPS ChatGPT 3/3 на другом подключении."
  },
  {
    "Id": "167dfb96671d26c3d00d8016d92d189a",
    "Name": "Dagami (DoH)",
    "Enabled": false,
    "Mode": "Doh",
    "Servers": [
      "193.233.244.38"
    ],
    "DnsPort": 53,
    "DohUrl": "https://dns.dagami.ru/dns-query",
    "Note": "Исторический IP 193.233.244.38 отсутствует в настройках владельца. В пробе 24.11.2026 TLS-сертификат этого узла не соответствовал dns.dagami.ru. Используйте текущий профиль Dagami с 48.102.213.179. https://dagami.ru/"
  },
  {
    "Id": "0303e052e09ee6556b5739827f9bfc57",
    "Name": "Dagami (UDP/TCP)",
    "Enabled": false,
    "Mode": "Udp",
    "Servers": [
      "193.233.244.38"
    ],
    "DnsPort": 53,
    "DohUrl": "",
    "Note": "Исторический IP 193.233.244.38 отсутствует в настройках владельца и дал тайм-аут DNS в пробе 24.11.2026. Текущий опубликованный основной IP 48.102.213.179. https://dagami.ru/"
  },
  {
    "Id": "017aed0f146f3e197740b0311911159b",
    "Name": "Zapret DNS (DoH)",
    "Enabled": false,
    "Mode": "Doh",
    "Servers": [
      "83.217.211.149"
    ],
    "DnsPort": 53,
    "DohUrl": "https://dns.zapret.moe/dns-query",
    "Note": "Сверено 24.11.2026. Владелец пометил статический bootstrap 83.217.211.149 как отключённый 02.11.2026. Текущий DoH без постоянного IP указан в https://dns.zapret.moe/reach.json. Apple-профиль ещё указывает dns.zapret.moe/dns-query без фиксированного IP. Этот исторический профиль выключен."
  },
  {
    "Id": "632d435d8696d07fef18b1007bfe2fa0",
    "Name": "Zapret DNS (UDP/TCP)",
    "Enabled": false,
    "Mode": "Udp",
    "Servers": [
      "83.217.211.149"
    ],
    "DnsPort": 53,
    "DohUrl": "",
    "Note": "Исторический профиль. Владелец сообщил о закрытии обычного DNS 53 и DoT 853 с 03.11.2026. Сверено 24.11.2026. Доступен DoH без постоянного IP. https://dns.zapret.moe/"
  },
  {
    "Id": "49495bf679479b860e0eacd0434becf0",
    "Name": "dns.malw.link (DoH)",
    "Enabled": true,
    "Mode": "Doh",
    "Servers": [
      "179.254.166.171"
    ],
    "DnsPort": 53,
    "DohUrl": "https://dns.malw.link/dns-query",
    "Note": "Владелец указывает DoH для предпочтительных 179.254.166.171 и 2a12:bec4:1bb0:27dd::2. В этом профиле IPv4. Сверено 24.11.2026. https://info.dns.malw.link/setup"
  },
  {
    "Id": "7673a33504ff7c7f74ebffc81430c66c",
    "Name": "dns.malw.link (UDP/TCP)",
    "Enabled": true,
    "Mode": "Udp",
    "Servers": [
      "179.254.166.171",
      "164.5.224.81"
    ],
    "DnsPort": 53,
    "DohUrl": "",
    "Note": "Два опубликованных IPv4 ответили в контрольном DNS-запросе 24.11.2026. Владелец также публикует IPv6 2a12:bec4:1bb0:27dd::2 и 2a13:82c0:0:d::3. В этом профиле IPv4. https://info.dns.malw.link/setup"
  },
  {
    "Id": "211e8c5747174188a8a7b10330c33d02",
    "Name": "MagicDNS (DoH, 2026-11)",
    "Enabled": true,
    "Mode": "Doh",
    "Servers": [
      "217.144.190.216",
      "109.120.187.249"
    ],
    "DnsPort": 53,
    "DohUrl": "https://api.magicinfo.art/rpc",
    "Note": "URL /rpc и эта пара IPv4 есть в опубликованном профиле владельца https://api.magicinfo.art/profile.mobileconfig. 24.11.2026 ответил 109.120.187.249, на 217.144.190.216 был тайм-аут. У нового /invite на двух других адресах штатная проба Pilot получила тайм-аут."
  },
  {
    "Id": "9a4e83485c9945c6ba6a46b914573e2c",
    "Name": "MagicDNS (UDP/TCP, 2026-11)",
    "Enabled": true,
    "Mode": "Udp",
    "Servers": [
      "217.144.190.216",
      "109.120.187.249"
    ],
    "DnsPort": 53,
    "DohUrl": "",
    "Note": "Историческая пара для обычного DNS. Оба IPv4 дали тайм-аут на 53 в контроле 24.11.2026. Действующий список для обычного DNS не опубликован на странице https://magicdns.ru/. Источник отправляет за параметрами в своего бота."
  },
  {
    "Id": "8952fe808e4144d3bb5b194c54d4b13e",
    "Name": "MagicDNS (DoH dns-query)",
    "Enabled": true,
    "Mode": "Doh",
    "Servers": [
      "217.144.190.216",
      "109.120.187.249"
    ],
    "DnsPort": 53,
    "DohUrl": "https://dns.magicdns.ru/dns-query",
    "Note": "URL и оба IPv4 есть в опубликованном профиле владельца https://dns.magicdns.ru/profile-dns-dnsquery.mobileconfig. 24.11.2026 ответил 109.120.187.249, на 217.144.190.216 был тайм-аут."
  },
  {
    "Id": "23f1ed5393af4e66b301cb0c6a8a8af6",
    "Name": "Dagami (DoH, 2026-11)",
    "Enabled": false,
    "Mode": "Doh",
    "Servers": [
      "48.102.213.179"
    ],
    "DnsPort": 53,
    "DohUrl": "https://dns.dagami.ru/dns-query",
    "Note": "Основной DoH с 48.102.213.179 и опубликованное зеркало ad.dagami.ru на том же IP ответили на контрольный DNS-запрос 24.11.2026. Резервный 31.76.52.235 из этой сети не ответил. https://dagami.ru/"
  },
  {
    "Id": "9802da98bb614fcabf7c848466bab209",
    "Name": "Dagami (UDP/TCP, 2026-11)",
    "Enabled": false,
    "Mode": "Udp",
    "Servers": [
      "48.102.213.179"
    ],
    "DnsPort": 53,
    "DohUrl": "",
    "Note": "Основной опубликованный IP 48.102.213.179 ответил по UDP/TCP на 53 в проверке 24.11.2026. https://dagami.ru/"
  },
  {
    "Id": "558caa6796a14bef89bdd665c3dc580a",
    "Name": "Dagami (DoH experimental DE)",
    "Enabled": false,
    "Mode": "Doh",
    "Servers": [
      "48.102.213.179"
    ],
    "DnsPort": 53,
    "DohUrl": "https://dns.dagami.ru/dns-lab/de",
    "Note": "Экспериментальный URL отсутствует в текущей инструкции владельца. В контрольной пробе 24.11.2026 DNS ответил. Основной опубликованный адрес указан на https://dagami.ru/."
  },
  {
    "Id": "d77c198cc7904cb2bae9209c0592d35f",
    "Name": "GeoHide (DoH RU)",
    "Enabled": false,
    "Mode": "Doh",
    "Servers": [
      "159.194.200.33",
      "37.230.192.51"
    ],
    "DnsPort": 53,
    "DohUrl": "https://geohide.ru/dns-query",
    "Note": "Оба IPv4 входят в публичный A для geohide.ru на 24.11.2026. 37.230.192.51 ответил по DoH, 159.194.200.33 дал тайм-аут с этого ПК. Постоянство этих IP как bootstrap не подтверждено. https://geohide.ru/"
  },
  {
    "Id": "2fe59910e5dd47078e0176f83136e21c",
    "Name": "Zapret DNS (DoH, 2026-11)",
    "Enabled": false,
    "Mode": "Doh",
    "Servers": [
      "38.180.241.191"
    ],
    "DnsPort": 53,
    "DohUrl": "https://dns.zapret.moe/dns-query",
    "Note": "Сверено 24.11.2026. Статический bootstrap 38.180.241.191 отсутствует в текущих настройках владельца и в контрольной пробе дал тайм-аут. Актуальный DoH без постоянного IP указан в https://dns.zapret.moe/reach.json. Исторический профиль выключен."
  },
  {
    "Id": "3ea710b39128490bb25d231049d06c51",
    "Name": "Zapret DNS (UDP/TCP, 2026-11)",
    "Enabled": false,
    "Mode": "Udp",
    "Servers": [
      "38.180.241.191"
    ],
    "DnsPort": 53,
    "DohUrl": "",
    "Note": "Исторический профиль. Владелец сообщил о закрытии обычного DNS 53 и DoT 853 с 03.11.2026. Сверено 24.11.2026. Доступен DoH без постоянного IP. https://dns.zapret.moe/"
  },
  {
    "Id": "babb69fa1dca4eb3ad760d93bfddbfd9",
    "Name": "DNS-AI (Cloudflare DoH)",
    "Enabled": true,
    "Mode": "Doh",
    "Servers": [
      "172.67.157.232",
      "104.21.82.125"
    ],
    "DnsPort": 53,
    "DohUrl": "https://cf.dns-ai.ru/dns-query",
    "Note": "Публичный запасной DoH владельца. Оба IPv4 сейчас возвращает публичный DNS, оба отвечали в пробах 24.11.2026. Это адреса Cloudflare с коротким TTL, постоянство не обещано. https://dns-ai.ru/"
  },
  {
    "Id": "78cb2baabadc4ca1832557a22c5d1467",
    "Name": "Astracat (Commonnet DoH)",
    "Enabled": false,
    "Mode": "Doh",
    "Servers": [
      "94.181.44.39"
    ],
    "DnsPort": 53,
    "DohUrl": "https://dns.commonnet.work/dns-query",
    "Note": "Владелец публикует URL, постоянный bootstrap не заявлен. Старый 94.181.44.39 дал тайм-аут 24.11.2026. Публичный A на момент проверки: 80.85.155.164 и 80.85.156.84. Эти IP могут меняться. https://github.com/ASTRACAT2022/host-DNS"
  },
  {
    "Id": "9087e298de4243f3a66d9587e867b59b",
    "Name": "WebShift DNS (DoH)",
    "Enabled": true,
    "Mode": "Doh",
    "Servers": [
      "31.31.207.68"
    ],
    "DnsPort": 53,
    "DohUrl": "https://d.webshiftvpn.ru/dns-query",
    "Note": "Пара URL и IPv4 подтверждена опубликованным профилем владельца и контрольным DoH-запросом 24.11.2026. https://dns.webshiftvpn.ru/webshift-dns.mobileconfig"
  },
  {
    "Id": "3de57da2b8464a05ad051793ab10587c",
    "Name": "KeyOneDay (UDP/TCP)",
    "Enabled": true,
    "Mode": "Udp",
    "Servers": [
      "2.58.127.63"
    ],
    "DnsPort": 53,
    "DohUrl": "",
    "Note": "Архив владельца указывает 2.58.127.63. 24.11.2026 в этой сети порт 53 дал тайм-аут. Страница сообщает о приостановке публичного SmartDNS и одновременно содержит старые сведения об активности. https://keyoneday.ru/dns.html"
  },
  {
    "Id": "c42458ff77de4651ae700f97d6c508ee",
    "Name": "dns.malw.link (UDP/TCP, резерв)",
    "Enabled": true,
    "Mode": "Udp",
    "Servers": [
      "164.5.224.81"
    ],
    "DnsPort": 53,
    "DohUrl": "",
    "Note": "Резервный IPv4 для обычного DNS в инструкции владельца. В контрольной пробе 24.11.2026 ответил. https://info.dns.malw.link/setup"
  },
  {
    "Id": "c34d7caffe4e4254b92cab992f296108",
    "Name": "DNS-AI (DoH 194.87.196.13)",
    "Enabled": true,
    "Mode": "Doh",
    "Servers": [
      "194.87.196.13"
    ],
    "DnsPort": 53,
    "DohUrl": "https://dns.dns-ai.ru/dns-query",
    "Note": "Опубликованный прямой DoH DNS-AI на одном IPv4. 24.11.2026 соединение с этого ПК завершилось тайм-аутом, а монитор владельца показывал отказ DoH основного узла. https://dns-ai.ru/status"
  },
  {
    "Id": "e588929cc7f0446a964deba1527599cc",
    "Name": "GeoHide EU (UDP/TCP)",
    "Enabled": true,
    "Mode": "Udp",
    "Servers": [
      "5.175.161.3",
      "37.221.93.128"
    ],
    "DnsPort": 53,
    "DohUrl": "",
    "Note": "https://status.dns.geohide.ru/en"
  },
  {
    "Id": "3a0b063ba978452186ca5d961619ac76",
    "Name": "GeoHide US (UDP/TCP 192.255.159.240)",
    "Enabled": true,
    "Mode": "Udp",
    "Servers": [
      "192.255.159.240"
    ],
    "DnsPort": 53,
    "DohUrl": "",
    "Note": "https://status.dns.geohide.ru/en"
  }
]
""";
}
