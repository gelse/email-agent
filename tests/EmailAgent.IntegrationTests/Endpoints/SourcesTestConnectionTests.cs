using System.Net;
using System.Text.Json;
using EmailAgent.Core.Audit;
using EmailAgent.IntegrationTests.Fixtures;
using FluentAssertions;
using Xunit;

namespace EmailAgent.IntegrationTests.Endpoints;

// ---------------------------------------------------------------------------
// M2T5 — POST /v1/user/sources/test-connection. The route is collection-level
// with a {type_id, config} body (no source id yet, no DEK header), and the
// contract has two halves: expected connector failures are VALUES in a 200
// {ok:false, error} body the SPA renders inline, while failures the API can
// prove at its own boundary (schema, plugin) are RFC 7807 400s raised before
// any socket opens. Greenmail is the live server for the credential cases —
// authentication is genuinely ON in the fixture.
// ---------------------------------------------------------------------------

[Collection("Postgres")]
public class SourcesTestConnectionTests(PostgresFixture postgres, GreenMailFixture greenmail)
    : IClassFixture<GreenMailFixture>
{
    /// <summary>
    /// Milestone row "surfaced by /test-connection": HTTP 200 with
    /// <c>{ok:false, error:"…"}</c> — the failure is in the body, not the
    /// status code (the SPA reads <c>ok</c>).
    /// </summary>
    [Fact]
    public async Task Sources_TestConnection_BadPassword_ReturnsOkFalse_WithError()
    {
        var host = await SourcesTestHost.CreateUserAsync(postgres);
        await using var factory = host.Factory;
        using var client = host.Client;

        using var response = await SendAsync(
            host, greenmail.CreateConfig(password: "definitely-not-the-password"));

        response.StatusCode.Should().Be(HttpStatusCode.OK,
            "an authentication failure is a plugin outcome, not an HTTP error");
        var body = await AccountTestHost.ReadJsonAsync(response);
        body.GetProperty("ok").GetBoolean().Should().BeFalse();
        body.GetProperty("error").GetString().Should()
            .StartWith("authentication failed",
                $"Greenmail answers a wrong LOGIN with `NO LOGIN failed …`, got: "
                + body.GetProperty("error").GetRawText());
    }

    /// <summary>Milestone row "Other validation failures (bad host…)".</summary>
    [Fact]
    public async Task Sources_TestConnection_BadHost_ReturnsOkFalse_WithError()
    {
        var host = await SourcesTestHost.CreateUserAsync(postgres);
        await using var factory = host.Factory;
        using var client = host.Client;

        // Nothing listens on port 1 of the loopback interface: the connect
        // fails before any IMAP or TLS traffic — the connection bucket.
        using var response = await SendAsync(
            host, greenmail.CreateConfig(host: "127.0.0.1", port: 1));

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await AccountTestHost.ReadJsonAsync(response);
        body.GetProperty("ok").GetBoolean().Should().BeFalse();
        body.GetProperty("error").GetString().Should()
            .StartWith("connection failed", "an unreachable endpoint is a transport outcome");
    }

    /// <summary>
    /// Milestone row (pinned name): <c>port=99999</c> is rejected by the
    /// JSON-Schema gate BEFORE any network call, so despite the name this is
    /// the RFC 7807 400 half of the contract — 200/{ok:false} would imply the
    /// plugin had been reached. The milestone's parenthetical asserts exactly
    /// that: "returns RFC 7807 400 with errors array".
    /// </summary>
    [Fact]
    public async Task Sources_TestConnection_PortOutOfRange_ReturnsOkFalse_WithError()
    {
        var host = await SourcesTestHost.CreateUserAsync(postgres);
        await using var factory = host.Factory;
        using var client = host.Client;

        using var response = await SendAsync(
            host, greenmail.CreateConfig(port: 99999));

        response.Content.Headers.ContentType!.ToString()
            .Should().Contain("application/problem+json");
        var reason = await SourcesTestHost.ReadErrorReasonAsync(
            response, HttpStatusCode.BadRequest, "/errors/config-validation", "port");
        reason.Should().Be("must be between 1 and 65535",
            "the milestone pins this exact reason");
    }

    [Fact]
    public async Task Sources_TestConnection_ValidConfigAgainstGreenmail_ReturnsOkTrue()
    {
        var host = await SourcesTestHost.CreateUserAsync(postgres);
        await using var factory = host.Factory;
        using var client = host.Client;

        using var response = await SendAsync(host, greenmail.CreateConfig());

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await AccountTestHost.ReadJsonAsync(response);
        body.GetProperty("ok").GetBoolean().Should().BeTrue(
            $"alice/alice is provisioned on {greenmail.ImapHost}:{greenmail.ImapPort} — got: "
            + body.GetProperty("error").GetRawText());
        body.GetProperty("error").ValueKind.Should().Be(System.Text.Json.JsonValueKind.Null);
    }

    [Fact]
    public async Task Sources_TestConnection_UnknownPlugin_Returns400_BeforeAnySocket()
    {
        var host = await SourcesTestHost.CreateUserAsync(postgres);
        await using var factory = host.Factory;
        using var client = host.Client;

        using var response = await client.SendAsync(SourcesTestHost.Request(
            HttpMethod.Post, "/v1/user/sources/test-connection", host.Bearer,
            new
            {
                type_id = "carrier-pigeon",
                config = greenmail.CreateConfig(),
            }));

        (await AccountTestHost.ReadProblemAsync(response, HttpStatusCode.BadRequest))
            .GetProperty("type").GetString().Should().Be("/errors/plugin-not-registered");
    }

    /// <summary>§15: every state change — including a mere connection test — is audited.</summary>
    [Fact]
    public async Task Sources_TestConnection_WritesAuditRow_WithOkOutcome()
    {
        var host = await SourcesTestHost.CreateUserAsync(postgres);
        await using var factory = host.Factory;
        using var client = host.Client;

        using (var failed = await SendAsync(
                   host, greenmail.CreateConfig(password: "wrong-again")))
        {
            failed.StatusCode.Should().Be(HttpStatusCode.OK);
        }

        using (var succeeded = await SendAsync(host, greenmail.CreateConfig()))
        {
            succeeded.StatusCode.Should().Be(HttpStatusCode.OK);
        }

        var audits = await AccountTestHost.AuditRowsAsync(factory, host.UserId);
        var rows = audits.Where(a => a.Action == AuditActions.TestSourceConnection).ToList();
        rows.Should().HaveCount(2, "one row per test, success or failure");
        // metadata_json is jsonb: Postgres re-serializes the stored text (a
        // space after every ':' and ','), so assert the parsed payload's
        // values rather than the writer's compact wire spelling.
        rows.Select(r => r.MetadataJson).Should().Contain(m =>
            JsonDocument.Parse(m ?? "{}", default).RootElement.GetProperty("ok").GetBoolean() == false);
        rows.Select(r => r.MetadataJson).Should().Contain(m =>
            JsonDocument.Parse(m ?? "{}", default).RootElement.GetProperty("ok").GetBoolean() == true);
        rows.Should().OnlyContain(r => r.TargetType == "source");
    }

    /// <summary>Request-shape gate: no body / bad JSON are §13.12 400s too.</summary>
    [Fact]
    public async Task Sources_TestConnection_MissingBody_Returns400_Validation()
    {
        var host = await SourcesTestHost.CreateUserAsync(postgres);
        await using var factory = host.Factory;
        using var client = host.Client;

        using var empty = await client.SendAsync(SourcesTestHost.RawRequest(
            HttpMethod.Post, "/v1/user/sources/test-connection", host.Bearer, string.Empty));
        (await SourcesTestHost.ReadErrorReasonAsync(
            empty, HttpStatusCode.BadRequest, "/errors/validation", "body"))
            .Should().Contain("required");

        using var noType = await client.SendAsync(SourcesTestHost.RawRequest(
            HttpMethod.Post, "/v1/user/sources/test-connection", host.Bearer,
            """{"config":{}}"""));
        (await SourcesTestHost.ReadErrorReasonAsync(
            noType, HttpStatusCode.BadRequest, "/errors/validation", "type_id"))
            .Should().NotBeNullOrWhiteSpace();
    }

    /// <summary>POSTs the given config to the collection-level test-connection route.</summary>
    private Task<HttpResponseMessage> SendAsync(SourcesHost host, System.Text.Json.JsonElement config)
        => host.Client.SendAsync(SourcesTestHost.Request(
            HttpMethod.Post, "/v1/user/sources/test-connection", host.Bearer,
            new { type_id = SourcesTestHost.ImapTypeId, config }));
}
