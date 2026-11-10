using FluentAssertions;
using Npgsql;

namespace EmailAgent.IntegrationTests.Schema;

/// <summary>
/// M1T2 schema assertions: query helpers over <c>information_schema</c>,
/// <c>pg_extension</c>, <c>pg_constraint</c> and <c>pg_indexes</c> for a
/// migrated database, plus the §6 column spec every table is checked against.
/// All table/column identifiers are passed as parameters (never interpolated)
/// and dynamic SQL only ever touches <c>public</c>.
/// </summary>
internal static class SchemaAssertions
{
    /// <summary>The 12 §6 tables plus refresh_tokens (SQ-1).</summary>
    public static readonly string[] RequiredTables =
    {
        "users",
        "one_time_passwords",
        "model_catalog",
        "message_sources",
        "agent_configs",
        "action_configs",
        "scheduled_jobs",
        "processed_messages",
        "pending_actions",
        "user_notifications",
        "audit_log",
        "encrypted_blobs",
        "refresh_tokens",
    };

    private const string Uuid = "uuid";
    private const string Text = "text";
    private const string Bytea = "bytea";
    private const string Jsonb = "jsonb";
    private const string Timestamptz = "timestamp with time zone";
    private const string Inet = "inet";
    private const string Bool = "boolean";
    private const string Integer = "integer";
    private const string Bigint = "bigint";
    private const string Numeric = "numeric";
    private const string Citext = "citext";
    private const string TextArray = "ARRAY";

    private const bool NotNull = false;
    private const bool Nullable = true;

