using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PapeleriaPos.Datos.Migraciones
{
    /// <inheritdoc />
    public partial class TicketAlCobrar : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Configuración › Ticket › «Al cobrar»: las sucursales que ya tenían su ticket lo siguen imprimiendo al cobrar, como
            // siempre (el enum se guarda como texto).
            migrationBuilder.AddColumn<string>(
                name: "AlCobrar",
                table: "ConfiguracionesTicket",
                type: "TEXT",
                nullable: false,
                defaultValue: "Imprimir");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "AlCobrar",
                table: "ConfiguracionesTicket");
        }
    }
}
