using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PapeleriaPos.Datos.Migraciones
{
    /// <inheritdoc />
    public partial class NotaEnElTicket : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // La nota del ticket (F4 al cobrar, como en eleventa; 2026-10-22): las ventas de antes no llevan.
            migrationBuilder.AddColumn<string>(
                name: "Nota",
                table: "Ventas",
                type: "TEXT",
                maxLength: 80,
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "Nota",
                table: "Ventas");
        }
    }
}
