using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PapeleriaPos.Datos.Migraciones
{
    /// <inheritdoc />
    public partial class TicketSinImprimir : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Si el ticket de una venta nunca salió (2026-10-22): su primera impresión no pide motivo. Las ventas de antes cuentan
            // como impresas: reimprimirlas pide motivo, como siempre.
            migrationBuilder.AddColumn<bool>(
                name: "TicketSinImprimir",
                table: "Ventas",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "TicketSinImprimir",
                table: "Ventas");
        }
    }
}
