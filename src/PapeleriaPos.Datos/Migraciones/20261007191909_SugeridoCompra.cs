using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PapeleriaPos.Datos.Migraciones
{
    /// <inheritdoc />
    public partial class SugeridoCompra : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "SugeridoCoberturaDias",
                table: "ConfiguracionesSucursal",
                type: "INTEGER",
                nullable: false,
                defaultValue: 14);

            migrationBuilder.AddColumn<int>(
                name: "SugeridoColchonDias",
                table: "ConfiguracionesSucursal",
                type: "INTEGER",
                nullable: false,
                defaultValue: 7);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "SugeridoCoberturaDias",
                table: "ConfiguracionesSucursal");

            migrationBuilder.DropColumn(
                name: "SugeridoColchonDias",
                table: "ConfiguracionesSucursal");
        }
    }
}
