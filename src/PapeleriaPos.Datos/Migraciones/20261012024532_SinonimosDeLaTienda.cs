using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PapeleriaPos.Datos.Migraciones
{
    /// <inheritdoc />
    public partial class SinonimosDeLaTienda : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "Sinonimos",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    Palabra = table.Column<string>(type: "TEXT", nullable: false),
                    Equivale = table.Column<string>(type: "TEXT", nullable: false),
                    Activo = table.Column<bool>(type: "INTEGER", nullable: false),
                    Version = table.Column<long>(type: "INTEGER", nullable: false),
                    ModificadoUtc = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Sinonimos", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_Sinonimos_Palabra_Equivale",
                table: "Sinonimos",
                columns: new[] { "Palabra", "Equivale" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Sinonimos_Version",
                table: "Sinonimos",
                column: "Version");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "Sinonimos");
        }
    }
}
