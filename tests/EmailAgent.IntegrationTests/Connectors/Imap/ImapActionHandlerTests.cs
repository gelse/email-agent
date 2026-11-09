using System.Text.Json;
using EmailAgent.Connectors.Imap;
using EmailAgent.Contracts.ActionHandlers;
using EmailAgent.IntegrationTests.Fixtures;
using FluentAssertions;
using MailKit.Net.Smtp;
using MimeKit;
using Xunit;

namespace EmailAgent.IntegrationTests.Connectors.Imap;

/// <summary>M4T2: Greenmail SMTP integration for imap-reply/imap-forward.</summary>
public sealed class ImapActionHandlerTests : IClassFixture<GreenMailFixture>, IAsyncLifetime
{
    private readonly GreenMailFixture _gm;

    public ImapActionHandlerTests(GreenMailFixture gm) => _gm = gm;

    public async Task InitializeAsync() => await _gm.PurgeMailAsync();
    public Task DisposeAsync() => Task.CompletedTask;

    private JsonElement SmtpConfig(string? from = null) => JsonSerializer.SerializeToElement(new
    {
        host = _gm.SmtpHost,
        port = (int)_gm.SmtpPort,
        use_ssl = false,
        username = GreenMailFixture.Username,
        password = GreenMailFixture.Password,
        from_address = from ?? "sender@test.local",
        from_name = "Sender",
    });

    private static SourceMessageSnapshot Snapshot(string? messageId = "<orig@test>", string? from = null) =>
        new("1", messageId, "Hello", from ?? GreenMailFixture.Email, null, "to@test", null, "original body");

    [Fact]
    public async Task Reply_Execute_LandsInMailbox_WithThreadingHeaders()
    {
        var handler = new ImapReplyActionHandler();
        var args = JsonSerializer.SerializeToElement(new { body = "reply text" });
        var ctx = new ActionContext(Guid.NewGuid(), Guid.NewGuid(), CancellationToken.None) { SourceMessage = Snapshot() };

        var result = await handler.ExecuteAsync(SmtpConfig(), args, ctx, CancellationToken.None);

        result.Ok.Should().BeTrue();
        result.SideEffects.Should().ContainSingle(e => e.Kind == SideEffectKinds.SentEmail);
        (await _gm.CountMessagesAsync(GreenMailFixture.Email)).Should().Be(1);

        var messages = await _gm.GetMessagesAsync(GreenMailFixture.Email);
        messages.Should().NotBeEmpty();
        // Raw MIME: Greenmail REST returns message metadata; fetch by id when available.
        if (messages[0].TryGetProperty("id", out var idProp))
        {
            var raw = await _gm.GetRawMessageAsync(idProp.ToString()!);
            var mime = MimeMessage.Load(new MemoryStream(System.Text.Encoding.UTF8.GetBytes(raw)));
            mime.Subject.Should().Be("Re: Hello");
            mime.InReplyTo.Should().Be("<orig@test>");
        }
    }

    [Fact]
    public async Task Reply_DryRun_NoSmtpConnection_MailboxEmpty()
    {
        var factory = new CountingSmtpClientFactory();
        var handler = new ImapReplyActionHandler(factory);
        var args = JsonSerializer.SerializeToElement(new { body = "reply text" });
        var ctx = new ActionContext(Guid.NewGuid(), Guid.NewGuid(), CancellationToken.None)
        { DryRun = true, SourceMessage = Snapshot() };

        var result = await handler.ExecuteAsync(SmtpConfig(), args, ctx, CancellationToken.None);

        result.Ok.Should().BeTrue();
        result.SideEffects.Should().ContainSingle(e => e.Kind == SideEffectKinds.WouldSendEmail);
        factory.CreateCount.Should().Be(0);
        (await _gm.CountMessagesAsync(GreenMailFixture.Email)).Should().Be(0);
    }

    [Fact]
    public async Task Reply_DryRun_DescriptionContainsIntent_NoPassword()
    {
        var handler = new ImapReplyActionHandler(new CountingSmtpClientFactory());
        var args = JsonSerializer.SerializeToElement(new { body = "reply text" });
        var ctx = new ActionContext(Guid.NewGuid(), Guid.NewGuid(), CancellationToken.None)
        { DryRun = true, SourceMessage = Snapshot() };

        var result = await handler.ExecuteAsync(SmtpConfig(), args, ctx, CancellationToken.None);

        var detail = result.SideEffects![0].Detail!;
        detail.Should().Contain("\"from\"");
        detail.Should().Contain("\"to\"");
        detail.Should().Contain("\"subject\"");
        detail.Should().Contain("\"body_text\"");
        detail.Should().Contain("\"smtp\"");
        detail.Should().NotContain(GreenMailFixture.Password);
        detail.Should().NotContain("\"password\"");
    }

