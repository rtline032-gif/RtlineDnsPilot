#if LEGACY_WINDOWS
using System.Text.Json;
using System.Text.Json.Serialization;
using JsonRequiredAttribute = NetPilot.LegacyJsonRequiredAttribute;

namespace NetPilot.Tests;

public static class LegacyJsonTests
{
    private const string FamilyJson = """{"Family":"IPv4","Enabled":true,"Source":"Dhcp","Servers":[],"EffectiveServers":[]}""";
    private static readonly string AdapterJson = "{\"AdapterId\":\"11111111-2222-3333-4444-555555555555\",\"Up\":true,\"IPv4\":"
        + FamilyJson + ",\"IPv6\":" + FamilyJson.Replace("IPv4", "IPv6", StringComparison.Ordinal) + "}";

    public static Task RunAsync(Action<string, bool, string> report)
    {
        void Check(string name, Action test)
        {
            try { test(); report(name, true, ""); }
            catch (Exception ex) { report(name, false, ex.Message); }
        }

        Check("legacy JSON preserves a complete DNS adapter snapshot", () =>
        {
            var snapshot = JsonData.Decode<AdapterDnsSnapshot>(AdapterJson);
            Require(snapshot.IPv4.Enabled && snapshot.IPv6.Family == DnsFamily.IPv6 && snapshot.Up, "A complete DNS snapshot changed.");
        });
        foreach (var field in new[] { "Family", "Enabled", "Source", "Servers", "EffectiveServers" })
        {
            string missing = field;
            Check("legacy JSON rejects omitted DNS family field " + missing, () =>
                ExpectJsonFailure(() => JsonData.Decode<DnsFamilySnapshot>(Without(FamilyJson, missing))));
        }
        foreach (var field in new[] { "AdapterId", "Up", "IPv4", "IPv6" })
        {
            string missing = field;
            Check("legacy JSON rejects omitted DNS adapter field " + missing, () =>
                ExpectJsonFailure(() => JsonData.Decode<AdapterDnsSnapshot>(Without(AdapterJson, missing))));
        }
        Check("legacy JSON rejects missing fields inside a present DNS family", () =>
            ExpectJsonFailure(() => JsonData.Decode<AdapterDnsSnapshot>(AdapterJson.Replace(FamilyJson, "{}", StringComparison.Ordinal))));
        Check("legacy JSON rejects nested missing fields in journal envelopes", () =>
            ExpectJsonFailure(() => JsonData.Decode<JournalEnvelope>("{\"Version\":2,\"Original\":" + Without(AdapterJson, "IPv6") + "}")));
        Check("legacy JSON does not let property initializers replace required journal metadata", () =>
            ExpectJsonFailure(() => JsonData.Decode<JournalEnvelope>("{\"Original\":" + AdapterJson + "}")));
        Check("legacy JSON accepts case-insensitive required property names", () =>
        {
            string json = AdapterJson.Replace("\"IPv4\":", "\"iPv4\":", StringComparison.Ordinal)
                .Replace("\"Enabled\":", "\"ENABLED\":", StringComparison.Ordinal);
            Require(JsonData.Decode<AdapterDnsSnapshot>(json).IPv4.Enabled, "Case-insensitive properties were lost.");
        });
        Check("legacy JSON continues to allow unknown properties", () =>
        {
            string json = AdapterJson[..^1] + ",\"FutureField\":{\"Original\":{}},\"MoreFuture\":[{}]}";
            Require(JsonData.Decode<AdapterDnsSnapshot>(json).Up, "Unknown fields changed a valid object.");
        });
        Check("legacy JSON checks required fields in list elements", () =>
            ExpectJsonFailure(() => JsonData.Decode<List<AdapterDnsSnapshot>>("[" + AdapterJson + "," + Without(AdapterJson, "Up") + "]")));
        Check("legacy JSON checks required fields in dictionary values", () =>
            ExpectJsonFailure(() => JsonData.Decode<Dictionary<string, DnsFamilySnapshot>>("{\"main\":" + FamilyJson + ",\"backup\":{}}")));
        Check("legacy JSON checks required fields in read-only dictionary values", () =>
            ExpectJsonFailure(() => JsonData.Decode<IReadOnlyDictionary<string, DnsFamilySnapshot>>("{\"main\":{}}")));
        Check("legacy JSON checks present optional nested objects", () =>
            ExpectJsonFailure(() => JsonData.Decode<OptionalEnvelope>("{\"Original\":{}}")));
        Check("legacy JSON leaves an absent optional initialized object alone", () =>
            Require(JsonData.Decode<OptionalEnvelope>("{}").Original is not null, "Optional constructor default changed."));
        Check("legacy JSON honors explicit property names", () =>
        {
            Require(JsonData.Decode<NamedRequired>("{\"DNS_VERSION\":4}").Version == 4, "JsonPropertyName was ignored.");
            ExpectJsonFailure(() => JsonData.Decode<NamedRequired>("{\"Version\":4}"));
        });
        Check("legacy JSON checks inherited required fields", () =>
            ExpectJsonFailure(() => JsonData.Decode<DerivedRequired>("{\"Name\":\"dns\"}")));
        Check("legacy JSON does not accept a malformed earlier duplicate object", () =>
            ExpectJsonFailure(() => JsonData.Decode<OptionalEnvelope>("{\"Original\":{},\"Original\":" + AdapterJson + "}")));
        Check("legacy JSON rejects a null root through JsonData", () =>
        {
            try { _ = JsonData.Decode<AdapterDnsSnapshot>("null"); }
            catch (InvalidDataException) { return; }
            throw new InvalidOperationException("A null document became an object.");
        });
        Check("legacy JSON retains nullable-reference semantics of JsonRequired", () =>
            Require(JsonData.Decode<OptionalEnvelope>("{\"Original\":null}").Original is null,
                "A present optional null changed into a missing object."));
        Check("legacy JSON permits a present required reference null like modern JsonRequired", () =>
            Require(JsonData.Decode<RequiredReference>("{\"Original\":null}").Original is null,
                "A present required null was not preserved for the existing semantic validator."));
        Check("legacy JSON still rejects null required value types", () =>
            ExpectJsonFailure(() => JsonData.Decode<NamedRequired>("{\"dns_version\":null}")));
        Check("legacy JSON still rejects a wrong nested JSON kind", () =>
            ExpectJsonFailure(() => JsonData.Decode<OptionalEnvelope>("{\"Original\":[]}")));
        Check("legacy JSON malformed documents remain errors", () =>
            ExpectJsonFailure(() => JsonData.Decode<JournalEnvelope>("{\"Version\":2,")));
        Check("legacy JSON required checks also execute through Clone", () =>
            ExpectJsonFailure(() => JsonData.Clone(new RequiredWrittenConditionally())));
        Check("legacy JSON Clone preserves valid DNS families", () =>
        {
            var original = JsonData.Decode<AdapterDnsSnapshot>(AdapterJson);
            var clone = JsonData.Clone(original);
            Require(!ReferenceEquals(original, clone) && clone.IPv6.Family == DnsFamily.IPv6, "Clone lost its validated snapshot.");
        });
        Check("legacy JSON required reachability does not cache a cycle cutoff as false", () =>
        {
            Require(JsonData.Decode<RequiredCycleA>("{\"Next\":null,\"Enabled\":true}").Enabled, "Valid cyclic type graph was rejected.");
            ExpectJsonFailure(() => JsonData.Decode<RequiredCycleB>("{\"Previous\":{}}"));
        });
        Check("legacy JSON accepts recursive type graphs without required fields", () =>
        {
            var value = JsonData.Decode<PlainCycle>("{\"Next\":{\"Next\":null},\"FutureField\":{}}" );
            Require(value.Next is not null && value.Next.Next is null, "An optional recursive graph changed.");
        });
#if NET8_0_OR_GREATER
        Check("legacy JSON required presence and null behavior match the modern serializer", () =>
        {
            foreach (string json in new[] { "{}", "{\"Item\":null}", "{\"item\":{}}", "{\"ITEM\":{\"Enabled\":false}}", "{\"Item\":{\"Enabled\":null}}" })
            {
                bool modern = Accepts(() => JsonSerializer.Deserialize<ModernEnvelope>(json, JsonData.Options));
                bool legacy = Accepts(() => JsonData.Decode<LegacyEnvelope>(json));
                Require(modern == legacy, "Modern/legacy acceptance differs for " + json);
            }
        });
        Check("legacy JSON duplicate nested fields match modern required and last-value semantics", () =>
        {
            foreach (string json in new[]
            {
                "{\"Item\":{},\"Item\":{\"Enabled\":true}}",
                "{\"Item\":{},\"iTeM\":{\"Enabled\":true}}",
                "{\"Item\":{\"Enabled\":true},\"Item\":{\"Enabled\":false}}",
                "{\"Item\":{\"Enabled\":true},\"ITEM\":null}"
            })
            {
                bool modern = Accepts(() => JsonSerializer.Deserialize<ModernEnvelope>(json, JsonData.Options));
                bool legacy = Accepts(() => JsonData.Decode<LegacyEnvelope>(json));
                Require(modern == legacy, "Modern/legacy duplicate acceptance differs for " + json);
                if (modern)
                {
                    var expected = JsonSerializer.Deserialize<ModernEnvelope>(json, JsonData.Options)?.Item?.Enabled;
                    var actual = JsonData.Decode<LegacyEnvelope>(json).Item?.Enabled;
                    Require(expected == actual, "Last case-insensitive duplicate value changed.");
                }
            }
        });
#endif
        return Task.CompletedTask;
    }

