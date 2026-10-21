using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PapeleriaPos.Datos.Migraciones
{
    /// <inheritdoc />
    public partial class InventarioMinimoYMaximo : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<long>(
                name: "InventarioMaximo",
                table: "Variantes",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "InventarioMinimo",
                table: "Variantes",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_MovimientosInventario_SucursalId_FechaUtc",
                table: "MovimientosInventario",
                columns: new[] { "SucursalId", "FechaUtc" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_MovimientosInventario_SucursalId_FechaUtc",
                table: "MovimientosInventario");

            migrationBuilder.DropColumn(
                name: "InventarioMaximo",
                table: "Variantes");

            migrationBuilder.DropColumn(
                name: "InventarioMinimo",
                table: "Variantes");
        }
    }
}
