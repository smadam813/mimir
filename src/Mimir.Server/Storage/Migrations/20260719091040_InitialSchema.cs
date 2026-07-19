using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Mimir.Server.Storage.Migrations
{
    /// <inheritdoc />
    public partial class InitialSchema : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AlterDatabase()
                .Annotation("Npgsql:PostgresExtension:vector", ",,");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // EF leaves this empty for an extension-only Up, which would strand the extension on
            // a revert. Nothing depends on it at this migration, so it is safe to drop.
            migrationBuilder.Sql("DROP EXTENSION IF EXISTS vector;");
        }
    }
}
