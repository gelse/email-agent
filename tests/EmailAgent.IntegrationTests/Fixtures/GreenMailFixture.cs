using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Containers;
using MailKit;
using MailKit.Net.Smtp;
using MailKit.Security;
using MimeKit;
using Xunit;

namespace EmailAgent.IntegrationTests.Fixtures;

/// <summary>
/// A Greenmail 2.0.1 container (IMAP + SMTP + REST) started before and torn down
/// after the test class that receives it — the IMAP counterpart of
/// <see cref="PostgresFixture"/>; M2T4 owns it and M2T7's E2E wiring reuses it.
/// </summary>
/// <remarks>
/// <para>
/// <b>Recorded substitutions vs the task file:</b> the official
/// <c>Testcontainers.GreenMail</c> NuGet package does not exist (404), so the core
/// <see cref="ContainerBuilder"/> drives the plain <c>greenmail/standalone</c> image;
/// the planned <c>greenmail/standalone:2.0.1-jdk21</c> tag does not exist on Docker
/// Hub, so <c>greenmail/standalone:2.0.1</c> (same GreenMail 2.0.1, JDK 11) is used;
/// and the fixture implements <c>IAsyncLifetime</c> — this repo's fixture pattern —
/// instead of the DoD's <c>IDisposable</c>.
/// </para>
/// <para>
/// <b>Authentication is genuinely ON.</b> The image's default <c>GREENMAIL_OPTS</c>
/// carries <c>-Dgreenmail.auth.disabled</c>, and that flag is presence-only: any
/// value (including <c>false</c>) switches authentication off, so the fixture
/// overrides the whole variable with the default MINUS that flag. Result: an unknown
/// login or a wrong password is rejected by the IMAP server, which is exactly what
/// <c>Imap_ValidateConfigAsync_WrongPassword_ReturnsValidationFailure</c> asserts.
/// </para>
/// <para>
/// <b>UIDs keep climbing across purges</b> — Greenmail does not reset UIDVALIDITY or
/// UIDNEXT when the mailbox is emptied — so the tests purge at their start and assert
/// relative/monotonic properties (counts, ordering, checkpoint == max) instead of
/// hard-coded UIDs.
/// </para>
/// </remarks>
public sealed class GreenMailFixture : IAsyncLifetime
{
    /// <summary>User provisioned through Greenmail's REST API (the image has no default users).</summary>
    public const string Username = "alice";

    /// <summary>Greenmail's credentials are test-only and never leave the container.</summary>
    public const string Password = "alice";

    /// <summary>The mailbox address SMTP delivery must target exactly.</summary>
    public const string Email = "alice@greenmail.test";

    /// <summary>Ports fixed by the greenmail/standalone image (inside the container).</summary>
    private const int ImapInternalPort = 3143;

    private const int ImapsInternalPort = 3993;
    private const int SmtpInternalPort = 3025;
    private const int RestInternalPort = 8080;

    /// <summary>
    /// The image's default <c>GREENMAIL_OPTS</c> minus <c>-Dgreenmail.auth.disabled</c>
    /// (setup.test.all provisions IMAP/SMTP/POP3/REST; hostname and the TLS keystore
    /// match the shipped defaults).
    /// </summary>
    private const string GreenmailOpts =
        "-Dgreenmail.setup.test.all -Dgreenmail.hostname=0.0.0.0 "
        + "-Dgreenmail.tls.keystore.file=/home/greenmail/greenmail.p12 "
        + "-Dgreenmail.tls.keystore.password=changeit";

    private readonly IContainer _container = new ContainerBuilder("greenmail/standalone:2.0.1")
        .WithEnvironment("GREENMAIL_OPTS", GreenmailOpts)
        .WithPortBinding(ImapInternalPort, true)
        .WithPortBinding(ImapsInternalPort, true)
        .WithPortBinding(SmtpInternalPort, true)
        .WithPortBinding(RestInternalPort, true)
        .WithWaitStrategy(Wait.ForUnixContainer()
            .UntilInternalTcpPortIsAvailable(ImapInternalPort)
            .UntilInternalTcpPortIsAvailable(ImapsInternalPort)
            .UntilInternalTcpPortIsAvailable(RestInternalPort))
        .Build();

    private HttpClient? _rest;

    /// <summary>Host the test process reaches Greenmail on (the Docker gateway from inside a container).</summary>
    public string ImapHost => _container.Hostname;

