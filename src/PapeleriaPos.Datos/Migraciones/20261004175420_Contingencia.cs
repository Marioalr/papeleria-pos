using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PapeleriaPos.Datos.Migraciones
{
    /// <inheritdoc />
    public partial class Contingencia : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "EnContingencia",
                table: "Ventas",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<bool>(
                name: "AbiertoEnContingencia",
                table: "Turnos",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<bool>(
                name: "EnContingencia",
                table: "MovimientosCaja",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<bool>(
                name: "EnContingencia",
                table: "Cortes",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<bool>(
                name: "EnContingencia",
                table: "Bitacora",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "EnContingencia",
                table: "Ventas");

            migrationBuilder.DropColumn(
                name: "AbiertoEnContingencia",
                table: "Turnos");

            migrationBuilder.DropColumn(
                name: "EnContingencia",
                table: "MovimientosCaja");

            migrationBuilder.DropColumn(
                name: "EnContingencia",
                table: "Cortes");

            migrationBuilder.DropColumn(
                name: "EnContingencia",
                table: "Bitacora");
        }
    }
}
