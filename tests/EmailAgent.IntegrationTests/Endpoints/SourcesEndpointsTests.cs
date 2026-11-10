using System.Net;
using System.Text;
using System.Text.Json;
using EmailAgent.Core.Audit;
using EmailAgent.Core.Security;
using EmailAgent.Infrastructure;
using EmailAgent.Infrastructure.Enums;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace EmailAgent.IntegrationTests.Endpoints;

// ---------------------------------------------------------------------------
// M2T5 — /v1/user/sources CRUD contract (task scope table + milestone
// verification rows). Testcontainers Postgres + the real host; every test
// gets its own user AND DEK (rate limiting is 60/min per user, and one DEK
// must stay stable across the calls of a single test or the GCM tag fails).
// The nine milestone-pinned names live here, in SourcesTestConnectionTests
// and in SourcesFetchNowTests.
// ---------------------------------------------------------------------------

[Collection("Postgres")]
public class SourcesEndpointsTests(PostgresFixture postgres)
{
    /// <summary>The passphrase the unwrap-dek suite's REAL key material is wrapped under.</summary>
    private const string UnwrapPassphrase = "m2t6-unwr4p-passphr4se";

    // -----------------------------------------------------------------------
    // POST — milestone rows
    // -----------------------------------------------------------------------

    /// <summary>
    /// Milestone row "User can add an IMAP source": the row, the encrypted
    /// blob, and a response that carries every §13.13 member but never a
    /// password.
    /// </summary>
    [Fact]
    public async Task Sources_Post_CreatesRow_AndEncryptedBlob_AndReturnsSourceWithoutPassword()
    {
        var host = await SourcesTestHost.CreateUserAsync(postgres);
        await using var factory = host.Factory;
        using var client = host.Client;

        var created = await SourcesTestHost.CreateSourceAsync(host, "work imap");
        var sourceId = SourcesTestHost.IdOf(created);

        // Response shape: the milestone's member list, and no password.
        created.GetProperty("type_id").GetString().Should().Be("imap");
        created.GetProperty("display_name").GetString().Should().Be("work imap");
        created.GetProperty("interval_minutes").GetInt32().Should().Be(
            5, "§6 default interval_minutes = 5 when the member is omitted");
        created.GetProperty("status").GetString().Should().Be("Active");
        created.GetProperty("checkpoint").GetProperty("last_uid").ValueKind
            .Should().Be(JsonValueKind.Null, "a fresh source has never run");
        created.GetProperty("last_run_at").ValueKind.Should().Be(JsonValueKind.Null);
        created.GetProperty("next_run_at").ValueKind.Should().Be(JsonValueKind.Null);

        var config = created.GetProperty("config");
        foreach (var member in new[] { "host", "port", "use_ssl", "username", "mailbox" })
        {
            config.TryGetProperty(member, out _).Should().BeTrue(
                $"the projection must expose '{member}'");
        }

        config.TryGetProperty("password", out _).Should().BeFalse(
            "no response path may carry the connector password");

        var raw = await GetSourceRawAsync(host.Client, host.Bearer, host.Dek, sourceId);
        raw.Should().NotContain("password", "the raw JSON body must not carry the field either");
        raw.Should().NotContain(SourcesTestHost.ConfigPassword,
            "and never the plaintext credential");

        // Acceptance criterion: deserialize into the published typed record
        // and round-trip it — the projection has no password member at all.
        var typed = System.Text.Json.JsonSerializer.Deserialize<EmailAgent.Api.Endpoints.SourceDto>(
            raw, new System.Text.Json.JsonSerializerOptions(
                System.Text.Json.JsonSerializerDefaults.Web));
        typed.Should().NotBeNull();
        System.Text.Json.JsonSerializer.Serialize(typed!)
            .Should().NotContain("password", "a typed round-trip cannot synthesize the member");

        // message_sources row.
        var row = await SourcesTestHost.SourceRowAsync(factory, sourceId);
        row.UserId.Should().Be(host.UserId);
        row.TypeId.Should().Be("imap");
        row.DisplayName.Should().Be("work imap");
        row.IntervalMinutes.Should().Be(5);
        row.Status.Should().Be(MessageSourceStatus.Active);
        row.CheckpointJson.Should().Be("{}");
        row.LastRunAt.Should().BeNull();
        row.NextRunAt.Should().BeNull();

        // encrypted_blobs row: purpose + owner + ciphertext without the plaintext.
        var blob = await SourcesTestHost.BlobAsync(factory, row.ConfigBlobId);
        blob.Purpose.Should().Be("message_source_config");
        blob.OwnerUserId.Should().Be(host.UserId);
        blob.Ciphertext.Should().NotBeNullOrEmpty();
        Encoding.UTF8.GetString(blob.Ciphertext)
            .Should().NotContain(SourcesTestHost.ConfigPassword,
                "the config must be encrypted under the user DEK, not stored in clear");

        // Audit: create_source for this caller.
        var audits = await AccountTestHost.AuditRowsAsync(factory, host.UserId);
        audits.Should().ContainSingle(a => a.Action == AuditActions.CreateSource)
            .Which.TargetId.Should().Be(sourceId.ToString());
    }

    /// <summary>Milestone row "Multiple IMAP sources per user allowed".</summary>
    [Fact]
    public async Task Sources_Post_TwoSources_ForSameUser_BothPersist()
    {
        var host = await SourcesTestHost.CreateUserAsync(postgres);
        await using var factory = host.Factory;
        using var client = host.Client;

        // Two POSTs with different credentials (create never opens a socket,
        // so the second account can be one no server ever sees).
        var first = await SourcesTestHost.CreateSourceAsync(
            host, "primary imap",
            SourcesTestHost.Config(username: "alice", password: "alice-primary"));
        var second = await SourcesTestHost.CreateSourceAsync(
            host, "backup imap",
            SourcesTestHost.Config(username: "bob", password: "alice-backup"));

        var firstId = SourcesTestHost.IdOf(first);
        var secondId = SourcesTestHost.IdOf(second);
        firstId.Should().NotBe(secondId);

        // Both rows persist for the same user.
        (await SourcesTestHost.SourceRowAsync(factory, firstId)).UserId.Should().Be(host.UserId);
        (await SourcesTestHost.SourceRowAsync(factory, secondId)).UserId.Should().Be(host.UserId);

        // GET /v1/user/sources returns both.
        using var listResponse = await client.SendAsync(SourcesTestHost.Request(
            HttpMethod.Get, "/v1/user/sources", host.Bearer, dek: host.Dek));
        listResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        var page = await AccountTestHost.ReadJsonAsync(listResponse);
        var ids = page.GetProperty("items").EnumerateArray()
            .Select(item => item.GetProperty("id").GetGuid()).ToList();
        ids.Should().Contain([firstId, secondId]);

        var audits = await AccountTestHost.AuditRowsAsync(factory, host.UserId);
        audits.Count(a => a.Action == AuditActions.CreateSource).Should().Be(2);
    }

