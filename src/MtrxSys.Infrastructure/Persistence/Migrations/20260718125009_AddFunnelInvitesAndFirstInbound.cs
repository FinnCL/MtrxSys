using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MtrxSys.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddFunnelInvitesAndFirstInbound : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "first_inbound_at",
                table: "contacts",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "funnel_invites",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    contact_id = table.Column<Guid>(type: "uuid", nullable: false),
                    prefill_text = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true),
                    auto_reply_text = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    engaged_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    auto_replied_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_funnel_invites", x => x.id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_funnel_invites_contact_id",
                table: "funnel_invites",
                column: "contact_id");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "funnel_invites");

            migrationBuilder.DropColumn(
                name: "first_inbound_at",
                table: "contacts");
        }
    }
}
