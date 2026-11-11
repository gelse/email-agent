using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using EmailAgent.Core.Security;
using EmailAgent.Infrastructure;
using EmailAgent.Infrastructure.Entities;
using FluentAssertions;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace EmailAgent.IntegrationTests.Endpoints;

// ---------------------------------------------------------------------------
// M2T5 shared harness for the /v1/user/sources suites, migrated by M5T1 to
// the session-key envelope: one user (and one DEK) per test, a request builder
// that carries the X-Crypto-Session header minted by POST /v1/account/unlock
// (the DEK never rides the wire again), and small DbContext readers for the
// rows the endpoint contracts pin (message_sources, encrypted_blobs,
// processed_messages). Users are seeded directly and tokens minted by the
// real issuer with an explicit "User" role claim (AdminTestHost.BearerAsync)
// — no login path, so no login rate-limit budget is spent (rate limiting is
// 60/min PER USER and each test gets its own).
// ---------------------------------------------------------------------------

/// <summary>One test caller: factory-hosted API, client, identity, DEK and crypto-session token.</summary>
internal sealed record SourcesHost(
    WebApplicationFactory<Program> Factory,
    HttpClient Client,
    Guid UserId,
    string Bearer,
    string Dek,
    string Session);

internal static class SourcesTestHost
{
    /// <summary>The session-key envelope header every /v1/user/* route is judged on (server: HttpCryptoSessionAccessor.HeaderName).</summary>
    public const string SessionHeaderName = "X-Crypto-Session";

    public const string ImapTypeId = "imap";

    /// <summary>Config password used where no live server is involved (create/PATCH).</summary>
    public const string ConfigPassword = "m2t5-Conf1g-Passw0rd";

    public static string UniqueEmail(string prefix = "m2t5")
        => $"{prefix}-{Guid.NewGuid():N}@example.test";