    /// <summary>
    /// Milestone row "Bad-config POST returns RFC 7807" — the pinned example
    /// <c>{name:"port", reason:"must be between 1 and 65535"}</c>.
    /// </summary>
    [Fact]
    public async Task Sources_Post_InvalidConfig_Returns400_ProblemDetails()
    {
        var host = await SourcesTestHost.CreateUserAsync(postgres);
        await using var factory = host.Factory;
        using var client = host.Client;

        var badConfig = SourcesTestHost.Config(port: -1);
        using var response = await client.SendAsync(SourcesTestHost.Request(
            HttpMethod.Post, "/v1/user/sources", host.Bearer,
            new { type_id = "imap", display_name = "bad port", config = badConfig },
            host.Dek));

        response.Content.Headers.ContentType!.ToString()
            .Should().Contain("application/problem+json");
        var reason = await SourcesTestHost.ReadErrorReasonAsync(
            response, HttpStatusCode.BadRequest,
            "/errors/config-validation", "port");
        reason.Should().Be("must be between 1 and 65535");

        // Nothing was written.
        (await AccountTestHost.AuditRowsAsync(factory, host.UserId))
            .Should().NotContain(a => a.Action == AuditActions.CreateSource);
    }

    // -----------------------------------------------------------------------
    // POST — gates
    // -----------------------------------------------------------------------

    [Fact]
    public async Task Sources_Post_UnknownTypeId_Returns400_PluginNotRegistered()
    {
        var host = await SourcesTestHost.CreateUserAsync(postgres);
        await using var factory = host.Factory;
        using var client = host.Client;

        using var response = await client.SendAsync(SourcesTestHost.Request(
            HttpMethod.Post, "/v1/user/sources", host.Bearer,
            new
            {
                type_id = "carrier-pigeon",
                display_name = "no plugin",
                config = SourcesTestHost.Config(),
            },
            host.Dek));

        var problem = await AccountTestHost.ReadProblemAsync(
            response, HttpStatusCode.BadRequest);
        problem.GetProperty("type").GetString().Should().Be(
            "/errors/plugin-not-registered",
            "an unregistered type_id is a client error raised before any socket opens");
        problem.GetProperty("detail").GetString().Should().Contain("carrier-pigeon");
    }

    [Fact]
    public async Task Sources_Post_MissingOrMalformedDek_Returns400_NamedXUserDek()
    {
        var host = await SourcesTestHost.CreateUserAsync(postgres);
        await using var factory = host.Factory;
        using var client = host.Client;

        var body = new
        {
            type_id = "imap",
            display_name = "dek gates",
            config = SourcesTestHost.Config(),
        };

        // Missing header.
        using var missing = await client.SendAsync(SourcesTestHost.Request(
            HttpMethod.Post, "/v1/user/sources", host.Bearer, body));
        (await SourcesTestHost.ReadErrorReasonAsync(
            missing, HttpStatusCode.BadRequest, "/errors/validation", "x-user-dek"))
            .Should().Contain("required");

        // Not base64.
        using var malformed = await client.SendAsync(SourcesTestHost.Request(
            HttpMethod.Post, "/v1/user/sources", host.Bearer, body, dek: "not base64 !!"));
        (await SourcesTestHost.ReadErrorReasonAsync(
            malformed, HttpStatusCode.BadRequest, "/errors/validation", "x-user-dek"))
            .Should().Contain("base64");

        // Wrong length (16 bytes ≠ 32).
        using var shortKey = await client.SendAsync(SourcesTestHost.Request(
            HttpMethod.Post, "/v1/user/sources", host.Bearer, body,
            dek: Convert.ToBase64String(new byte[16])));
        (await SourcesTestHost.ReadErrorReasonAsync(
            shortKey, HttpStatusCode.BadRequest, "/errors/validation", "x-user-dek"))
            .Should().Contain("32");

        (await AccountTestHost.AuditRowsAsync(factory, host.UserId))
            .Should().BeEmpty("a rejected request writes no audit row");
    }

    [Fact]
    public async Task Sources_Post_InvalidBodyShapes_Return400_Validation()
    {
        var host = await SourcesTestHost.CreateUserAsync(postgres);
        await using var factory = host.Factory;
        using var client = host.Client;

        // config absent.
        using var absent = await client.SendAsync(SourcesTestHost.RawRequest(
            HttpMethod.Post, "/v1/user/sources", host.Bearer,
            """{"type_id":"imap","display_name":"x"}""", host.Dek));
        (await SourcesTestHost.ReadErrorReasonAsync(
            absent, HttpStatusCode.BadRequest, "/errors/validation", "config"))
            .Should().Contain("config is required");

        // config explicitly null — distinguishable from absent.
        using var explicitNull = await client.SendAsync(SourcesTestHost.RawRequest(
            HttpMethod.Post, "/v1/user/sources", host.Bearer,
            """{"type_id":"imap","display_name":"x","config":null}""", host.Dek));
        (await SourcesTestHost.ReadErrorReasonAsync(
            explicitNull, HttpStatusCode.BadRequest, "/errors/validation", "config"))
            .Should().Contain("JSON object");

        // Missing type_id / blank display_name.
        using var shapeless = await client.SendAsync(SourcesTestHost.RawRequest(
            HttpMethod.Post, "/v1/user/sources", host.Bearer,
            """{"display_name":"  ","config":{}}""", host.Dek));
        var shapeProblem = await AccountTestHost.ReadProblemAsync(
            shapeless, HttpStatusCode.BadRequest);
        shapeProblem.GetProperty("type").GetString().Should().Be("/errors/validation");
        shapeProblem.GetProperty("errors").EnumerateArray()
            .Select(e => e.GetProperty("name").GetString())
            .Should().Contain(["type_id", "display_name"]);

        // Malformed JSON never reaches the framework binder as a 400 with a
        // different shape — it is a §13.12 validation document too.
        using var malformed = await client.SendAsync(SourcesTestHost.RawRequest(
            HttpMethod.Post, "/v1/user/sources", host.Bearer, "{not json", host.Dek));
        (await SourcesTestHost.ReadErrorReasonAsync(
            malformed, HttpStatusCode.BadRequest, "/errors/validation", "body"))
            .Should().NotBeNullOrWhiteSpace();

        // Empty body.
        using var empty = await client.SendAsync(SourcesTestHost.RawRequest(
            HttpMethod.Post, "/v1/user/sources", host.Bearer, "", host.Dek));
        (await SourcesTestHost.ReadErrorReasonAsync(
            empty, HttpStatusCode.BadRequest, "/errors/validation", "body"))
            .Should().Contain("required");
    }

