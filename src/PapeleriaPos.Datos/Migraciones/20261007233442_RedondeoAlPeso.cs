using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PapeleriaPos.Datos.Migraciones
{
    /// <inheritdoc />
    public partial class RedondeoAlPeso : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<long>(
                name: "RedondeoCentavos",
                table: "Ventas",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0L);

            migrationBuilder.AddColumn<long>(
                name: "RedondeoCentavos",
                table: "Devoluciones",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0L);

            migrationBuilder.AddColumn<long>(
                name: "RedondeoCentavos",
                table: "Cortes",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0L);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "RedondeoCentavos",
                table: "Ventas");

            migrationBuilder.DropColumn(
                name: "RedondeoCentavos",
                table: "Devoluciones");

            migrationBuilder.DropColumn(
                name: "RedondeoCentavos",
                table: "Cortes");
        }
    }
}
