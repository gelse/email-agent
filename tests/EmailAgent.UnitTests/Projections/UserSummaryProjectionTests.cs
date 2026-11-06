using System.Reflection;
using System.Text.Json;
using EmailAgent.Api.Endpoints;
using EmailAgent.Api.Projections;
using EmailAgent.Infrastructure.Entities;
using EmailAgent.Infrastructure.Enums;
using FluentAssertions;
using Xunit;

namespace EmailAgent.UnitTests.Projections;

/// <summary>
/// M1T7 task step 3: the admin user projection must never expose password or
/// key material. Two layers — compile-time (the response record has no such
/// members, so including one is impossible without changing this test) and a
/// reflection pin on the exact member set (an accidental addition fails
/// loudly, per the task's Risks).
/// </summary>
public class UserSummaryProjectionTests
{
    [Fact]
    public void UserSummaryProjection_DoesNotExposeDekColumns()
    {
        // 1) Reflection pin: exactly the seven non-sensitive members — the
        //    forbidden columns can never appear among them.
        var exposed = typeof(UserSummaryResponse)
            .GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Select(property => property.Name)
            .OrderBy(name => name);

        exposed.Should().BeEquivalentTo(new[]
        {
            "CreatedAt",
            "Email",
            "Id",
            "LastLoginAt",
            "MustChangePassword",
            "Role",
            "Status",
        });

        // 2) Functional check: a user loaded WITH sentinel key material
        //    projects only the allowed fields.
        var user = new User
        {
            Id = Guid.NewGuid(),
            Email = "projection@example.test",
            Role = UserRole.User,
            Status = UserStatus.Pending,
            MustChangePassword = true,
            CreatedAt = DateTimeOffset.UtcNow,
            LastLoginAt = null,
            PasswordHash = "sentinel-password-hash",
            KdfSalt = new byte[] { 0x01, 0x02 },
            KdfParams = JsonDocument.Parse("{\"memory_kib\":65536}"),
            DekWrapped = new byte[] { 0x03, 0x04, 0x05 },
            DekNonce = new byte[] { 0x06, 0x07 },
        };

        var summary = UserSummaryProjection.FromUser(user);

        summary.Id.Should().Be(user.Id);
        summary.Email.Should().Be(user.Email);
        summary.Role.Should().Be(UserRole.User);
        summary.Status.Should().Be(UserStatus.Pending);
        summary.MustChangePassword.Should().BeTrue();
        summary.CreatedAt.Should().Be(user.CreatedAt);
        summary.LastLoginAt.Should().BeNull();

        // 3) Serialization check: none of the sensitive column names reaches
        //    the wire (the wire names are pinned snake_case by the response
        //    record; PascalCase spellings are checked for belt and braces).
        var json = JsonSerializer.Serialize(summary);
        foreach (var forbidden in new[]
        {
            "password_hash", "PasswordHash",
            "kdf_salt", "KdfSalt",
            "kdf_params", "KdfParams",
            "dek_wrapped", "DekWrapped",
            "dek_nonce", "DekNonce",
            "passphrase", "Passphrase",
        })
        {
            json.Should().NotContain(forbidden);
        }
    }
}
