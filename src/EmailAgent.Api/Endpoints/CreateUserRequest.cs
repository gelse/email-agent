using System.Text.Json.Serialization;
using EmailAgent.Infrastructure.Enums;
using FluentValidation;

namespace EmailAgent.Api.Endpoints;

/// <summary>
/// JSON body of <c>POST /v1/admin/users</c> (M1T7): the new account's email
/// and — optionally — its role. <c>role</c> defaults to <c>User</c>; creating
/// an <c>Admin</c> requires the caller to be an Admin, which the group-level
/// <c>[Authorize(Roles = "Admin")]</c> already guarantees for every caller.
/// </summary>
public sealed record CreateUserRequest
{
    /// <summary>Target address (unique — the <c>users.email</c> citext column).</summary>
    [JsonPropertyName("email")]
    public string? Email { get; init; }

    /// <summary>§6 <c>users.role</c>; omitted → <see cref="UserRole.User"/>.</summary>
    [JsonPropertyName("role")]
    [JsonConverter(typeof(JsonStringEnumConverter))]
    public UserRole Role { get; init; } = UserRole.User;
}

/// <summary>
/// M1T7 task step 7 validation: <c>email</c> non-empty and well-formed,
/// <c>role</c> a defined enum member (numeric JSON like <c>42</c> parses into
/// an undefined value and must not reach the store). Failures surface as the
/// §13.12 <c>errors</c> array on a 400 <c>/errors/validation</c> document.
/// </summary>
public sealed class CreateUserRequestValidator : AbstractValidator<CreateUserRequest>
{
    public CreateUserRequestValidator()
    {
        RuleFor(request => request.Email)
            .NotEmpty()
            .WithMessage("email is required.")
            .EmailAddress()
            .WithMessage("email must be a valid address.");

        RuleFor(request => request.Role)
            .IsInEnum()
            .WithMessage("role must be either Admin or User.");
    }
}