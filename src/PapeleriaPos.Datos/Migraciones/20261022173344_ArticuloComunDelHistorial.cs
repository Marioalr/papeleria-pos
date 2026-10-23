using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PapeleriaPos.Datos.Migraciones
{
    /// <inheritdoc />
    public partial class ArticuloComunDelHistorial : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "EsArticuloComun",
                table: "VentasHistoricasLineas",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);

            // Lo que ya se importó (revisión del PR #131, 2026-10-22): para cada código que no era del catálogo de eleventa, la
            // importación creó un producto inactivo con el código como su Id de origen («codigo:COMUN-…»). Son artículos comunes
            // los de código COMUN-… y, de esos códigos, los del departamento de los comunes de eleventa («Productos Comunes»),
            // como los marca ahora la importación (ImportadorEleventa.Comunes). Un producto del catálogo de eleventa en ese
            // departamento sigue siendo producto.
            migrationBuilder.Sql(
                """
                UPDATE "VentasHistoricasLineas" SET "EsArticuloComun" = 1
                WHERE "VarianteId" IN (
                    SELECT m."IdNuevo" FROM "MapeosImportacion" AS m
                    INNER JOIN "Variantes" AS v ON v."Id" = m."IdNuevo"
                    INNER JOIN "Productos" AS p ON p."Id" = v."ProductoId"
                    LEFT JOIN "Departamentos" AS d ON d."Id" = p."DepartamentoId"
                    WHERE m."Origen" = 'eleventa' AND m."Entidad" = 'Variante' AND m."IdOrigen" LIKE 'codigo:%'
                      AND (substr(m."IdOrigen", 8) LIKE 'COMUN-%' OR trim(d."Nombre") = 'Productos Comunes' COLLATE NOCASE));
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Sin rehacer la tabla (SQLite ya quita una columna): rehecha con el modelo de la migración de antes, ImporteCentavos
            // perdería el valor por omisión que le puso su migración y la base regresada no sería como la de antes.
            migrationBuilder.Sql("""ALTER TABLE "VentasHistoricasLineas" DROP COLUMN "EsArticuloComun";""");
        }
    }
}