    /// <summary>
    /// table → column → (data_type, is_nullable) as §6 + the M1 decisions
    /// specify (SQ-6 revoked_at, SQ-11 key-material nullability, SQ-1).
    /// </summary>
    public static readonly IReadOnlyDictionary<string, IReadOnlyDictionary<string, (string Type, bool Nullable)>> ColumnSpec =
        new Dictionary<string, IReadOnlyDictionary<string, (string, bool)>>
        {
            ["users"] = new Dictionary<string, (string, bool)>
            {
                ["id"] = (Uuid, NotNull),
                ["email"] = (Citext, NotNull),
                ["role"] = (Text, NotNull),
                ["status"] = (Text, NotNull),
                ["password_hash"] = (Text, NotNull),
                ["must_change_password"] = (Bool, NotNull),
                ["kdf_salt"] = (Bytea, Nullable),
                ["kdf_params"] = (Jsonb, Nullable),
                ["dek_wrapped"] = (Bytea, Nullable),
                ["dek_nonce"] = (Bytea, Nullable),
                ["created_at"] = (Timestamptz, NotNull),
                ["last_login_at"] = (Timestamptz, Nullable),
                // M1T4 / SQ-10: Identity housekeeping columns (additive to §6 —
                // recorded as Resolution Q-A11 in questions/open-questions-auth.md).
                ["access_failed_count"] = (Integer, NotNull),
                ["concurrency_stamp"] = (Text, Nullable),
                ["email_confirmed"] = (Bool, NotNull),
                ["lockout_enabled"] = (Bool, NotNull),
                ["lockout_end"] = (Timestamptz, Nullable),
                ["normalized_email"] = (Text, Nullable),
                ["normalized_user_name"] = (Text, Nullable),
                ["phone_number"] = (Text, Nullable),
                ["phone_number_confirmed"] = (Bool, NotNull),
                ["security_stamp"] = (Text, Nullable),
                ["two_factor_enabled"] = (Bool, NotNull),
                ["user_name"] = (Text, Nullable),
            },
            ["one_time_passwords"] = new Dictionary<string, (string, bool)>
            {
                ["id"] = (Uuid, NotNull),
                ["user_id"] = (Uuid, NotNull),
                ["otp_hash"] = (Bytea, NotNull),
                ["expires_at"] = (Timestamptz, NotNull),
                ["used_at"] = (Timestamptz, Nullable),
                ["revoked_at"] = (Timestamptz, Nullable),
                ["created_by"] = (Uuid, NotNull),
                ["created_at"] = (Timestamptz, NotNull),
            },
            ["model_catalog"] = new Dictionary<string, (string, bool)>
            {
                ["id"] = (Uuid, NotNull),
                ["display_name"] = (Text, NotNull),
                ["provider"] = (Text, NotNull),
                ["base_url"] = (Text, NotNull),
                ["model_name"] = (Text, NotNull),
                ["api_key_secret_ref"] = (Text, Nullable),
                ["price_per_1k_input_usd"] = (Numeric, Nullable),
                ["price_per_1k_output_usd"] = (Numeric, Nullable),
                ["tool_calling_probed_at"] = (Timestamptz, Nullable),
                ["tool_calling_probe_result"] = (Text, Nullable),
                ["enabled"] = (Bool, NotNull),
                ["created_by"] = (Uuid, NotNull),
                ["created_at"] = (Timestamptz, NotNull),
            },
            ["message_sources"] = new Dictionary<string, (string, bool)>
            {
                ["id"] = (Uuid, NotNull),
                ["user_id"] = (Uuid, NotNull),
                ["type_id"] = (Text, NotNull),
                ["display_name"] = (Text, NotNull),
                ["config_blob_id"] = (Uuid, NotNull),
                ["interval_minutes"] = (Integer, NotNull),
                ["checkpoint_json"] = (Jsonb, NotNull),
                ["status"] = (Text, NotNull),
                ["last_run_at"] = (Timestamptz, Nullable),
                ["next_run_at"] = (Timestamptz, Nullable),
                ["created_at"] = (Timestamptz, NotNull),
            },
            ["agent_configs"] = new Dictionary<string, (string, bool)>
            {
                ["id"] = (Uuid, NotNull),
                ["user_id"] = (Uuid, NotNull),
                ["source_id"] = (Uuid, NotNull),
                ["model_id"] = (Uuid, NotNull),
                ["prompt_text"] = (Text, Nullable),
                ["prompt_blob_id"] = (Uuid, Nullable),
                ["prefilter_json"] = (Jsonb, NotNull),
                ["prefilter_combiner"] = (Text, NotNull),
                ["allowed_action_type_ids"] = (TextArray, NotNull),
                ["abort_on_action_failure"] = (Bool, NotNull),
                ["enabled"] = (Bool, NotNull),
            },
            ["action_configs"] = new Dictionary<string, (string, bool)>
            {
                ["id"] = (Uuid, NotNull),
                ["user_id"] = (Uuid, NotNull),
                ["type_id"] = (Text, NotNull),
                ["display_name"] = (Text, NotNull),
                ["config_blob_id"] = (Uuid, NotNull),
                ["require_confirmation"] = (Bool, NotNull),
                ["dry_run_default"] = (Bool, NotNull),
                ["created_at"] = (Timestamptz, NotNull),
            },
            ["scheduled_jobs"] = new Dictionary<string, (string, bool)>
            {
                ["id"] = (Uuid, NotNull),
                ["source_id"] = (Uuid, NotNull),
                ["scheduled_for"] = (Timestamptz, NotNull),
                ["started_at"] = (Timestamptz, Nullable),
                ["finished_at"] = (Timestamptz, Nullable),
                ["status"] = (Text, NotNull),
                ["error"] = (Text, Nullable),
                ["attempt"] = (Integer, NotNull),
                ["created_at"] = (Timestamptz, NotNull),
            },
            ["processed_messages"] = new Dictionary<string, (string, bool)>
            {
                ["id"] = (Uuid, NotNull),
                ["source_id"] = (Uuid, NotNull),
                ["remote_uid"] = (Text, NotNull),
                ["received_remote_at"] = (Timestamptz, Nullable),
                ["stage"] = (Text, NotNull),
                ["prefilter_matched"] = (Bool, NotNull),
                ["model_decision_json"] = (Jsonb, Nullable),
                ["actions_executed_json"] = (Jsonb, Nullable),
                ["prompt_tokens"] = (Integer, Nullable),
                ["completion_tokens"] = (Integer, Nullable),
                ["total_tokens"] = (Integer, Nullable),
                ["cost_usd"] = (Numeric, Nullable),
                ["body_blob_id"] = (Uuid, Nullable),
                ["created_at"] = (Timestamptz, NotNull),
            },
            ["pending_actions"] = new Dictionary<string, (string, bool)>
            {
                ["id"] = (Uuid, NotNull),
                ["processed_message_id"] = (Uuid, NotNull),
                ["action_config_id"] = (Uuid, NotNull),
                ["args_json"] = (Jsonb, NotNull),
                ["status"] = (Text, NotNull),
                ["requested_at"] = (Timestamptz, NotNull),
                ["decided_at"] = (Timestamptz, Nullable),
                ["executed_at"] = (Timestamptz, Nullable),
                ["decided_by"] = (Uuid, Nullable),
                ["expires_at"] = (Timestamptz, Nullable),
                ["result_json"] = (Jsonb, Nullable),
                ["context_json"] = (Jsonb, Nullable),
                ["created_at"] = (Timestamptz, NotNull),
            },
            ["user_notifications"] = new Dictionary<string, (string, bool)>
            {
                ["id"] = (Uuid, NotNull),
                ["user_id"] = (Uuid, NotNull),
                ["kind"] = (Text, NotNull),
                ["title"] = (Text, NotNull),
                ["body"] = (Text, Nullable),
                ["related_type"] = (Text, Nullable),
                ["related_id"] = (Uuid, Nullable),
                ["created_at"] = (Timestamptz, NotNull),
                ["read_at"] = (Timestamptz, Nullable),
            },
            ["audit_log"] = new Dictionary<string, (string, bool)>
            {
                ["id"] = (Bigint, NotNull),
                ["actor_user_id"] = (Uuid, Nullable),
                ["actor_role"] = (Text, Nullable),
                ["action"] = (Text, NotNull),
                ["target_type"] = (Text, Nullable),
                ["target_id"] = (Text, Nullable),
                ["metadata_json"] = (Jsonb, Nullable),
                ["ip"] = (Inet, Nullable),
                ["created_at"] = (Timestamptz, NotNull),
                ["hmac"] = (Bytea, NotNull),
            },
            ["encrypted_blobs"] = new Dictionary<string, (string, bool)>
            {
                ["id"] = (Uuid, NotNull),
                ["owner_user_id"] = (Uuid, Nullable),
                ["ciphertext"] = (Bytea, NotNull),
                ["nonce"] = (Bytea, NotNull),
                ["purpose"] = (Text, NotNull),
                ["created_at"] = (Timestamptz, NotNull),
                ["rotated_at"] = (Timestamptz, Nullable),
            },
            ["refresh_tokens"] = new Dictionary<string, (string, bool)>
            {
                ["id"] = (Uuid, NotNull),
                ["user_id"] = (Uuid, NotNull),
                ["family_id"] = (Uuid, NotNull),
                ["token_hash"] = (Bytea, NotNull),
                ["issued_at"] = (Timestamptz, NotNull),
                ["expires_at"] = (Timestamptz, NotNull),
                ["used_at"] = (Timestamptz, Nullable),
                ["revoked_at"] = (Timestamptz, Nullable),
                ["replaced_by_id"] = (Uuid, Nullable),
                ["user_agent"] = (Text, Nullable),
                ["ip"] = (Inet, Nullable),
            },
        };

