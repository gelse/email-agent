using System.Text.Json;
using EmailAgent.Connectors.Imap;
using EmailAgent.Contracts.ActionHandlers;
using EmailAgent.Contracts.Schema;
using FluentAssertions;
using Xunit;

namespace EmailAgent.UnitTests.Connectors.Imap;

public class ImapActionSchemaTests
{
    [Fact]
    public void ConfigSchema_HappyPath()
    {
        using var doc = JsonDocument.Parse("""{"host":"smtp.x","username":"u","password":"p","from_address":"a@b.com"}""");
        JsonSchemaValidator.Validate(ImapActionConfigSchema.Build(), doc.RootElement).Ok.Should().BeTrue();
    }

    [Fact]
    public void ConfigSchema_RejectsMissingHost()
    {
        using var doc = JsonDocument.Parse("""{"username":"u","password":"p","from_address":"a@b.com"}""");
        var r = JsonSchemaValidator.Validate(ImapActionConfigSchema.Build(), doc.RootElement);
        r.Ok.Should().BeFalse();
    }

    [Fact]
    public void ConfigSchema_RejectsBadPort()
    {
        using var doc = JsonDocument.Parse("""{"host":"h","port":99999,"username":"u","password":"p","from_address":"a@b.com"}""");
        JsonSchemaValidator.Validate(ImapActionConfigSchema.Build(), doc.RootElement).Ok.Should().BeFalse();
    }

    [Fact]
    public void ConfigSchema_RejectsAdditionalProperties()
    {
        using var doc = JsonDocument.Parse("""{"host":"h","username":"u","password":"p","from_address":"a@b.com","extra":1}""");
        JsonSchemaValidator.Validate(ImapActionConfigSchema.Build(), doc.RootElement).Ok.Should().BeFalse();
    }

    [Fact]
    public async Task ReplyArgsSchema_HappyPath()
    {
        var h = new ImapReplyActionHandler();
        using var doc = JsonDocument.Parse("""{"body":"hello"}""");
        (await h.ValidateArgsAsync(default, doc.RootElement, CancellationToken.None)).Ok.Should().BeTrue();
    }

    [Fact]
    public async Task ReplyArgsSchema_RejectsBodyWrongType()
    {
        var h = new ImapReplyActionHandler();
        using var doc = JsonDocument.Parse("""{"body":42}""");
        (await h.ValidateArgsAsync(default, doc.RootElement, CancellationToken.None)).Ok.Should().BeFalse();
    }

    [Fact]
    public async Task ReplyArgsSchema_RejectsUnknownProperty()
    {
        var h = new ImapReplyActionHandler();
        using var doc = JsonDocument.Parse("""{"body":"x","oops":1}""");
        (await h.ValidateArgsAsync(default, doc.RootElement, CancellationToken.None)).Ok.Should().BeFalse();
    }

    [Fact]
    public async Task Forward_ValidateArgs_TryParse_RejectsGarbageAddress()
    {
        var h = new ImapForwardActionHandler();
        using var doc = JsonDocument.Parse("""{"to":"not-an-email"}""");
        var r = await h.ValidateArgsAsync(default, doc.RootElement, CancellationToken.None);
        r.Ok.Should().BeFalse();
        r.Issues.Should().Contain(i => i.Path == "to" && i.Reason == "not a valid email address");
    }

    [Fact]
    public void Subject_NoDoublePrefix_Reply()
    {
        ImapReplyActionHandler.ResolveSubject(null, "Re: hi", reply: true).Should().Be("Re: hi");
        ImapReplyActionHandler.ResolveSubject(null, "hi", reply: true).Should().Be("Re: hi");
        ImapReplyActionHandler.ResolveSubject(null, "Fwd: hi", reply: true).Should().Be("Fwd: hi");
    }

    [Fact]
    public void Subject_NoDoublePrefix_Forward()
    {
        ImapReplyActionHandler.ResolveSubject(null, "Fwd: hi", reply: false).Should().Be("Fwd: hi");
        ImapReplyActionHandler.ResolveSubject(null, "hi", reply: false).Should().Be("Fwd: hi");
    }
}
