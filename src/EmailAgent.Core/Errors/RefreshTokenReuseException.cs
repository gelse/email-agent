namespace EmailAgent.Core.Errors;

/// <summary>
/// Thrown when an already-used or revoked refresh token is presented again
/// (§13.11 / Q-API5 reuse detection). By then the store has revoked the whole
/// <c>family_id</c> partition and written the <c>refresh_reuse_detected</c>
/// audit row; M1T6 maps this type to RFC 7807 401.
/// </summary>
public sealed class RefreshTokenReuseException : Exception
{
    public RefreshTokenReuseException()
        : base("This refresh token has already been used; the session family has been revoked.")
    {
    }

    public RefreshTokenReuseException(string message)
        : base(message)
    {
    }

    public RefreshTokenReuseException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
