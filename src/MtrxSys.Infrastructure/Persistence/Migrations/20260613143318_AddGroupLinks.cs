using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MtrxSys.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddGroupLinks : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "group_links",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    invite_code = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    invite_url = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    source_channel = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: true),
                    group_name = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: true),
                    participant_count = table.Column<int>(type: "integer", nullable: true),
                    status = table.Column<int>(type: "integer", nullable: false),
                    matched_keyword = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: true),
                    whatsapp_group_id = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: true),
                    collected_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    resolved_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_group_links", x => x.id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_group_links_invite_code",
                table: "group_links",
                column: "invite_code",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_group_links_matched_keyword",
                table: "group_links",
                column: "matched_keyword");

            migrationBuilder.CreateIndex(
                name: "IX_group_links_status",
                table: "group_links",
                column: "status");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "group_links");
        }
    }
}
