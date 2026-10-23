using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PapeleriaPos.Datos.Migraciones
{
    /// <inheritdoc />
    public partial class CancelarMovimientos : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Una entrada o salida de efectivo equivocada se cancela (2026-10-22), con su motivo, en su mismo turno y antes de su
            // corte: se queda, pero ya no cuenta. Las de antes siguen como estaban.
            migrationBuilder.AddColumn<Guid>(
                name: "CanceladoPorId",
                table: "MovimientosCaja",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "CanceladoUtc",
                table: "MovimientosCaja",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "MotivoCancelacion",
                table: "MovimientosCaja",
                type: "TEXT",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // SQLite quita las columnas sin rehacer la tabla (ALTER TABLE … DROP COLUMN): lo demás de MovimientosCaja se queda como
            // estaba, con sus valores por omisión (EnContingencia, de la migración Contingencia). DropColumn la rehace con el
            // modelo de la migración de antes, que no los trae, y la base regresada ya no sería igual (MigracionesTests).
            migrationBuilder.Sql("""
                ALTER TABLE "MovimientosCaja" DROP COLUMN "CanceladoPorId";
                ALTER TABLE "MovimientosCaja" DROP COLUMN "CanceladoUtc";
                ALTER TABLE "MovimientosCaja" DROP COLUMN "MotivoCancelacion";
                """);
        }
    }
}
