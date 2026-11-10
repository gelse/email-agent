namespace EmailAgent.Spa.Auth;

/// <summary>
/// M1T8: the singleton in-memory access-token store (§13.10 / Q-API4).
/// The token lives in a private field for the lifetime of the WASM app and
/// is <b>never</b> written to <c>localStorage</c>, <c>sessionStorage</c> or
/// a cookie — losing it on F5 is by design; the httpOnly <c>ea_refresh</c>
/// cookie plus <see cref="RefreshCookieAuthenticator"/> restore the session.
/// M2T6 adds the sibling <see cref="DekStore"/> sub-store for the M2-only
/// data-encryption key (SQ-12) — same lifetime rules, same "memory only".
/// </summary>
public sealed class TokenStore
{
    private readonly Lock _gate = new();
    private string? _token;

    public TokenStore()
    {
        DekStore = new DekStore();
    }

    /// <summary>
    /// M2-only (SQ-12): the unwrapped per-user data-encryption key the
    /// <c>X-User-Dek</c> transport carries. It is a sub-store of this
    /// singleton rather than its own DI service, so one lifecycle governs
    /// both values and <see cref="Clear"/> drops them together.
    /// </summary>
    public DekStore DekStore { get; }

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
    /// M2T6 review fix (CRITICAL): stores the token of a <b>fresh sign-in</b>
    /// and drops any cached DEK first, so a key unwrapped for one account can
    /// never ride along into another account's session (it would be sent as
    /// <c>X-User-Dek</c> for the wrong caller and could wrap a blob nobody can
    /// open — there is no re-wrap path in M2). Use this for login/otp-login;
    /// <see cref="RefreshCookieAuthenticator"/> uses it too when a refresh
    /// returns a token whose <c>sub</c> differs from the session's (the
    /// shared <c>ea_refresh</c> cookie can belong to an account signed in
    /// from another tab). <see cref="Set"/> stays for a same-principal
    /// refresh, which must keep its key.
    /// </summary>
    public void SignIn(string token)
    {
        DekStore.Clear();
        Set(token);
    }

    /// <summary>
    /// M2T6: caches the plaintext DEK returned by
    /// <c>POST /v1/user/account/unwrap-dek</c> for this session (thin
    /// delegate onto <see cref="DekStore"/> so callers have one obvious entry
    /// point). Never persisted; see <see cref="DekStore"/>.
    /// </summary>
    public void SetDek(string dek) => DekStore.SetDek(dek);

    /// <summary>Clears the token, session flags and DEK, and notifies subscribers.</summary>
    public void Clear()
    {
        lock (_gate)
        {
            _token = null;
        }

        NeedsPassphraseSetup = false;
        SessionEmail = null;
        DekStore.Clear();
        Changed?.Invoke();
    }
}

/// <summary>
/// M2T6 (SQ-12 / Q-E9): the in-memory home of the caller's unwrapped
/// data-encryption key — the value the M2-only <c>X-User-Dek</c> header
/// carries on <c>/v1/user/sources/*</c> requests. M2-only: M5's session-key
/// envelope (M5T1 / §13.17) removes the header and this store with it.
/// </summary>
/// <remarks>
/// The DEK is held as base64 (the header's wire form), never written to
/// <c>localStorage</c>, <c>sessionStorage</c>, IndexedDB or a cookie, and is
/// dropped by <see cref="TokenStore.Clear"/> on logout — so a page refresh
/// re-prompts for the passphrase. The SPA never derives it in the browser:
/// the passphrase goes to <c>POST /v1/user/account/unwrap-dek</c> and the
/// server unwraps the key for the signed-in caller.
/// </remarks>
public sealed class DekStore
{
    private readonly Lock _gate = new();
    private string? _dek;

    /// <summary>Base64 of the 32-byte DEK, or <c>null</c> when the session is locked.</summary>
    public string? DekBase64
    {
        get
        {
            lock (_gate)
            {
                return _dek;
            }
        }
    }

    /// <summary>True while no key is cached — the Sources page renders the passphrase prompt.</summary>
    public bool IsEmpty => string.IsNullOrWhiteSpace(DekBase64);

    /// <summary>Caches <paramref name="dek"/> for the lifetime of the session (never persisted).</summary>
    public void SetDek(string dek)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(dek);
        lock (_gate)
        {
            _dek = dek;
        }
    }

    /// <summary>Drops the key from memory (logout / account switch).</summary>
    public void Clear()
    {
        lock (_gate)
        {
            _dek = null;
        }
    }
}
