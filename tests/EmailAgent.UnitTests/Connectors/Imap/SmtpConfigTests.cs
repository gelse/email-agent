using System.Text.Json;
using EmailAgent.Connectors.Imap;
using FluentAssertions;
using Xunit;

namespace EmailAgent.UnitTests.Connectors.Imap;

public class SmtpConfigTests
{
    [Fact]
    public void FromJson_RoundTrip()
    {
        using var doc = JsonDocument.Parse("""{"host":"smtp.x","port":465,"use_ssl":true,"username":"u","password":"p","from_address":"a@b","from_name":"A"}""");
        var c = SmtpConfig.FromJson(doc.RootElement);
        c.Host.Should().Be("smtp.x");
        c.Port.Should().Be(465);
        c.UseSsl.Should().BeTrue();
        c.Username.Should().Be("u");
        c.Password.Should().Be("p");
        c.FromAddress.Should().Be("a@b");
        c.FromName.Should().Be("A");
    }

    [Fact]
    public void PortDefault_UseSslTrue_465()
    {
        using var doc = JsonDocument.Parse("""{"host":"h","username":"u","password":"p","from_address":"a@b","use_ssl":true}""");
        SmtpConfig.FromJson(doc.RootElement).Port.Should().Be(465);
    }

    [Fact]
    public void PortDefault_UseSslFalse_587()
    {
        using var doc = JsonDocument.Parse("""{"host":"h","username":"u","password":"p","from_address":"a@b"}""");
        SmtpConfig.FromJson(doc.RootElement).Port.Should().Be(587);
    }

    [Fact]
    public void ToString_RedactsPassword()
    {
        using var doc = JsonDocument.Parse("""{"host":"h","username":"u","password":"SECRET","from_address":"a@b"}""");
        var s = SmtpConfig.FromJson(doc.RootElement).ToString();
        s.Should().Contain("***");
        s.Should().NotContain("SECRET");
    }
}
