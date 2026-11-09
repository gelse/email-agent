using EmailAgent.Contracts;
using Json.Schema;

namespace EmailAgent.Api.Actions;

/// <summary>
/// M4T3: typed failures for the actions/notifications API surface. Endpoint
/// handlers map each to its §13.12 RFC 7807 document (services never touch
/// <c>HttpContext</c>).
/// </summary>
public static class ActionErrors
{
    public static class Types
    {
        public const string Validation = "/errors/validation";
        public const string NotFound = "/errors/not-found";
        public const string Conflict = "/errors/conflict";
        public const string Unauthorized = "/errors/unauthorized";
        public const string Forbidden = "/errors/forbidden";

        public const string ConfigValidation = "/errors/config-validation";
        public const string ActionNotFound = "/errors/action-not-found";
        public const string PluginNotRegistered = "/errors/plugin-not-registered";
        public const string NotificationNotFound = "/errors/notification-not-found";
        public const string DekRequired = "/errors/dek-required";
        public const string DekInvalid = "/errors/dek-invalid";
        public const string ActionInUse = "/errors/action-in-use";
        public const string InternalBlobMissing = "/errors/internal-blob-missing";
    }
}

public sealed class ActionConfigNotFoundException(Guid actionId)
    : Exception($"No action config with id {actionId}.")
{
    public Guid ActionId { get; } = actionId;
}

public sealed class ActionPluginNotRegisteredException(string typeId)
    : Exception($"No action-handler plugin is registered under '{typeId}'.")
{
    public string TypeId { get; } = typeId;
}

public sealed class ActionConfigValidationException(
    string? error, IReadOnlyList<ValidationIssue> issues, JsonSchema schema)
    : Exception(string.IsNullOrWhiteSpace(error) ? "The action configuration is invalid." : error)
{
    public IReadOnlyList<ValidationIssue> Issues { get; } = issues ?? throw new ArgumentNullException(nameof(issues));
    public JsonSchema Schema { get; } = schema ?? throw new ArgumentNullException(nameof(schema));
}

public sealed class ActionDekRequiredException()
    : Exception("The X-User-Dek header is required.");

public sealed class ActionDekInvalidException(Guid blobId, Exception? innerException = null)
    : Exception("The supplied data-encryption key does not open this action configuration.", innerException)
{
    public Guid BlobId { get; } = blobId;
}

public sealed class ActionBlobMissingException(Guid blobId, string reason)
    : Exception($"Encrypted blob {blobId} cannot be read: {reason}.")
{
    public Guid BlobId { get; } = blobId;
}

public sealed class ActionInUseException(Guid actionId)
    : Exception($"Action config {actionId} is referenced by pending actions and cannot be deleted.")
{
    public Guid ActionId { get; } = actionId;
}

public sealed class NotificationNotFoundException(Guid notificationId)
    : Exception($"No notification with id {notificationId}.")
{
    public Guid NotificationId { get; } = notificationId;
}
