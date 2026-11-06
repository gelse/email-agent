using System;
using System.Net;
using System.Text.Json;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace EmailAgent.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class InitialSchema : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // M1T2 task step 11: extensions first, before any table that uses
            // them (`users.email` is citext; pgcrypto is required by §6 —
            // gen_random_uuid / seed tooling). Hand-inserted raw SQL; the
            // AlterDatabase() below carries the model's citext annotation.
            migrationBuilder.Sql("CREATE EXTENSION IF NOT EXISTS citext;");
            migrationBuilder.Sql("CREATE EXTENSION IF NOT EXISTS pgcrypto;");

            migrationBuilder.AlterDatabase()
                .Annotation("Npgsql:PostgresExtension:citext", ",,");

            migrationBuilder.CreateTable(
                name: "encrypted_blobs",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    owner_user_id = table.Column<Guid>(type: "uuid", nullable: true),
                    ciphertext = table.Column<byte[]>(type: "bytea", nullable: false),
                    nonce = table.Column<byte[]>(type: "bytea", nullable: false),
                    purpose = table.Column<string>(type: "text", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamptz", nullable: false, defaultValueSql: "now()"),
                    rotated_at = table.Column<DateTimeOffset>(type: "timestamptz", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_encrypted_blobs", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "users",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    email = table.Column<string>(type: "citext", nullable: false),
                    role = table.Column<string>(type: "text", nullable: false),
                    status = table.Column<string>(type: "text", nullable: false),
                    password_hash = table.Column<string>(type: "text", nullable: false),
                    must_change_password = table.Column<bool>(type: "boolean", nullable: false, defaultValue: true),
                    kdf_salt = table.Column<byte[]>(type: "bytea", nullable: true),
                    kdf_params = table.Column<JsonDocument>(type: "jsonb", nullable: true),
                    dek_wrapped = table.Column<byte[]>(type: "bytea", nullable: true),
                    dek_nonce = table.Column<byte[]>(type: "bytea", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamptz", nullable: false, defaultValueSql: "now()"),
                    last_login_at = table.Column<DateTimeOffset>(type: "timestamptz", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_users", x => x.id);
                    table.CheckConstraint("ck_users_role", "role IN ('Admin', 'User')");
                    table.CheckConstraint("ck_users_status", "status IN ('Pending', 'Active', 'Disabled')");
                });

            migrationBuilder.CreateTable(
                name: "action_configs",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    type_id = table.Column<string>(type: "text", nullable: false),
                    display_name = table.Column<string>(type: "text", nullable: false),
                    config_blob_id = table.Column<Guid>(type: "uuid", nullable: false),
                    require_confirmation = table.Column<bool>(type: "boolean", nullable: false, defaultValue: true),
                    dry_run_default = table.Column<bool>(type: "boolean", nullable: false, defaultValue: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamptz", nullable: false, defaultValueSql: "now()")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_action_configs", x => x.id);
                    table.ForeignKey(
                        name: "FK_action_configs_encrypted_blobs_config_blob_id",
                        column: x => x.config_blob_id,
                        principalTable: "encrypted_blobs",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_action_configs_users_user_id",
                        column: x => x.user_id,
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "audit_log",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    actor_user_id = table.Column<Guid>(type: "uuid", nullable: true),
                    actor_role = table.Column<string>(type: "text", nullable: true),
                    action = table.Column<string>(type: "text", nullable: false),
                    target_type = table.Column<string>(type: "text", nullable: true),
                    target_id = table.Column<string>(type: "text", nullable: true),
                    metadata_json = table.Column<string>(type: "jsonb", nullable: true),
                    ip = table.Column<IPAddress>(type: "inet", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamptz", nullable: false, defaultValueSql: "now()"),
                    hmac = table.Column<byte[]>(type: "bytea", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_audit_log", x => x.id);
                    table.ForeignKey(
                        name: "FK_audit_log_users_actor_user_id",
                        column: x => x.actor_user_id,
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "message_sources",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    type_id = table.Column<string>(type: "text", nullable: false),
                    display_name = table.Column<string>(type: "text", nullable: false),
                    config_blob_id = table.Column<Guid>(type: "uuid", nullable: false),
                    interval_minutes = table.Column<int>(type: "integer", nullable: false, defaultValue: 5),
                    checkpoint_json = table.Column<string>(type: "jsonb", nullable: false, defaultValue: "{}"),
                    status = table.Column<string>(type: "text", nullable: false),
                    last_run_at = table.Column<DateTimeOffset>(type: "timestamptz", nullable: true),
                    next_run_at = table.Column<DateTimeOffset>(type: "timestamptz", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamptz", nullable: false, defaultValueSql: "now()")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_message_sources", x => x.id);
                    table.CheckConstraint("ck_message_sources_status", "status IN ('Active', 'Paused', 'Error')");
                    table.ForeignKey(
                        name: "FK_message_sources_encrypted_blobs_config_blob_id",
                        column: x => x.config_blob_id,
                        principalTable: "encrypted_blobs",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_message_sources_users_user_id",
                        column: x => x.user_id,
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "model_catalog",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    display_name = table.Column<string>(type: "text", nullable: false),
                    provider = table.Column<string>(type: "text", nullable: false),
                    base_url = table.Column<string>(type: "text", nullable: false),
                    model_name = table.Column<string>(type: "text", nullable: false),
                    api_key_secret_ref = table.Column<string>(type: "text", nullable: true),
                    price_per_1k_input_usd = table.Column<decimal>(type: "numeric", nullable: true),
                    price_per_1k_output_usd = table.Column<decimal>(type: "numeric", nullable: true),
                    tool_calling_probed_at = table.Column<DateTimeOffset>(type: "timestamptz", nullable: true),
                    tool_calling_probe_result = table.Column<string>(type: "text", nullable: true),
                    enabled = table.Column<bool>(type: "boolean", nullable: false, defaultValue: true),
                    created_by = table.Column<Guid>(type: "uuid", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamptz", nullable: false, defaultValueSql: "now()")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_model_catalog", x => x.id);
                    table.CheckConstraint("ck_model_catalog_tool_calling_probe_result", "tool_calling_probe_result IN ('Pass', 'Fail')");
                    table.ForeignKey(
                        name: "FK_model_catalog_users_created_by",
                        column: x => x.created_by,
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "one_time_passwords",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    otp_hash = table.Column<byte[]>(type: "bytea", nullable: false),
                    expires_at = table.Column<DateTimeOffset>(type: "timestamptz", nullable: false),
                    used_at = table.Column<DateTimeOffset>(type: "timestamptz", nullable: true),
                    revoked_at = table.Column<DateTimeOffset>(type: "timestamptz", nullable: true),
                    created_by = table.Column<Guid>(type: "uuid", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamptz", nullable: false, defaultValueSql: "now()")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_one_time_passwords", x => x.id);
                    table.ForeignKey(
                        name: "FK_one_time_passwords_users_user_id",
                        column: x => x.user_id,
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "refresh_tokens",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    family_id = table.Column<Guid>(type: "uuid", nullable: false),
                    token_hash = table.Column<byte[]>(type: "bytea", nullable: false),
                    issued_at = table.Column<DateTimeOffset>(type: "timestamptz", nullable: false),
                    expires_at = table.Column<DateTimeOffset>(type: "timestamptz", nullable: false),
                    used_at = table.Column<DateTimeOffset>(type: "timestamptz", nullable: true),
                    revoked_at = table.Column<DateTimeOffset>(type: "timestamptz", nullable: true),
                    replaced_by_id = table.Column<Guid>(type: "uuid", nullable: true),
                    user_agent = table.Column<string>(type: "text", nullable: true),
                    ip = table.Column<IPAddress>(type: "inet", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_refresh_tokens", x => x.id);
                    table.ForeignKey(
                        name: "FK_refresh_tokens_refresh_tokens_replaced_by_id",
                        column: x => x.replaced_by_id,
                        principalTable: "refresh_tokens",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_refresh_tokens_users_user_id",
                        column: x => x.user_id,
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "user_notifications",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    kind = table.Column<string>(type: "text", nullable: false),
                    title = table.Column<string>(type: "text", nullable: false),
                    body = table.Column<string>(type: "text", nullable: true),
                    related_type = table.Column<string>(type: "text", nullable: true),
                    related_id = table.Column<Guid>(type: "uuid", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamptz", nullable: false, defaultValueSql: "now()"),
                    read_at = table.Column<DateTimeOffset>(type: "timestamptz", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_user_notifications", x => x.id);
                    table.ForeignKey(
                        name: "FK_user_notifications_users_user_id",
                        column: x => x.user_id,
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "processed_messages",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    source_id = table.Column<Guid>(type: "uuid", nullable: false),
                    remote_uid = table.Column<string>(type: "text", nullable: false),
                    received_remote_at = table.Column<DateTimeOffset>(type: "timestamptz", nullable: true),
                    stage = table.Column<string>(type: "text", nullable: false, defaultValue: "fetched"),
                    prefilter_matched = table.Column<bool>(type: "boolean", nullable: false, defaultValue: false),
                    model_decision_json = table.Column<string>(type: "jsonb", nullable: true),
                    actions_executed_json = table.Column<string>(type: "jsonb", nullable: true),
                    prompt_tokens = table.Column<int>(type: "integer", nullable: true),
                    completion_tokens = table.Column<int>(type: "integer", nullable: true),
                    total_tokens = table.Column<int>(type: "integer", nullable: true),
                    cost_usd = table.Column<decimal>(type: "numeric", nullable: true),
                    body_blob_id = table.Column<Guid>(type: "uuid", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamptz", nullable: false, defaultValueSql: "now()")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_processed_messages", x => x.id);
                    table.ForeignKey(
                        name: "FK_processed_messages_encrypted_blobs_body_blob_id",
                        column: x => x.body_blob_id,
                        principalTable: "encrypted_blobs",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_processed_messages_message_sources_source_id",
                        column: x => x.source_id,
                        principalTable: "message_sources",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "scheduled_jobs",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    source_id = table.Column<Guid>(type: "uuid", nullable: false),
                    scheduled_for = table.Column<DateTimeOffset>(type: "timestamptz", nullable: false),
                    started_at = table.Column<DateTimeOffset>(type: "timestamptz", nullable: true),
                    finished_at = table.Column<DateTimeOffset>(type: "timestamptz", nullable: true),
                    status = table.Column<string>(type: "text", nullable: false),
                    error = table.Column<string>(type: "text", nullable: true),
                    attempt = table.Column<int>(type: "integer", nullable: false, defaultValue: 0),
                    created_at = table.Column<DateTimeOffset>(type: "timestamptz", nullable: false, defaultValueSql: "now()")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_scheduled_jobs", x => x.id);
                    table.CheckConstraint("ck_scheduled_jobs_status", "status IN ('Pending', 'Running', 'Succeeded', 'Failed', 'DeadLettered')");
                    table.ForeignKey(
                        name: "FK_scheduled_jobs_message_sources_source_id",
                        column: x => x.source_id,
                        principalTable: "message_sources",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "agent_configs",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    source_id = table.Column<Guid>(type: "uuid", nullable: false),
                    model_id = table.Column<Guid>(type: "uuid", nullable: false),
                    prompt_text = table.Column<string>(type: "text", nullable: true),
                    prompt_blob_id = table.Column<Guid>(type: "uuid", nullable: true),
                    prefilter_json = table.Column<string>(type: "jsonb", nullable: false, defaultValue: "[]"),
                    prefilter_combiner = table.Column<string>(type: "text", nullable: false, defaultValue: "OR"),
                    allowed_action_type_ids = table.Column<string[]>(type: "text[]", nullable: false, defaultValueSql: "'{}'"),
                    abort_on_action_failure = table.Column<bool>(type: "boolean", nullable: false, defaultValue: false),
                    enabled = table.Column<bool>(type: "boolean", nullable: false, defaultValue: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_agent_configs", x => x.id);
                    table.ForeignKey(
                        name: "FK_agent_configs_encrypted_blobs_prompt_blob_id",
                        column: x => x.prompt_blob_id,
                        principalTable: "encrypted_blobs",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_agent_configs_message_sources_source_id",
                        column: x => x.source_id,
                        principalTable: "message_sources",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_agent_configs_model_catalog_model_id",
                        column: x => x.model_id,
                        principalTable: "model_catalog",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_agent_configs_users_user_id",
                        column: x => x.user_id,
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "pending_actions",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    processed_message_id = table.Column<Guid>(type: "uuid", nullable: false),
                    action_config_id = table.Column<Guid>(type: "uuid", nullable: false),
                    args_json = table.Column<string>(type: "jsonb", nullable: false),
                    status = table.Column<string>(type: "text", nullable: false, defaultValue: "Pending"),
                    requested_at = table.Column<DateTimeOffset>(type: "timestamptz", nullable: false),
                    decided_at = table.Column<DateTimeOffset>(type: "timestamptz", nullable: true),
                    executed_at = table.Column<DateTimeOffset>(type: "timestamptz", nullable: true),
                    decided_by = table.Column<Guid>(type: "uuid", nullable: true),
                    expires_at = table.Column<DateTimeOffset>(type: "timestamptz", nullable: true),
                    result_json = table.Column<string>(type: "jsonb", nullable: true),
                    context_json = table.Column<string>(type: "jsonb", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamptz", nullable: false, defaultValueSql: "now()")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_pending_actions", x => x.id);
                    table.CheckConstraint("ck_pending_actions_status", "status IN ('Pending', 'Approved', 'Rejected', 'Executed', 'Expired')");
                    table.ForeignKey(
                        name: "FK_pending_actions_action_configs_action_config_id",
                        column: x => x.action_config_id,
                        principalTable: "action_configs",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_pending_actions_processed_messages_processed_message_id",
                        column: x => x.processed_message_id,
                        principalTable: "processed_messages",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_pending_actions_users_decided_by",
                        column: x => x.decided_by,
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_action_configs_config_blob_id",
                table: "action_configs",
                column: "config_blob_id");

            migrationBuilder.CreateIndex(
                name: "IX_action_configs_user_id",
                table: "action_configs",
                column: "user_id");

            migrationBuilder.CreateIndex(
                name: "IX_agent_configs_model_id",
                table: "agent_configs",
                column: "model_id");

            migrationBuilder.CreateIndex(
                name: "IX_agent_configs_prompt_blob_id",
                table: "agent_configs",
                column: "prompt_blob_id");

            migrationBuilder.CreateIndex(
                name: "IX_agent_configs_source_id",
                table: "agent_configs",
                column: "source_id");

            migrationBuilder.CreateIndex(
                name: "IX_agent_configs_user_id",
                table: "agent_configs",
                column: "user_id");

            migrationBuilder.CreateIndex(
                name: "ix_audit_log_action",
                table: "audit_log",
                column: "action");

            migrationBuilder.CreateIndex(
                name: "ix_audit_log_actor_created_at",
                table: "audit_log",
                columns: new[] { "actor_user_id", "created_at" },
                descending: new[] { false, true });

            migrationBuilder.CreateIndex(
                name: "ix_audit_log_created_at",
                table: "audit_log",
                column: "created_at",
                descending: new bool[0]);

            migrationBuilder.CreateIndex(
                name: "IX_message_sources_config_blob_id",
                table: "message_sources",
                column: "config_blob_id");

            migrationBuilder.CreateIndex(
                name: "IX_message_sources_user_id",
                table: "message_sources",
                column: "user_id");

            migrationBuilder.CreateIndex(
                name: "IX_model_catalog_created_by",
                table: "model_catalog",
                column: "created_by");

            migrationBuilder.CreateIndex(
                name: "ix_one_time_passwords_user_created",
                table: "one_time_passwords",
                columns: new[] { "user_id", "created_at" },
                descending: new[] { false, true });

            migrationBuilder.CreateIndex(
                name: "IX_pending_actions_action_config_id",
                table: "pending_actions",
                column: "action_config_id");

            migrationBuilder.CreateIndex(
                name: "IX_pending_actions_decided_by",
                table: "pending_actions",
                column: "decided_by");

            migrationBuilder.CreateIndex(
                name: "IX_pending_actions_processed_message_id",
                table: "pending_actions",
                column: "processed_message_id");

            migrationBuilder.CreateIndex(
                name: "IX_processed_messages_body_blob_id",
                table: "processed_messages",
                column: "body_blob_id");

            migrationBuilder.CreateIndex(
                name: "ix_processed_messages_source_created",
                table: "processed_messages",
                columns: new[] { "source_id", "created_at" },
                descending: new[] { false, true });

            // M1T2 task steps 11–12: the three §6 UNIQUE constraints are real
            // pg_constraint entries (contype 'u'), not just unique indexes, so
            // the EF-generated CreateIndex(unique: true) calls are replaced
            // with ALTER TABLE ... ADD CONSTRAINT here (the model keeps its
            // unique index; the constraint creates the backing index with the
            // same name). Down() needs no counterpart — DROP TABLE removes
            // the constraints.
            migrationBuilder.Sql(
                "ALTER TABLE \"processed_messages\" ADD CONSTRAINT \"ux_processed_messages_source_remote_uid\" UNIQUE (\"source_id\", \"remote_uid\");");

            migrationBuilder.Sql(
                "ALTER TABLE \"refresh_tokens\" ADD CONSTRAINT \"ux_refresh_tokens_hash\" UNIQUE (\"token_hash\");");

            migrationBuilder.Sql(
                "ALTER TABLE \"users\" ADD CONSTRAINT \"ux_users_email\" UNIQUE (\"email\");");

            migrationBuilder.CreateIndex(
                name: "IX_refresh_tokens_replaced_by_id",
                table: "refresh_tokens",
                column: "replaced_by_id");

            migrationBuilder.CreateIndex(
                name: "ix_refresh_tokens_family",
                table: "refresh_tokens",
                column: "family_id");

            migrationBuilder.CreateIndex(
                name: "ix_refresh_tokens_user",
                table: "refresh_tokens",
                column: "user_id");

            migrationBuilder.CreateIndex(
                name: "IX_scheduled_jobs_source_id",
                table: "scheduled_jobs",
                column: "source_id");

            migrationBuilder.CreateIndex(
                name: "ix_user_notifications_user_created",
                table: "user_notifications",
                columns: new[] { "user_id", "created_at" },
                descending: new[] { false, true });

            // M1T2 task step 11: partial indexes — EF's fluent API cannot
            // express them in this migration without diverging from the model,
            // so they are emitted as raw SQL (dropped with their tables on
            // Down()). §6 query patterns: queue scans, unread list, recency.
            migrationBuilder.Sql(
                "CREATE INDEX ix_pending_actions_status_requested ON pending_actions (status, requested_at) WHERE status = 'Pending';");

            migrationBuilder.Sql(
                "CREATE INDEX ix_scheduled_jobs_status_scheduled_for ON scheduled_jobs (status, scheduled_for) WHERE status = 'Pending';");

            migrationBuilder.Sql(
                "CREATE INDEX ix_user_notifications_unread ON user_notifications (user_id, created_at DESC) WHERE read_at IS NULL;");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "agent_configs");

            migrationBuilder.DropTable(
                name: "audit_log");

            migrationBuilder.DropTable(
                name: "one_time_passwords");

            migrationBuilder.DropTable(
                name: "pending_actions");

            migrationBuilder.DropTable(
                name: "refresh_tokens");

            migrationBuilder.DropTable(
                name: "scheduled_jobs");

            migrationBuilder.DropTable(
                name: "user_notifications");

            migrationBuilder.DropTable(
                name: "model_catalog");

            migrationBuilder.DropTable(
                name: "action_configs");

            migrationBuilder.DropTable(
                name: "processed_messages");

            migrationBuilder.DropTable(
                name: "message_sources");

            migrationBuilder.DropTable(
                name: "encrypted_blobs");

            migrationBuilder.DropTable(
                name: "users");

            // Mirror of the hand-inserted CREATE EXTENSION statements in Up();
            // runs last so no citext column still depends on the extension.
            migrationBuilder.Sql("DROP EXTENSION IF EXISTS citext;");
            migrationBuilder.Sql("DROP EXTENSION IF EXISTS pgcrypto;");
        }
    }
}
