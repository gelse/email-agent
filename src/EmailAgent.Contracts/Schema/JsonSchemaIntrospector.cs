using System.Text.Json;
using Json.Schema;

namespace EmailAgent.Contracts.Schema;

/// <summary>
/// Reads a JSON Schema without instantiating anything (M2T2): the SPA uses
/// <see cref="ListFields"/> to mirror required flags in its hand-written form
/// (M2T6, Q-F4), the plugin loader uses <see cref="IsValidSchemaText"/> to
/// screen a manifest's <c>ConfigSchema</c> / <c>ArgsSchema</c> (M2T3), and the
/// admin plugin viewer lists the fields (M7b).
/// </summary>
public static class JsonSchemaIntrospector
{
    /// <summary>
    /// Flattens the schema's <c>properties</c> into <see cref="SchemaField"/>
    /// records. Nested object properties are emitted both as themselves
    /// (type <c>object</c>) and as one entry per nested key with a dotted
    /// path (<c>tls.min_version</c>). Nested <c>Required</c> is relative to
    /// the containing object: <c>tls.min_version</c> reports <c>true</c> when
    /// the <c>tls</c> schema requires it, even if <c>tls</c> itself is
    /// optional — consumers must treat a field as mandatory only when it and
    /// every dotted ancestor are required. A schema without
    /// <c>properties</c> (or a boolean schema) yields an empty list; fields
    /// reachable only through <c>$ref</c> or composition (<c>allOf</c> ...) are
    /// not followed — plugin schemas must declare <c>properties</c> inline for
    /// introspection.
    /// </summary>
    public static IReadOnlyList<SchemaField> ListFields(JsonSchema schema)
    {
        // Root.Source is the schema's own JSON — the same text FromText parsed
        // or the builder produced — so both creation paths introspect alike.
        var root = schema.Root.Source;
        List<SchemaField> fields = [];
        if (root.ValueKind == JsonValueKind.Object)
            CollectFields(root, prefix: null, fields);
        return fields;
    }

    /// <summary>
    /// Parses <paramref name="text"/> as a schema and evaluates it once
    /// against <c>{}</c>; returns <c>false</c> when the text is not valid
    /// JSON, not a valid schema document, or references something it cannot
    /// resolve at its root (dangling/external <c>$ref</c>). References nested
    /// inside <c>properties</c> only resolve when an instance reaches them —
    /// <see cref="JsonSchemaValidator.Validate(JsonSchema, JsonElement)"/>
    /// maps those failures to a failing result instead of throwing.
    /// </summary>
    public static bool IsValidSchemaText(string text)
    {
        try
        {
            // Fresh registry per call: a schema with an $id would otherwise be
            // rejected the second time it is parsed in this process
            // ("Overwriting registered schemas is not permitted").
            var schema = JsonSchema.FromText(
                text,
                new BuildOptions { SchemaRegistry = new SchemaRegistry() });

            // Throwaway evaluation so a schema that parses but cannot be
            // evaluated fails the loader gate here, not later at validation.
            using var empty = JsonDocument.Parse("{}");
            _ = schema.Evaluate(
                empty.RootElement,
                new EvaluationOptions { OutputFormat = OutputFormat.List });

            return true;
        }
        catch (Exception)
        {
            // Callers only need a yes/no gate; JsonSchema.Net distinguishes
            // reader, schema, argument and reference-resolution failures we
            // deliberately collapse (task step 3: catch, return false).
            return false;
        }
    }

    private static void CollectFields(
        JsonElement schemaObject,
        string? prefix,
        List<SchemaField> fields)
    {
        if (!schemaObject.TryGetProperty("properties", out var properties) ||
            properties.ValueKind != JsonValueKind.Object)
        {
            return;
        }

        var required = ReadRequired(schemaObject);

        foreach (var property in properties.EnumerateObject())
        {
            var name = prefix is null ? property.Name : $"{prefix}.{property.Name}";
            var subSchema = property.Value;

            fields.Add(new SchemaField
            {
                Name = name,
                Type = ReadType(subSchema),
                Required = required.Contains(property.Name),
                Description = subSchema.TryGetProperty("description", out var description) &&
                              description.ValueKind == JsonValueKind.String
                    ? description.GetString()
                    : null
            });

            if (subSchema.ValueKind == JsonValueKind.Object &&
                subSchema.TryGetProperty("properties", out _))
            {
                CollectFields(subSchema, name, fields);
            }
        }
    }

    /// <summary>The declared <c>type</c> keyword; a multi-type array is joined with <c>|</c>; absent means <c>any</c>.</summary>
    private static string ReadType(JsonElement subSchema)
    {
        if (subSchema.TryGetProperty("type", out var type))
        {
            if (type.ValueKind == JsonValueKind.String)
                return type.GetString()!;

            if (type.ValueKind == JsonValueKind.Array)
            {
                var union = string.Join(
                    "|",
                    type.EnumerateArray()
                        .Where(t => t.ValueKind == JsonValueKind.String)
                        .Select(t => t.GetString()));
                if (union.Length > 0)
                    return union;
            }
        }

        return "any";
    }

    private static HashSet<string> ReadRequired(JsonElement schemaObject)
    {
        var required = new HashSet<string>(StringComparer.Ordinal);
        if (schemaObject.TryGetProperty("required", out var array) &&
            array.ValueKind == JsonValueKind.Array)
        {
            foreach (var entry in array.EnumerateArray())
            {
                if (entry.ValueKind == JsonValueKind.String)
                    required.Add(entry.GetString()!);
            }
        }

        return required;
    }
}
