using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PapeleriaPos.Datos.Migraciones
{
    /// <inheritdoc />
    public partial class SaldoDevuelto : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<long>(
                name: "SaldoDevueltoCentavos",
                table: "Cortes",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0L);

            // Los que ya estaban son abonos (el enum se guarda como texto).
            migrationBuilder.AddColumn<string>(
                name: "Tipo",
                table: "Abonos",
                type: "TEXT",
                nullable: false,
                defaultValue: "Abono");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "SaldoDevueltoCentavos",
                table: "Cortes");

            migrationBuilder.DropColumn(
                name: "Tipo",
                table: "Abonos");
        }
    }
}
