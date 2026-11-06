namespace EmailAgent.Infrastructure.Enums;

/// <summary>
/// §6 <c>users.role</c>: stored as text via <c>HasConversion&lt;string&gt;()</c>
/// ("Admin" / "User") with a mirroring CHECK constraint (M1T2 task step 4).
/// </summary>
public enum UserRole
{
    Admin,
    User,
}
