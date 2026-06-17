using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MtrxSys.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddGroupLinkJoinedAt : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "joined_at",
                table: "group_links",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_group_links_joined_at",
                table: "group_links",
                column: "joined_at");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_group_links_joined_at",
                table: "group_links");

            migrationBuilder.DropColumn(
                name: "joined_at",
                table: "group_links");
        }
    }
}
