namespace NetPilot;

/// <summary>A public, dated catalog shipped with the reviewed application package.</summary>
public static class BundledChannels
{
    // Metadata-only corrections do not advance the effective-configuration
    // revision: accepting a new revision intentionally restores missing entries.
    public const string Revision = "2026-11-19-r3";
    public const string PreviousRevision = "2026-11-19";
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

    // The first three also appear in AppConfig.Defaults. Put the GeoHide reserve
    // first so the slot freed by migration goes to the requested replacement.
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
    "Note": "Европейский узел GeoHide для DNS через HTTPS. https://status.dns.geohide.ru/en"
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
    "Note": "Сверено 17.11.2026: https://bezmezhau.com/setup. Текущая опубликованная пара: 143.20.64.55 и 31.58.216.201; DoH URL не изменился. Старый primary 181.215.40.49 больше не указан, но его отключение не доказано. Владелец предупреждает: эти IP и DoH могут не работать в РФ; отдельный адрес для РФ находится в закреплённом сообщении https://t.me/bezmezhau и при сверке не получен. Подтверждена публикация параметров, а не работоспособность в вашей сети. Вход, генерация и безопасность DNS этим не подтверждаются."
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
    "Note": "Провайдер сообщает: порт 53 закрыт; используйте DoH. Без открытого DNS fallback. Срез публичных проверок 16.11.2026 с одного ПК: ChatGPT HTTP 200 0/3; Gemini /app HTTP 200 2/3. Успех публичного HTTPS не подтверждает вход, генерацию или безопасность DNS."
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
    "Note": "Сверено 22.10.2026: https://dns.dagami.ru/. Адрес опубликован в редакции от 21.10.2026. Фильтры; технические журналы соединений содержат домены, время и объём данных по сообщению провайдера. Успех публичного HTTPS не подтверждает авторизованный Codex. Повторно 16.11.2026 с этого ПК не проверялся; сведения о доступности отсутствуют. Исторический IP из набора 22.10.2026; новые варианты того же провайдера включены отдельными профилями. Этот исторический вариант выключен. Успех публичного HTTPS не подтверждает вход, генерацию или безопасность DNS."
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
    "Note": "Сверено 22.10.2026: https://dns.dagami.ru/. Адрес от 21.10.2026. Фильтры рекламы/телеметрии; технические журналы соединений с доменами; без шифрования DNS. Успех публичного HTTPS не подтверждает авторизованный Codex. Повторно 16.11.2026 с этого ПК не проверялся; сведения о доступности отсутствуют. Исторический IP из набора 22.10.2026; новые варианты того же провайдера включены отдельными профилями. Этот исторический вариант выключен. Успех публичного HTTPS не подтверждает вход, генерацию или безопасность DNS."
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
    "Note": "Сверено 17.11.2026: https://dns.zapret.moe/. Провайдер поддерживает только DoH через Cloudflare без постоянного IP. Сохранённый статический bootstrap 83.217.211.149 не соответствует текущему документированному подключению; это не доказывает отключение именно этого IP. Приложение требует явные bootstrap IP и режим без постоянного IP не поддерживает. Историческая конфигурация сохранена выключенной; ранее сохранённые настройки не меняются. Успех публичного HTTPS не подтверждает вход, генерацию или безопасность DNS."
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
    "Note": "Сверено 17.11.2026: https://dns.zapret.moe/. По сообщению провайдера обычный DNS (порт 53) и DoT (порт 853) закрыты с 03.11.2026. Этот UDP/TCP-профиль больше не соответствует поддерживаемому режиму Zapret. Историческая конфигурация сохранена выключенной; ранее сохранённые настройки не меняются. Поддерживается только DoH через Cloudflare без постоянного IP; приложение требует явные bootstrap IP и этот режим не поддерживает. Успех публичного HTTPS не подтверждает вход, генерацию или безопасность DNS."
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
    "Note": "Сверено 22.10.2026: https://info.dns.malw.link/setup. DoH опубликован для основного IP. Для 164.5.224.81 шифрование не заявлено: он оставлен только в обычном DNS-профиле. Включены фильтры. Успех публичного HTTPS не подтверждает авторизованный Codex. Срез публичных проверок 16.11.2026 с одного ПК: ChatGPT HTTP 200 3/3; Gemini /app HTTP 200 0/1. Успех публичного HTTPS не подтверждает вход, генерацию или безопасность DNS."
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
    "Note": "Сверено 22.10.2026: https://info.dns.malw.link/setup. Обычный DNS без шифрования. Включены фильтры рекламы, аналитики и телеметрии; отдельные сайты могут блокироваться. Успех публичного HTTPS не подтверждает авторизованный Codex. Повторно 16.11.2026 с этого ПК не проверялся; сведения о доступности отсутствуют. Успех публичного HTTPS не подтверждает вход, генерацию или безопасность DNS."
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
    "Note": "Параметры: https://magicdns.ru/. Проверено 16.11.2026 с этого ПК: ChatGPT /cdn-cgi/trace HTTP 200 3/3, Gemini /app HTTP 200 3/3 (предел заголовков 64 КБ). Обычный монитор с пределом 16 КБ может ошибочно показывать Gemini недоступным. TLS проверялся штатно. Вход, генерация и WebSocket не проверялись; результат не гарантирует безопасность провайдера или работу в другой сети."
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
    "Note": "Параметры: https://magicdns.ru/. Проверено 16.11.2026 с этого ПК: ChatGPT /cdn-cgi/trace HTTP 200 3/3, Gemini /app HTTP 200 3/3 (предел заголовков 64 КБ). Обычный монитор с пределом 16 КБ может ошибочно показывать Gemini недоступным. TLS проверялся штатно. Вход, генерация и WebSocket не проверялись; результат не гарантирует безопасность провайдера или работу в другой сети."
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
    "Note": "Параметры: https://magicdns.ru/. Проверено 16.11.2026 с этого ПК: ChatGPT /cdn-cgi/trace HTTP 200 3/3, Gemini /app HTTP 200 3/3 (предел заголовков 64 КБ). Обычный монитор с пределом 16 КБ может ошибочно показывать Gemini недоступным. TLS проверялся штатно. Вход, генерация и WebSocket не проверялись; результат не гарантирует безопасность провайдера или работу в другой сети."
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
    "Note": "https://dns.dagami.ru/ Срез публичных проверок 16.11.2026 с одного ПК: ChatGPT HTTP 200 0/2; Gemini /app HTTP 200 0/1. В этом файле выключен после неуспешной проверки; при необходимости можно включить вручную. Успех публичного HTTPS не подтверждает вход, генерацию или безопасность DNS."
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
    "Note": "https://dns.dagami.ru/ Срез публичных проверок 16.11.2026 с одного ПК: ChatGPT HTTP 200 0/2; Gemini /app HTTP 200 0/1. В этом файле выключен после неуспешной проверки; при необходимости можно включить вручную. Успех публичного HTTPS не подтверждает вход, генерацию или безопасность DNS."
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
    "Note": "https://dns.dagami.ru/; experimental gateway Срез публичных проверок 16.11.2026 с одного ПК: ChatGPT HTTP 200 0/2; Gemini /app HTTP 200 0/1. В этом файле выключен после неуспешной проверки; при необходимости можно включить вручную. Успех публичного HTTPS не подтверждает вход, генерацию или безопасность DNS."
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
    "Note": "https://geohide.ru/; https://status.dns.geohide.ru/ru Срез публичных проверок 16.11.2026 с одного ПК: ChatGPT HTTP 200 0/2; Gemini /app HTTP 200 0/1. В этом файле выключен после неуспешной проверки; при необходимости можно включить вручную. Успех публичного HTTPS не подтверждает вход, генерацию или безопасность DNS. Сверка 17.11.2026: конфигурация https://geohide.ru/static/metadata/servers.json вернула HTTP 503; текущая региональная пара IP и её соответствие DoH URL не подтверждены. Адреса status-страницы не подставляются вместо настроек. Старые параметры сохранены без объявления их отключёнными."
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
    "Note": "Сверено 17.11.2026: https://dns.zapret.moe/. Провайдер поддерживает только DoH через Cloudflare без постоянного IP. Сохранённый статический bootstrap 38.180.241.191 не соответствует текущему документированному подключению; это не доказывает отключение именно этого IP. Приложение требует явные bootstrap IP и режим без постоянного IP не поддерживает. Историческая конфигурация сохранена выключенной; ранее сохранённые настройки не меняются. Успех публичного HTTPS не подтверждает вход, генерацию или безопасность DNS."
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
    "Note": "Сверено 17.11.2026: https://dns.zapret.moe/. По сообщению провайдера обычный DNS (порт 53) и DoT (порт 853) закрыты с 03.11.2026. Этот UDP/TCP-профиль больше не соответствует поддерживаемому режиму Zapret. Историческая конфигурация сохранена выключенной; ранее сохранённые настройки не меняются. Поддерживается только DoH через Cloudflare без постоянного IP; приложение требует явные bootstrap IP и этот режим не поддерживает. Успех публичного HTTPS не подтверждает вход, генерацию или безопасность DNS."
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
    "Note": "https://dns-ai.ru/; bootstrap observed via OS resolver 2026-11-16, HTTPS certificate validation retained Срез публичных проверок 16.11.2026 с одного ПК: ChatGPT HTTP 200 0/3; Gemini /app HTTP 200 3/3. Успех публичного HTTPS не подтверждает вход, генерацию или безопасность DNS. Сверено 17.11.2026: https://dns-ai.ru/ подтверждает DoH https://cf.dns-ai.ru/dns-query, но не задаёт постоянные IP Cloudflare. Каталожные IP — наблюдение 16.11.2026, их актуальность сейчас не подтверждена. Адрес личного устройства ru.dns-ai.ru не является общим публичным профилем и в набор не включён."
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
    "Note": "https://github.com/ASTRACAT2022/host-DNS/blob/main/README.md; bootstrap observed via OS resolver 2026-11-16, HTTPS certificate validation retained Срез публичных проверок 16.11.2026 с одного ПК: ChatGPT HTTP 200 0/2; Gemini /app HTTP 200 0/1. В этом файле выключен после неуспешной проверки; при необходимости можно включить вручную. Успех публичного HTTPS не подтверждает вход, генерацию или безопасность DNS. Сверено 17.11.2026: https://github.com/ASTRACAT2022/host-DNS/blob/main/README.md подтверждает https://dns.commonnet.work/dns-query, но не публикует постоянный bootstrap IP. Каталожный 94.181.44.39 остаётся историческим наблюдением 16.11.2026, текущим адресом владельца не подтверждён. Работоспособность не проверялась."
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
    "Note": "WebShift DNS: https://dns.webshiftvpn.ru/"
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
    "Note": "https://keyoneday.ru/dns.html"
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
    "Note": "https://info.dns.malw.link/setup"
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
    "Note": "https://dns-ai.ru/"
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
