using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PapeleriaPos.Datos.Migraciones
{
    /// <inheritdoc />
    public partial class ImprimirLoDelCelular : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Imprimir el ticket de lo que cobra el celular (2026-11-26; docs/celular-como-caja.md, paso 11): el interruptor de
            // Configuración › Ticket llega apagado, y cada caja dice su impresora de tickets la próxima vez que abra su ventana
            // (mientras, vacía: «todavía no lo dice»).
            migrationBuilder.AddColumn<bool>(
                name: "ImprimirLoDelCelular",
                table: "ConfiguracionesTicket",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<string>(
                name: "ImpresoraDeTickets",
                table: "Cajas",
                type: "TEXT",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "ImprimirLoDelCelular",
                table: "ConfiguracionesTicket");

            migrationBuilder.DropColumn(
                name: "ImpresoraDeTickets",
                table: "Cajas");
        }
    }
}
