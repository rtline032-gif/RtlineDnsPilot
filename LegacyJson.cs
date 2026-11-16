#if LEGACY_WINDOWS
using System.Collections.Concurrent;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace NetPilot;

// System.Text.Json 6 predates JsonRequired. Its replacement is enforced by
// LegacyJson.Validate before every JsonData.Decode, including nested DTOs/Clone.
[AttributeUsage(AttributeTargets.Property | AttributeTargets.Field)]
internal sealed class LegacyJsonRequiredAttribute : Attribute { }

internal static class LegacyJson
{
    private sealed record Member(string Name, string? ExplicitName, Type Type, bool Required, bool Field);
    private static readonly ConcurrentDictionary<Type, Member[]> Members = new();
    private static readonly ConcurrentDictionary<(Type Type, bool IncludeFields), bool> RequiredReachable = new();

    internal static void Validate<T>(string json, JsonSerializerOptions options)
    {
        if (!NeedsValidation(typeof(T), options.IncludeFields)) return;
        using var document = JsonDocument.Parse(json, new JsonDocumentOptions
        {
            AllowTrailingCommas = options.AllowTrailingCommas,
            CommentHandling = options.ReadCommentHandling,
            MaxDepth = options.MaxDepth
        });
        ValidateValue(document.RootElement, typeof(T), options, "$");
    }

    private static void ValidateValue(JsonElement value, Type type, JsonSerializerOptions options, string path)
    {
        // JsonRequired is a presence constraint, not a non-null constraint. The
        // serializer still rejects null value types and existing DNS validators
        // still reject null families/arrays. Do not change either contract here.
        if (value.ValueKind == JsonValueKind.Null) return;
        type = Nullable.GetUnderlyingType(type) ?? type;
        if (!NeedsValidation(type, options.IncludeFields)) return;
        if (type == typeof(object) || type == typeof(JsonElement) || type == typeof(JsonDocument)
            || type == typeof(string) || type.IsPrimitive || type.IsEnum) return;

        var interfaces = type.GetInterfaces().Prepend(type).ToArray();
        var dictionary = interfaces.FirstOrDefault(t => t.IsGenericType
            && (t.GetGenericTypeDefinition() == typeof(IDictionary<,>) || t.GetGenericTypeDefinition() == typeof(IReadOnlyDictionary<,>)));
        if (dictionary is not null)
        {
            if (value.ValueKind == JsonValueKind.Object)
                foreach (var property in value.EnumerateObject())
                    ValidateValue(property.Value, dictionary.GetGenericArguments()[1], options, path + "." + property.Name);
            return;
        }
        var sequence = interfaces.FirstOrDefault(t => t.IsGenericType && t.GetGenericTypeDefinition() == typeof(IEnumerable<>));
        if (type.IsArray || sequence is not null)
        {
            if (value.ValueKind == JsonValueKind.Array)
            {
                var elementType = type.IsArray ? type.GetElementType()! : sequence!.GetGenericArguments()[0];
                int index = 0;
                foreach (var element in value.EnumerateArray()) ValidateValue(element, elementType, options, path + "[" + index++ + "]");
            }
            return;
        }
        // Leave wrong JSON kinds and scalar conversions to System.Text.Json so
        // its existing errors, enum rules and named-float handling are preserved.
        if (value.ValueKind != JsonValueKind.Object) return;

        var comparer = options.PropertyNameCaseInsensitive ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
        var contract = Members.GetOrAdd(type, Describe).Where(m => !m.Field || options.IncludeFields)
            .ToDictionary(m => m.ExplicitName ?? options.PropertyNamingPolicy?.ConvertName(m.Name) ?? m.Name, comparer);
        var present = new HashSet<string>(comparer);
        foreach (var property in value.EnumerateObject())
        {
            if (!contract.TryGetValue(property.Name, out var member)) continue; // Unknown properties remain allowed.
            present.Add(property.Name);
            ValidateValue(property.Value, member.Type, options, path + "." + property.Name);
        }
        foreach (var pair in contract)
            if (pair.Value.Required && !present.Contains(pair.Key))
                throw new JsonException("Отсутствует обязательное поле JSON: " + path + "." + pair.Key + ".", path, null, null);
    }