    // -----------------------------------------------------------------------
    // GET one — ownership oracle
    // -----------------------------------------------------------------------

    [Fact]
    public async Task Sources_Get_UnknownId_Returns404_SourceNotFound()
    {
        var host = await SourcesTestHost.CreateUserAsync(postgres);
        await using var factory = host.Factory;
        using var client = host.Client;

        using var response = await client.SendAsync(SourcesTestHost.Request(
            HttpMethod.Get, $"/v1/user/sources/{Guid.NewGuid()}",
            host.Bearer, dek: host.Dek));

        var problem = await AccountTestHost.ReadProblemAsync(response, HttpStatusCode.NotFound);
        problem.GetProperty("type").GetString().Should().Be("/errors/source-not-found");
    }

    /// <summary>
    /// A foreign id must be indistinguishable from an unknown one: 404 on
    /// read, update and delete — never 403, which would confirm existence.
    /// </summary>
    [Fact]
    public async Task Sources_CrossUser_Requests_Return404_Never403()
    {
        var host = await SourcesTestHost.CreateUserAsync(postgres);
        await using var factory = host.Factory;
        using var client = host.Client;

        var victim = await SourcesTestHost.CreateSourceAsync(host, "private source");
        var victimId = SourcesTestHost.IdOf(victim);

        var (intruderId, intruderBearer, intruderDek) =
            await SourcesTestHost.AddUserAsync(factory);
        intruderId.Should().NotBe(host.UserId);

        using var get = await client.SendAsync(SourcesTestHost.Request(
            HttpMethod.Get, $"/v1/user/sources/{victimId}",
            intruderBearer, dek: intruderDek));
        using var patch = await client.SendAsync(SourcesTestHost.Request(
            HttpMethod.Patch, $"/v1/user/sources/{victimId}",
            intruderBearer, new { display_name = "stolen" }, intruderDek));
        using var delete = await client.SendAsync(SourcesTestHost.Request(
            HttpMethod.Delete, $"/v1/user/sources/{victimId}",
            intruderBearer, dek: intruderDek));

        foreach (var response in new[] { get, patch, delete })
        {
            response.StatusCode.Should().Be(
                HttpStatusCode.NotFound, "403 would be an existence oracle");
            (await AccountTestHost.ReadJsonAsync(response))
                .GetProperty("type").GetString().Should().Be("/errors/source-not-found");
        }

        // …and the victim's row is untouched.
        var row = await SourcesTestHost.SourceRowAsync(factory, victimId);
        row.DisplayName.Should().Be("private source");
        row.UserId.Should().Be(host.UserId);
    }

    [Fact]
    public async Task Sources_Get_WrongDek_Returns400_DekInvalid()
    {
        var host = await SourcesTestHost.CreateUserAsync(postgres);
        await using var factory = host.Factory;
        using var client = host.Client;

        var created = await SourcesTestHost.CreateSourceAsync(host, "dek rotation");
        var sourceId = SourcesTestHost.IdOf(created);

        using var response = await client.SendAsync(SourcesTestHost.Request(
            HttpMethod.Get, $"/v1/user/sources/{sourceId}",
            host.Bearer, dek: SourcesTestHost.NewDek()));

        var problem = await AccountTestHost.ReadProblemAsync(response, HttpStatusCode.BadRequest);
        problem.GetProperty("type").GetString().Should().Be("/errors/dek-invalid");
    }

    // -----------------------------------------------------------------------
    // GET list — filters, cursor pages, headers
    // -----------------------------------------------------------------------

    [Fact]
    public async Task Sources_List_ReturnsOnlyCallersSources_AndRateLimitHeaders()
    {
        var host = await SourcesTestHost.CreateUserAsync(postgres);
        await using var factory = host.Factory;
        using var client = host.Client;

        var mine1 = SourcesTestHost.IdOf(await SourcesTestHost.CreateSourceAsync(host, "mine one"));
        var mine2 = SourcesTestHost.IdOf(await SourcesTestHost.CreateSourceAsync(host, "mine two"));

        var (otherId, otherBearer, otherDek) = await SourcesTestHost.AddUserAsync(factory);
        using (var otherCreate = await client.SendAsync(SourcesTestHost.Request(
            HttpMethod.Post, "/v1/user/sources", otherBearer,
            new
            {
                type_id = "imap",
                display_name = "not yours",
                config = SourcesTestHost.Config(),
            },
            otherDek)))
        {
            otherCreate.StatusCode.Should().Be(HttpStatusCode.Created);
        }

        using var response = await client.SendAsync(SourcesTestHost.Request(
            HttpMethod.Get, "/v1/user/sources", host.Bearer, dek: host.Dek));
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        // §13.14 on every authenticated response.
        response.Headers.Contains("X-RateLimit-Limit").Should().BeTrue();
        response.Headers.Contains("X-RateLimit-Remaining").Should().BeTrue();
        response.Headers.Contains("X-RateLimit-Reset").Should().BeTrue();
        response.Headers.GetValues("X-RateLimit-Limit").Single().Should().Be("60");

        var page = await AccountTestHost.ReadJsonAsync(response);
        page.GetProperty("items").EnumerateArray()
            .Select(item => item.GetProperty("id").GetGuid())
            .Should().BeEquivalentTo([mine1, mine2],
                "the caller sees only their own rows — never the other account's");
        otherId.Should().NotBeEmpty();
    }

