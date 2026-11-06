using System.Text.Json;
using EmailAgent.Api.Endpoints;
using FluentAssertions;
using Xunit;

namespace EmailAgent.UnitTests.Sources;

/// <summary>
/// M2T5 §14 request-shape validation for the three <c>/v1/user/sources</c>
/// request records. These failures become 400 <c>/errors/validation</c>
/// documents whose <c>errors[].name</c> is the snake_case wire field; the
/// plugin-schema failures (<c>/errors/config-validation</c>) are a separate
/// class raised inside <see cref="EmailAgent.Api.Sources.SourceService"/> and
/// are asserted in the integration suite.
/// </summary>
public class SourceRequestValidatorTests
{
    // -----------------------------------------------------------------------
    // POST /v1/user/sources
    // -----------------------------------------------------------------------

    [Fact]
    public void Create_ValidRequest_Passes()
    {
        var result = new CreateSourceRequestValidator().Validate(new CreateSourceRequest
        {
            TypeId = "imap",
            DisplayName = "home imap",
            Config = ObjectConfig(),
        });

        result.IsValid.Should().BeTrue(
            "interval_minutes is optional and defaults to 5 in the service");
    }

    [Fact]
    public void Create_MissingTypeId_FailsWithTypeIdField()
    {
        var result = new CreateSourceRequestValidator().Validate(new CreateSourceRequest
        {
            DisplayName = "home imap",
            Config = ObjectConfig(),
        });

        result.IsValid.Should().BeFalse();
        result.Errors.Should().Contain(e =>
            e.PropertyName == nameof(CreateSourceRequest.TypeId)
            && e.ErrorMessage.Contains("type_id"));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Create_BlankDisplayName_FailsWithDisplayNameField(string? displayName)
    {
        var result = new CreateSourceRequestValidator().Validate(new CreateSourceRequest
        {
            TypeId = "imap",
            DisplayName = displayName,
            Config = ObjectConfig(),
        });

        result.IsValid.Should().BeFalse();
        result.Errors.Should().Contain(e =>
            e.PropertyName == nameof(CreateSourceRequest.DisplayName));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(1441)]
    public void Create_IntervalOutOfRange_FailsWithIntervalField(int intervalMinutes)
    {
        var result = new CreateSourceRequestValidator().Validate(new CreateSourceRequest
        {
            TypeId = "imap",
            DisplayName = "home imap",
            IntervalMinutes = intervalMinutes,
            Config = ObjectConfig(),
        });

        result.IsValid.Should().BeFalse(
            "Q-O4 pins the knob to 1..1440 minutes");
        result.Errors.Should().Contain(e =>
            e.PropertyName == nameof(CreateSourceRequest.IntervalMinutes)
            && e.ErrorMessage.Contains("1") && e.ErrorMessage.Contains("1440"));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(5)]
    [InlineData(1440)]
    public void Create_IntervalInRange_Passes(int intervalMinutes)
    {
        var result = new CreateSourceRequestValidator().Validate(new CreateSourceRequest
        {
            TypeId = "imap",
            DisplayName = "home imap",
            IntervalMinutes = intervalMinutes,
            Config = ObjectConfig(),
        });

        result.IsValid.Should().BeTrue();
    }

    [Fact]
    public void Create_AbsentConfig_FailsAsRequired()
    {
        var result = new CreateSourceRequestValidator().Validate(new CreateSourceRequest
        {
            TypeId = "imap",
            DisplayName = "home imap",
        });

        result.IsValid.Should().BeFalse();
        result.Errors.Should().Contain(e =>
            e.PropertyName == nameof(CreateSourceRequest.Config)
            && e.ErrorMessage.Contains("config is required"));
    }

    [Fact]
    public void Create_ExplicitNullConfig_FailsAsNonObject()
    {
        // "config": null and an absent "config" must stay distinguishable.
        var result = new CreateSourceRequestValidator().Validate(new CreateSourceRequest
        {
            TypeId = "imap",
            DisplayName = "home imap",
            Config = JsonDocument.Parse("null").RootElement.Clone(),
        });

        result.IsValid.Should().BeFalse();
        result.Errors.Should().Contain(e =>
            e.PropertyName == nameof(CreateSourceRequest.Config)
            && e.ErrorMessage.Contains("must be a JSON object"));
    }

    // -----------------------------------------------------------------------
    // PATCH /v1/user/sources/{id}
    // -----------------------------------------------------------------------

    [Fact]
    public void Update_AllMembersAbsent_Passes()
    {
        // Every member optional: an empty PATCH body is legal (no-op).
        var result = new UpdateSourceRequestValidator().Validate(new UpdateSourceRequest());

        result.IsValid.Should().BeTrue();
    }

    [Fact]
    public void Update_IntervalOutOfRange_FailsWithIntervalField()
    {
        var result = new UpdateSourceRequestValidator().Validate(new UpdateSourceRequest
        {
            IntervalMinutes = 1441,
        });

        result.IsValid.Should().BeFalse();
        result.Errors.Should().Contain(e =>
            e.PropertyName == nameof(UpdateSourceRequest.IntervalMinutes));
    }

    [Fact]
    public void Update_NullConfig_Fails_NonObjectConfig_Fails()
    {
        var nullConfig = new UpdateSourceRequest
        {
            Config = JsonDocument.Parse("null").RootElement.Clone(),
        };
        var arrayConfig = new UpdateSourceRequest
        {
            Config = JsonDocument.Parse("[1,2]").RootElement.Clone(),
        };

        new UpdateSourceRequestValidator().Validate(nullConfig).IsValid.Should().BeFalse();
        new UpdateSourceRequestValidator().Validate(arrayConfig).IsValid.Should().BeFalse();
    }

    // -----------------------------------------------------------------------
    // POST /v1/user/sources/test-connection
    // -----------------------------------------------------------------------

    [Fact]
    public void TestConnection_ValidRequest_Passes()
    {
        var result = new TestConnectionRequestValidator().Validate(new TestConnectionRequest
        {
            TypeId = "imap",
            Config = ObjectConfig(),
        });

        result.IsValid.Should().BeTrue();
    }

    [Fact]
    public void TestConnection_MissingTypeIdAndConfig_FailsBoth()
    {
        var result = new TestConnectionRequestValidator().Validate(new TestConnectionRequest());

        result.IsValid.Should().BeFalse();
        result.Errors.Should().Contain(e =>
            e.PropertyName == nameof(TestConnectionRequest.TypeId));
        result.Errors.Should().Contain(e =>
            e.PropertyName == nameof(TestConnectionRequest.Config));
    }

    private static JsonElement ObjectConfig()
    {
        using var document = JsonDocument.Parse("""{"host":"imap.local"}""");
        return document.RootElement.Clone();
    }
}
