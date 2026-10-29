using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PapeleriaPos.Datos.Migraciones
{
    /// <summary>
    /// Los usuarios de eleventa (el piloto alfa, 2026-10-25): el número de eleventa de cada usuario que vino de allá (uno por
    /// sucursal) y, en la bandeja hacia eleventa, a nombre de quién va cada cosa. El índice de la sucursal de cada usuario se
    /// queda (revisión del PR #144: EF Core lo quitaba porque el nuevo empieza con la sucursal).
    /// </summary>
    public partial class UsuariosDeEleventa : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "EleventaId",
                table: "Usuarios",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "CajeroEleventa",
                table: "EnviosAEleventa",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_Usuarios_SucursalId_EleventaId",
                table: "Usuarios",
                columns: new[] { "SucursalId", "EleventaId" },
                unique: true,
                filter: "\"EleventaId\" IS NOT NULL");
        }

        /// <summary>
        /// Las columnas se quitan con el DROP COLUMN de SQLite y no con DropColumn de EF Core, que rehace la tabla con el modelo de
        /// la migración de antes: ese modelo no trae los valores por omisión que dejaron las columnas agregadas después (la de la
        /// primera cuenta en Usuarios), y regresar no dejaría la base como estaba (MigracionesTests).
        /// </summary>
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_Usuarios_SucursalId_EleventaId",
                table: "Usuarios");

            migrationBuilder.Sql("ALTER TABLE \"Usuarios\" DROP COLUMN \"EleventaId\";");

            migrationBuilder.Sql("ALTER TABLE \"EnviosAEleventa\" DROP COLUMN \"CajeroEleventa\";");
        }
    }
}
