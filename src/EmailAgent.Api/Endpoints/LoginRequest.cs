namespace EmailAgent.Api.Endpoints;

/// <summary>
/// Form payload of <c>POST /v1/account/login</c> (M1T6 task step 4 —
/// <c>application/x-www-form-urlencoded</c>, not JSON).
/// </summary>
public sealed record LoginRequest
{
    /// <summary>Account email (the <c>users.email</c> citext column).</summary>
    public string Email { get; init; } = string.Empty;

    /// <summary>The candidate login password.</summary>
    public string Password { get; init; } = string.Empty;
}
