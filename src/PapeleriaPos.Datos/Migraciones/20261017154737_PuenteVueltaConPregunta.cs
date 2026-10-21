using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PapeleriaPos.Datos.Migraciones
{
    /// <inheritdoc />
    public partial class PuenteVueltaConPregunta : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "PorRevisarPorque",
                table: "EnviosAEleventa",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "PorRevisarUtc",
                table: "EnviosAEleventa",
                type: "TEXT",
                nullable: true);

            // Las que ya estaban se quedan sin artículos guardados (una lista vacía, que el JSON entiende).
            migrationBuilder.AddColumn<string>(
                name: "Articulos",
                table: "DevolucionesDesdeEleventa",
                type: "TEXT",
                nullable: false,
                defaultValue: "[]");

            migrationBuilder.AddColumn<Guid>(
                name: "QuizaEsDevolucionId",
                table: "DevolucionesDesdeEleventa",
                type: "TEXT",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_DevolucionesDesdeEleventa_QuizaEsDevolucionId",
                table: "DevolucionesDesdeEleventa",
                column: "QuizaEsDevolucionId");

            migrationBuilder.AddForeignKey(
                name: "FK_DevolucionesDesdeEleventa_Devoluciones_QuizaEsDevolucionId",
                table: "DevolucionesDesdeEleventa",
                column: "QuizaEsDevolucionId",
                principalTable: "Devoluciones",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_DevolucionesDesdeEleventa_Devoluciones_QuizaEsDevolucionId",
                table: "DevolucionesDesdeEleventa");

            migrationBuilder.DropIndex(
                name: "IX_DevolucionesDesdeEleventa_QuizaEsDevolucionId",
                table: "DevolucionesDesdeEleventa");

            migrationBuilder.DropColumn(
                name: "PorRevisarPorque",
                table: "EnviosAEleventa");

            migrationBuilder.DropColumn(
                name: "PorRevisarUtc",
                table: "EnviosAEleventa");

            migrationBuilder.DropColumn(
                name: "Articulos",
                table: "DevolucionesDesdeEleventa");

            migrationBuilder.DropColumn(
                name: "QuizaEsDevolucionId",
                table: "DevolucionesDesdeEleventa");
        }
    }
}
