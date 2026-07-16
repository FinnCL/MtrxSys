using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MtrxSys.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class DropOwnedGroups : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "owned_group_members");

            migrationBuilder.DropTable(
                name: "owned_groups");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "owned_groups",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    dispatch_exemption_enabled = table.Column<bool>(type: "boolean", nullable: false, defaultValue: false),
                    name = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: false),
                    wa_group_id = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_owned_groups", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "owned_group_members",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    owned_group_id = table.Column<Guid>(type: "uuid", nullable: false),
                    phone_e164 = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_owned_group_members", x => x.id);
                    table.ForeignKey(
                        name: "FK_owned_group_members_owned_groups_owned_group_id",
                        column: x => x.owned_group_id,
                        principalTable: "owned_groups",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_owned_group_members_owned_group_id",
                table: "owned_group_members",
                column: "owned_group_id");

            migrationBuilder.CreateIndex(
                name: "IX_owned_group_members_phone_e164",
                table: "owned_group_members",
                column: "phone_e164");

            migrationBuilder.CreateIndex(
                name: "IX_owned_groups_wa_group_id",
                table: "owned_groups",
                column: "wa_group_id",
                unique: true);
        }
    }
}
