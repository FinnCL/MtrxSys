using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MtrxSys.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddSendDeliveryAck : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "ack",
                table: "send_audit_log",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "delivered_at",
                table: "send_audit_log",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "wa_message_id",
                table: "send_audit_log",
                type: "character varying(200)",
                maxLength: 200,
                nullable: false,
                defaultValue: "");

            migrationBuilder.CreateIndex(
                name: "IX_send_audit_log_wa_message_id",
                table: "send_audit_log",
                column: "wa_message_id");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_send_audit_log_wa_message_id",
                table: "send_audit_log");

            migrationBuilder.DropColumn(
                name: "ack",
                table: "send_audit_log");

            migrationBuilder.DropColumn(
                name: "delivered_at",
                table: "send_audit_log");

            migrationBuilder.DropColumn(
                name: "wa_message_id",
                table: "send_audit_log");
        }
    }
}
