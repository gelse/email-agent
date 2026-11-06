using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace EmailAgent.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class PluginSchema : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "plugin_approvals",
                columns: table => new
                {
                    plugin_id = table.Column<string>(type: "text", nullable: false),
                    version = table.Column<string>(type: "text", nullable: false),
                    approved_by_user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    approved_at = table.Column<DateTimeOffset>(type: "timestamptz", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_plugin_approvals", x => new { x.plugin_id, x.version });
                    table.ForeignKey(
                        name: "FK_plugin_approvals_users_approved_by_user_id",
                        column: x => x.approved_by_user_id,
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "plugin_signatures",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    plugin_id = table.Column<string>(type: "text", nullable: false),
                    version = table.Column<string>(type: "text", nullable: false),
                    assembly_sha256 = table.Column<byte[]>(type: "bytea", nullable: false),
                    signature = table.Column<byte[]>(type: "bytea", nullable: false),
                    first_seen_at = table.Column<DateTimeOffset>(type: "timestamptz", nullable: false),
                    last_seen_at = table.Column<DateTimeOffset>(type: "timestamptz", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_plugin_signatures", x => x.id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_plugin_approvals_approved_by_user_id",
                table: "plugin_approvals",
                column: "approved_by_user_id");

            // M2T3/SQ-4: the (plugin_id, version) uniqueness is a real
            // pg_constraint entry (contype 'u'), not just a unique index, so
            // the EF-generated CreateIndex(unique: true) call is replaced with
            // ALTER TABLE ... ADD CONSTRAINT here (the model keeps its unique
            // index; the constraint creates the backing index with the same
            // name). Down() needs no counterpart — DROP TABLE removes the
            // constraint.
            migrationBuilder.Sql(
                "ALTER TABLE \"plugin_signatures\" ADD CONSTRAINT \"ux_plugin_signatures_plugin_id_version\" UNIQUE (\"plugin_id\", \"version\");");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "plugin_approvals");

            migrationBuilder.DropTable(
                name: "plugin_signatures");
        }
    }
}
