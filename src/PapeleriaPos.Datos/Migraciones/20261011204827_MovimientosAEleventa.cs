using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PapeleriaPos.Datos.Migraciones
{
    /// <summary>La bandeja hacia eleventa lleva también las entradas y salidas de efectivo (el piloto, paso 4).</summary>
    public partial class MovimientosAEleventa : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "MovimientoId",
                table: "EnviosAEleventa",
                type: "TEXT",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_EnviosAEleventa_MovimientoId",
                table: "EnviosAEleventa",
                column: "MovimientoId",
                unique: true);

            migrationBuilder.AddForeignKey(
                name: "FK_EnviosAEleventa_MovimientosCaja_MovimientoId",
                table: "EnviosAEleventa",
                column: "MovimientoId",
                principalTable: "MovimientosCaja",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_EnviosAEleventa_MovimientosCaja_MovimientoId",
                table: "EnviosAEleventa");

            migrationBuilder.DropIndex(
                name: "IX_EnviosAEleventa_MovimientoId",
                table: "EnviosAEleventa");

            migrationBuilder.DropColumn(
                name: "MovimientoId",
                table: "EnviosAEleventa");
        }
    }
}
