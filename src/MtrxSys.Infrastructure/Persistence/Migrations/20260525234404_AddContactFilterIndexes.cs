using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MtrxSys.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddContactFilterIndexes : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateIndex(
                name: "IX_contacts_group_tag",
                table: "contacts",
                column: "group_tag");

            migrationBuilder.CreateIndex(
                name: "IX_contacts_opt_out_at",
                table: "contacts",
                column: "opt_out_at");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_contacts_group_tag",
                table: "contacts");

            migrationBuilder.DropIndex(
                name: "IX_contacts_opt_out_at",
                table: "contacts");
        }
    }
}
