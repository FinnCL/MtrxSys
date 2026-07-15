using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MtrxSys.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddWarmupCircleAndChatMessageTimestampIndex : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "warmup_circle",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    phone_e164 = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    name = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: true),
                    added_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_warmup_circle", x => x.id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_chat_messages_timestamp",
                table: "chat_messages",
                column: "timestamp");

            migrationBuilder.CreateIndex(
                name: "IX_warmup_circle_phone_e164",
                table: "warmup_circle",
                column: "phone_e164",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "warmup_circle");

            migrationBuilder.DropIndex(
                name: "IX_chat_messages_timestamp",
                table: "chat_messages");
        }
    }
}
