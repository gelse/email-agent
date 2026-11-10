using System.Text.Json.Serialization;
using EmailAgent.Api.Pagination;
using EmailAgent.Infrastructure.Entities;

namespace EmailAgent.Api.Endpoints;

public sealed class NotificationDto
{
    [JsonPropertyName("id")] public Guid Id { get; set; }
    [JsonPropertyName("kind")] public string Kind { get; set; } = string.Empty;
    [JsonPropertyName("title")] public string Title { get; set; } = string.Empty;
    [JsonPropertyName("body")] public string? Body { get; set; }
    [JsonPropertyName("related_type")] public string? RelatedType { get; set; }
    [JsonPropertyName("related_id")] public Guid? RelatedId { get; set; }
    [JsonPropertyName("created_at")] public DateTimeOffset CreatedAt { get; set; }
    [JsonPropertyName("read_at")] public DateTimeOffset? ReadAt { get; set; }

    public static NotificationDto FromEntity(UserNotification n) => new()
    {
        Id = n.Id,
        Kind = n.Kind,
        Title = n.Title,
        Body = n.Body,
        RelatedType = n.RelatedType,
        RelatedId = n.RelatedId,
        CreatedAt = n.CreatedAt,
        ReadAt = n.ReadAt,
    };
}

public sealed record ReadAllResponse
{
    [JsonPropertyName("updated")] public int Updated { get; init; }
}

[JsonSerializable(typeof(NotificationDto))]
[JsonSerializable(typeof(PagedResult<NotificationDto>))]
[JsonSerializable(typeof(ReadAllResponse))]
public partial class NotificationsJsonContext : JsonSerializerContext { }