    /// <summary>M4T2: SMTP host (same container as IMAP — Q-C7).</summary>
    public string SmtpHost => _container.Hostname;

    /// <summary>Published host port of Greenmail's plaintext IMAP endpoint (3143 inside).</summary>
    public ushort ImapPort => _container.GetMappedPublicPort(ImapInternalPort);

    /// <summary>Published host port of Greenmail's implicit-TLS IMAP endpoint (3993 inside).</summary>
    public ushort ImapsPort => _container.GetMappedPublicPort(ImapsInternalPort);

    /// <summary>Published host port Greenmail's SMTP endpoint (3025 inside) — used to seed messages.</summary>
    public ushort SmtpPort => _container.GetMappedPublicPort(SmtpInternalPort);

    /// <summary>Published host port of Greenmail's REST API (8080 inside) — readiness, user provisioning, purge.</summary>
    public ushort RestPort => _container.GetMappedPublicPort(RestInternalPort);

    /// <inheritdoc />
    public async Task InitializeAsync()
    {
        await _container.StartAsync();

        _rest = new HttpClient
        {
            BaseAddress = new Uri($"http://{ImapHost}:{RestPort}"),
            Timeout = TimeSpan.FromSeconds(10),
        };

        await WaitForRestApiAsync();
        await CreateUserAsync();
    }

    /// <inheritdoc />
    public Task DisposeAsync()
    {
        _rest?.Dispose();
        return _container.DisposeAsync().AsTask();
    }

    /// <summary>
    /// Empties every mailbox so the next test seeds from a clean slate (the UID
    /// high-water mark is intentionally NOT reset — see the class remarks).
    /// </summary>
    public async Task PurgeMailAsync()
    {
        using var response = await RestClient.PostAsync(
            "/api/mail/purge", new StringContent(string.Empty));
        response.EnsureSuccessStatusCode();
    }

    /// <summary>
    /// M4T2/T5: counts messages delivered to <paramref name="userEmail"/> via
    /// Greenmail's REST API (pinned to the 2.0.1 image).
    /// </summary>
    public async Task<int> CountMessagesAsync(string userEmail)
    {
        var messages = await GetMessagesAsync(userEmail).ConfigureAwait(false);
        return messages.Count;
    }

    /// <summary>
    /// M4T5: lists messages for <paramref name="userEmail"/> (raw JSON array)
    /// via <c>GET /api/user/{email}/messages</c>.
    /// </summary>
    public async Task<IReadOnlyList<JsonElement>> GetMessagesAsync(string userEmail)
    {
        using var response = await RestClient.GetAsync(
            $"/api/user/{Uri.EscapeDataString(userEmail)}/messages").ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            // Fallback: some endpoints key by login rather than full email.
            var login = userEmail.Contains('@', StringComparison.Ordinal)
                ? userEmail[..userEmail.IndexOf('@', StringComparison.Ordinal)]
                : userEmail;
            using var retry = await RestClient.GetAsync(
                $"/api/user/{Uri.EscapeDataString(login)}/messages").ConfigureAwait(false);
            retry.EnsureSuccessStatusCode();
            var retryJson = await retry.Content.ReadAsStringAsync().ConfigureAwait(false);
            using var retryDoc = JsonDocument.Parse(retryJson);
            return retryDoc.RootElement.ValueKind == JsonValueKind.Array
                ? retryDoc.RootElement.EnumerateArray().Select(e => e.Clone()).ToList()
                : [];
        }

