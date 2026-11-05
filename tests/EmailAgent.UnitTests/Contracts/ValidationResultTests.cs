using System.Text.Json;
using EmailAgent.Contracts;
using FluentAssertions;
using Xunit;

namespace EmailAgent.UnitTests.Contracts;

/// <summary>
/// M2T1 step 5/15: <see cref="ValidationResult"/> factory methods and the
/// canonical JSON shape (<c>ok</c>/<c>error</c>/<c>issues</c> with
/// <c>path</c>/<c>reason</c> per M2T2).
/// </summary>
public class ValidationResultTests
{
    [Fact]
    public void ValidationResult_Success_And_Failure_Factory_Methods()
    {
        var success = ValidationResult.Success();
        success.Ok.Should().BeTrue();
        success.Error.Should().BeNull();
        success.Issues!.Should().BeNull();

        var failure = ValidationResult.Failure("invalid config");
        failure.Ok.Should().BeFalse();
        failure.Error.Should().Be("invalid config");
        failure.Issues!.Should().BeNull();

        var failureWithIssues = ValidationResult.Failure(
            "invalid config",
            [new ValidationIssue("password", "required")]);
        failureWithIssues.Ok.Should().BeFalse();
        failureWithIssues.Issues!.Should().ContainSingle()
            .Which.Should().Be(new ValidationIssue("password", "required"));
    }

    [Fact]
    public void ValidationResult_With_Issues_RoundTripsJson()
    {
        var result = ValidationResult.Failure(
            "invalid config",
            [
                new ValidationIssue("password", "required"),
                new ValidationIssue("port", "maximum"),
            ]);

        var json = JsonSerializer.Serialize(result);

        json.Should().Be(
            "{\"ok\":false,\"error\":\"invalid config\"," +
            "\"issues\":[{\"path\":\"password\",\"reason\":\"required\"}," +
            "{\"path\":\"port\",\"reason\":\"maximum\"}]}");

        var back = JsonSerializer.Deserialize<ValidationResult>(json);

        back.Should().NotBeNull();
        back!.Ok.Should().BeFalse();
        back.Error.Should().Be("invalid config");
        back.Issues!.Should().NotBeNull();
        back.Issues!.Should().Equal(
            new ValidationIssue("password", "required"),
            new ValidationIssue("port", "maximum"));
    }

    [Fact]
    public void ValidationResult_Without_Issues_RoundTripsJson()
    {
        var json = JsonSerializer.Serialize(ValidationResult.Success());

        json.Should().Be("{\"ok\":true,\"error\":null,\"issues\":null}");

        var back = JsonSerializer.Deserialize<ValidationResult>(json);

        back.Should().NotBeNull();
        back!.Ok.Should().BeTrue();
        back.Error.Should().BeNull();
        back.Issues!.Should().BeNull();
    }

    [Fact]
    public void ValidationIssue_MissingRequiredMember_ThrowsInsteadOfNull()
    {
        // [JsonRequired]: both members are non-nullable — a partial issue must
        // fail loudly instead of yielding null into string.
        var act = () => JsonSerializer.Deserialize<ValidationIssue>("{\"path\":\"password\"}");

        act.Should().Throw<JsonException>();
    }
}