    private static bool Accepts(Action action)
    {
        try { action(); return true; }
        catch (JsonException) { return false; }
    }
    private static void ExpectJsonFailure(Action action)
    {
        if (Accepts(action)) throw new InvalidOperationException("Required or invalid JSON was accepted.");
    }
    private static string Without(string json, string name)
    {
        using var document = JsonDocument.Parse(json);
        return "{" + string.Join(",", document.RootElement.EnumerateObject().Where(p => p.Name != name)
            .Select(p => JsonSerializer.Serialize(p.Name) + ":" + p.Value.GetRawText())) + "}";
    }
    private static void Require(bool value, string message) { if (!value) throw new InvalidOperationException(message); }

    public sealed class JournalEnvelope
    {
        [JsonRequired] public int Version { get; set; } = 2;
        [JsonRequired] public AdapterDnsSnapshot? Original { get; set; }
    }
    public sealed class OptionalEnvelope { public AdapterDnsSnapshot? Original { get; set; } = new(); }
    public sealed class RequiredReference { [JsonRequired] public AdapterDnsSnapshot? Original { get; set; } }
    public sealed class NamedRequired { [JsonRequired, JsonPropertyName("dns_version")] public int Version { get; set; } }
    public class BaseRequired { [JsonRequired] public bool Enabled { get; set; } }
    public sealed class DerivedRequired : BaseRequired { public string Name { get; set; } = ""; }
    public sealed class RequiredWrittenConditionally { [JsonRequired, JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)] public int Version { get; set; } }
    public sealed class LegacyEnvelope { [JsonRequired] public LegacyItem? Item { get; set; } }
    public sealed class LegacyItem { [JsonRequired] public bool Enabled { get; set; } }
    public sealed class RequiredCycleA
    {
        public RequiredCycleB? Next { get; set; }
        [JsonRequired] public bool Enabled { get; set; }
    }
    public sealed class RequiredCycleB { public RequiredCycleA? Previous { get; set; } }
    public sealed class PlainCycle { public PlainCycle? Next { get; set; } }
#if NET8_0_OR_GREATER
    public sealed class ModernEnvelope { [System.Text.Json.Serialization.JsonRequired] public ModernItem? Item { get; set; } }
    public sealed class ModernItem { [System.Text.Json.Serialization.JsonRequired] public bool Enabled { get; set; } }
#endif
}
#endif