        var json = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
        using var doc = JsonDocument.Parse(json);
        return doc.RootElement.ValueKind == JsonValueKind.Array
            ? doc.RootElement.EnumerateArray().Select(e => e.Clone()).ToList()
            : [];
    }

    /// <summary>
    /// M4T5: fetches the raw MIME content for a message id returned by the REST
    /// API (endpoint shape varies by Greenmail version; best-effort).
    /// </summary>
    public async Task<string> GetRawMessageAsync(string messageId)
    {
        // Greenmail 2.x: GET /api/mail/{id}
        using var response = await RestClient.GetAsync(
            $"/api/mail/{Uri.EscapeDataString(messageId)}").ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadAsStringAsync().ConfigureAwait(false);
    }

    /// <summary>
    /// Seeds one message into <see cref="Email"/> through Greenmail's SMTP endpoint —
    /// no production endpoint is involved (AGENTS.md hard constraint).
    /// </summary>
    /// <param name="subject">The <c>Subject</c> header the listing tests assert on.</param>
    /// <param name="body">The plain-text body.</param>
    /// <param name="attachmentFileName">When set, a second body part carrying this file name is attached.</param>
    /// <param name="attachmentContent">The attachment payload — kept out of the IMAP body fetch on purpose (Q-C5).</param>
    /// <param name="transferEncoding">
    /// Forces the text part's Content-Transfer-Encoding (e.g. quoted-printable or
    /// base64) so the connector's raw-literal decode path is exercised end to end —
    /// <c>BODY.PEEK[n]</c> returns still-encoded octets (review finding F1).
    /// </param>
    public async Task SendAsync(
        string subject,
        string body,
        string? attachmentFileName = null,
        string? attachmentContent = null,
        ContentEncoding? transferEncoding = null)
    {
        var message = new MimeMessage
        {
            Subject = subject,
            Date = DateTimeOffset.UtcNow,
        };
        message.From.Add(MailboxAddress.Parse("sender@corp.test"));

        // Greenmail delivers to the user whose address matches the envelope exactly.
        message.To.Add(MailboxAddress.Parse(Email));

        if (attachmentFileName is null)
        {
            var text = new TextPart("plain") { Text = body };

            if (transferEncoding is not null)
            {
                text.ContentTransferEncoding = transferEncoding.Value;
            }

            message.Body = text;
        }
        else
        {
            var builder = new BodyBuilder { TextBody = body };
            builder.Attachments.Add(
                attachmentFileName,
                Encoding.UTF8.GetBytes(attachmentContent ?? string.Empty),
                new ContentType("text", "plain"));
            message.Body = builder.ToMessageBody();
        }

        using var smtp = new SmtpClient();
        await smtp.ConnectAsync(ImapHost, SmtpPort, SecureSocketOptions.None);
        await smtp.SendAsync(message);
        await smtp.DisconnectAsync(true);
    }

    /// <summary>
    /// The <c>ConfigSchema</c> document under test: Greenmail's host/ports with the
    /// fixture's credentials, plaintext by default (Greenmail's 3143 advertises no
    /// STARTTLS, so the session really is plaintext) or implicit TLS on 3993.
    /// Every override exists so a test can break exactly one field — password, host,
    /// port or mailbox — without rebuilding the JSON.
    /// </summary>
    public JsonElement CreateConfig(
        bool useSsl = false,
        string mailbox = "INBOX",
        string? password = null,
        string? host = null,
        int? port = null)
    {
        // Serialized, not string-interpolated: a mailbox/password/host containing a
        // quote or backslash must not be able to break (or steer) the JSON (review N3).
        var json = JsonSerializer.Serialize(new
        {
            host = host ?? ImapHost,
            port = port ?? (useSsl ? ImapsPort : ImapPort),
            use_ssl = useSsl,
            username = Username,
            password = password ?? Password,
            mailbox,
        });

        using var doc = JsonDocument.Parse(json);
        return doc.RootElement.Clone();
    }

    /// <summary>Greenmail's REST client; throws when the fixture has not been initialized.</summary>
    private HttpClient RestClient => _rest
        ?? throw new InvalidOperationException(
            "GreenMailFixture was not initialized — InitializeAsync must run first.");

    /// <summary>
    /// Greenmail answers <c>/api/service/readiness</c> with 200 only once its services
    /// are up; the container wait strategy covers the ports, this covers the API.
    /// </summary>
    private async Task WaitForRestApiAsync()
    {
        for (var attempt = 1; attempt <= 60; attempt++)
        {
            try
            {
                using var response = await RestClient.GetAsync("/api/service/readiness");
                if (response.IsSuccessStatusCode)
                {
                    return;
                }
            }
            catch (HttpRequestException)
            {
                // Not listening yet — retry below.
            }
            catch (TaskCanceledException)
            {
                // Per-request timeout while the JVM is still warming up — retry below.
            }

            await Task.Delay(TimeSpan.FromSeconds(1));
        }

        throw new InvalidOperationException(
            $"Greenmail's REST API at {RestClient.BaseAddress} was not ready after 60 seconds.");
    }

    /// <summary>Provisions the single test user the IMAP tests authenticate as.</summary>
    private async Task CreateUserAsync()
    {
        using var content = JsonContent.Create(new
        {
            login = Username,
            password = Password,
            email = Email,
        });

        using var response = await RestClient.PostAsync("/api/user", content);
        response.EnsureSuccessStatusCode();
    }
}
