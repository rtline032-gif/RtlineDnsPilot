using System.Security.Cryptography;
using System.Text;

namespace NetPilot;

/// <summary>A dated, source-checked addition requested by the user; no remote catalog execution.</summary>
public static class BundledChannels
{
    public const string Revision = "2026-10-22";

    public static List<ChannelProfile> Additions() =>
    [
        Profile("Xbox DNS (UDP/TCP)", ["111.88.96.54", "111.88.96.55"], "", "https://xbox-dns.ru/", "На 22.10.2026 сайт предупреждает о временной недоступности. Обычный DNS без шифрования."),
        Profile("Bezmezhau (UDP/TCP)", ["181.215.40.49", "31.58.216.201"], "", "https://bezmezhau.com/", "Обычный DNS без шифрования. Доступность проверяется с этого ПК."),
        Profile("Comss.one (DoH)", ["195.133.25.16"], "https://dns.comss.one/dns-query", "https://www.comss.ru/page.php?id=7315", "Официально опубликованный bootstrap DoH. Фильтрация рекламы, трекеров и вредоносных доменов."),
        Profile("Comss.one (DoH, резерв)", ["83.220.169.155", "212.109.195.93"], "https://dns.comss.one/dns-query", "https://www.comss.ru/page.php?id=7315", "Дополнительный вариант DoH через другие IP того же провайдера. Доступность HTTPS проверяется приложением; без перехода на открытый DNS при ошибке."),
        Profile("Comss.one (UDP/TCP)", ["83.220.169.155", "212.109.195.93"], "", "https://www.comss.ru/page.php?id=7315", "Стандартные DNS-адреса. Фильтрация рекламы, трекеров и вредоносных доменов; без шифрования DNS."),
        Profile("GeoHide (UDP/TCP)", ["159.194.200.33", "37.230.192.51"], "", "https://geohide.ru/", "IP подтверждены официальной страницей https://status.dns.geohide.ru/en. Обычный DNS; чужие uptime-проценты не импортируются."),
        Profile("Dagami (DoH)", ["193.233.244.38"], "https://dns.dagami.ru/dns-query", "https://dns.dagami.ru/", "Адрес опубликован в редакции от 21.10.2026. Фильтры; технические журналы соединений содержат домены, время и объём данных по сообщению провайдера."),
        Profile("Dagami (UDP/TCP)", ["193.233.244.38"], "", "https://dns.dagami.ru/", "Адрес от 21.10.2026. Фильтры рекламы/телеметрии; технические журналы соединений с доменами; без шифрования DNS."),
        Profile("Zapret DNS (DoH)", ["83.217.211.149"], "https://dns.zapret.moe/dns-query", "https://dns.zapret.moe/", "Публичный DNS проекта с отдельным списком сервисов; без фильтрации рекламы. Сторонние программы не устанавливаются."),
        Profile("Zapret DNS (UDP/TCP)", ["83.217.211.149"], "", "https://dns.zapret.moe/", "Обычный DNS без шифрования и фильтрации рекламы. Это профиль DNS, программы Zapret не устанавливаются."),
        Profile("dns.malw.link (DoH)", ["179.254.166.171"], "https://dns.malw.link/dns-query", "https://info.dns.malw.link/setup", "DoH опубликован для основного IP. Для 164.5.224.81 шифрование не заявлено: он оставлен только в обычном DNS-профиле. Включены фильтры."),
        Profile("dns.malw.link (UDP/TCP)", ["179.254.166.171", "164.5.224.81"], "", "https://info.dns.malw.link/setup", "Обычный DNS без шифрования. Включены фильтры рекламы, аналитики и телеметрии; отдельные сайты могут блокироваться.")
    ];

    private static ChannelProfile Profile(string name, string[] servers, string doh, string source, string note)
    {
        var identity = name + "|" + string.Join(",", servers) + "|" + doh;
        return new ChannelProfile
        {
            Id = new Guid(SHA256.HashData(Encoding.UTF8.GetBytes(identity)).AsSpan(0, 16)).ToString("N"),
            Name = name, Servers = servers, DohUrl = doh, Mode = doh.Length == 0 ? DnsMode.Udp : DnsMode.Doh,
            Note = "Сверено 22.10.2026: " + source + ". " + note + " Успех публичного HTTPS не подтверждает авторизованный Codex."
        };
    }

    public static AppConfig AddMissing(AppConfig current, out int added)
    {
        current.Validate();
        var result = JsonData.Clone(current);
        added = 0;
        foreach (var source in Additions())
        {
            if (result.Profiles.Any(p => Guid.Parse(p.Id) == Guid.Parse(source.Id) || AppConfig.SameEffectiveConfiguration(p, source))) continue;
            if (result.Profiles.Count == 32) break;
            var profile = JsonData.Clone(source);
            while (result.Profiles.Any(p => StringComparer.OrdinalIgnoreCase.Equals(p.Name.Trim(), profile.Name.Trim()))) profile.Name += " +";
            result.Profiles.Add(profile);
            added++;
        }
        result.Validate();
        return result;
    }
}
