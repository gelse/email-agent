using System.Text.Json;
using System.Text.RegularExpressions;
using Json.Schema;

namespace EmailAgent.Contracts.Schema;

/// <summary>
/// Validates a JSON instance against a JSON Schema and reports field-level
/// issues (M2T2; consumed by M2T5's config save path and the fetch-now
/// re-validation at execution time).
/// </summary>
/// <remarks>
/// <see cref="ValidationIssue.Path"/> is a JSON Pointer into the instance
/// (<c>""</c> for the root, <c>/port</c>, <c>/tls/min_version</c>) and
/// <see cref="ValidationIssue.Reason"/> is the failing JSON-Schema keyword
/// (<c>required</c>, <c>type</c>, <c>maximum</c>, <c>additionalProperties</c>,
/// ...). A failing result always carries at least one issue: when the schema
/// rejects the whole instance without blaming a member (boolean <c>false</c>
/// schema), a root issue with reason <c>false</c> is reported. Container
/// keywords other than <c>properties</c> (e.g. <c>anyOf</c>) may add a
/// parent-level issue next to their branch detail. Malformed schema text,
/// malformed instance JSON and schemas that cannot be evaluated (e.g. a
/// dangling <c>$ref</c>, which JsonSchema.Net resolves lazily at evaluation)
/// all surface as failing results, never as exceptions.
/// </remarks>
public static class JsonSchemaValidator
{
    /// <summary>Canonical <c>error</c> string for a schema-validation failure (M2T2 §In).</summary>
    private const string ValidationFailed = "schema validation failed";

    /// <summary><c>error</c> when the schema text parses but cannot be evaluated (e.g. dangling <c>$ref</c>).</summary>
    private const string EvaluationFailed = "schema evaluation failed";

    /// <summary>
    /// Matches the quoted member names JsonSchema.Net embeds in the
    /// <c>required</c> / <c>additionalProperties</c> messages, e.g.
    /// <c>Required properties ["password"] are not present</c>.
    /// </summary>
    private static readonly Regex QuotedNames =
        new("\"([^\"]+)\"", RegexOptions.Compiled | RegexOptions.CultureInvariant);

    /// <summary>
    /// Evaluates <paramref name="instance"/> against <paramref name="schema"/>.
    /// Returns <c>Ok=true</c> with <c>Issues=null</c> on success, or
    /// <c>Ok=false</c>, <c>Error="schema validation failed"</c> and one issue
    /// per failing member on failure. A schema that cannot be evaluated (e.g.
    /// a dangling <c>$ref</c>) yields <c>Error="schema evaluation failed"</c>
    /// instead of throwing.
    /// </summary>
    public static ValidationResult Validate(JsonSchema schema, JsonElement instance)
    {
        EvaluationResults result;
        try
        {
            result = schema.Evaluate(
                instance,
                new EvaluationOptions { OutputFormat = OutputFormat.List });
        }
        catch (JsonSchemaException)
        {
            // RefResolutionException (a JsonSchemaException) for a dangling or
            // external $ref: JsonSchema.Net defers reference resolution to
            // evaluation, so a plugin's schema that parses can still fail
            // here. M2T5 must get a failing result, not a 500.
            return ValidationResult.Failure(EvaluationFailed);
        }

        if (result.IsValid)
            return ValidationResult.Success();

        var issues = new List<ValidationIssue>();
        var seen = new HashSet<(string Path, string Reason)>();
        CollectIssues(result, issues, seen);

        if (issues.Count == 0)
        {
            // Boolean `false` schema: the only error carries an empty keyword
            // and no member, so nothing above is attributed — blame the root
            // rather than returning an empty issue list.
            issues.Add(new ValidationIssue(string.Empty, "false"));
        }

        return ValidationResult.Failure(ValidationFailed, issues);
    }

