using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MtrxSys.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddDispatchTemplateId : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "template_id",
                table: "dispatch_jobs",
                type: "uuid",
                nullable: false,
                defaultValue: new Guid("00000000-0000-0000-0000-000000000000"));

            migrationBuilder.CreateIndex(
                name: "IX_dispatch_jobs_template_id",
                table: "dispatch_jobs",
                column: "template_id");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_dispatch_jobs_template_id",
                table: "dispatch_jobs");

            migrationBuilder.DropColumn(
                name: "template_id",
                table: "dispatch_jobs");
        }
    }
}
