using EmailAgent.Contracts;
using Json.Schema;

namespace EmailAgent.Api.Sources;

/// <summary>
/// M2T5: typed failures <see cref="SourceService"/> raises. The endpoint
/// layer (<c>SourcesHandlers</c>) maps each one to its §13.12 RFC 7807
/// document, so the service never touches <c>HttpContext</c> and every
/// outcome stays unit-testable.
/// </summary>
public static class SourceErrors
{
    /// <summary>The §13.12 error classes this namespace emits.</summary>
    public static class Types
    {
        public const string Validation = "/errors/validation";
        public const string NotFound = "/errors/not-found";
        public const string Conflict = "/errors/conflict";
        public const string Unauthorized = "/errors/unauthorized";
        public const string Forbidden = "/errors/forbidden";
        public const string ConfigValidation = "/errors/config-validation";
        public const string SourceNotFound = "/errors/source-not-found";
        public const string PluginNotRegistered = "/errors/plugin-not-registered";
        public const string DekInvalid = "/errors/dek-invalid";
        public const string InternalBlobMissing = "/errors/internal-blob-missing";

        /// <summary>
        /// The plugin's connection test failed with an exception the API cannot
        /// classify (task file "RFC 7807 error types" — the fourth documented
        /// class; every *expected* failure stays in the 200 <c>{ok:false}</c>
        /// body or the 400 config-validation document).
        /// </summary>
        public const string SourceConnectionFailed = "/errors/source-connection-failed";
    }
}

/// <summary>
/// No <c>message_sources</c> row with that id belongs to the caller. The
/// cross-user case raises this too — a foreign id must be indistinguishable
/// from an unknown one (no existence oracle; task Risks).
/// </summary>
public sealed class SourceNotFoundException(Guid sourceId) : Exception($"No message source with id {sourceId}.")
{
    public Guid SourceId { get; } = sourceId;
}

/// <summary>
/// <c>message_sources.type_id</c> names a plugin the registry does not hold
/// (never loaded, or dropped by a SIGHUP reload).
/// </summary>
public sealed class PluginNotRegisteredException(string typeId)
    : Exception($"No message-source plugin is registered under '{typeId}'.")
{
    public string TypeId { get; } = typeId;
}

/// <summary>
/// The connector config failed the plugin's <c>ConfigSchema</c> (M2T2).
/// Carries the field-level issues <b>and the schema that produced them</b> so
/// the handler can render the <c>errors: [{name, reason}]</c> array of a 400
/// <c>/errors/config-validation</c> document: <see cref="ConfigErrorMapper"/>
/// resolves each failing keyword against the very schema the plugin declared
/// (<c>port: -1</c> → <c>must be between 1 and 65535</c>), which it can only
/// do when the exception carries it.
/// </summary>
public sealed class ConfigValidationException(string? error, IReadOnlyList<ValidationIssue> issues, JsonSchema schema)
    : Exception(string.IsNullOrWhiteSpace(error) ? "The source configuration is invalid." : error)
{
    public IReadOnlyList<ValidationIssue> Issues { get; } = issues ?? throw new ArgumentNullException(nameof(issues));

    /// <summary>The plugin's <c>ConfigSchema</c> the issues were raised against.</summary>
    public JsonSchema Schema { get; } = schema ?? throw new ArgumentNullException(nameof(schema));
}

/// <summary>
/// The supplied <c>X-User-Dek</c> does not decrypt the stored blob: wrong
/// key, wrong owner binding, or a corrupt row. M2 cannot verify the header
/// against <c>users.dek_wrapped</c> (that wrap needs the passphrase-derived
/// KEK), so an unauthenticating unwrap is the only signal — §13.12 400
/// <c>/errors/dek-invalid</c>, never a 500.
/// </summary>
public sealed class DekInvalidException(Guid blobId, Exception innerException) : Exception(
    "The supplied data-encryption key does not open this source's configuration.",
    innerException)
{
    public Guid BlobId { get; } = blobId;
}

/// <summary>
/// A source row points at an <c>encrypted_blobs</c> row that does not exist
/// (or is owned by someone else / has the wrong purpose). A server-side
/// integrity violation, not a client error — §13.12
/// <c>/errors/internal-blob-missing</c>.
/// </summary>
public sealed class InternalBlobMissingException(Guid blobId, string reason)
    : Exception($"Encrypted blob {blobId} cannot be read: {reason}.")
{
    public Guid BlobId { get; } = blobId;
}

/// <summary>
/// <c>plugin.ValidateConfigAsync</c> threw instead of returning a
/// <see cref="ValidationResult"/>: a failure the API cannot attribute to
/// credentials, transport or schema. Only an *unexpected* exception lands
/// here — authentication and connection problems come back as
/// <c>{ ok: false, error }</c> with HTTP 200 — so the handler answers the
/// task's documented §13.12 <c>/errors/source-connection-failed</c> 500
/// rather than the generic <c>/errors/internal</c>.
/// </summary>
public sealed class SourceConnectionFailedException(string typeId, Exception innerException)
    : Exception($"The connection test for '{typeId}' failed unexpectedly.", innerException)
{
    public string TypeId { get; } = typeId;
}

/// <summary>
/// The source still has rows in <c>processed_messages</c>, so the
/// <c>ON DELETE RESTRICT</c> foreign key would reject the delete. M2T5
/// pre-checks and answers 409 <c>/errors/conflict</c> instead of letting the
/// constraint surface as a 500; the processed rows are retained per Q-O15
/// (relaxing the FK is a separate, auditable decision deferred to M6aT7).
/// </summary>
public sealed class SourceHasMessagesException(Guid sourceId)
    : Exception($"Source {sourceId} still has processed messages and cannot be deleted.")
{
    public Guid SourceId { get; } = sourceId;
}
