using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PapeleriaPos.Datos.Migraciones
{
    /// <inheritdoc />
    public partial class TarjetasDelContador : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "ContadorJson",
                table: "Impresoras",
                type: "TEXT",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // SQLite quita la columna sin rehacer la tabla (ALTER TABLE … DROP COLUMN), como en las otras migraciones.
            migrationBuilder.Sql("ALTER TABLE \"Impresoras\" DROP COLUMN \"ContadorJson\";");
        }
    }
}