    /// <summary>
    /// Task integration row: <c>?limit=2</c> then <c>?limit=2&amp;cursor=…</c>
    /// returns disjoint items, and the last page omits <c>next_cursor</c>.
    /// </summary>
    [Fact]
    public async Task Sources_List_PagesAreDisjoint_AndNextCursorOmittedOnLastPage()
    {
        var host = await SourcesTestHost.CreateUserAsync(postgres);
        await using var factory = host.Factory;
        using var client = host.Client;

        List<Guid> all = [];
        for (var i = 0; i < 5; i++)
        {
            all.Add(SourcesTestHost.IdOf(
                await SourcesTestHost.CreateSourceAsync(host, $"paged source {i}")));
        }

        List<Guid> seen = [];
        string? cursor = null;
        var pages = 0;

        while (true)
        {
            // next_cursor is base64url (query-safe): sent VERBATIM, without
            // percent-encoding, to pin the wire format the docs promise M2T6
            // can concatenate into the query string as-is.
            var path = "/v1/user/sources?limit=2"
                + (cursor is null ? string.Empty : $"&cursor={cursor}");
            using var response = await client.SendAsync(SourcesTestHost.Request(
                HttpMethod.Get, path, host.Bearer, dek: host.Dek));
            response.StatusCode.Should().Be(HttpStatusCode.OK);

            var page = await AccountTestHost.ReadJsonAsync(response);
            var items = page.GetProperty("items").EnumerateArray().ToList();
            seen.AddRange(items.Select(i => i.GetProperty("id").GetGuid()));

            // Keyset order: created_at DESC within and across pages.
            var stamps = items.Select(i => i.GetProperty("created_at").GetDateTimeOffset())
                .ToList();
            stamps.Should().BeInDescendingOrder();

            pages++;
            if (page.TryGetProperty("next_cursor", out var next))
            {
                cursor = next.GetString();
                cursor.Should().NotBeNullOrWhiteSpace();
                continue;
            }

            // Absent member = last page.
            pages.Should().Be(3, "5 items at limit 2 => pages of 2, 2, 1");
            break;
        }

        pages.Should().Be(3);
        seen.Should().OnlyHaveUniqueItems("pages must not repeat a row");
        seen.Should().BeEquivalentTo(all, "the three pages cover every source exactly once");

        // A bad cursor is a validation failure, not a 500.
        using var badCursor = await client.SendAsync(SourcesTestHost.Request(
            HttpMethod.Get, "/v1/user/sources?limit=2&cursor=%25%25%25",
            host.Bearer, dek: host.Dek));
        (await SourcesTestHost.ReadErrorReasonAsync(
            badCursor, HttpStatusCode.BadRequest, "/errors/validation", "cursor"))
            .Should().NotBeNullOrWhiteSpace();
    }

    [Fact]
    public async Task Sources_List_InvalidCursorLimitStatus_Return400_NamedField()
    {
        var host = await SourcesTestHost.CreateUserAsync(postgres);
        await using var factory = host.Factory;
        using var client = host.Client;

        await SourcesTestHost.CreateSourceAsync(host, "listed");

        foreach (var (query, name) in new[]
                 {
                     ("?limit=0", "limit"),
                     ("?limit=101", "limit"),
                     ("?limit=abc", "limit"),
                     ("?status=Weird", "status"),
                 })
        {
            using var response = await client.SendAsync(SourcesTestHost.Request(
                HttpMethod.Get, "/v1/user/sources" + query, host.Bearer, dek: host.Dek));
            var reason = await SourcesTestHost.ReadErrorReasonAsync(
                response, HttpStatusCode.BadRequest, "/errors/validation", name);
            reason.Should().NotBeNullOrWhiteSpace();
        }
    }

    [Fact]
    public async Task Sources_List_Filters_ByTypeIdAndStatus()
    {
        var host = await SourcesTestHost.CreateUserAsync(postgres);
        await using var factory = host.Factory;
        using var client = host.Client;

        var activeImap = SourcesTestHost.IdOf(
            await SourcesTestHost.CreateSourceAsync(host, "active imap"));

        // A paused twin, set through the row (no PATCH status field in M2).
        var paused = SourcesTestHost.IdOf(
            await SourcesTestHost.CreateSourceAsync(host, "paused imap"));
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<EmailAgentDbContext>();
            var row = await db.MessageSources.SingleAsync(s => s.Id == paused);
            row.Status = MessageSourceStatus.Paused;
            await db.SaveChangesAsync();
        }

        // type_id filter: only imap rows of this caller.
        using (var byType = await client.SendAsync(SourcesTestHost.Request(
            HttpMethod.Get, "/v1/user/sources?type_id=imap", host.Bearer, dek: host.Dek)))
        {
            byType.StatusCode.Should().Be(HttpStatusCode.OK);
            (await IdsAsync(byType)).Should().Contain(activeImap);
        }

        // A type this caller does not own: empty page, still 200.
        using (var byOtherType = await client.SendAsync(SourcesTestHost.Request(
            HttpMethod.Get, "/v1/user/sources?type_id=openai-compatible",
            host.Bearer, dek: host.Dek)))
        {
            byOtherType.StatusCode.Should().Be(HttpStatusCode.OK);
            (await IdsAsync(byOtherType)).Should().BeEmpty(
                "no source of that type belongs to this caller");
        }

        // status filter: Active excludes the paused twin…
        using (var active = await client.SendAsync(SourcesTestHost.Request(
            HttpMethod.Get, "/v1/user/sources?status=Active", host.Bearer, dek: host.Dek)))
        {
            var ids = await IdsAsync(active);
            ids.Should().Contain(activeImap).And.NotContain(paused);
        }

