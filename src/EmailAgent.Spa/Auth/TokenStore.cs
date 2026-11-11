namespace EmailAgent.Spa.Auth;

/// <summary>
/// M1T8: the singleton in-memory access-token store (§13.10 / Q-API4).
/// The token lives in a private field for the lifetime of the WASM app and
/// is <b>never</b> written to <c>localStorage</c>, <c>sessionStorage</c> or
/// a cookie — losing it on F5 is by design; the httpOnly <c>ea_refresh</c>
/// cookie plus <see cref="RefreshCookieAuthenticator"/> restore the session.
/// M5T1 replaces M2T6's <c>DekStore</c> sub-store with the sibling
/// <see cref="CryptoSessionStore"/> slot holding the vault-unlock
/// <c>X-Crypto-Session</c> token (SQ-M5-1 / §13.17) — same lifetime rules,
/// same "memory only"; the token is lost on page refresh (re-prompt; the
/// default explicitly does NOT take <c>sessionStorage</c>).
/// </summary>
public sealed class TokenStore
{
    private readonly Lock _gate = new();
    private string? _token;

    /// <summary>
    /// M5T1 (SQ-M5-1): the opaque session-key token returned once by
    /// <c>POST /v1/account/unlock</c>. It is a sub-store of this singleton
    /// rather than its own DI service, so one lifecycle governs both values
    /// and <see cref="Clear"/> drops them together.
    /// </summary>
    public CryptoSessionStore CryptoSession { get; } = new();

    /// <summary>The current bearer access token, or <c>null</c> when signed out.</summary>
    public string? AccessToken
    {
        get
        {
            lock (_gate)
            {
                return _token;
            }
        }
    }

    /// <summary>
    /// Session flag retained from the login/otp-login response
    /// (<c>must_complete_first_login</c>): after the forced change-password
    /// the SPA still owes the user the set-passphrase screen when
    /// <c>dek_wrapped</c> was NULL at login time (task step 10).
    /// </summary>
    public bool NeedsPassphraseSetup { get; set; }

    /// <summary>The signed-in user's email as typed at login (top-bar display; the M1 JWT carries no email claim).</summary>
    public string? SessionEmail { get; set; }

    /// <summary>Raised whenever the token or session state changes.</summary>
    public event Action? Changed;

    /// <summary>Stores <paramref name="token"/> and notifies subscribers.</summary>
    public void Set(string token)
    {
        ArgumentNullException.ThrowIfNull(token);
        lock (_gate)
        {
            _token = token;
        }

        Changed?.Invoke();
    }

    /// <summary>
    /// M2T6 review fix (CRITICAL), carried into M5T1: stores the token of a
    /// <b>fresh sign-in</b> and drops the cached crypto-session token first,
    /// so a vault unlock minted for one account can never ride along into
    /// another account's session (it would be presented as
    /// <c>X-Crypto-Session</c> for the wrong caller and resolve to nothing —
    /// the server keys sessions by user). Use this for login/otp-login;
    /// <see cref="RefreshCookieAuthenticator"/> uses it too when a refresh
    /// returns a token whose <c>sub</c> differs from the session's (the
    /// shared <c>ea_refresh</c> cookie can belong to an account signed in
    /// from another tab). <see cref="Set"/> stays for a same-principal
    /// refresh, which must keep its unlock.
    /// </summary>
    public void SignIn(string token)
    {
        CryptoSession.Clear();
        Set(token);
    }

    /// <summary>Clears the token, session flags and crypto-session, and notifies subscribers.</summary>
    public void Clear()
    {
        lock (_gate)
        {
            _token = null;
        }

        NeedsPassphraseSetup = false;
        SessionEmail = null;
        CryptoSession.Clear();
        Changed?.Invoke();
    }
}

/// <summary>
/// M5T1 (SQ-M5-1 / §13.17): the in-memory home of the vault-unlock
/// <c>X-Crypto-Session</c> token — the credential every <c>/v1/user/*</c>
/// request carries beside the JWT. M2T6's <c>DekStore</c> is gone with the
/// <c>X-User-Dek</c> header: the DEK itself never reaches the browser again
/// after unlock; the browser POSTs it once to <c>POST /v1/account/unlock</c>
/// and keeps only this opaque token.
/// </summary>
/// <remarks>
/// The token is held as the opaque string the server returned, never written
/// to <c>localStorage</c>, <c>sessionStorage</c>, IndexedDB or a cookie, and
/// is dropped by <see cref="TokenStore.Clear"/> on logout and by
/// <see cref="TokenStore.SignIn"/> on an account switch — so a page refresh
/// re-prompts for the passphrase (the DEFAULT per SQ-M5-1: no persistence
/// path). The server-side session lives in the API's in-process
/// <c>ICryptoSessionStore</c> and expires on its own idle/absolute timeouts.
/// </remarks>
public sealed class CryptoSessionStore
{
    private readonly Lock _gate = new();
    private string? _token;

    /// <summary>The opaque session-key token, or <c>null</c> when the vault is locked.</summary>
    public string? Token
    {
        get
        {
            lock (_gate)
            {
                return _token;
            }
        }
    }

    /// <summary>True while no token is held — the Sources/Actions pages render the unlock prompt.</summary>
    public bool IsEmpty => string.IsNullOrWhiteSpace(Token);

    /// <summary>Caches <paramref name="token"/> for the lifetime of the session (never persisted).</summary>
    public void SetToken(string token)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(token);
        lock (_gate)
        {
            _token = token;
        }
    }

    /// <summary>Drops the token from memory (logout / account switch / re-lock).</summary>
    public void Clear()
    {
        lock (_gate)
        {
            _token = null;
        }
    }
}
