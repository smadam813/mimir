using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Mimir.Server.Storage.Migrations
{
    /// <inheritdoc />
    public partial class DistillerSchema : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "distillation_started_at",
                table: "episodes",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_episodes_distillation",
                table: "episodes",
                column: "distillation",
                filter: "sealed_at IS NOT NULL AND distillation <> 'Done'");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_episodes_distillation",
                table: "episodes");

            migrationBuilder.DropColumn(
                name: "distillation_started_at",
                table: "episodes");
        }
    }
}
