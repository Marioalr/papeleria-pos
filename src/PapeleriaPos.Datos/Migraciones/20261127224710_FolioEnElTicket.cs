using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PapeleriaPos.Datos.Migraciones
{
    /// <inheritdoc />
    public partial class FolioEnElTicket : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Si el ticket del cobro salió con el folio de eleventa (el ensayo de la matriz, 2026-11-27): con el de la caja ya no se
            // busca para devolver. Lo de antes queda en falso: no se sabe con cuál salió.
            migrationBuilder.AddColumn<bool>(
                name: "FolioEnElTicket",
                table: "EnviosAEleventa",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "FolioEnElTicket",
                table: "EnviosAEleventa");
        }
    }
}
