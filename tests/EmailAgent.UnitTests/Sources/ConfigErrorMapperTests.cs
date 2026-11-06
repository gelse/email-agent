using EmailAgent.Api.Sources;
using EmailAgent.Contracts;
using EmailAgent.Contracts.Schema;
using FluentAssertions;
using Xunit;

namespace EmailAgent.UnitTests.Sources;

/// <summary>
/// M2T5: the <c>errors: [{name, reason}]</c> renderer of the 400
/// <c>/errors/config-validation</c> document. The milestone pins the exact
/// example <c>{name:"port", reason:"must be between 1 and 65535"}</c> — a
/// human-readable rendering of the failing keyword against the plugin's own
/// schema, not the raw keyword — plus JSON-Pointer decoding of nested wire
/// names.
/// </summary>
public class ConfigErrorMapperTests
{
    [Fact]
    public void Map_PortRangeViolation_UsesThePinnedContractWording()
    {
        // port=-1 / port=99999 both fail minimum/maximum on the IMAP schema.
        var issues = new[] { new ValidationIssue("/port", "maximum") };

        var mapped = ConfigErrorMapper.Map(ImapConfigSchema.Build(), issues);

        mapped.Should().ContainSingle();
        mapped[0].Name.Should().Be("port");
        mapped[0].Reason.Should().Be(
            "must be between 1 and 65535",
            "the milestone asserts this exact reason in Sources_Post_InvalidConfig_Returns400_ProblemDetails");
    }

    [Fact]
    public void Map_MinimumViolation_ReportsBothBoundsToo()
    {
        var issues = new[] { new ValidationIssue("/port", "minimum") };

        var mapped = ConfigErrorMapper.Map(ImapConfigSchema.Build(), issues);

        mapped[0].Reason.Should().Be("must be between 1 and 65535",
            "a bounded numeric member reports BOTH bounds whichever side was crossed");
    }

    [Fact]
    public void Map_RequiredAndMinLength_ResolveHumanReadableReasons()
    {
        var issues = new[]
        {
            new ValidationIssue("/password", "required"),
            new ValidationIssue("/mailbox", "minLength"),
        };

        var mapped = ConfigErrorMapper.Map(ImapConfigSchema.Build(), issues);

        mapped[0].Name.Should().Be("password");
        mapped[0].Reason.Should().Be("required");
        mapped[1].Name.Should().Be("mailbox");
        mapped[1].Reason.Should().Be("must be at least 1 characters");
    }

    [Fact]
    public void Map_UnknownMember_FallsBackToTheRootSchema_WithoutDroppingTheIssue()
    {
        // A member outside the plugin's properties cannot be resolved — the
        // issue must still reach the client instead of vanishing.
        var issues = new[] { new ValidationIssue("/surprise", "type") };

        var mapped = ConfigErrorMapper.Map(ImapConfigSchema.Build(), issues);

        mapped.Should().ContainSingle();
        mapped[0].Name.Should().Be("surprise");
        mapped[0].Reason.Should().NotBeNullOrWhiteSpace();
    }

    [Fact]
    public void Map_EveryIssue_IsMappedExactlyOnceInOrder()
    {
        var issues = new[]
        {
            new ValidationIssue("/host", "required"),
            new ValidationIssue("/port", "maximum"),
            new ValidationIssue("/username", "minLength"),
        };

        var mapped = ConfigErrorMapper.Map(ImapConfigSchema.Build(), issues);

        mapped.Select(m => m.Name).Should().Equal("host", "port", "username");
    }

    [Theory]
    [InlineData("", "config")]
    [InlineData("/", "config")]
    [InlineData("/port", "port")]
    [InlineData("/tls/min_version", "tls.min_version")]
    [InlineData("/path~1with~0escape", "path/with~escape")]
    public void WireName_DecodesJsonPointerSegments(string pointer, string expected)
        => ConfigErrorMapper.WireName(pointer).Should().Be(expected);

    [Fact]
    public void Map_RootIssue_BlamesTheConfigRequestMember()
    {
        // "config must be a JSON object" arrives as a root-level issue.
        var issues = new[] { new ValidationIssue(string.Empty, "type") };

        var mapped = ConfigErrorMapper.Map(ImapConfigSchema.Build(), issues);

        mapped[0].Name.Should().Be(ConfigErrorMapper.RootName);
        mapped[0].Reason.Should().Be("must be of type object");
    }

    [Fact]
    public void Map_WithNoIssues_ReturnsAnEmptyArray()
        => ConfigErrorMapper.Map(ImapConfigSchema.Build(), Array.Empty<ValidationIssue>())
            .Should().BeEmpty();
}
