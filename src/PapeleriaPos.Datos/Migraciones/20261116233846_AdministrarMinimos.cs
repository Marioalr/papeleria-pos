using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PapeleriaPos.Datos.Migraciones
{
    /// <summary>«Administrar mínimos» (dueño, 2026-11-16): el interruptor de Configuración › Inventario, apagado por omisión.</summary>
    public partial class AdministrarMinimos : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "AdministrarMinimos",
                table: "ConfiguracionesSucursal",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Sin rehacer la tabla (SQLite ya quita una columna): rehecha con el modelo de la migración de antes, AlertasApagadas y
            // el ajuste de las etiquetas perderían el valor por omisión que les pusieron sus migraciones.
            migrationBuilder.Sql("""ALTER TABLE "ConfiguracionesSucursal" DROP COLUMN "AdministrarMinimos";""");
        }
    }
}
