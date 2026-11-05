namespace EmailAgent.Core.Security;

/// <summary>
/// Thrown when a passphrase does not unwrap the user's DEK — the Argon2id
/// KEK derived from it fails AES-GCM authentication. Typed so M1T6 can map
/// it to RFC 7807 401 without string matching; no secret material is ever
/// included in the message.
/// </summary>
public sealed class InvalidPassphraseException : Exception
{
    public InvalidPassphraseException()
        : base("The supplied passphrase does not unwrap this account's data-encryption key.")
    {
    }

    public InvalidPassphraseException(string message)
        : base(message)
    {
    }

    public InvalidPassphraseException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}