    public static async Task AssertExtensionsInstalled(string connectionString)
    {
        var extensions = await QueryAsync(
            connectionString,
            "SELECT extname FROM pg_extension WHERE extname IN ('citext', 'pgcrypto')",
            Array.Empty<(string, object?)>(),
            reader => reader.GetString(0));

        extensions.Should().Contain("citext")
            .And.Contain("pgcrypto", "the migration must enable both extensions before creating tables");
    }

    public static async Task AssertTableExists(string connectionString, string table)
    {
        var tables = await QueryAsync(
            connectionString,
            "SELECT table_name FROM information_schema.tables WHERE table_schema = 'public' AND table_type = 'BASE TABLE'",
            Array.Empty<(string, object?)>(),
            reader => reader.GetString(0));

        tables.Should().Contain(table);
    }

    /// <summary>
    /// Every §6 column of <paramref name="table"/> exists with exactly the
    /// expected data type and nullability (no missing, no extra columns).
    /// </summary>
    public static async Task AssertColumnsMatchSpec(string connectionString, string table)
    {
        var spec = ColumnSpec[table];

        var actual = await QueryAsync(
            connectionString,
            // Extension base types (citext) report data_type = 'USER-DEFINED';
            // udt_name then carries the real type name.
            "SELECT column_name, "
                + "CASE WHEN data_type = 'USER-DEFINED' THEN udt_name ELSE data_type END, "
                + "is_nullable FROM information_schema.columns "
                + "WHERE table_schema = 'public' AND table_name = @table ORDER BY ordinal_position",
            new (string, object?)[] { ("@table", table) },
            reader => (
                Name: reader.GetString(0),
                Type: reader.GetString(1),
                Nullable: reader.GetString(2)));

        actual.Select(column => column.Name)
            .Should().BeEquivalentTo(spec.Keys, $"{table} must contain exactly the §6 columns");

        foreach (var column in actual)
        {
            var expected = spec[column.Name];
            column.Type.Should().Be(expected.Type, $"{table}.{column.Name} data type must match §6");
            column.Nullable.Should().Be(
                expected.Nullable ? "YES" : "NO",
                $"{table}.{column.Name} nullability must match §6");
        }
    }

