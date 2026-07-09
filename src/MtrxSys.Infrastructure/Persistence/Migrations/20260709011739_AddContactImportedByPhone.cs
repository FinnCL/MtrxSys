using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MtrxSys.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddContactImportedByPhone : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "imported_by_phone",
                table: "contacts",
                type: "character varying(20)",
                maxLength: 20,
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "imported_by_phone",
                table: "contacts");
        }
    }
}
