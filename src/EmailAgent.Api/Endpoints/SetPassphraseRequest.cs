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
    /// <summary>The KEK-deriving passphrase (12..512 chars; M1 set only the floor — M2T6 added the ceiling).</summary>
    [JsonPropertyName("passphrase")]
    public string? Passphrase { get; init; }

    /// <summary>Exact confirmation of <see cref="Passphrase"/>.</summary>
    [JsonPropertyName("passphrase_confirm")]
    public string? PassphraseConfirm { get; init; }
}

/// <summary>
/// M1T6 task step 6 validation: <c>passphrase.Length &gt;= 12</c> and
/// <c>passphrase == passphrase_confirm</c>; M2T6 adds the 512-character
/// ceiling that keeps the value inside unwrap-dek's 4 KiB body cap.
/// Failures surface as the §13.12
/// <c>errors</c> array on a 400 <c>/errors/validation</c> problem document.
/// </summary>
public sealed class SetPassphraseRequestValidator : AbstractValidator<SetPassphraseRequest>
{
    /// <summary>The M1 first-login passphrase floor (task step 6).</summary>
    public const int MinPassphraseLength = 12;

    /// <summary>
    /// M2T6 review fix (round 2): the ceiling that keeps a passphrase
    /// <i>un-unlockable</i> from ever being stored — <c>POST
    /// /v1/user/account/unwrap-dek</c> caps its whole JSON body at 4 KiB, and
    /// JSON escaping can inflate a value six-fold, so 512 characters is the
    /// largest input that always fits. M1 shipped no maximum ("no other
    /// policy in M1"), which is exactly how a stored passphrase could exceed
    /// the unwrap cap and strand the account behind an impossible unlock.
    /// </summary>
    public const int MaxPassphraseLength = 512;

    public SetPassphraseRequestValidator()
    {
        RuleFor(request => request.Passphrase)
            .NotEmpty()
            .WithMessage("passphrase is required.")
            .MinimumLength(MinPassphraseLength)
            .WithMessage($"passphrase must be at least {MinPassphraseLength} characters.")
            .MaximumLength(MaxPassphraseLength)
            .WithMessage($"passphrase must be at most {MaxPassphraseLength} characters.");

        RuleFor(request => request.PassphraseConfirm)
            .Equal(request => request.Passphrase)
            .WithMessage("passphrase_confirm does not match passphrase.");
    }
}
