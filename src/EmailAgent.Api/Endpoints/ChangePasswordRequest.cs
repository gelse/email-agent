using System.Text.Json.Serialization;
using FluentValidation;

namespace EmailAgent.Api.Endpoints;

/// <summary>
/// JSON body of <c>POST /v1/account/change-password</c>. The endpoint takes
/// two shapes (M1T6 task step 7): the first-login flow sends only
/// <c>new_password</c> + <c>new_password_confirm</c> (the OTP-derived session
/// is the credential — Alice never learned the temporary password), while
/// the subsequent flow additionally requires <c>old_password</c>.
/// </summary>
public sealed record ChangePasswordRequest
{
    /// <summary>Current password — required only when <c>must_change_password</c> is false.</summary>
    [JsonPropertyName("old_password")]
    public string? OldPassword { get; init; }

    /// <summary>The chosen new password (Identity policy: ≥ 12 chars, digit, mixed case, non-alphanumeric).</summary>
    [JsonPropertyName("new_password")]
    public string? NewPassword { get; init; }

    /// <summary>Exact confirmation of <see cref="NewPassword"/>.</summary>
    [JsonPropertyName("new_password_confirm")]
    public string? NewPasswordConfirm { get; init; }
}

/// <summary>
/// M1T6 task step 7 validation. <paramref name="firstLogin"/> selects the
/// flow: the first-login shape never demands <c>old_password</c> (the
/// endpoint ignores it if present), the subsequent shape requires it.
/// </summary>
public sealed class ChangePasswordRequestValidator : AbstractValidator<ChangePasswordRequest>
{
    public ChangePasswordRequestValidator(bool firstLogin)
    {
        RuleFor(request => request.NewPassword)
            .NotEmpty()
            .WithMessage("new_password is required.");

        RuleFor(request => request.NewPasswordConfirm)
            .NotEmpty()
            .WithMessage("new_password_confirm is required.")
            .Equal(request => request.NewPassword)
            .WithMessage("new_password_confirm does not match new_password.");

        When(_ => !firstLogin, () =>
        {
            RuleFor(request => request.OldPassword)
                .NotEmpty()
                .WithMessage("old_password is required for this flow.");
        });
    }
}
