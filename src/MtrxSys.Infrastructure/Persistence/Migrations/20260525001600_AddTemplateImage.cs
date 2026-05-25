using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MtrxSys.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddTemplateImage : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<byte[]>(
                name: "image_data",
                table: "message_templates",
                type: "bytea",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "image_mime_type",
                table: "message_templates",
                type: "text",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "image_data",
                table: "message_templates");

            migrationBuilder.DropColumn(
                name: "image_mime_type",
                table: "message_templates");
        }
    }
}