    /// <summary>The <c>data_type</c> of one column (fails when it does not exist).</summary>
    public static async Task<string> GetColumnTypeAsync(
        string connectionString,
        string table,
        string column)
    {
        var types = await QueryAsync(
            connectionString,
            "SELECT CASE WHEN data_type = 'USER-DEFINED' THEN udt_name ELSE data_type END "
                + "FROM information_schema.columns "
                + "WHERE table_schema = 'public' AND table_name = @table AND column_name = @column",
            new (string, object?)[] { ("@table", table), ("@column", column) },
            reader => reader.GetString(0));

        types.Should().ContainSingle($"{table}.{column} must exist");
        return types[0];
    }

    public static async Task AssertColumnIsNullable(
        string connectionString,
        string table,
        string column,
        bool expectedNullable)
    {
        var rows = await QueryAsync(
            connectionString,
            "SELECT is_nullable FROM information_schema.columns "
                + "WHERE table_schema = 'public' AND table_name = @table AND column_name = @column",
            new (string, object?)[] { ("@table", table), ("@column", column) },
            reader => reader.GetString(0));

        rows.Should().ContainSingle($"{table}.{column} must exist");
        rows[0].Should().Be(expectedNullable ? "YES" : "NO", $"{table}.{column} nullability");
    }

    public static async Task AssertColumnDefaultContains(
        string connectionString,
        string table,
        string column,
        string fragment)
    {
        var rows = await QueryAsync(
            connectionString,
            "SELECT COALESCE(column_default, '') FROM information_schema.columns "
                + "WHERE table_schema = 'public' AND table_name = @table AND column_name = @column",
            new (string, object?)[] { ("@table", table), ("@column", column) },
            reader => reader.GetString(0));

        rows.Should().ContainSingle($"{table}.{column} must exist");
        rows[0].Should().Contain(fragment, $"{table}.{column} default");
    }

    /// <summary>A UNIQUE *constraint* (pg_constraint contype 'u') on exactly these columns.</summary>
    public static async Task AssertUniqueConstraint(
        string connectionString,
        string table,
        params string[] columns)
    {
        var constraints = await GetConstraintsAsync(connectionString, table);

        constraints.Where(c => c.Contype == "u")
            .Should().Contain(
                c => c.Columns.ToHashSet(StringComparer.Ordinal).SetEquals(columns),
                $"table {table} needs a UNIQUE constraint on ({string.Join(", ", columns)}) "
                    + "— checked via pg_constraint per M1T2 task step 12");
    }

    /// <summary>A CHECK constraint (contype 'c') whose expression mentions every value.</summary>
    public static async Task AssertCheckConstraintValues(
        string connectionString,
        string table,
        params string[] values)
    {
        var constraints = await GetConstraintsAsync(connectionString, table);

        constraints.Where(c => c.Contype == "c")
            .Should().Contain(
                c => values.All(value => c.Definition.Contains(value, StringComparison.Ordinal)),
                $"table {table} needs a CHECK constraint covering ({string.Join(", ", values)})");
    }

    /// <summary>An index with this name exists; optionally it must carry a WHERE clause.</summary>
    public static async Task AssertIndexExists(
        string connectionString,
        string table,
        string indexName,
        bool requireFilter)
    {
        var definitions = await QueryAsync(
            connectionString,
            "SELECT indexdef FROM pg_indexes WHERE schemaname = 'public' AND tablename = @table",
            new (string, object?)[] { ("@table", table) },
            reader => reader.GetString(0));

        var match = definitions.FirstOrDefault(
            definition => definition.Contains($"INDEX {indexName} ON", StringComparison.Ordinal)
                || definition.Contains($"INDEX \"{indexName}\" ON", StringComparison.Ordinal));

        match.Should().NotBeNullOrEmpty($"index {indexName} must exist on {table}");
        if (requireFilter)
        {
            match.Should().Contain("WHERE", $"index {indexName} must be a partial index");
        }
    }

