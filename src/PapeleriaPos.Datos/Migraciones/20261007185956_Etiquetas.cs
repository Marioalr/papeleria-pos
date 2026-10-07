using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PapeleriaPos.Datos.Migraciones
{
    /// <inheritdoc />
    public partial class Etiquetas : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "EtiquetasAjusteXDecimas",
                table: "ConfiguracionesSucursal",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<int>(
                name: "EtiquetasAjusteYDecimas",
                table: "ConfiguracionesSucursal",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<string>(
                name: "EtiquetasFormato",
                table: "ConfiguracionesSucursal",
                type: "TEXT",
                nullable: false,
                defaultValue: "5160");

            migrationBuilder.AddColumn<string>(
                name: "EtiquetasPropio",
                table: "ConfiguracionesSucursal",
                type: "TEXT",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "EtiquetasAjusteXDecimas",
                table: "ConfiguracionesSucursal");

            migrationBuilder.DropColumn(
                name: "EtiquetasAjusteYDecimas",
                table: "ConfiguracionesSucursal");

            migrationBuilder.DropColumn(
                name: "EtiquetasFormato",
                table: "ConfiguracionesSucursal");

            migrationBuilder.DropColumn(
                name: "EtiquetasPropio",
                table: "ConfiguracionesSucursal");
        }
    }
}
