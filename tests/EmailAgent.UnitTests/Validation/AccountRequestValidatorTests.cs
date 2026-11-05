using EmailAgent.Api.Endpoints;
using FluentAssertions;
using Xunit;

namespace EmailAgent.UnitTests.Validation;

/// <summary>
/// M1T6 task steps 6–7: the two-input passphrase rule (Q-A4/Q-E1) and the
/// first-login vs subsequent change-password shapes. Failures become the
/// §13.12 <c>errors</c> array on a 400 (covered end-to-end by the
/// integration suite).
/// </summary>
public class AccountRequestValidatorTests
{
    [Fact]
    public void SetPassphrase_ValidInputs_Pass()
    {
        var result = new SetPassphraseRequestValidator().Validate(new SetPassphraseRequest
        {
            Passphrase = "correct horse battery staple",
            PassphraseConfirm = "correct horse battery staple",
        });

        result.IsValid.Should().BeTrue();
    }

    [Fact]
    public void SetPassphrase_TooShort_FailsWithPassphraseField()
    {
        var result = new SetPassphraseRequestValidator().Validate(new SetPassphraseRequest
        {
            Passphrase = "short",
            PassphraseConfirm = "short",
        });

        result.IsValid.Should().BeFalse();
        // The endpoint maps CLR names to the snake_case wire fields (§13.12);
        // integration coverage asserts the wire shape end to end.
        result.Errors.Should().Contain(e => e.PropertyName == nameof(SetPassphraseRequest.Passphrase));
        result.Errors.Should().Contain(e => e.ErrorMessage.Contains("12"));
    }

    [Fact]
    public void SetPassphrase_MismatchedConfirmation_FailsWithConfirmField()
    {
        var result = new SetPassphraseRequestValidator().Validate(new SetPassphraseRequest
        {
            Passphrase = "correct horse battery staple",
            PassphraseConfirm = "at least twelve chars here",
        });

        result.IsValid.Should().BeFalse();
        result.Errors.Should().Contain(e => e.PropertyName == nameof(SetPassphraseRequest.PassphraseConfirm));
    }

    [Fact]
    public void ChangePassword_FirstLogin_DoesNotRequireOldPassword()
    {
        var result = new ChangePasswordRequestValidator(firstLogin: true).Validate(
            new ChangePasswordRequest
            {
                OldPassword = null,
                NewPassword = "NewPassw0rd!abc",
                NewPasswordConfirm = "NewPassw0rd!abc",
            });

        result.IsValid.Should().BeTrue(
            "the OTP-derived session is the credential — no old_password in the first-login shape");
    }

    [Fact]
    public void ChangePassword_Subsequent_RequiresOldPassword()
    {
        var result = new ChangePasswordRequestValidator(firstLogin: false).Validate(
            new ChangePasswordRequest
            {
                OldPassword = null,
                NewPassword = "NewPassw0rd!abc",
                NewPasswordConfirm = "NewPassw0rd!abc",
            });

        result.IsValid.Should().BeFalse();
        result.Errors.Should().Contain(e => e.PropertyName == nameof(ChangePasswordRequest.OldPassword));
    }

    [Fact]
    public void ChangePassword_MismatchedConfirmation_Fails()
    {
        var result = new ChangePasswordRequestValidator(firstLogin: false).Validate(
            new ChangePasswordRequest
            {
                OldPassword = "TempPassw0rd!xyz",
                NewPassword = "NewPassw0rd!abc",
                NewPasswordConfirm = "NewPassw0rd!xyz",
            });

        result.IsValid.Should().BeFalse();
        result.Errors.Should().Contain(e => e.PropertyName == nameof(ChangePasswordRequest.NewPasswordConfirm));
    }
}
