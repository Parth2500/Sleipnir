using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Sleipnir.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class Initial : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "users",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "TEXT", nullable: false),
                    display_name = table.Column<string>(type: "TEXT", maxLength: 40, nullable: false, collation: "NOCASE"),
                    role = table.Column<string>(type: "TEXT", nullable: false),
                    created_at = table.Column<DateTime>(type: "TEXT", nullable: false),
                    allowed_root = table.Column<string>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_users", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "devices",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "TEXT", nullable: false),
                    user_id = table.Column<Guid>(type: "TEXT", nullable: false),
                    name = table.Column<string>(type: "TEXT", nullable: false),
                    platform = table.Column<string>(type: "TEXT", nullable: false),
                    token_hash = table.Column<string>(type: "TEXT", nullable: false),
                    created_at = table.Column<DateTime>(type: "TEXT", nullable: false),
                    last_seen_at = table.Column<DateTime>(type: "TEXT", nullable: true),
                    revoked_at = table.Column<DateTime>(type: "TEXT", nullable: true),
                    is_local = table.Column<bool>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_devices", x => x.id);
                    table.ForeignKey(
                        name: "fk_devices_users_user_id",
                        column: x => x.user_id,
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "ix_devices_token_hash",
                table: "devices",
                column: "token_hash",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_devices_user_id",
                table: "devices",
                column: "user_id");

            migrationBuilder.CreateIndex(
                name: "ix_users_display_name",
                table: "users",
                column: "display_name",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "devices");

            migrationBuilder.DropTable(
                name: "users");
        }
    }
}
