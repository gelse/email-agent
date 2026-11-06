using System.Text.Json;
using EmailAgent.Connectors.Imap;
using EmailAgent.Contracts;
using EmailAgent.Contracts.Schema;
using FluentAssertions;
using Xunit;

namespace EmailAgent.UnitTests.Connectors.Imap;

/// <summary>
/// M2T4 unit matrix for <see cref="ImapConfig"/> (plan §Steps 2): member mapping,
/// the documented defaults, and the fact that a required member is neither silently
/// defaulted nor ignored — M2T2's <c>JsonSchemaValidator</c> is the same gate
/// <c>ValidateConfigAsync</c> runs before it opens a socket.
/// </summary>
[Trait("Category", "M2T4")]
public class ImapConfigTests
{
    private const string MissingPassword =
        """{"host":"imap.example.com","port":993,"use_ssl":true,"username":"alice","mailbox":"INBOX"}""";

    private const string PortAsText =
        """{"host":"imap.example.com","port":"993","use_ssl":true,"username":"alice","password":"secret","mailbox":"INBOX"}""";

    [Fact]
    public void ImapConfig_FromJson_AppliesDefaults()
    {
        // port 0 and a missing mailbox are exactly the two cases plan §Steps 2 normalises.
        using var doc = JsonDocument.Parse(
            """{"host":"imap.example.com","port":0,"use_ssl":false,"username":"alice","password":"secret"}""");

        var cfg = ImapConfig.FromJson(doc.RootElement);

        cfg.Port.Should().Be(993, "port 0 falls back to the schema's default 993");
        cfg.Mailbox.Should().Be("INBOX", "a missing mailbox falls back to INBOX");
        cfg.Host.Should().Be("imap.example.com");
        cfg.Username.Should().Be("alice");
        cfg.Password.Should().Be("secret");
        cfg.UseSsl.Should().BeFalse();
    }

    [Fact]
    public void ImapConfig_FromJson_MapsEveryMember()
    {
        using var doc = JsonDocument.Parse(
            """{"host":"imap.example.com","port":1143,"use_ssl":false,"username":"alice","password":"secret","mailbox":"Archive"}""");

        var cfg = ImapConfig.FromJson(doc.RootElement);

        cfg.Host.Should().Be("imap.example.com");
        cfg.Port.Should().Be(1143, "a non-zero port is never overridden");
        cfg.UseSsl.Should().BeFalse();
        cfg.Username.Should().Be("alice");
        cfg.Password.Should().Be("secret");
        cfg.Mailbox.Should().Be("Archive", "a configured mailbox is never replaced by INBOX");
    }

    [Fact]
    public void ImapConfig_FromJson_RejectsMissingRequiredField()
    {
        using var doc = JsonDocument.Parse(MissingPassword);

        // M2T2's validator is the contract ValidateConfigAsync's schema gate uses …
        var schemaResult = JsonSchemaValidator.Validate(ImapConfigSchema.Build(), doc.RootElement);
        schemaResult.Ok.Should().BeFalse();
        schemaResult.Error.Should().Be("schema validation failed");
        schemaResult.Issues!.Should().ContainSingle()
            .Which.Should().Be(new ValidationIssue("/password", "required"));

        // … and the typed parse must fail loudly rather than default a secret away.
        Action parse = () => ImapConfig.FromJson(doc.RootElement);
        parse.Should().Throw<JsonException>().WithMessage("*'password'*");
    }

    [Fact]
    public void ImapConfig_FromJson_RejectsMistypedMember()
    {
        using var doc = JsonDocument.Parse(PortAsText);

        Action parse = () => ImapConfig.FromJson(doc.RootElement);
        parse.Should().Throw<JsonException>().WithMessage("*'port'*");
    }
}
