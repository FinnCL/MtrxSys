using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MtrxSys.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddOwnedGroupDispatchExemption : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "dispatch_exemption_enabled",
                table: "owned_groups",
                type: "boolean",
                nullable: false,
                defaultValue: false);

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
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "owned_group_members");

            migrationBuilder.DropColumn(
                name: "dispatch_exemption_enabled",
                table: "owned_groups");
        }
    }
}
