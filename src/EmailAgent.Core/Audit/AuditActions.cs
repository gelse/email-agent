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

    /// <summary>
    /// M2T5: <c>POST /v1/user/sources</c> created a <c>message_sources</c> row
    /// plus its <c>message_source_config</c> blob (§15). Never carries the
    /// connector config or any credential in <c>metadata_json</c>.
    /// </summary>
    public const string CreateSource = "create_source";

    /// <summary>M2T5: <c>PATCH /v1/user/sources/{id}</c>; changed field names ride in <c>metadata_json</c>.</summary>
    public const string UpdateSource = "update_source";

    /// <summary>M2T5: <c>DELETE /v1/user/sources/{id}</c> — source row + config blob in one transaction (SQ-7).</summary>
    public const string DeleteSource = "delete_source";

    /// <summary>M2T5: <c>POST /v1/user/sources/test-connection</c>; the outcome (<c>ok</c>) rides in <c>metadata_json</c>.</summary>
    public const string TestSourceConnection = "test_source_connection";

    /// <summary>M2T5: <c>POST /v1/user/sources/{id}/fetch-now</c>; counts ride in <c>metadata_json</c>.</summary>
    public const string FetchSourceNow = "fetch_source_now";

    /// <summary>
    /// M2T6: <c>POST /v1/user/account/unwrap-dek</c> failed because the
    /// passphrase did not unwrap the stored DEK. Failure-only — a successful
    /// unwrap is not audited — and <c>metadata_json</c> never carries the
    /// passphrase, the KEK or the DEK (Q-E9).
    /// </summary>
    public const string DekUnwrapFailed = "dek_unwrap_failed";

    /// <summary>
    /// M3T2 (Q-O17): the tool-calling capabilities probe rejected a model
    /// (§11 / SQ-M3-11). Failure-only — pass outcomes ride as metadata on
    /// M3T3's <c>CreateModel</c>/<c>UpdateModel</c> rows, so M3 has exactly one
    /// probe audit action. <c>target_id</c> is the model id on the PATCH path
    /// and <c>NULL</c> on the POST path (no catalog row exists yet);
    /// <c>metadata_json</c> carries the canonical request wire body and a
    /// 256-byte response excerpt — never the plaintext <c>api_key</c>.
    /// </summary>
    public const string ModelProbeFailed = "model_probe_failed";

    /// <summary>
    /// M3T3 (Q-O17): <c>POST /v1/admin/models</c> inserted a
    /// <c>model_catalog</c> row plus its system-owned <c>model_api_key</c>
    /// blob (§6/§7). <c>metadata_json</c> carries the catalog fields and the
    /// probe outcome — never the api_key (plaintext or wrapped).
    /// </summary>
    public const string CreateModel = "create_model";

    /// <summary>
    /// M3T3 (Q-O17): <c>PATCH /v1/admin/models/{id}</c>; the
    /// <c>changed_fields</c> array and the probe outcome ride in
    /// <c>metadata_json</c> — never the api_key.
    /// </summary>
    public const string UpdateModel = "update_model";

    /// <summary>
    /// M3T3 (Q-O17): <c>DELETE /v1/admin/models/{id}</c> — catalog row plus
    /// its <c>model_api_key</c> blob in one transaction.
    /// </summary>
    public const string DeleteModel = "delete_model";
}
