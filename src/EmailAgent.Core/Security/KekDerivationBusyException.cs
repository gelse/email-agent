namespace EmailAgent.Core.Security;

/// <summary>
/// Thrown when a KEK-derivation (or DEK unwrap) for the same user is
/// already in flight and the per-user zero-wait mutex rejects the new
/// caller (Q-E5). M1T6 maps this to RFC 7807 429; M1 runs a per-process
/// mutex only — cross-process coordination is out of scope for M1.
/// </summary>
public sealed class KekDerivationBusyException : Exception
{
    public KekDerivationBusyException()
        : base("A key-derivation for this user is already in progress; retry shortly.")
    {
    }

    public KekDerivationBusyException(string message)
        : base(message)
    {
    }

    public KekDerivationBusyException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}