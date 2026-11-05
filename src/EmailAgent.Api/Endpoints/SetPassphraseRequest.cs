using System.Text.Json.Serialization;
using FluentValidation;

namespace EmailAgent.Api.Endpoints;

/// <summary>
/// JSON body of <c>POST /v1/account/set-passphrase</c>: two inputs per
/// Q-A4 / Q-E1 (the passphrase is confirmed independently of the login
/// password).
/// </summary>
public sealed record SetPassphraseRequest
{
    /// <summary>The KEK-deriving passphrase (≥ 12 chars, no other policy in M1).</summary>
    [JsonPropertyName("passphrase")]
    public string? Passphrase { get; init; }

    /// <summary>Exact confirmation of <see cref="Passphrase"/>.</summary>
    [JsonPropertyName("passphrase_confirm")]
    public string? PassphraseConfirm { get; init; }
}

/// <summary>
/// M1T6 task step 6 validation: <c>passphrase.Length &gt;= 12</c> and
/// <c>passphrase == passphrase_confirm</c>. Failures surface as the §13.12
/// <c>errors</c> array on a 400 <c>/errors/validation</c> problem document.
/// </summary>
public sealed class SetPassphraseRequestValidator : AbstractValidator<SetPassphraseRequest>
{
    /// <summary>The M1 first-login passphrase floor (task step 6).</summary>
    public const int MinPassphraseLength = 12;

    public SetPassphraseRequestValidator()
    {
        RuleFor(request => request.Passphrase)
            .NotEmpty()
            .WithMessage("passphrase is required.")
            .MinimumLength(MinPassphraseLength)
            .WithMessage($"passphrase must be at least {MinPassphraseLength} characters.");

        RuleFor(request => request.PassphraseConfirm)
            .Equal(request => request.Passphrase)
            .WithMessage("passphrase_confirm does not match passphrase.");
    }
}
