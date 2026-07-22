using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Mimir.Server.Storage.Migrations
{
    /// <inheritdoc />
    public partial class InjectionSchema : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "injections",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    session_id = table.Column<string>(type: "text", nullable: false),
                    project_id = table.Column<Guid>(type: "uuid", nullable: false),
                    at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    lane = table.Column<string>(type: "text", nullable: false),
                    query_context = table.Column<string>(type: "text", nullable: true),
                    chars = table.Column<int>(type: "integer", nullable: false),
                    verdict = table.Column<string>(type: "text", nullable: true),
                    verdict_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    items = table.Column<string>(type: "jsonb", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_injections", x => x.id);
                    table.ForeignKey(
                        name: "FK_injections_projects_project_id",
                        column: x => x.project_id,
                        principalTable: "projects",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_injections_project_id",
                table: "injections",
                column: "project_id");

            migrationBuilder.CreateIndex(
                name: "IX_injections_session_id",
                table: "injections",
                column: "session_id");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "injections");
        }
    }
}
