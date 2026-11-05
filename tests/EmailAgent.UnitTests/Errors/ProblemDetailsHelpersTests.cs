using System.Text.Json;
using EmailAgent.Api.Errors;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace EmailAgent.UnitTests.Errors;

/// <summary>
/// M1T6 task step 1: the RFC 7807 helper's contract — status, content type
/// and the §13.12 member set, including the optional <c>errors</c> array.
/// The result is executed against a bare <see cref="DefaultHttpContext"/> so
/// the assertions observe the real serialized document, not just the inputs.
/// </summary>
public class ProblemDetailsHelpersTests
{
    [Fact]
    public async Task Problem_Returns_ExpectedShape()
    {
        var ctx = NewContext("/v1/account/login");

        var result = ProblemDetailsHelpers.Problem(
            ctx,
            StatusCodes.Status401Unauthorized,
            "/errors/unauthorized",
            "Unauthorized",
            "Invalid email or password.");

        var (status, contentType, body) = await ExecuteAsync(result, ctx);

        status.Should().Be(StatusCodes.Status401Unauthorized);
        contentType.Should().Contain("application/problem+json");
        body.GetProperty("type").GetString().Should().Be("/errors/unauthorized");
        body.GetProperty("title").GetString().Should().Be("Unauthorized");
        body.GetProperty("status").GetInt32().Should().Be(401);
        body.GetProperty("detail").GetString().Should().Be("Invalid email or password.");
        body.GetProperty("instance").GetString().Should().Be("/v1/account/login");
        body.TryGetProperty("errors", out _).Should().BeFalse(
            "errors is omitted when there is no field-level detail (§13.12 errors?)");
    }

    [Fact]
    public async Task Problem_With_Errors_Field()
    {
        var ctx = NewContext("/v1/account/set-passphrase");

        var result = ProblemDetailsHelpers.Problem(
            ctx,
            StatusCodes.Status400BadRequest,
            "/errors/validation",
            "Validation failed",
            "One or more fields are invalid.",
            new[]
            {
                (Name: "passphrase_confirm", Reason: "passphrase_confirm does not match passphrase."),
                (Name: "passphrase", Reason: "passphrase must be at least 12 characters."),
            });

        var (status, contentType, body) = await ExecuteAsync(result, ctx);

        status.Should().Be(StatusCodes.Status400BadRequest);
        contentType.Should().Contain("application/problem+json");
        body.GetProperty("type").GetString().Should().Be("/errors/validation");
        body.GetProperty("status").GetInt32().Should().Be(400);

        var errors = body.GetProperty("errors");
        errors.ValueKind.Should().Be(JsonValueKind.Array);
        errors.GetArrayLength().Should().Be(2);
        errors[0].GetProperty("name").GetString().Should().Be("passphrase_confirm");
        errors[0].GetProperty("reason").GetString()
            .Should().Be("passphrase_confirm does not match passphrase.");
        errors[1].GetProperty("name").GetString().Should().Be("passphrase");
        errors[1].GetProperty("reason").GetString()
            .Should().Be("passphrase must be at least 12 characters.");
    }

    private static DefaultHttpContext NewContext(string path)
    {
        var ctx = new DefaultHttpContext();
        ctx.Request.Path = path;
        return ctx;
    }

    private static async Task<(int Status, string ContentType, JsonElement Body)> ExecuteAsync(
        IResult result,
        DefaultHttpContext ctx)
    {
        ctx.RequestServices = new ServiceCollection().AddLogging().BuildServiceProvider();
        ctx.Response.Body = new MemoryStream();

        await result.ExecuteAsync(ctx);

        ctx.Response.Body.Position = 0;
        using var document = await JsonDocument.ParseAsync(ctx.Response.Body);
        return (ctx.Response.StatusCode, ctx.Response.ContentType ?? string.Empty, document.RootElement.Clone());
    }
}