        // …and Paused selects exactly it.
        using (var pausedOnly = await client.SendAsync(SourcesTestHost.Request(
            HttpMethod.Get, "/v1/user/sources?status=Paused", host.Bearer, dek: host.Dek)))
        {
            (await IdsAsync(pausedOnly)).Should().Equal(
                [paused], "the paused filter selects exactly the paused row");
        }
    }

    // -----------------------------------------------------------------------
    // PATCH — semantics + the password keep/replace contract
    // -----------------------------------------------------------------------

    [Fact]
    public async Task Sources_Patch_UpdatesDisplayNameAndInterval_AndAudits()
    {
        var host = await SourcesTestHost.CreateUserAsync(postgres);
        await using var factory = host.Factory;
        using var client = host.Client;

        var created = await SourcesTestHost.CreateSourceAsync(host, "before rename");
        var sourceId = SourcesTestHost.IdOf(created);

        using var response = await client.SendAsync(SourcesTestHost.Request(
            HttpMethod.Patch, $"/v1/user/sources/{sourceId}", host.Bearer,
            new { display_name = "after rename", interval_minutes = 30 }, host.Dek));
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var updated = await AccountTestHost.ReadJsonAsync(response);
        updated.GetProperty("display_name").GetString().Should().Be("after rename");
        updated.GetProperty("interval_minutes").GetInt32().Should().Be(30);
        updated.GetProperty("id").GetGuid().Should().Be(sourceId);

        var row = await SourcesTestHost.SourceRowAsync(factory, sourceId);
        row.DisplayName.Should().Be("after rename");
        row.IntervalMinutes.Should().Be(30);

        var audits = await AccountTestHost.AuditRowsAsync(factory, host.UserId);
        audits.Should().ContainSingle(a => a.Action == AuditActions.UpdateSource)
            .Which.MetadataJson.Should().Contain("changed_fields");
    }

    [Fact]
    public async Task Sources_Patch_PasswordAbsent_KeepsStoredPassword()
    {
        var host = await SourcesTestHost.CreateUserAsync(postgres);
        await using var factory = host.Factory;
        using var client = host.Client;

        var created = await SourcesTestHost.CreateSourceAsync(host, "keep pw");
        var sourceId = SourcesTestHost.IdOf(created);

        // A config WITHOUT a password member (the SPA never collected it).
        using var response = await client.SendAsync(SourcesTestHost.RawRequest(
            HttpMethod.Patch, $"/v1/user/sources/{sourceId}", host.Bearer,
            """{"config":{"host":"imap.changed.example","port":143,"use_ssl":false,"username":"m2t5-user","mailbox":"INBOX"}}""",
            host.Dek));
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var row = await SourcesTestHost.SourceRowAsync(factory, sourceId);
        var stored = await SourcesTestHost.UnwrapConfigAsync(
            factory, host.UserId, host.Dek, row.ConfigBlobId);
        stored.Should().Contain("imap.changed.example", "the other members were updated");
        using var document = JsonDocument.Parse(stored);
        document.RootElement.GetProperty("password").GetString()
            .Should().Be(SourcesTestHost.ConfigPassword,
                "an absent password member must keep the stored one");
    }

    [Fact]
    public async Task Sources_Patch_PasswordEmptyString_KeepsStoredPassword()
    {
        var host = await SourcesTestHost.CreateUserAsync(postgres);
        await using var factory = host.Factory;
        using var client = host.Client;

        var created = await SourcesTestHost.CreateSourceAsync(host, "empty pw keeps");
        var sourceId = SourcesTestHost.IdOf(created);

        using var response = await client.SendAsync(SourcesTestHost.RawRequest(
            HttpMethod.Patch, $"/v1/user/sources/{sourceId}", host.Bearer,
            $$$"""{"config":{"host":"imap.example.test","port":993,"use_ssl":true,"username":"m2t5-user","password":"","mailbox":"INBOX"}}""",
            host.Dek));
        response.StatusCode.Should().Be(HttpStatusCode.OK,
            "the JSON-Schema minLength must not reject the documented empty password");

        var row = await SourcesTestHost.SourceRowAsync(factory, sourceId);
        var stored = await SourcesTestHost.UnwrapConfigAsync(
            factory, host.UserId, host.Dek, row.ConfigBlobId);
        using var document = JsonDocument.Parse(stored);
        document.RootElement.GetProperty("password").GetString()
            .Should().Be(SourcesTestHost.ConfigPassword,
                "\"\" means keep — same rule as an absent member");
    }

    [Fact]
    public async Task Sources_Patch_PasswordNewValue_ReplacesStoredPassword()
    {
        var host = await SourcesTestHost.CreateUserAsync(postgres);
        await using var factory = host.Factory;
        using var client = host.Client;

        var created = await SourcesTestHost.CreateSourceAsync(host, "rotate pw");
        var sourceId = SourcesTestHost.IdOf(created);
        var oldBlobId = (await SourcesTestHost.SourceRowAsync(factory, sourceId)).ConfigBlobId;

        const string NewPassword = "r0tated-M2T5-Passw0rd";
        using var response = await client.SendAsync(SourcesTestHost.RawRequest(
            HttpMethod.Patch, $"/v1/user/sources/{sourceId}", host.Bearer,
            $$$"""{"config":{"host":"imap.example.test","port":993,"use_ssl":true,"username":"m2t5-user","password":"{{{NewPassword}}}","mailbox":"INBOX"}}""",
            host.Dek));
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        // The response still never echoes it…
        var raw = await response.Content.ReadAsStringAsync();
        raw.Should().NotContain("password");

        // …while the stored document carries the replacement.
        var row = await SourcesTestHost.SourceRowAsync(factory, sourceId);
        row.ConfigBlobId.Should().NotBe(oldBlobId,
            "PATCH must re-encrypt into a NEW blob row, not overwrite the old one");
        (await SourcesTestHost.FindBlobAsync(factory, oldBlobId)).Should().BeNull(
            "the superseded blob (holding the old password) must be deleted in the same transaction");
        var stored = await SourcesTestHost.UnwrapConfigAsync(
            factory, host.UserId, host.Dek, row.ConfigBlobId);
        using var document = JsonDocument.Parse(stored);
        document.RootElement.GetProperty("password").GetString().Should().Be(NewPassword);
    }

    [Fact]
    public async Task Sources_Patch_IntervalOutOfRange_Returns400_Validation()
    {
        var host = await SourcesTestHost.CreateUserAsync(postgres);
        await using var factory = host.Factory;
        using var client = host.Client;

        var sourceId = SourcesTestHost.IdOf(
            await SourcesTestHost.CreateSourceAsync(host, "interval bounds"));

        foreach (var interval in new[] { 0, 1441 })
        {
            using var response = await client.SendAsync(SourcesTestHost.Request(
                HttpMethod.Patch, $"/v1/user/sources/{sourceId}", host.Bearer,
                new { interval_minutes = interval }, host.Dek));
            var reason = await SourcesTestHost.ReadErrorReasonAsync(
                response, HttpStatusCode.BadRequest,
                "/errors/validation", "interval_minutes");
            reason.Should().Contain("1").And.Contain("1440");
        }
    }

    [Fact]
    public async Task Sources_Patch_UnknownId_Returns404()
    {
        var host = await SourcesTestHost.CreateUserAsync(postgres);
        await using var factory = host.Factory;
        using var client = host.Client;

        using var response = await client.SendAsync(SourcesTestHost.Request(
            HttpMethod.Patch, $"/v1/user/sources/{Guid.NewGuid()}", host.Bearer,
            new { display_name = "ghost" }, host.Dek));

        (await AccountTestHost.ReadProblemAsync(response, HttpStatusCode.NotFound))
            .GetProperty("type").GetString().Should().Be("/errors/source-not-found");
    }

    // -----------------------------------------------------------------------
    // DELETE — 204 happy path, 409 while rows exist (Q-O15 / FK RESTRICT)
    // -----------------------------------------------------------------------

    [Fact]
    public async Task Sources_Delete_OwnEmptySource_Returns204_RemovesRowAndBlob()
    {
        var host = await SourcesTestHost.CreateUserAsync(postgres);
        await using var factory = host.Factory;
        using var client = host.Client;

        var sourceId = SourcesTestHost.IdOf(
            await SourcesTestHost.CreateSourceAsync(host, "to be deleted"));
        var blobId = (await SourcesTestHost.SourceRowAsync(factory, sourceId)).ConfigBlobId;

        // No DEK header on DELETE: nothing is decrypted.
        using var response = await client.SendAsync(SourcesTestHost.Request(
            HttpMethod.Delete, $"/v1/user/sources/{sourceId}", host.Bearer));
        response.StatusCode.Should().Be(HttpStatusCode.NoContent);

        using var getAfter = await client.SendAsync(SourcesTestHost.Request(
            HttpMethod.Get, $"/v1/user/sources/{sourceId}", host.Bearer, dek: host.Dek));
        getAfter.StatusCode.Should().Be(HttpStatusCode.NotFound);

        (await SourcesTestHost.FindBlobAsync(factory, blobId)).Should().BeNull(
            "SQ-7: the config blob is cascade-deleted with its source row");

        var audits = await AccountTestHost.AuditRowsAsync(factory, host.UserId);
        audits.Should().ContainSingle(a => a.Action == AuditActions.DeleteSource);
    }

    /// <summary>
    /// Q-O15: rows are retained when a source is deleted — but the FK is
    /// RESTRICT in M2, so the API refuses with an explicit 409 (M6aT7
    /// relaxes this when the erasure cascade exists).
    /// </summary>
    [Fact]
    public async Task Sources_Delete_SourceWithProcessedMessages_Returns409_AndRetainsRows()
    {
        var host = await SourcesTestHost.CreateUserAsync(postgres);
        await using var factory = host.Factory;
        using var client = host.Client;

        var sourceId = SourcesTestHost.IdOf(
            await SourcesTestHost.CreateSourceAsync(host, "has messages"));
        await SourcesTestHost.InsertProcessedMessageAsync(factory, sourceId, "uid-1");

        using var response = await client.SendAsync(SourcesTestHost.Request(
            HttpMethod.Delete, $"/v1/user/sources/{sourceId}", host.Bearer));

        var problem = await AccountTestHost.ReadProblemAsync(
            response, HttpStatusCode.Conflict);
        problem.GetProperty("type").GetString().Should().Be("/errors/conflict");

        // Source and message both survive.
        var row = await SourcesTestHost.SourceRowAsync(factory, sourceId);
        row.Should().NotBeNull();
        (await SourcesTestHost.MessagesAsync(factory, sourceId)).Should().ContainSingle();

        var audits = await AccountTestHost.AuditRowsAsync(factory, host.UserId);
        audits.Should().NotContain(a => a.Action == AuditActions.DeleteSource,
            "the refused delete must not claim a success it did not have");
    }

    // -----------------------------------------------------------------------
    // Cross-cutting: auth gates, rate-limit headers, KDF endpoint
    // -----------------------------------------------------------------------

    [Fact]
    public async Task Sources_Anonymous_Get_Returns401_AndAdminRoleToken_Returns403()
    {
        var host = await SourcesTestHost.CreateUserAsync(postgres);
        await using var factory = host.Factory;
        using var client = host.Client;

        using var anonymous = await client.GetAsync("/v1/user/sources");
        var unauthorized = await AccountTestHost.ReadProblemAsync(
            anonymous, HttpStatusCode.Unauthorized);
        unauthorized.GetProperty("type").GetString().Should().Be("/errors/unauthorized");

        // A valid token of the wrong role passes authentication but not authorization.
        var adminBearer = await SourcesTestHost.AdminBearerAsync(factory);
        using var wrongRole = await client.SendAsync(SourcesTestHost.Request(
            HttpMethod.Get, "/v1/user/sources", adminBearer, dek: host.Dek));
        var forbidden = await AccountTestHost.ReadProblemAsync(
            wrongRole, HttpStatusCode.Forbidden);
        forbidden.GetProperty("type").GetString().Should().Be("/errors/forbidden");
    }

    /// <summary>M2T5's KDF addition: the SPA's only source of Argon2id material.</summary>
    [Fact]
    public async Task Kdf_Get_WithPassphraseMaterial_ReturnsSaltAndParams_WithoutWrappedKey()
    {
        var host = await SourcesTestHost.CreateUserAsync(postgres);
        await using var factory = host.Factory;
        using var client = host.Client;

        var salt = await SourcesTestHost.SetKdfMaterialAsync(factory, host.UserId);

        using var response = await client.SendAsync(SourcesTestHost.Request(
            HttpMethod.Get, "/v1/user/account/kdf", host.Bearer));
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var body = await AccountTestHost.ReadJsonAsync(response);
        body.GetProperty("kdf_salt").GetString()
            .Should().Be(Convert.ToBase64String(salt));
        body.GetProperty("kdf_params").ValueKind.Should().Be(JsonValueKind.Object);

        var raw = await response.Content.ReadAsStringAsync();
        raw.Should().NotContain("dek_wrapped")
            .And.NotContain("dek_nonce")
            .And.NotContain("passphrase",
                "the endpoint hands out derivation inputs only — never wrapped key material");
    }

    [Fact]
    public async Task Kdf_Get_WithoutPassphrase_Returns409_PassphraseNotSet()
    {
        var host = await SourcesTestHost.CreateUserAsync(postgres);
        await using var factory = host.Factory;
        using var client = host.Client;

        using var response = await client.SendAsync(SourcesTestHost.Request(
            HttpMethod.Get, "/v1/user/account/kdf", host.Bearer));

        (await AccountTestHost.ReadProblemAsync(response, HttpStatusCode.Conflict))
            .GetProperty("type").GetString().Should().Be("/errors/passphrase-not-set");
    }

    // -----------------------------------------------------------------------
    // Cross-cutting: POST /v1/user/account/unwrap-dek (M2T6, adjudication R2)
    // -----------------------------------------------------------------------

    /// <summary>
    /// R2 happy path: the passphrase travels once in the body, the server
    /// runs <c>IEncryptionService.UnwrapDek</c> for the signed-in caller and
    /// the 200 body is exactly the 32-byte DEK — never the passphrase or the
    /// wrapped material.
    /// </summary>
    [Fact]
    public async Task UnwrapDek_CorrectPassphrase_ReturnsPlaintextDek_WithoutEchoingPassphrase()
    {
        var host = await SourcesTestHost.CreateUserAsync(postgres);
        await using var factory = host.Factory;
        using var client = host.Client;

        var expectedDek = await SourcesTestHost.SetRealKdfMaterialAsync(
            factory, host.UserId, UnwrapPassphrase);

        using var response = await client.SendAsync(SourcesTestHost.Request(
            HttpMethod.Post, "/v1/user/account/unwrap-dek", host.Bearer,
            new { passphrase = UnwrapPassphrase }));

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        response.Headers.Contains("Cache-Control").Should().BeTrue(
            "the plaintext DEK answer declares its caching intent explicitly");
        response.Headers.CacheControl!.NoStore.Should().BeTrue(
            "the one body that carries the DEK must never be cacheable (M2T6 review fix)");
        response.Content.Headers.ContentType!.ToString()
            .Should().Contain("application/json");
        var body = await AccountTestHost.ReadJsonAsync(response);
        body.GetProperty("dek").GetString().Should().Be(
            expectedDek, "the endpoint hands back exactly the DEK the wrap was minted with");

        var raw = await response.Content.ReadAsStringAsync();
        raw.Should().NotContain(UnwrapPassphrase,
            "the passphrase never round-trips to the caller");
        raw.Should().NotContain("\"passphrase\"",
            "the 200 body is { dek } and nothing else");
        raw.Should().NotContain("dek_wrapped").And.NotContain("dek_nonce",
            "wrapped material stays server-side");
    }

    /// <summary>
    /// Failure-only audit (M2T6): a probe leaves evidence BEFORE the 401, and
    /// the row's metadata is empty — no passphrase, KEK or DEK (Q-E9).
    /// </summary>
    [Fact]
    public async Task UnwrapDek_WrongPassphrase_Returns401_CredentialMismatch_AndAuditsFailure()
    {
        var host = await SourcesTestHost.CreateUserAsync(postgres);
        await using var factory = host.Factory;
        using var client = host.Client;

        await SourcesTestHost.SetRealKdfMaterialAsync(factory, host.UserId, UnwrapPassphrase);

        using var response = await client.SendAsync(SourcesTestHost.Request(
            HttpMethod.Post, "/v1/user/account/unwrap-dek", host.Bearer,
            new { passphrase = "definitely-not-the-passphrase" }));

        var problem = await AccountTestHost.ReadProblemAsync(
            response, HttpStatusCode.Unauthorized);
        problem.GetProperty("type").GetString().Should().Be("/errors/credential-mismatch");
        problem.GetProperty("detail").GetString().Should().Be("The passphrase is not valid.");

        var audits = await AccountTestHost.AuditRowsAsync(factory, host.UserId);
        var failure = audits.Should().ContainSingle(
            a => a.Action == AuditActions.DekUnwrapFailed).Which;
        failure.TargetId.Should().Be(host.UserId.ToString());
        failure.TargetType.Should().Be("user");

        using var metadata = JsonDocument.Parse(failure.MetadataJson ?? "{}");
        metadata.RootElement.EnumerateObject().Should().BeEmpty(
            "the failure row mirrors login_failure: an empty metadata payload (Q-E9)");
        failure.MetadataJson.Should().NotContain(UnwrapPassphrase);
    }

    /// <summary>
    /// Handler order: the no-material sentinel fires BEFORE the body is read,
    /// so even an empty payload (a 400 in every other case) is a 409 pointing
    /// the SPA at the first-login passphrase flow.
    /// </summary>
    [Fact]
    public async Task UnwrapDek_NoKeyMaterial_Returns409_BeforeReadingBody()
    {
        var host = await SourcesTestHost.CreateUserAsync(postgres);
        await using var factory = host.Factory;
        using var client = host.Client;

        using var empty = await client.SendAsync(SourcesTestHost.RawRequest(
            HttpMethod.Post, "/v1/user/account/unwrap-dek", host.Bearer, string.Empty));
        (await AccountTestHost.ReadProblemAsync(empty, HttpStatusCode.Conflict))
            .GetProperty("type").GetString().Should().Be(
                "/errors/passphrase-not-set",
                "no wrapped DEK exists, whatever the payload said");

        using var withBody = await client.SendAsync(SourcesTestHost.Request(
            HttpMethod.Post, "/v1/user/account/unwrap-dek", host.Bearer,
            new { passphrase = UnwrapPassphrase }));
        (await AccountTestHost.ReadProblemAsync(withBody, HttpStatusCode.Conflict))
            .GetProperty("type").GetString().Should().Be("/errors/passphrase-not-set");
    }

    /// <summary>
    /// Missing, empty and whitespace-only passphrases are §13.12 validation
    /// errors naming <c>passphrase</c> (no length bound: any present value
    /// falls through to the uniform 401 below).
    /// </summary>
    [Fact]
    public async Task UnwrapDek_MissingOrEmptyPassphrase_Returns400_NamedPassphrase()
    {
        var host = await SourcesTestHost.CreateUserAsync(postgres);
        await using var factory = host.Factory;
        using var client = host.Client;

        // Material FIRST: without it the 409 sentinel precedes any body read.
        await SourcesTestHost.SetRealKdfMaterialAsync(factory, host.UserId, UnwrapPassphrase);

        using var missing = await client.SendAsync(SourcesTestHost.RawRequest(
            HttpMethod.Post, "/v1/user/account/unwrap-dek", host.Bearer, "{}"));
        (await SourcesTestHost.ReadErrorReasonAsync(
            missing, HttpStatusCode.BadRequest, "/errors/validation", "passphrase"))
            .Should().Contain("required");

        using var empty = await client.SendAsync(SourcesTestHost.RawRequest(
            HttpMethod.Post, "/v1/user/account/unwrap-dek", host.Bearer,
            """{"passphrase":""}"""));
        (await SourcesTestHost.ReadErrorReasonAsync(
            empty, HttpStatusCode.BadRequest, "/errors/validation", "passphrase"))
            .Should().Contain("required");

        using var whitespace = await client.SendAsync(SourcesTestHost.RawRequest(
            HttpMethod.Post, "/v1/user/account/unwrap-dek", host.Bearer,
            """{"passphrase":"    "}"""));
        (await SourcesTestHost.ReadErrorReasonAsync(
            whitespace, HttpStatusCode.BadRequest, "/errors/validation", "passphrase"))
            .Should().Contain("required");
    }

    [Fact]
    public async Task UnwrapDek_MalformedBody_Returns400_NotValidJson()
    {
        var host = await SourcesTestHost.CreateUserAsync(postgres);
        await using var factory = host.Factory;
        using var client = host.Client;

        await SourcesTestHost.SetRealKdfMaterialAsync(factory, host.UserId, UnwrapPassphrase);

        using var response = await client.SendAsync(SourcesTestHost.RawRequest(
            HttpMethod.Post, "/v1/user/account/unwrap-dek", host.Bearer, "{not json"));

        (await SourcesTestHost.ReadErrorReasonAsync(
            response, HttpStatusCode.BadRequest, "/errors/validation", "passphrase"))
            .Should().Be("The request body is not valid JSON.");
    }

    /// <summary>
    /// M2T6 review fix: the body read is bounded (4 KiB) before anything is
    /// buffered whole, so a multi-megabyte POST cannot park a string in
    /// memory while the per-user KEK mutex is held. Same problem type and
    /// field name as every other body rejection.
    /// </summary>
    [Fact]
    public async Task UnwrapDek_OversizedBody_Returns400_NamedPassphrase()
    {
        var host = await SourcesTestHost.CreateUserAsync(postgres);
        await using var factory = host.Factory;
        using var client = host.Client;

        await SourcesTestHost.SetRealKdfMaterialAsync(factory, host.UserId, UnwrapPassphrase);

        var oversized = new string('a', 64 * 1024);
        using var response = await client.SendAsync(SourcesTestHost.RawRequest(
            HttpMethod.Post, "/v1/user/account/unwrap-dek", host.Bearer,
            $"{{\"passphrase\":\"{oversized}\"}}"));

        (await SourcesTestHost.ReadErrorReasonAsync(
            response, HttpStatusCode.BadRequest, "/errors/validation", "passphrase"))
            .Should().Contain("too large");
    }

    /// <summary>
    /// Status gate precedes the sentinel: a Disabled account is 403 even with
    /// no key material (which would otherwise answer 409).
    /// </summary>
    [Fact]
    public async Task UnwrapDek_DisabledAccount_Returns403_BeforeSentinel()
    {
        var host = await SourcesTestHost.CreateUserAsync(postgres);
        await using var factory = host.Factory;
        using var client = host.Client;

        var disabledId = await AdminTestHost.SeedUserAsync(
            factory, SourcesTestHost.UniqueEmail("disabled"), UserRole.User, UserStatus.Disabled);
        var disabledBearer = await AdminTestHost.BearerAsync(factory, disabledId, "User");

        using var response = await client.SendAsync(SourcesTestHost.Request(
            HttpMethod.Post, "/v1/user/account/unwrap-dek", disabledBearer,
            new { passphrase = UnwrapPassphrase }));

        (await AccountTestHost.ReadProblemAsync(response, HttpStatusCode.Forbidden))
            .GetProperty("type").GetString().Should().Be("/errors/forbidden");
    }

    [Fact]
    public async Task UnwrapDek_Anonymous_Returns401_RFC7807()
    {
        var host = await SourcesTestHost.CreateUserAsync(postgres);
        await using var factory = host.Factory;
        using var client = host.Client;

        using var response = await client.PostAsync(
            "/v1/user/account/unwrap-dek",
            new StringContent(
                $"{{\"passphrase\":\"{UnwrapPassphrase}\"}}", Encoding.UTF8, "application/json"));

        (await AccountTestHost.ReadProblemAsync(response, HttpStatusCode.Unauthorized))
            .GetProperty("type").GetString().Should().Be("/errors/unauthorized");
    }

    /// <summary>
    /// Q-E5's zero-wait per-user KEK mutex: losers map to 429 with
    /// <c>Retry-After: 1</c>, the winner still returns the DEK. The critical
    /// section is widened to 256 MiB of Argon2id (the same cost trick as
    /// <c>Argon2idAesGcmEncryptionServiceTests</c>' barrier test) so every
    /// concurrent caller arrives while the winner is still deriving.
    /// </summary>
    [Fact]
    public async Task UnwrapDek_ConcurrentDerivation_Returns429_WithRetryAfter()
    {
        var host = await SourcesTestHost.CreateUserAsync(postgres);
        await using var factory = host.Factory;
        using var client = host.Client;

        var parameters = KdfParams.Default() with { MemoryKb = 262144 };
        await SourcesTestHost.SetRealKdfMaterialAsync(
            factory, host.UserId, UnwrapPassphrase, parameters);

        var responses = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ =>
            client.SendAsync(SourcesTestHost.Request(
                HttpMethod.Post, "/v1/user/account/unwrap-dek", host.Bearer,
                new { passphrase = UnwrapPassphrase }))));

        try
        {
            responses.Count(r => r.StatusCode == HttpStatusCode.OK)
                .Should().BeGreaterOrEqualTo(1, "the mutex winner always unwraps");

            var busy = responses
                .Where(r => r.StatusCode == HttpStatusCode.TooManyRequests)
                .ToList();
            busy.Should().NotBeEmpty(
                "callers arriving mid-derivation observe the typed busy exception (Q-E5)");

            foreach (var response in busy)
            {
                response.Headers.RetryAfter?.ToString().Should().Be("1");
                var problem = await AccountTestHost.ReadProblemAsync(
                    response, HttpStatusCode.TooManyRequests);
                problem.GetProperty("type").GetString().Should().Be("/errors/rate-limited");
            }
        }
        finally
        {
            foreach (var response in responses)
            {
                response.Dispose();
            }
        }
    }

    // -----------------------------------------------------------------------
    // Helpers
    // -----------------------------------------------------------------------

    private static async Task<string> GetSourceRawAsync(
        HttpClient client, string bearer, string dek, Guid sourceId)
    {
        using var response = await client.SendAsync(SourcesTestHost.Request(
            HttpMethod.Get, $"/v1/user/sources/{sourceId}", bearer, dek: dek));
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        return await response.Content.ReadAsStringAsync();
    }

    private static async Task<List<Guid>> IdsAsync(HttpResponseMessage response)
    {
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var page = await AccountTestHost.ReadJsonAsync(response);
        return
        [
            .. page.GetProperty("items").EnumerateArray()
                .Select(item => item.GetProperty("id").GetGuid())
        ];
    }
}
