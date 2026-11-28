using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PapeleriaPos.Datos.Migraciones
{
    /// <inheritdoc />
    public partial class SinFolioEnElTicket : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Desde 2026-11-27 la venta tiene un solo folio, el de su ticket (los de la caja siguen a eleventa): ya no hace falta anotar con
            // cuál salió (FolioEnElTicket, del PR #220). Solo decía eso; no se pierde nada más.
            migrationBuilder.DropColumn(
                name: "FolioEnElTicket",
                table: "EnviosAEleventa");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "FolioEnElTicket",
                table: "EnviosAEleventa",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);
        }
    }
}
