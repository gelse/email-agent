namespace EmailAgent.Spa.Auth;

/// <summary>
/// M1T8: the singleton in-memory access-token store (§13.10 / Q-API4).
/// The token lives in a private field for the lifetime of the WASM app and
/// is <b>never</b> written to <c>localStorage</c>, <c>sessionStorage</c> or
/// a cookie — losing it on F5 is by design; the httpOnly <c>ea_refresh</c>
/// cookie plus <see cref="RefreshCookieAuthenticator"/> restore the session.
/// </summary>
public sealed class TokenStore
{
    private readonly object _gate = new();
    private string? _token;

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

    /// <summary>Clears the token and session flags and notifies subscribers.</summary>
    public void Clear()
    {
        lock (_gate)
        {
            _token = null;
        }

        NeedsPassphraseSetup = false;
        SessionEmail = null;
        Changed?.Invoke();
    }
}
