using System;
using Microsoft.EntityFrameworkCore.Migrations;
using NpgsqlTypes;
using Pgvector;

#nullable disable

namespace Mimir.Server.Storage.Migrations
{
    /// <inheritdoc />
    public partial class WisdomSchema : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "converted_at",
                table: "harvested_items",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "wisdom",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    kind = table.Column<string>(type: "text", nullable: false),
                    scope_project_id = table.Column<Guid>(type: "uuid", nullable: false),
                    text = table.Column<string>(type: "text", nullable: false),
                    embedding = table.Column<Vector>(type: "vector(1024)", nullable: false),
                    tsv = table.Column<NpgsqlTsVector>(type: "tsvector", nullable: true, computedColumnSql: "to_tsvector('english', text)", stored: true),
                    reinforcement = table.Column<int>(type: "integer", nullable: false),
                    last_confirmed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    contested_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    retired_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    superseded_by = table.Column<Guid>(type: "uuid", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_wisdom", x => x.id);
                    table.ForeignKey(
                        name: "FK_wisdom_projects_scope_project_id",
                        column: x => x.scope_project_id,
                        principalTable: "projects",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_wisdom_wisdom_superseded_by",
                        column: x => x.superseded_by,
                        principalTable: "wisdom",
                        principalColumn: "id",
                        onDelete: ReferentialAction.SetNull);
                });

            migrationBuilder.CreateTable(
                name: "provenance",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    wisdom_id = table.Column<Guid>(type: "uuid", nullable: false),
                    episode_id = table.Column<Guid>(type: "uuid", nullable: true),
                    event_id = table.Column<Guid>(type: "uuid", nullable: true),
                    harvested_item_id = table.Column<Guid>(type: "uuid", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_provenance", x => x.id);
                    table.ForeignKey(
                        name: "FK_provenance_episodes_episode_id",
                        column: x => x.episode_id,
                        principalTable: "episodes",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_provenance_events_event_id",
                        column: x => x.event_id,
                        principalTable: "events",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_provenance_harvested_items_harvested_item_id",
                        column: x => x.harvested_item_id,
                        principalTable: "harvested_items",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_provenance_wisdom_wisdom_id",
                        column: x => x.wisdom_id,
                        principalTable: "wisdom",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "wisdom_versions",
                columns: table => new
                {
                    wisdom_id = table.Column<Guid>(type: "uuid", nullable: false),
                    version = table.Column<int>(type: "integer", nullable: false),
                    text = table.Column<string>(type: "text", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    cause = table.Column<string>(type: "text", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_wisdom_versions", x => new { x.wisdom_id, x.version });
                    table.ForeignKey(
                        name: "FK_wisdom_versions_wisdom_wisdom_id",
                        column: x => x.wisdom_id,
                        principalTable: "wisdom",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_harvested_items_converted_at",
                table: "harvested_items",
                column: "converted_at",
                filter: "converted_at IS NULL");

            migrationBuilder.CreateIndex(
                name: "IX_provenance_episode_id",
                table: "provenance",
                column: "episode_id");

            migrationBuilder.CreateIndex(
                name: "IX_provenance_event_id",
                table: "provenance",
                column: "event_id");

            migrationBuilder.CreateIndex(
                name: "IX_provenance_harvested_item_id",
                table: "provenance",
                column: "harvested_item_id");

            migrationBuilder.CreateIndex(
                name: "IX_provenance_wisdom_id",
                table: "provenance",
                column: "wisdom_id");

            migrationBuilder.CreateIndex(
                name: "IX_wisdom_embedding",
                table: "wisdom",
                column: "embedding")
                .Annotation("Npgsql:IndexMethod", "hnsw")
                .Annotation("Npgsql:IndexOperators", new[] { "vector_cosine_ops" });

            migrationBuilder.CreateIndex(
                name: "IX_wisdom_scope_project_id",
                table: "wisdom",
                column: "scope_project_id");

            migrationBuilder.CreateIndex(
                name: "IX_wisdom_superseded_by",
                table: "wisdom",
                column: "superseded_by");

            migrationBuilder.CreateIndex(
                name: "IX_wisdom_tsv",
                table: "wisdom",
                column: "tsv")
                .Annotation("Npgsql:IndexMethod", "gin");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "provenance");

            migrationBuilder.DropTable(
                name: "wisdom_versions");

            migrationBuilder.DropTable(
                name: "wisdom");

            migrationBuilder.DropIndex(
                name: "IX_harvested_items_converted_at",
                table: "harvested_items");

            migrationBuilder.DropColumn(
                name: "converted_at",
                table: "harvested_items");
        }
    }
}