    /// <summary>The index definition (pg_indexes.indexdef) contains this fragment — pins sort order / columns.</summary>
    public static async Task AssertIndexDefinitionContains(
        string connectionString,
        string table,
        string indexName,
        string fragment)
    {
        var definitions = await QueryAsync(
            connectionString,
            "SELECT indexdef FROM pg_indexes WHERE schemaname = 'public' AND tablename = @table",
            new (string, object?)[] { ("@table", table) },
            reader => reader.GetString(0));

        var match = definitions.FirstOrDefault(
            definition => definition.Contains($"INDEX {indexName} ON", StringComparison.Ordinal)
                || definition.Contains($"INDEX \"{indexName}\" ON", StringComparison.Ordinal));

        match.Should().NotBeNullOrEmpty($"index {indexName} must exist on {table}");
        match.Should().Contain(fragment, $"index {indexName} definition");
    }

    /// <summary>A foreign key on this column exists with the expected ON DELETE action.</summary>
    public static async Task AssertForeignKeyAction(
        string connectionString,
        string table,
        string column,
        string onDeleteAction)
    {
        var foreignKeys = await GetForeignKeyDefinitionsAsync(connectionString, table);

        foreignKeys.Should().Contain(
            definition => definition.Contains($"({column}) REFERENCES", StringComparison.Ordinal)
                && definition.Contains(onDeleteAction, StringComparison.Ordinal),
            $"table {table} needs an FK on {column} with {onDeleteAction} (design decision pinned by M1T2)");
    }

    /// <summary>No foreign key at all on this column — soft references (Q-API47 / §6).</summary>
    public static async Task AssertNoForeignKeyOnColumn(
        string connectionString,
        string table,
        string column)
    {
        var foreignKeys = await GetForeignKeyDefinitionsAsync(connectionString, table);

        foreignKeys.Should().NotContain(
            definition => definition.Contains($"({column}) REFERENCES", StringComparison.Ordinal),
            $"table {table}.{column} must stay a soft reference without an FK constraint");
    }

    private static async Task<List<string>> GetForeignKeyDefinitionsAsync(
        string connectionString,
        string table)
    {
        return await QueryAsync(
            connectionString,
            "SELECT pg_get_constraintdef(c.oid) FROM pg_constraint c "
                + "WHERE c.conrelid = to_regclass(@table) AND c.contype = 'f'",
            // Quoted so mixed-case tables (AspNet*) survive to_regclass — an
            // unquoted name is lowercased by the parser and resolves to NULL.
            new (string, object?)[] { ("@table", $"\"{table}\"") },
            reader => reader.GetString(0));
    }

    private static async Task<List<Constraint>> GetConstraintsAsync(
        string connectionString,
        string table)
    {
        return await QueryAsync(
            connectionString,
            // pg_constraint.contype is the internal "char" type — cast to text
            // so Npgsql can materialize it as a string.
            "SELECT c.conname, c.contype::text, "
                + "ARRAY(SELECT att.attname FROM unnest(c.conkey) AS key_col(attnum) "
                + "JOIN pg_attribute att ON att.attrelid = c.conrelid AND att.attnum = key_col.attnum), "
                + "COALESCE(pg_get_expr(c.conbin, c.conrelid), '') "
                + "FROM pg_constraint c WHERE c.conrelid = to_regclass(@table)",
            new (string, object?)[] { ("@table", $"\"{table}\"") },
            reader => new Constraint(
                Name: reader.GetString(0),
                Contype: reader.GetString(1),
                Columns: reader.GetFieldValue<string[]>(2),
                Definition: reader.GetString(3)));
    }

    private static async Task<List<T>> QueryAsync<T>(
        string connectionString,
        string sql,
        (string Name, object? Value)[] parameters,
        Func<NpgsqlDataReader, T> map)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();

        await using var command = new NpgsqlCommand(sql, connection);
        foreach (var (name, value) in parameters)
        {
            command.Parameters.AddWithValue(name, value ?? DBNull.Value);
        }

        List<T> results = [];
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            results.Add(map(reader));
        }

        return results;
    }

    private sealed record Constraint(string Name, string Contype, string[] Columns, string Definition);
}
