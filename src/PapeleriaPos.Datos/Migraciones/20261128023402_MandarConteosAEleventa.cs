using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PapeleriaPos.Datos.Migraciones
{
    /// <inheritdoc />
    public partial class MandarConteosAEleventa : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // El POS manda el conteo aceptado a eleventa (2026-11-27; docs/propuestas/conteo-del-celular-a-eleventa.md, entrega 3): el
            // interruptor «Mandar los conteos a eleventa» (apagado), el renglón del conteo en la bandeja (ConteoRenglonId) y, en cada
            // renglón, la marca de su ajuste (MarcaUtc, fijada al aceptar), el ID con que quedó en eleventa y por qué no lo tomó.
            migrationBuilder.AddColumn<Guid>(
                name: "ConteoRenglonId",
                table: "EnviosAEleventa",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "AjusteEleventa",
                table: "ConteosDelCelularRenglones",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "EleventaMotivo",
                table: "ConteosDelCelularRenglones",
                type: "TEXT",
                maxLength: 500,
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "MarcaUtc",
                table: "ConteosDelCelularRenglones",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "MandarConteosAEleventa",
                table: "ConfiguracionesSucursal",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);

            migrationBuilder.CreateIndex(
                name: "IX_EnviosAEleventa_ConteoRenglonId",
                table: "EnviosAEleventa",
                column: "ConteoRenglonId",
                unique: true);

            migrationBuilder.AddForeignKey(
                name: "FK_EnviosAEleventa_ConteosDelCelularRenglones_ConteoRenglonId",
                table: "EnviosAEleventa",
                column: "ConteoRenglonId",
                principalTable: "ConteosDelCelularRenglones",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_EnviosAEleventa_ConteosDelCelularRenglones_ConteoRenglonId",
                table: "EnviosAEleventa");

            migrationBuilder.DropIndex(
                name: "IX_EnviosAEleventa_ConteoRenglonId",
                table: "EnviosAEleventa");

            migrationBuilder.DropColumn(
                name: "ConteoRenglonId",
                table: "EnviosAEleventa");

            migrationBuilder.DropColumn(
                name: "AjusteEleventa",
                table: "ConteosDelCelularRenglones");

            migrationBuilder.DropColumn(
                name: "EleventaMotivo",
                table: "ConteosDelCelularRenglones");

            migrationBuilder.DropColumn(
                name: "MarcaUtc",
                table: "ConteosDelCelularRenglones");

            // Sin rehacer la tabla (como «Administrar mínimos»): rehecha con el modelo de antes perdería los valores por omisión.
            migrationBuilder.Sql("""ALTER TABLE "ConfiguracionesSucursal" DROP COLUMN "MandarConteosAEleventa";""");
        }
    }
}
