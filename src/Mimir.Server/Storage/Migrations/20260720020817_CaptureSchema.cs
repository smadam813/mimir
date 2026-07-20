using System;
using Microsoft.EntityFrameworkCore.Migrations;
using NpgsqlTypes;

#nullable disable

namespace Mimir.Server.Storage.Migrations
{
    /// <inheritdoc />
    public partial class CaptureSchema : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "projects",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    identity = table.Column<string>(type: "text", nullable: false),
                    root_paths = table.Column<string[]>(type: "text[]", nullable: false),
                    display_name = table.Column<string>(type: "text", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_projects", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "episodes",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    session_id = table.Column<string>(type: "text", nullable: false),
                    project_id = table.Column<Guid>(type: "uuid", nullable: false),
                    started_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    sealed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    seal_reason = table.Column<string>(type: "text", nullable: true),
                    cwd = table.Column<string>(type: "text", nullable: false),
                    distillation = table.Column<string>(type: "text", nullable: false),
                    distilled_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_episodes", x => x.id);
                    table.ForeignKey(
                        name: "FK_episodes_projects_project_id",
                        column: x => x.project_id,
                        principalTable: "projects",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "events",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    episode_id = table.Column<Guid>(type: "uuid", nullable: false),
                    seq = table.Column<int>(type: "integer", nullable: false),
                    type = table.Column<string>(type: "text", nullable: false),
                    at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    payload = table.Column<string>(type: "jsonb", nullable: false),
                    payload_full_size = table.Column<int>(type: "integer", nullable: false),
                    salient = table.Column<bool>(type: "boolean", nullable: false),
                    tsv = table.Column<NpgsqlTsVector>(type: "tsvector", nullable: true, computedColumnSql: "jsonb_to_tsvector('english', payload, '[\"string\"]')", stored: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_events", x => x.id);
                    table.ForeignKey(
                        name: "FK_events_episodes_episode_id",
                        column: x => x.episode_id,
                        principalTable: "episodes",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.InsertData(
                table: "projects",
                columns: new[] { "id", "display_name", "identity", "root_paths" },
                values: new object[] { new Guid("00000000-0000-0000-0000-000000000001"), "Global", "mimir:global", new string[0] });

            migrationBuilder.CreateIndex(
                name: "IX_episodes_project_id",
                table: "episodes",
                column: "project_id");

            migrationBuilder.CreateIndex(
                name: "IX_episodes_session_id",
                table: "episodes",
                column: "session_id",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_events_episode_id_seq",
                table: "events",
                columns: new[] { "episode_id", "seq" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_events_tsv",
                table: "events",
                column: "tsv")
                .Annotation("Npgsql:IndexMethod", "gin");

            migrationBuilder.CreateIndex(
                name: "IX_projects_identity",
                table: "projects",
                column: "identity",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_projects_root_paths",
                table: "projects",
                column: "root_paths")
                .Annotation("Npgsql:IndexMethod", "gin");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "events");

            migrationBuilder.DropTable(
                name: "episodes");

            migrationBuilder.DropTable(
                name: "projects");
        }
    }
}
