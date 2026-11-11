using System.Security.Claims;
using System.Security.Cryptography;
using EmailAgent.Core.Security;
using Microsoft.AspNetCore.Http;

namespace EmailAgent.Api.Auth;

/// <summary>
/// M5T1 (SQ-M5-1): the request-scoped <see cref="ICryptoSessionAccessor"/> —
/// resolves the caller's crypto session from the <c>X-Crypto-Session</c>
/// header against the singleton <see cref="ICryptoSessionStore"/>, using the
/// JWT's <c>sub</c> as the user key. Stateless: every call re-resolves, and a
/// successful <see cref="TryGetDek"/> returns a fresh DEK copy the caller owns
/// (one per call — no shared per-request buffer that one consumer could zero
/// under another).
/// </summary>
/// <remarks>
/// The 401-vs-403 split rests here: an absent or bad JWT never reaches this
/// type (the bearer handler answers 401), while a valid JWT whose session is
/// missing, unknown or expired maps to
/// <see cref="CryptoSessionStatus.Locked"/>/<see cref="CryptoSessionStatus.Absent"/>/<see cref="CryptoSessionStatus.Expired"/>
/// and to the <c>403 /errors/crypto-locked</c> document
/// <see cref="CryptoSessionMiddleware"/> writes. The old <c>X-User-Dek</c>
/// header is never read — M5T1 is §13.16's removal point (Q-E9).
/// </remarks>
public sealed class HttpCryptoSessionAccessor : ICryptoSessionAccessor
{
    /// <summary>The request header carrying the opaque crypto-session token.</summary>
    public const string HeaderName = "X-Crypto-Session";

    private readonly IHttpContextAccessor _httpAccessor;
    private readonly ICryptoSessionStore _store;

    public HttpCryptoSessionAccessor(IHttpContextAccessor httpAccessor, ICryptoSessionStore store)
    {
        _httpAccessor = httpAccessor ?? throw new ArgumentNullException(nameof(httpAccessor));
        _store = store ?? throw new ArgumentNullException(nameof(store));
    }

    /// <inheritdoc />
    public bool IsUnlocked
    {
        get
        {
            var result = TryGetDek();
            // The DEK itself is not needed for the gate — zero the copy this
            // probe produced instead of letting it ride to the GC.
            if (result.Dek is not null)
            {
                CryptographicOperations.ZeroMemory(result.Dek);
            }

            return result.IsOk;
        }
    }

    /// <inheritdoc />
    public CryptoSessionResult TryGetDek()
    {
        var ctx = _httpAccessor.HttpContext;
        var userId = ResolveUserId(ctx);
        if (userId is null)
        {
            // No resolvable subject on the principal — treat as locked; the
            // endpoint's own actor resolution owns the 401 for that case.
            return CryptoSessionResult.Locked();
        }

        var token = ctx!.Request.Headers[HeaderName].ToString();
        if (string.IsNullOrWhiteSpace(token))
        {
            // No credential presented: the vault is locked (the SPA prompts),
            // rather than "this presented token is absent".
            return CryptoSessionResult.Locked();
        }

        return _store.TryGetDek(userId.Value, token, ctx.RequestAborted);
    }

    /// <inheritdoc />
    public void Touch()
    {
        var ctx = _httpAccessor.HttpContext;
        var userId = ResolveUserId(ctx);
        var token = ctx?.Request.Headers[HeaderName].ToString();
        if (userId is null || string.IsNullOrWhiteSpace(token))
        {
            return;
        }

        _store.Touch(userId.Value, token);
    }

    /// <summary>
    /// The JWT subject (JwtBearer maps <c>sub</c> onto
    /// <see cref="ClaimTypes.NameIdentifier"/>; both spellings are probed —
    /// the <see cref="RateLimitPolicies.ResolveUserKey"/> pattern), or
    /// <c>null</c> when there is no request or no parseable id.
    /// </summary>
    private static Guid? ResolveUserId(HttpContext? ctx)
    {
        if (ctx is null)
        {
            return null;
        }

        var raw = ctx.User.FindFirstValue(ClaimTypes.NameIdentifier)
            ?? ctx.User.FindFirstValue("sub");
        return Guid.TryParse(raw, out var userId) ? userId : null;
    }
}
