using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PapeleriaPos.Datos.Migraciones
{
    /// <summary>
    /// Varias impresoras por sucursal (2026-11-03, docs/impresion.md, «Varias impresoras»). La impresora que tenía cada sucursal (su IP, su
    /// aviso de tinta y cada cuánto se lee, en <c>ConfiguracionesImpresion</c>) pasa a ser su impresora 1 en la tabla nueva
    /// <c>Impresoras</c>, una WF-6590, y sus lecturas y lo que salió de ella quedan ligados a esa impresora. También tiene su impresora 1 una
    /// sucursal con lecturas o impresiones pero sin configuración. Sus costos no cambian: la impresora 1 no tiene nada propio y cobra con los
    /// de la sucursal, como antes.
    /// <para>
    /// Las columnas se quitan con <c>ALTER TABLE … DROP COLUMN</c> de SQLite, sin rehacer las tablas: así las de los trabajos (con sus
    /// miniaturas) no se copian, y al regresar la migración las columnas vuelven como estaban (MigracionesTests). Al regresar, la primera
    /// impresora de cada sucursal vuelve a ser la de la sucursal; las demás y lo propio de cada una se pierden (antes había una sola).
    /// </para>
    /// </summary>
    public partial class VariasImpresoras : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "ImpresoraId",
                table: "TrabajosImpresion",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "ImpresoraId",
                table: "LecturasImpresora",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "ImpresoraId",
                table: "HistorialCostosImpresion",
                type: "TEXT",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "Impresoras",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    SucursalId = table.Column<Guid>(type: "TEXT", nullable: false),
                    Numero = table.Column<int>(type: "INTEGER", nullable: false),
                    Nombre = table.Column<string>(type: "TEXT", nullable: false),
                    Perfil = table.Column<string>(type: "TEXT", nullable: false),
                    Host = table.Column<string>(type: "TEXT", nullable: true),
                    AvisoTintaPorcentaje = table.Column<int>(type: "INTEGER", nullable: false),
                    MinutosEntreLecturas = table.Column<int>(type: "INTEGER", nullable: false),
                    CostosJson = table.Column<string>(type: "TEXT", nullable: true),
                    Activa = table.Column<bool>(type: "INTEGER", nullable: false),
                    CreadaUtc = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Impresoras", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Impresoras_Sucursales_SucursalId",
                        column: x => x.SucursalId,
                        principalTable: "Sucursales",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_TrabajosImpresion_ImpresoraId_ImpresoUtc",
                table: "TrabajosImpresion",
                columns: new[] { "ImpresoraId", "ImpresoUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_LecturasImpresora_ImpresoraId_TomadaUtc",
                table: "LecturasImpresora",
                columns: new[] { "ImpresoraId", "TomadaUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_Impresoras_SucursalId_Numero",
                table: "Impresoras",
                columns: new[] { "SucursalId", "Numero" },
                unique: true);

            // La impresora de cada sucursal pasa a ser su impresora 1, con su IP, su aviso y cada cuánto se lee. El Id, un GUID nuevo en
            // mayúsculas, como guarda los GUID el POS.
            migrationBuilder.Sql("""
                INSERT INTO "Impresoras" ("Id", "SucursalId", "Numero", "Nombre", "Perfil", "Host", "AvisoTintaPorcentaje", "MinutosEntreLecturas",
                    "CostosJson", "Activa", "CreadaUtc")
                SELECT upper(hex(randomblob(4)) || '-' || hex(randomblob(2)) || '-4' || substr(hex(randomblob(2)), 2) || '-'
                        || substr('89AB', 1 + (abs(random()) % 4), 1) || substr(hex(randomblob(2)), 2) || '-' || hex(randomblob(6))),
                    s."SucursalId", 1, 'WF-6590', 'WF6590', c."ImpresoraHost", COALESCE(c."AvisoTintaPorcentaje", 10),
                    COALESCE(c."MinutosEntreLecturas", 5), NULL, 1, strftime('%Y-%m-%d %H:%M:%S', 'now')
                FROM (SELECT "SucursalId" FROM "ConfiguracionesImpresion"
                      UNION SELECT "SucursalId" FROM "LecturasImpresora"
                      UNION SELECT "SucursalId" FROM "TrabajosImpresion") AS s
                LEFT JOIN "ConfiguracionesImpresion" AS c ON c."SucursalId" = s."SucursalId";
                """);

            // Sus lecturas y lo que salió de ella (también las copias de su panel): de la impresora 1 de su sucursal.
            migrationBuilder.Sql("""
                UPDATE "LecturasImpresora" SET "ImpresoraId" = (SELECT i."Id" FROM "Impresoras" AS i
                    WHERE i."SucursalId" = "LecturasImpresora"."SucursalId" AND i."Numero" = 1)
                WHERE "ImpresoraId" IS NULL;
                """);
            migrationBuilder.Sql("""
                UPDATE "TrabajosImpresion" SET "ImpresoraId" = (SELECT i."Id" FROM "Impresoras" AS i
                    WHERE i."SucursalId" = "TrabajosImpresion"."SucursalId" AND i."Numero" = 1)
                WHERE "ImpresoraId" IS NULL;
                """);

            migrationBuilder.Sql("ALTER TABLE \"ConfiguracionesImpresion\" DROP COLUMN \"ImpresoraHost\";");
            migrationBuilder.Sql("ALTER TABLE \"ConfiguracionesImpresion\" DROP COLUMN \"AvisoTintaPorcentaje\";");
            migrationBuilder.Sql("ALTER TABLE \"ConfiguracionesImpresion\" DROP COLUMN \"MinutosEntreLecturas\";");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Como las agregó la migración «Impresora» (2026-10-04): con sus valores por omisión.
            migrationBuilder.Sql("ALTER TABLE \"ConfiguracionesImpresion\" ADD \"AvisoTintaPorcentaje\" INTEGER NOT NULL DEFAULT 10;");
            migrationBuilder.Sql("ALTER TABLE \"ConfiguracionesImpresion\" ADD \"ImpresoraHost\" TEXT NULL;");
            migrationBuilder.Sql("ALTER TABLE \"ConfiguracionesImpresion\" ADD \"MinutosEntreLecturas\" INTEGER NOT NULL DEFAULT 5;");

            // La primera impresora de cada sucursal (la activa de número más bajo) vuelve a ser la de la sucursal. Si la sucursal no tenía
            // configuración, se le pone una con los costos por omisión ('{}'), como si no tuviera.
            migrationBuilder.Sql("""
                INSERT INTO "ConfiguracionesImpresion" ("SucursalId", "CostosJson", "ModificadoUtc")
                SELECT DISTINCT i."SucursalId", '{}', strftime('%Y-%m-%d %H:%M:%S', 'now') FROM "Impresoras" AS i
                WHERE i."Host" IS NOT NULL
                  AND NOT EXISTS (SELECT 1 FROM "ConfiguracionesImpresion" AS c WHERE c."SucursalId" = i."SucursalId");
                """);
            migrationBuilder.Sql("""
                UPDATE "ConfiguracionesImpresion" SET
                    "ImpresoraHost" = (SELECT i."Host" FROM "Impresoras" AS i WHERE i."SucursalId" = "ConfiguracionesImpresion"."SucursalId"
                        ORDER BY i."Activa" DESC, i."Numero" LIMIT 1),
                    "AvisoTintaPorcentaje" = COALESCE((SELECT i."AvisoTintaPorcentaje" FROM "Impresoras" AS i
                        WHERE i."SucursalId" = "ConfiguracionesImpresion"."SucursalId" ORDER BY i."Activa" DESC, i."Numero" LIMIT 1), 10),
                    "MinutosEntreLecturas" = COALESCE((SELECT i."MinutosEntreLecturas" FROM "Impresoras" AS i
                        WHERE i."SucursalId" = "ConfiguracionesImpresion"."SucursalId" ORDER BY i."Activa" DESC, i."Numero" LIMIT 1), 5);
                """);

            // Lo propio de cada impresora que se reemplazó no es un costo de la sucursal: antes no había dónde ponerlo.
            migrationBuilder.Sql("DELETE FROM \"HistorialCostosImpresion\" WHERE \"ImpresoraId\" IS NOT NULL;");

            migrationBuilder.DropIndex(
                name: "IX_TrabajosImpresion_ImpresoraId_ImpresoUtc",
                table: "TrabajosImpresion");

            migrationBuilder.DropIndex(
                name: "IX_LecturasImpresora_ImpresoraId_TomadaUtc",
                table: "LecturasImpresora");

            migrationBuilder.Sql("ALTER TABLE \"TrabajosImpresion\" DROP COLUMN \"ImpresoraId\";");
            migrationBuilder.Sql("ALTER TABLE \"LecturasImpresora\" DROP COLUMN \"ImpresoraId\";");
            migrationBuilder.Sql("ALTER TABLE \"HistorialCostosImpresion\" DROP COLUMN \"ImpresoraId\";");

            migrationBuilder.DropTable(
                name: "Impresoras");
        }
    }
}
