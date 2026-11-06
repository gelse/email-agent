namespace EmailAgent.Infrastructure.Enums;

/// <summary>
/// §6 <c>users.status</c>: stored as text via
/// <c>HasConversion&lt;string&gt;()</c> ("Pending" / "Active" / "Disabled")
/// with a mirroring CHECK constraint (M1T2 task step 4).
/// </summary>
public enum UserStatus
{
    Pending,
    Active,
    Disabled,
}
