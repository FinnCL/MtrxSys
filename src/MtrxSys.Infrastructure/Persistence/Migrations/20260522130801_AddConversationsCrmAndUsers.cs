using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MtrxSys.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddConversationsCrmAndUsers : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "stage",
                table: "contacts",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "stage_changed_at",
                table: "contacts",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "contact_notes",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    contact_id = table.Column<Guid>(type: "uuid", nullable: false),
                    body = table.Column<string>(type: "text", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    created_by_user_id = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_contact_notes", x => x.id);
                    table.ForeignKey(
                        name: "FK_contact_notes_contacts_contact_id",
                        column: x => x.contact_id,
                        principalTable: "contacts",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "contact_stage_changes",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    contact_id = table.Column<Guid>(type: "uuid", nullable: false),
                    from_stage = table.Column<int>(type: "integer", nullable: true),
                    to_stage = table.Column<int>(type: "integer", nullable: false),
                    changed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    changed_by_user_id = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_contact_stage_changes", x => x.id);
                    table.ForeignKey(
                        name: "FK_contact_stage_changes_contacts_contact_id",
                        column: x => x.contact_id,
                        principalTable: "contacts",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "contact_tags",
                columns: table => new
                {
                    name = table.Column<string>(type: "character varying(60)", maxLength: 60, nullable: false),
                    color = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_contact_tags", x => x.name);
                });

            migrationBuilder.CreateTable(
                name: "conversations",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    wa_chat_id = table.Column<string>(type: "character varying(80)", maxLength: 80, nullable: false),
                    contact_id = table.Column<Guid>(type: "uuid", nullable: true),
                    title = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    is_group = table.Column<bool>(type: "boolean", nullable: false),
                    last_message_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    last_message_preview = table.Column<string>(type: "character varying(320)", maxLength: 320, nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_conversations", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "users",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    email = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    password_hash = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: false),
                    display_name = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    last_login_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_users", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "contact_tag_assignments",
                columns: table => new
                {
                    contact_id = table.Column<Guid>(type: "uuid", nullable: false),
                    tag_name = table.Column<string>(type: "character varying(60)", maxLength: 60, nullable: false),
                    assigned_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_contact_tag_assignments", x => new { x.contact_id, x.tag_name });
                    table.ForeignKey(
                        name: "FK_contact_tag_assignments_contact_tags_tag_name",
                        column: x => x.tag_name,
                        principalTable: "contact_tags",
                        principalColumn: "name",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_contact_tag_assignments_contacts_contact_id",
                        column: x => x.contact_id,
                        principalTable: "contacts",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "chat_messages",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    conversation_id = table.Column<Guid>(type: "uuid", nullable: false),
                    wa_message_id = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: false),
                    direction = table.Column<int>(type: "integer", nullable: false),
                    author_phone = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: true),
                    body = table.Column<string>(type: "text", nullable: false),
                    timestamp = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    media_url = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_chat_messages", x => x.id);
                    table.ForeignKey(
                        name: "FK_chat_messages_conversations_conversation_id",
                        column: x => x.conversation_id,
                        principalTable: "conversations",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_contacts_stage",
                table: "contacts",
                column: "stage");

            migrationBuilder.CreateIndex(
                name: "IX_chat_messages_conversation_id",
                table: "chat_messages",
                column: "conversation_id");

            migrationBuilder.CreateIndex(
                name: "IX_chat_messages_conversation_id_timestamp",
                table: "chat_messages",
                columns: new[] { "conversation_id", "timestamp" });

            migrationBuilder.CreateIndex(
                name: "IX_chat_messages_wa_message_id",
                table: "chat_messages",
                column: "wa_message_id",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_contact_notes_contact_id",
                table: "contact_notes",
                column: "contact_id");

            migrationBuilder.CreateIndex(
                name: "IX_contact_stage_changes_contact_id",
                table: "contact_stage_changes",
                column: "contact_id");

            migrationBuilder.CreateIndex(
                name: "IX_contact_tag_assignments_tag_name",
                table: "contact_tag_assignments",
                column: "tag_name");

            migrationBuilder.CreateIndex(
                name: "IX_conversations_contact_id",
                table: "conversations",
                column: "contact_id");

            migrationBuilder.CreateIndex(
                name: "IX_conversations_last_message_at",
                table: "conversations",
                column: "last_message_at");

            migrationBuilder.CreateIndex(
                name: "IX_conversations_wa_chat_id",
                table: "conversations",
                column: "wa_chat_id",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_users_email",
                table: "users",
                column: "email",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "chat_messages");

            migrationBuilder.DropTable(
                name: "contact_notes");

            migrationBuilder.DropTable(
                name: "contact_stage_changes");

            migrationBuilder.DropTable(
                name: "contact_tag_assignments");

            migrationBuilder.DropTable(
                name: "users");

            migrationBuilder.DropTable(
                name: "conversations");

            migrationBuilder.DropTable(
                name: "contact_tags");

            migrationBuilder.DropIndex(
                name: "IX_contacts_stage",
                table: "contacts");

            migrationBuilder.DropColumn(
                name: "stage",
                table: "contacts");

            migrationBuilder.DropColumn(
                name: "stage_changed_at",
                table: "contacts");
        }
    }
}
