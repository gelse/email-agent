namespace EmailAgent.Core.Identity;

/// <summary>
/// Result of issuing or rotating a refresh token: the persisted record plus
/// the raw opaque token, which only the API layer ever sees (it is written to
/// the <c>ea_refresh</c> cookie and never persisted — SQ-9 / Q-A10).
/// </summary>
public sealed record IssuedRefreshToken(RefreshTokenRecord Record, string RawToken);