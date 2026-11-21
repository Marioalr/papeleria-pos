using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PapeleriaPos.Datos.Migraciones
{
    /// <inheritdoc />
    public partial class FormatoDelTicket : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Configuración › Ticket › cómo sale (2026-11-21): las sucursales que ya tenían su ticket guardan «{}», que se lee como
            // FormatoTicket.Predeterminado (como salía, con los nombres con mayúsculas).
            migrationBuilder.AddColumn<string>(
                name: "Formato",
                table: "ConfiguracionesTicket",
                type: "TEXT",
                nullable: false,
                defaultValue: "{}");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "Formato",
                table: "ConfiguracionesTicket");
        }
    }
}