    private static bool NeedsValidation(Type type, bool includeFields)
    {
        type = Nullable.GetUnderlyingType(type) ?? type;
        var key = (type, includeFields);
        if (RequiredReachable.TryGetValue(key, out bool known)) return known;
        bool result = HasRequired(type, includeFields, new HashSet<Type>());
        // Cache only a complete search from this root. A false returned while
        // cutting a DFS cycle says nothing about that nested type's full graph.
        RequiredReachable.TryAdd(key, result);
        return result;
    }

    private static bool HasRequired(Type type, bool includeFields, HashSet<Type> visiting)
    {
        type = Nullable.GetUnderlyingType(type) ?? type;
        if (type == typeof(object) || type == typeof(JsonElement) || type == typeof(JsonDocument)
            || type == typeof(string) || type.IsPrimitive || type.IsEnum) return false;
        if (RequiredReachable.TryGetValue((type, includeFields), out bool known)) return known;
        if (!visiting.Add(type)) return false;
        try
        {
            var interfaces = type.GetInterfaces().Prepend(type).ToArray();
            var dictionary = interfaces.FirstOrDefault(t => t.IsGenericType
                && (t.GetGenericTypeDefinition() == typeof(IDictionary<,>) || t.GetGenericTypeDefinition() == typeof(IReadOnlyDictionary<,>)));
            if (dictionary is not null) return HasRequired(dictionary.GetGenericArguments()[1], includeFields, visiting);
            if (type.IsArray) return HasRequired(type.GetElementType()!, includeFields, visiting);
            var sequence = interfaces.FirstOrDefault(t => t.IsGenericType && t.GetGenericTypeDefinition() == typeof(IEnumerable<>));
            if (sequence is not null) return HasRequired(sequence.GetGenericArguments()[0], includeFields, visiting);
            return Members.GetOrAdd(type, Describe).Where(m => !m.Field || includeFields)
                .Any(m => m.Required || HasRequired(m.Type, includeFields, visiting));
        }
        finally { visiting.Remove(type); }
    }

    private static Member[] Describe(Type type)
    {
        var result = new List<Member>();
        foreach (var property in type.GetProperties(BindingFlags.Public | BindingFlags.Instance))
        {
            if (property.GetIndexParameters().Length != 0) continue;
            bool required = IsRequired(property);
            bool ignored = property.GetCustomAttribute<JsonIgnoreAttribute>()?.Condition == JsonIgnoreCondition.Always;
            bool writable = property.SetMethod?.IsPublic == true || property.IsDefined(typeof(JsonIncludeAttribute));
            if (required && (ignored || !writable)) throw new InvalidOperationException("Обязательное JSON-поле недоступно для чтения: " + type.Name + "." + property.Name);
            if (ignored || !writable) continue;
            result.Add(new(property.Name, property.GetCustomAttribute<JsonPropertyNameAttribute>()?.Name, property.PropertyType, required, false));
        }
        foreach (var field in type.GetFields(BindingFlags.Public | BindingFlags.Instance))
        {
            if (field.GetCustomAttribute<JsonIgnoreAttribute>()?.Condition == JsonIgnoreCondition.Always || field.IsInitOnly) continue;
            result.Add(new(field.Name, field.GetCustomAttribute<JsonPropertyNameAttribute>()?.Name, field.FieldType,
                IsRequired(field), !field.IsDefined(typeof(JsonIncludeAttribute))));
        }
        return result.ToArray();
    }

    private static bool IsRequired(MemberInfo member) => member.IsDefined(typeof(LegacyJsonRequiredAttribute))
        || member.CustomAttributes.Any(a => a.AttributeType.FullName == "System.Text.Json.Serialization.JsonRequiredAttribute");
}
#endif
