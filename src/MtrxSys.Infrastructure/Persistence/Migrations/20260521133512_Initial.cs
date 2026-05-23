using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MtrxSys.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class Initial : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "contacts",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    phone_e164 = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    group_tag = table.Column<string>(type: "character varying(80)", maxLength: 80, nullable: true),
                    theme = table.Column<string>(type: "character varying(80)", maxLength: 80, nullable: true),
                    opt_in_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    opt_out_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    last_sent_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_contacts", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "daily_send_counts",
                columns: table => new
                {
                    date_utc = table.Column<DateOnly>(type: "date", nullable: false),
                    sent_count = table.Column<int>(type: "integer", nullable: false, defaultValue: 0),
                    warmup_day_index = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_daily_send_counts", x => x.date_utc);
                });

            migrationBuilder.CreateTable(
                name: "dispatch_jobs",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    contact_id = table.Column<Guid>(type: "uuid", nullable: false),
                    status = table.Column<int>(type: "integer", nullable: false),
                    scheduled_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    sent_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    waha_message_id = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: true),
                    error_reason = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_dispatch_jobs", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "message_templates",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    slot = table.Column<int>(type: "integer", nullable: false),
                    content_spintax = table.Column<string>(type: "text", nullable: false),
                    active = table.Column<bool>(type: "boolean", nullable: false, defaultValue: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_message_templates", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "send_audit_log",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    dispatch_job_id = table.Column<Guid>(type: "uuid", nullable: false),
                    phone_e164 = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    rendered_text = table.Column<string>(type: "text", nullable: false),
                    typing_ms = table.Column<int>(type: "integer", nullable: false),
                    delay_ms = table.Column<int>(type: "integer", nullable: false),
                    occurred_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_send_audit_log", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "system_state",
                columns: table => new
                {
                    id = table.Column<int>(type: "integer", nullable: false),
                    consecutive_failures = table.Column<int>(type: "integer", nullable: false, defaultValue: 0),
                    circuit_open_until = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    paused_reason = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_system_state", x => x.id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_contacts_phone_e164",
                table: "contacts",
                column: "phone_e164",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_dispatch_jobs_status_scheduled_at",
                table: "dispatch_jobs",
                columns: new[] { "status", "scheduled_at" });

            migrationBuilder.CreateIndex(
                name: "IX_message_templates_slot_active",
                table: "message_templates",
                columns: new[] { "slot", "active" });

            migrationBuilder.CreateIndex(
                name: "IX_send_audit_log_occurred_at",
                table: "send_audit_log",
                column: "occurred_at");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "contacts");

            migrationBuilder.DropTable(
                name: "daily_send_counts");

            migrationBuilder.DropTable(
                name: "dispatch_jobs");

            migrationBuilder.DropTable(
                name: "message_templates");

            migrationBuilder.DropTable(
                name: "send_audit_log");

            migrationBuilder.DropTable(
                name: "system_state");
        }
    }
}
