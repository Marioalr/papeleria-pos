using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PapeleriaPos.Datos.Migraciones
{
    /// <inheritdoc />
    public partial class CreditoSinConexion : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTime>(
                name: "FueraDeCorteUtc",
                table: "Abonos",
                type: "TEXT",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_RevisionesCredito_MovimientoId_Motivo",
                table: "RevisionesCredito",
                columns: new[] { "MovimientoId", "Motivo" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_RevisionesCredito_MovimientoId_Motivo",
                table: "RevisionesCredito");

            migrationBuilder.DropColumn(
                name: "FueraDeCorteUtc",
                table: "Abonos");
        }
    }
}