    /// <summary>A fresh 32-byte DEK in the base64 form POST /v1/account/unlock expects.</summary>
    public static string NewDek() => Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));

    /// <summary>Migrated shared Postgres, factory, client, one fresh user + DEK + unlocked session.</summary>
    public static async Task<SourcesHost> CreateUserAsync(PostgresFixture postgres)
    {
        var connectionString = await AccountTestHost.MigrateAsync(postgres);
        var factory = AccountTestHost.CreateFactory(connectionString);
        var client = AccountTestHost.CreateClient(factory);
        var (userId, bearer, dek, session) = await AddUserAsync(factory);
        return new SourcesHost(factory, client, userId, bearer, dek, session);
    }

    /// <summary>A second caller on the SAME factory (cross-user scenarios), unlocked with its OWN DEK.</summary>
    public static async Task<(Guid UserId, string Bearer, string Dek, string Session)> AddUserAsync(
        WebApplicationFactory<Program> factory)
    {
        var userId = await AdminTestHost.SeedUserAsync(factory, UniqueEmail());
        var bearer = await AdminTestHost.BearerAsync(factory, userId, "User");
        var dek = NewDek();
        var session = await UnlockAsync(AccountTestHost.CreateClient(factory), bearer, dek);
        return (userId, bearer, dek, session);
    }

    /// <summary>An admin-role token for the 403 proof (unlocked like any caller).</summary>
    public static async Task<(string Bearer, string Session)> AdminAsync(
        WebApplicationFactory<Program> factory)
    {
        var adminId = await AdminTestHost.SeedUserAsync(
            factory, UniqueEmail("admin"), EmailAgent.Infrastructure.Enums.UserRole.Admin);
        var bearer = await AdminTestHost.BearerAsync(factory, adminId, "Admin");
        var session = await UnlockAsync(AccountTestHost.CreateClient(factory), bearer, NewDek());
        return (bearer, session);
    }

    /// <summary>An admin-role token for tests that do not need a session.</summary>
    public static async Task<string> AdminBearerAsync(WebApplicationFactory<Program> factory)
    {
        var adminId = await AdminTestHost.SeedUserAsync(
            factory, UniqueEmail("admin"), EmailAgent.Infrastructure.Enums.UserRole.Admin);
        return await AdminTestHost.BearerAsync(factory, adminId, "Admin");
    }

    /// <summary>
    /// POST /v1/account/unlock with a 32-byte DEK and return the opaque
    /// crypto-session token (M5T1 SQ-M5-1). The server wraps whatever DEK the
    /// test supplies under a fresh session key — no passphrase leg, no
    /// production service (AGENTS.md hard constraint 3).
    /// </summary>
    public static async Task<string> UnlockAsync(
        HttpClient client, string bearer, string dek)
    {
        using var request = Request(
            HttpMethod.Post, "/v1/account/unlock", bearer, new { dek });
        using var response = await client.SendAsync(request);
        response.StatusCode.Should().Be(System.Net.HttpStatusCode.OK,
            "unlock must mint a session: " + await response.Content.ReadAsStringAsync());
        var body = await AccountTestHost.ReadJsonAsync(response);
        return body.GetProperty("crypto_session_token").GetString()!;
    }

    /// <summary>Bearer + optional X-Crypto-Session + optional JSON body (serialized verbatim).</summary>
    public static HttpRequestMessage Request(
        HttpMethod method,
        string path,
        string bearer,
        object? body = null,
        string? session = null)
    {
        var request = new HttpRequestMessage(method, path)
        {
            Headers = { Authorization = new AuthenticationHeaderValue("Bearer", bearer) },
        };

        if (session is not null)
        {
            request.Headers.Add(SessionHeaderName, session);
        }

        if (body is not null)
        {
            request.Content = new StringContent(
                JsonSerializer.Serialize(body), Encoding.UTF8, "application/json");
        }

        return request;
    }

    /// <summary>A request whose body is caller-authored JSON (or raw text).</summary>
    public static HttpRequestMessage RawRequest(
        HttpMethod method,
        string path,
        string bearer,
        string json,
        string? session = null,
        string contentType = "application/json")
    {
        var request = Request(method, path, bearer, session: session);
        request.Content = new StringContent(json, Encoding.UTF8, contentType);
        return request;
    }

    /// <summary>
    /// A schema-valid IMAP config document. Defaults are inert (no socket is
    /// opened by create/PATCH); <see cref="GreenMailFixture"/>-backed tests
    /// override host/port/credentials with the fixture's own values.
    /// </summary>
    public static JsonElement Config(
        string host = "imap.example.test",
        int port = 993,
        bool useSsl = true,
        string username = "m2t5-user",
        string password = ConfigPassword,
        string mailbox = "INBOX")
    {
        var json = JsonSerializer.Serialize(new
        {
            host,
            port,
            use_ssl = useSsl,
            username,
            password,
            mailbox,
        });

        using var document = JsonDocument.Parse(json);
        return document.RootElement.Clone();
    }

    /// <summary>POST /v1/user/sources asserting 201; returns the created body.</summary>
    public static async Task<JsonElement> CreateSourceAsync(
        SourcesHost host,
        string displayName,
        JsonElement? config = null,
        string typeId = ImapTypeId,
        int? intervalMinutes = null)
    {
        var body = new Dictionary<string, object?>
        {
            ["type_id"] = typeId,
            ["display_name"] = displayName,
            ["config"] = config ?? Config(),
        };

        if (intervalMinutes is not null)
        {
            body["interval_minutes"] = intervalMinutes;
        }

        using var response = await host.Client.SendAsync(
            Request(HttpMethod.Post, "/v1/user/sources", host.Bearer, body, host.Session));
        response.StatusCode.Should().Be(System.Net.HttpStatusCode.Created,
            $"create must succeed for '{displayName}': "
            + await response.Content.ReadAsStringAsync());
        return await AccountTestHost.ReadJsonAsync(response);
    }

    /// <summary>The source's id as a Guid (typed response member).</summary>
    public static Guid IdOf(JsonElement source) => source.GetProperty("id").GetGuid();

    // -----------------------------------------------------------------------
    // Database readers/writers — direct rows, never a production service.
    // -----------------------------------------------------------------------

    public static async Task<MessageSource> SourceRowAsync(
        WebApplicationFactory<Program> factory, Guid sourceId)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<EmailAgentDbContext>();
        return await db.MessageSources.AsNoTracking()
            .SingleAsync(s => s.Id == sourceId);
    }

    public static async Task<EncryptedBlob> BlobAsync(
        WebApplicationFactory<Program> factory, Guid blobId)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<EmailAgentDbContext>();
        return await db.EncryptedBlobs.AsNoTracking()
            .SingleAsync(b => b.Id == blobId);
    }

    /// <summary>The same lookup without asserting existence (delete scenarios).</summary>
    public static async Task<EncryptedBlob?> FindBlobAsync(
        WebApplicationFactory<Program> factory, Guid blobId)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<EmailAgentDbContext>();
        return await db.EncryptedBlobs.AsNoTracking()
            .FirstOrDefaultAsync(b => b.Id == blobId);
    }

    public static async Task<List<ProcessedMessage>> MessagesAsync(
        WebApplicationFactory<Program> factory, Guid sourceId)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<EmailAgentDbContext>();
        return await db.ProcessedMessages.AsNoTracking()
            .Where(m => m.SourceId == sourceId)
            .OrderBy(m => m.RemoteUid)
            .ToListAsync();
    }

    public static async Task<int> ModelCatalogCountAsync(WebApplicationFactory<Program> factory)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<EmailAgentDbContext>();
        return await db.ModelCatalog.AsNoTracking().CountAsync();
    }

    /// <summary>
    /// Inserts a <c>processed_messages</c> row with raw SQL (only the NOT NULL
    /// columns; the rest take their §6 defaults) — the delete-with-rows
    /// scenario without dragging a live mailbox into it.
    /// </summary>
    public static async Task InsertProcessedMessageAsync(
        WebApplicationFactory<Program> factory, Guid sourceId, string remoteUid)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<EmailAgentDbContext>();
        await db.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO processed_messages (id, source_id, remote_uid)
            VALUES ({Guid.NewGuid()}, {sourceId}, {remoteUid})
            """);
    }

    /// <summary>
    /// Decrypts a stored config blob with the caller's DEK through the real
    /// <see cref="IEncryptionService"/> — how the tests observe the
    /// password-keep/replace semantics the wire contract never shows.
    /// </summary>
    public static async Task<string> UnwrapConfigAsync(
        WebApplicationFactory<Program> factory, Guid userId, string dek, Guid blobId)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<EmailAgentDbContext>();
        var encryption = scope.ServiceProvider.GetRequiredService<IEncryptionService>();
        var blob = await db.EncryptedBlobs.AsNoTracking().SingleAsync(b => b.Id == blobId);

        var plaintext = encryption.UnwrapWithUserDek(
            userId, Convert.FromBase64String(dek), blob.Ciphertext, blob.Nonce);
        return Encoding.UTF8.GetString(plaintext);
    }

    /// <summary>Plants Argon2id salt + params so GET /v1/user/account/kdf (the M2 route, session-gated since M5T1) has material.</summary>
    public static async Task<byte[]> SetKdfMaterialAsync(
        WebApplicationFactory<Program> factory, Guid userId)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<EmailAgentDbContext>();
        var user = await db.Users.SingleAsync(u => u.Id == userId);

        var salt = RandomNumberGenerator.GetBytes(16);
        user.KdfSalt = salt;
        user.KdfParams = JsonSerializer.SerializeToDocument(
            EmailAgent.Core.Security.KdfParams.Default());
        user.DekWrapped = new byte[48];
        user.DekNonce = new byte[12];
        await db.SaveChangesAsync();
        return salt;
    }

    /// <summary>
    /// M2T6: plants REAL passphrase material for the legacy M2 unwrap flow —
    /// Argon2id salt + params plus the wrapped DEK produced by the factory's
    /// own <see cref="IEncryptionService"/> — and returns the plaintext DEK
    /// (base64) the endpoint must hand back. Kept for the M2 unwrap-dek
    /// contract that survives M5T1 (the route persists; it is simply
    /// session-gated now). <see cref="SetKdfMaterialAsync"/> only plants dummy
    /// bytes for <c>GET /kdf</c>; these four columns are cryptographically
    /// consistent, so the unwrap below actually authenticates. The optional
    /// <paramref name="parameters"/> widen the Argon2id cost for tests that
    /// need to overlap derivations (the Q-E5 busy path).
    /// </summary>
    public static async Task<string> SetRealKdfMaterialAsync(
        WebApplicationFactory<Program> factory,
        Guid userId,
        string passphrase,
        KdfParams? parameters = null)
    {
        parameters ??= KdfParams.Default();

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<EmailAgentDbContext>();
        var encryption = scope.ServiceProvider.GetRequiredService<IEncryptionService>();
        var user = await db.Users.SingleAsync(u => u.Id == userId);

        var salt = RandomNumberGenerator.GetBytes(16);
        var wrapped = encryption.GenerateAndWrapDek(userId, passphrase, salt, parameters);

        user.KdfSalt = salt;
        user.KdfParams = JsonSerializer.SerializeToDocument(parameters);
        user.DekWrapped = wrapped.Wrapped;
        user.DekNonce = wrapped.Nonce;
        await db.SaveChangesAsync();

        return Convert.ToBase64String(wrapped.Dek);
    }

    // -----------------------------------------------------------------------
    // Problem-document assertions
    // -----------------------------------------------------------------------

    /// <summary>Reads the problem document and returns the reason of one errors[] entry.</summary>
    public static async Task<string> ReadErrorReasonAsync(
        HttpResponseMessage response,
        System.Net.HttpStatusCode expectedStatus,
        string expectedType,
        string errorName)
    {
        var problem = await AccountTestHost.ReadProblemAsync(response, expectedStatus);
        problem.GetProperty("type").GetString().Should().Be(expectedType);

        var errors = problem.GetProperty("errors");
        errors.ValueKind.Should().Be(JsonValueKind.Array, "§13.12 puts field failures in errors[]");

        foreach (var entry in errors.EnumerateArray())
        {
            if (entry.GetProperty("name").GetString() == errorName)
            {
                return entry.GetProperty("reason").GetString()!;
            }
        }

        throw new Xunit.Sdk.XunitException(
            $"no errors[] entry named '{errorName}' in: {problem.GetRawText()}");
    }
}
