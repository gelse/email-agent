namespace EmailAgent.Core.Audit;

/// <summary>
/// Canonical <c>audit_log.action</c> values the M1 surface emits (task step 9;
/// M1T6/M1T7/M1T9 fill in more). Kept as constants so the writer, the
/// verifier and the tests share one spelling.
/// </summary>
public static class AuditActions
{
    public const string LoginSuccess = "login_success";
    public const string LoginFailure = "login_failure";
    public const string RefreshSuccess = "refresh_success";
    public const string RefreshReuseDetected = "refresh_reuse_detected";
    public const string Logout = "logout";
    public const string ChangePassword = "change_password";
    public const string SetPassphrase = "set_passphrase";
    public const string OtpLoginSuccess = "otp_login_success";
    public const string OtpLoginFailure = "otp_login_failure";
    public const string AdminCreateUser = "admin_create_user";
    public const string AdminOtpIssued = "admin_otp_issued";

    /// <summary>M1T7: <c>GET /v1/admin/users</c> (the query parameters ride in <c>metadata_json</c>).</summary>
    public const string AdminListUsers = "admin_list_users";

    /// <summary>M1T7: <c>GET /v1/admin/users/{id}</c>.</summary>
    public const string AdminReadUser = "admin_read_user";

    /// <summary>
    /// M2T3: a plugin passed every load gate (signature or trust anchor,
    /// approval, manifest validation) and was registered (§9 / Q-O5). Emitted
    /// with a NULL actor — system event.
    /// </summary>
    public const string PluginLoaded = "plugin_loaded";

    /// <summary>
    /// M2T3: the loader refused a plugin; the reason (<c>unsigned</c>,
    /// <c>signature_mismatch</c>, <c>not_approved</c>, <c>manifest_invalid</c>,
    /// <c>load_failed</c>) rides in <c>metadata_json</c>. System event.
    /// </summary>
    public const string PluginRejected = "plugin_rejected";
}
