using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PapeleriaPos.Datos.Migraciones
{
    /// <inheritdoc />
    public partial class AltaDeCajas : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTime>(
                name: "AltaUtc",
                table: "Cajas",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Equipo",
                table: "Cajas",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "LlaveHuella",
                table: "Cajas",
                type: "TEXT",
                maxLength: 64,
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_Cajas_LlaveHuella",
                table: "Cajas",
                column: "LlaveHuella",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_Cajas_LlaveHuella",
                table: "Cajas");

            migrationBuilder.DropColumn(
                name: "AltaUtc",
                table: "Cajas");

            migrationBuilder.DropColumn(
                name: "Equipo",
                table: "Cajas");

            migrationBuilder.DropColumn(
                name: "LlaveHuella",
                table: "Cajas");
        }
    }
}
