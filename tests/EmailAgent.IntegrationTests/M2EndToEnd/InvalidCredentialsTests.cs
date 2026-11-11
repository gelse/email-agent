using System.Net;
using EmailAgent.Core.Audit;
using EmailAgent.IntegrationTests.Endpoints;
using EmailAgent.IntegrationTests.Fixtures;
using FluentAssertions;
using Xunit;

namespace EmailAgent.IntegrationTests.M2EndToEnd;

/// <summary>
/// Smoke flow 2 of M2T7 (invalid credentials) as xUnit facts: a wrong IMAP
/// password is a plugin VALUE in an always-200 body, while a config the API
/// can prove wrong at its own boundary is an RFC 7807 400. Greenmail's
/// authentication is genuinely on (fixture minus <c>auth.disabled</c>), so the
/// 200/{ok:false} half talks to a real LOGIN rejection — no production service
/// is contacted (AGENTS.md hard constraint).
/// </summary>
[Trait("Category", "M2EndToEnd")]
[Collection(M2E2ECollection.Name)]
public sealed class InvalidCredentialsTests(PostgresFixture postgres, GreenMailFixture greenMail)
    : IClassFixture<GreenMailFixture>
{
    /// <summary>
    /// Smoke flow 2, step 2: <c>POST /v1/user/sources/test-connection</c> with
    /// <c>password='wrong'</c> → HTTP 200 <c>{ok:false, error:"authentication
    /// failed…"</c>} — the failure lives in the body so the SPA can render it
    /// inline (milestone exit criterion 2, "surfaced in the UI").
    /// </summary>
    [Fact]
    public async Task InvalidCredentials_TestConnection_ReturnsOkFalse_WithAuthError()
    {
        var host = await SourcesTestHost.CreateUserAsync(postgres);
        await using var factory = host.Factory;
        using var client = host.Client;

        using var response = await client.SendAsync(SourcesTestHost.Request(
            HttpMethod.Post, "/v1/user/sources/test-connection", host.Bearer,
            new
            {
                type_id = SourcesTestHost.ImapTypeId,
                config = greenMail.CreateConfig(password: "definitely-not-the-password"),
            },
            session: host.Session));

        response.StatusCode.Should().Be(
            HttpStatusCode.OK, "an authentication failure is a plugin outcome, not an HTTP error");
        var body = await AccountTestHost.ReadJsonAsync(response);
        body.GetProperty("ok").GetBoolean().Should().BeFalse();
        body.GetProperty("error").GetString().Should().StartWith(
            "authentication failed",
            "Greenmail rejects a wrong LOGIN with `NO LOGIN failed …`, got: "
            + body.GetProperty("error").GetRawText());
    }

    /// <summary>
    /// Smoke flow 2, step 3: <c>POST /v1/user/sources</c> with
    /// <c>port=99999</c> → HTTP 400 <c>application/problem+json</c>,
    /// <c>type=/errors/config-validation</c>, <c>errors[]</c> carrying the
    /// pinned <c>{name:"port", reason:"must be between 1 and 65535"}</c>.
    /// </summary>
    [Fact]
    public async Task InvalidCredentials_BadPort_Returns400_ProblemDetails_WithErrors()
    {
        var host = await SourcesTestHost.CreateUserAsync(postgres);
        await using var factory = host.Factory;
        using var client = host.Client;

        using var response = await client.SendAsync(SourcesTestHost.Request(
            HttpMethod.Post, "/v1/user/sources", host.Bearer,
            new
            {
                type_id = SourcesTestHost.ImapTypeId,
                display_name = "bad port source",
                config = SourcesTestHost.Config(port: 99999),
            },
            session: host.Session));

        response.Content.Headers.ContentType!.ToString()
            .Should().Contain("application/problem+json");
        var reason = await SourcesTestHost.ReadErrorReasonAsync(
            response, HttpStatusCode.BadRequest, "/errors/config-validation", "port");
        reason.Should().Be(
            "must be between 1 and 65535", "the milestone pins this exact reason");

        // The rejected create wrote nothing (no source row, no audit claim).
        (await AccountTestHost.AuditRowsAsync(factory, host.UserId))
            .Should().NotContain(a => a.Action == AuditActions.CreateSource);
    }
}
