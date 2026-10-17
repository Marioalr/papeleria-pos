using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PapeleriaPos.Datos.Migraciones
{
    /// <inheritdoc />
    public partial class PuenteAnotacionElegida : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Las devoluciones de eleventa que esperaban la pregunta «¿Es la misma?» no se habían pasado a su venta: se quitan, y
            // la siguiente lectura las vuelve a traer (son de los últimos 31 días) y las pasa, o las retiene mientras una de
            // aquí de su ticket espere su anotación a mano (revisión del 2026-10-17: ya no se adivina, se elige).
            migrationBuilder.Sql("""
                DELETE FROM "DevolucionesDesdeEleventa" WHERE "QuizaEsDevolucionId" IS NOT NULL AND "DevolucionId" IS NULL;
                """);

            // Lo que quedó por revisar antes de que se guardara por qué y desde cuándo (revisión del 2026-10-17): el porqué se
            // saca del texto que se guardó, como lo pone hoy la bandeja (lo que no pasa solo, «Otro»; lo que se anota a mano;
            // lo que eleventa ya tenía), y desde cuándo, del último intento o de cuando se creó.
            migrationBuilder.Sql("""
                UPDATE "EnviosAEleventa" SET "PorRevisarPorque" = CASE
                    WHEN "Error" LIKE 'Se dejó para revisar a mano%' OR "Error" LIKE 'Eleventa ya no lo tiene%' THEN 'AnotarAMano'
                    WHEN "Error" LIKE 'Su venta%' OR "Error" LIKE 'Bajó la cuenta%' OR "Error" LIKE 'Llegó cuando su turno%'
                        OR "Error" LIKE 'No se manda:%' OR "Error" LIKE 'Fue con %' OR "Error" LIKE 'No está cobrada%'
                        OR "Error" LIKE 'No tiene artículos%' THEN 'Otro'
                    WHEN "Error" LIKE '%ya está cancelado%' OR "Error" LIKE '%ya no está «%' THEN 'EleventaYaLoTenia'
                    WHEN "Error" LIKE '%a mano%' THEN 'AnotarAMano'
                    ELSE 'Otro' END
                WHERE "Estado" = 'PorRevisar' AND "PorRevisarPorque" IS NULL;
                UPDATE "EnviosAEleventa" SET "PorRevisarUtc" = COALESCE("UltimoIntentoUtc", "CreadoUtc")
                WHERE "Estado" = 'PorRevisar' AND "PorRevisarUtc" IS NULL;
                """);

            migrationBuilder.DropForeignKey(
                name: "FK_DevolucionesDesdeEleventa_Devoluciones_QuizaEsDevolucionId",
                table: "DevolucionesDesdeEleventa");

            migrationBuilder.DropIndex(
                name: "IX_DevolucionesDesdeEleventa_DevolucionId",
                table: "DevolucionesDesdeEleventa");

            migrationBuilder.DropIndex(
                name: "IX_DevolucionesDesdeEleventa_QuizaEsDevolucionId",
                table: "DevolucionesDesdeEleventa");

            migrationBuilder.DropColumn(
                name: "QuizaEsDevolucionId",
                table: "DevolucionesDesdeEleventa");

            migrationBuilder.CreateIndex(
                name: "IX_DevolucionesDesdeEleventa_DevolucionId",
                table: "DevolucionesDesdeEleventa",
                column: "DevolucionId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_DevolucionesDesdeEleventa_DevolucionId",
                table: "DevolucionesDesdeEleventa");

            migrationBuilder.AddColumn<Guid>(
                name: "QuizaEsDevolucionId",
                table: "DevolucionesDesdeEleventa",
                type: "TEXT",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_DevolucionesDesdeEleventa_DevolucionId",
                table: "DevolucionesDesdeEleventa",
                column: "DevolucionId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_DevolucionesDesdeEleventa_QuizaEsDevolucionId",
                table: "DevolucionesDesdeEleventa",
                column: "QuizaEsDevolucionId");

            migrationBuilder.AddForeignKey(
                name: "FK_DevolucionesDesdeEleventa_Devoluciones_QuizaEsDevolucionId",
                table: "DevolucionesDesdeEleventa",
                column: "QuizaEsDevolucionId",
                principalTable: "Devoluciones",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);
        }
    }
}