    /// <summary>
    /// Parses <paramref name="schemaJson"/> with <c>JsonSchema.FromText</c> and
    /// validates <paramref name="instance"/> against it. Unparseable schema
    /// text yields <c>Error="invalid schema JSON"</c>.
    /// </summary>
    public static ValidationResult Validate(string schemaJson, JsonElement instance)
    {
        JsonSchema schema;
        try
        {
            // A fresh registry per call: JsonSchema.Net's global registry
            // refuses to register the same $id twice, which would make a
            // perfectly valid plugin schema fail on its second use in one
            // process (save then fetch-now, plugin reload).
            schema = JsonSchema.FromText(
                schemaJson,
                new BuildOptions { SchemaRegistry = new SchemaRegistry() });
        }
        catch (Exception)
        {
            // JsonSchema.Net reports broken schema text with several exception
            // types (reader, schema, argument); all of them mean the same to a
            // caller — the schema document is unusable.
            return ValidationResult.Failure("invalid schema JSON");
        }

        return Validate(schema, instance);
    }

    /// <summary>
    /// Parses both documents and validates. Unparseable instance text yields
    /// <c>Error="invalid instance JSON"</c>; unparseable schema text yields
    /// <c>Error="invalid schema JSON"</c>. Convenience overload for tests and
    /// callers that already hold raw JSON.
    /// </summary>
    public static ValidationResult Validate(string schemaJson, string instanceJson)
    {
        JsonDocument instanceDoc;
        try
        {
            instanceDoc = JsonDocument.Parse(instanceJson);
        }
        catch (JsonException)
        {
            return ValidationResult.Failure("invalid instance JSON");
        }

        using (instanceDoc)
            return Validate(schemaJson, instanceDoc.RootElement);
    }

    /// <summary>
    /// Walks the <c>List</c>-format evaluation tree and turns each failing
    /// keyword into one <see cref="ValidationIssue"/>.
    /// </summary>
    private static void CollectIssues(
        EvaluationResults node,
        List<ValidationIssue> issues,
        HashSet<(string Path, string Reason)> seen)
    {
        var pointer = node.InstanceLocation.ToString();

        if (node.Errors is { Count: > 0 })
        {
            foreach (var error in node.Errors)
            {
                // `properties` reports an aggregate over its children; the
                // child nodes carry the specific keyword and path, so the
                // aggregate would only duplicate them at the parent pointer.
                if (error.Key == "properties")
                    continue;

                // The false-schema leaf under `additionalProperties: false`
                // reports an empty keyword; the parent's message already
                // names the offending member.
                if (string.IsNullOrEmpty(error.Key))
                    continue;

                if (error.Key is "required" or "additionalProperties")
                {
                    // These keywords evaluate at the containing object but
                    // fail for specific members — attribute each issue to the
                    // member by extending the object's pointer.
                    var names = QuotedNames.Matches(error.Value);
                    if (names.Count == 0)
                    {
                        Add(pointer, error.Key);
                    }
                    else
                    {
                        foreach (Match name in names)
                            Add($"{pointer}/{Escape(Decode(name.Groups[1].Value))}", error.Key);
                    }
                }
                else
                {
                    Add(pointer, error.Key);
                }
            }
        }

        if (node.Details is not null)
        {
            foreach (var child in node.Details)
                CollectIssues(child, issues, seen);
        }

        return;

        void Add(string path, string reason)
        {
            if (seen.Add((path, reason)))
                issues.Add(new ValidationIssue(path, reason));
        }
    }

    /// <summary>
    /// The member names JsonSchema.Net embeds in <c>required</c> /
    /// <c>additionalProperties</c> messages are JSON-escaped (a <c>"</c>
    /// inside a name appears as <c>\u0022</c>); decode them before building
    /// the pointer, falling back to the token verbatim when it is not a
    /// well-formed string body.
    /// </summary>
    private static string Decode(string token)
    {
        try
        {
            using var parsed = JsonDocument.Parse($"\"{token}\"");
            return parsed.RootElement.GetString() ?? token;
        }
        catch (JsonException)
        {
            return token;
        }
    }

    /// <summary>JSON-Pointer token escaping (<c>~</c> → <c>~0</c>, <c>/</c> → <c>~1</c>).</summary>
    private static string Escape(string name) =>
        name.Replace("~", "~0", StringComparison.Ordinal)
            .Replace("/", "~1", StringComparison.Ordinal);
}
