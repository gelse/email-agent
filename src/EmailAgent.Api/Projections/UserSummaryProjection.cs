using EmailAgent.Api.Endpoints;
using EmailAgent.Infrastructure.Entities;

namespace EmailAgent.Api.Projections;

/// <summary>
/// M1T7 task step 3: the <c>User</c> → <see cref="UserSummaryResponse"/>
/// mapping used by both read endpoints. Deliberately hand-written (not
/// auto-mapped) so the wire projection is an explicit allowlist —
/// <c>PasswordHash</c>, <c>KdfSalt</c>, <c>KdfParams</c>, <c>DekWrapped</c>
/// and <c>DekNonce</c> are never read here, and the response record has no
/// members for them (asserted by <c>UserSummaryProjection_DoesNotExposeDekColumns</c>).
/// </summary>
public static class UserSummaryProjection
{
    /// <summary>Projects a stored user to the §13.3 admin summary DTO.</summary>
    public static UserSummaryResponse FromUser(User user)
    {
        ArgumentNullException.ThrowIfNull(user);

        return new UserSummaryResponse
        {
            Id = user.Id,
            Email = user.Email ?? string.Empty,
            Role = user.Role,
            Status = user.Status,
            MustChangePassword = user.MustChangePassword,
            CreatedAt = user.CreatedAt,
            LastLoginAt = user.LastLoginAt,
        };
    }
}
