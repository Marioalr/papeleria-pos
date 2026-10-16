using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PapeleriaPos.Datos.Migraciones
{
    /// <inheritdoc />
    public partial class ConfiguracionNueva : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "Direccion",
                table: "ConfiguracionesSucursal",
                type: "TEXT",
                maxLength: 120,
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "FichasEnEspera",
                table: "ConfiguracionesSucursal",
                type: "INTEGER",
                nullable: false,
                defaultValue: 3);

            migrationBuilder.AddColumn<long>(
                name: "FondoSugeridoCentavos",
                table: "ConfiguracionesSucursal",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "NombreTienda",
                table: "ConfiguracionesSucursal",
                type: "TEXT",
                maxLength: 60,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Telefono",
                table: "ConfiguracionesSucursal",
                type: "TEXT",
                maxLength: 30,
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "Direccion",
                table: "ConfiguracionesSucursal");

            migrationBuilder.DropColumn(
                name: "FichasEnEspera",
                table: "ConfiguracionesSucursal");

            migrationBuilder.DropColumn(
                name: "FondoSugeridoCentavos",
                table: "ConfiguracionesSucursal");

            migrationBuilder.DropColumn(
                name: "NombreTienda",
                table: "ConfiguracionesSucursal");

            migrationBuilder.DropColumn(
                name: "Telefono",
                table: "ConfiguracionesSucursal");
        }
    }
}