    [Fact]
    public async Task Reply_NoSourceContext_FailsGracefully()
    {
        var handler = new ImapReplyActionHandler();
        var args = JsonSerializer.SerializeToElement(new { body = "x" });
        var ctx = new ActionContext(Guid.NewGuid(), Guid.NewGuid(), CancellationToken.None);

        var result = await handler.ExecuteAsync(SmtpConfig(), args, ctx, CancellationToken.None);
        result.Ok.Should().BeFalse();
        result.Error.Should().Be("no source message context");
    }

    [Fact]
    public async Task Reply_WrongSmtpPassword_ReturnsOkFalse_Sanitized()
    {
        var handler = new ImapReplyActionHandler();
        var bad = JsonSerializer.SerializeToElement(new
        {
            host = _gm.SmtpHost, port = (int)_gm.SmtpPort, use_ssl = false,
            username = GreenMailFixture.Username, password = "WRONG",
            from_address = "sender@test.local",
        });
        var args = JsonSerializer.SerializeToElement(new { body = "x" });
        var ctx = new ActionContext(Guid.NewGuid(), Guid.NewGuid(), CancellationToken.None) { SourceMessage = Snapshot() };

        var result = await handler.ExecuteAsync(bad, args, ctx, CancellationToken.None);
        result.Ok.Should().BeFalse();
        result.Error.Should().StartWith("smtp send failed:");
        result.Error.Should().NotContain("WRONG");
    }

    [Fact]
    public async Task Forward_Execute_AppendsOriginalBody_AndNote()
    {
        var handler = new ImapForwardActionHandler();
        var args = JsonSerializer.SerializeToElement(new { to = GreenMailFixture.Email, note = "FYI" });
        var ctx = new ActionContext(Guid.NewGuid(), Guid.NewGuid(), CancellationToken.None) { SourceMessage = Snapshot() };

        var result = await handler.ExecuteAsync(SmtpConfig(), args, ctx, CancellationToken.None);

        result.Ok.Should().BeTrue();
        result.SideEffects.Should().ContainSingle(e => e.Kind == SideEffectKinds.SentEmail);
        (await _gm.CountMessagesAsync(GreenMailFixture.Email)).Should().Be(1);
    }

    [Fact]
    public async Task Forward_NoOriginalBody_WhenIncludeOriginalBodyFalse()
    {
        var handler = new ImapForwardActionHandler();
        var args = JsonSerializer.SerializeToElement(new { to = GreenMailFixture.Email, note = "note only", include_original_body = false });
        var ctx = new ActionContext(Guid.NewGuid(), Guid.NewGuid(), CancellationToken.None)
        { DryRun = true, SourceMessage = Snapshot() };

        var result = await handler.ExecuteAsync(SmtpConfig(), args, ctx, CancellationToken.None);
        result.Ok.Should().BeTrue();
        result.SideEffects![0].Detail.Should().Contain("\"original_body_included\":false");
        result.SideEffects![0].Detail.Should().Contain("note only");
    }

    [Fact]
    public async Task Forward_DryRun_NoConnection()
    {
        var factory = new CountingSmtpClientFactory();
        var handler = new ImapForwardActionHandler(factory);
        var args = JsonSerializer.SerializeToElement(new { to = GreenMailFixture.Email, note = "x" });
        var ctx = new ActionContext(Guid.NewGuid(), Guid.NewGuid(), CancellationToken.None)
        { DryRun = true, SourceMessage = Snapshot() };

        var result = await handler.ExecuteAsync(SmtpConfig(), args, ctx, CancellationToken.None);
        result.Ok.Should().BeTrue();
        factory.CreateCount.Should().Be(0);
    }

    private sealed class CountingSmtpClientFactory : ISmtpClientFactory
    {
        public int CreateCount { get; private set; }
        public SmtpClient Create() { CreateCount++; return new SmtpClient(); }
    }
}
