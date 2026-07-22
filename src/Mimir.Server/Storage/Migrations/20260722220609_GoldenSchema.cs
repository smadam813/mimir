using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Mimir.Server.Storage.Migrations
{
    /// <inheritdoc />
    public partial class GoldenSchema : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "golden_cases",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    query_context = table.Column<string>(type: "text", nullable: false),
                    project_id = table.Column<Guid>(type: "uuid", nullable: false),
                    expected_wisdom_id = table.Column<Guid>(type: "uuid", nullable: false),
                    created_from_injection_id = table.Column<Guid>(type: "uuid", nullable: true),
                    note = table.Column<string>(type: "text", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_golden_cases", x => x.id);
                    table.ForeignKey(
                        name: "FK_golden_cases_injections_created_from_injection_id",
                        column: x => x.created_from_injection_id,
                        principalTable: "injections",
                        principalColumn: "id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_golden_cases_projects_project_id",
                        column: x => x.project_id,
                        principalTable: "projects",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_golden_cases_wisdom_expected_wisdom_id",
                        column: x => x.expected_wisdom_id,
                        principalTable: "wisdom",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_golden_cases_created_from_injection_id",
                table: "golden_cases",
                column: "created_from_injection_id");

            migrationBuilder.CreateIndex(
                name: "IX_golden_cases_expected_wisdom_id",
                table: "golden_cases",
                column: "expected_wisdom_id");

            migrationBuilder.CreateIndex(
                name: "IX_golden_cases_project_id",
                table: "golden_cases",
                column: "project_id");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "golden_cases");
        }
    }
}
