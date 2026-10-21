using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PapeleriaPos.Datos.Migraciones
{
    /// <inheritdoc />
    public partial class MostradorYTemporadas : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "Exhibidores",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    Nombre = table.Column<string>(type: "TEXT", maxLength: 40, nullable: false),
                    Tipo = table.Column<string>(type: "TEXT", nullable: false),
                    DesdeDia = table.Column<int>(type: "INTEGER", nullable: true),
                    DesdeMes = table.Column<int>(type: "INTEGER", nullable: true),
                    HastaDia = table.Column<int>(type: "INTEGER", nullable: true),
                    HastaMes = table.Column<int>(type: "INTEGER", nullable: true),
                    Orden = table.Column<int>(type: "INTEGER", nullable: false),
                    Activo = table.Column<bool>(type: "INTEGER", nullable: false),
                    Version = table.Column<long>(type: "INTEGER", nullable: false),
                    ModificadoUtc = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Exhibidores", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "LugaresExhibidor",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    ExhibidorId = table.Column<Guid>(type: "TEXT", nullable: false),
                    Nombre = table.Column<string>(type: "TEXT", maxLength: 40, nullable: false),
                    Orden = table.Column<int>(type: "INTEGER", nullable: false),
                    Version = table.Column<long>(type: "INTEGER", nullable: false),
                    ModificadoUtc = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_LugaresExhibidor", x => x.Id);
                    table.ForeignKey(
                        name: "FK_LugaresExhibidor_Exhibidores_ExhibidorId",
                        column: x => x.ExhibidorId,
                        principalTable: "Exhibidores",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "ProductosExhibidos",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    LugarId = table.Column<Guid>(type: "TEXT", nullable: false),
                    VarianteId = table.Column<Guid>(type: "TEXT", nullable: false),
                    Posicion = table.Column<int>(type: "INTEGER", nullable: false),
                    Version = table.Column<long>(type: "INTEGER", nullable: false),
                    ModificadoUtc = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ProductosExhibidos", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ProductosExhibidos_LugaresExhibidor_LugarId",
                        column: x => x.LugarId,
                        principalTable: "LugaresExhibidor",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_ProductosExhibidos_Variantes_VarianteId",
                        column: x => x.VarianteId,
                        principalTable: "Variantes",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_Exhibidores_Version",
                table: "Exhibidores",
                column: "Version");

            migrationBuilder.CreateIndex(
                name: "IX_LugaresExhibidor_ExhibidorId",
                table: "LugaresExhibidor",
                column: "ExhibidorId");

            migrationBuilder.CreateIndex(
                name: "IX_LugaresExhibidor_Version",
                table: "LugaresExhibidor",
                column: "Version");

            migrationBuilder.CreateIndex(
                name: "IX_ProductosExhibidos_LugarId_VarianteId",
                table: "ProductosExhibidos",
                columns: new[] { "LugarId", "VarianteId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ProductosExhibidos_VarianteId",
                table: "ProductosExhibidos",
                column: "VarianteId");

            migrationBuilder.CreateIndex(
                name: "IX_ProductosExhibidos_Version",
                table: "ProductosExhibidos",
                column: "Version");

            SembrarLoDelDueno(migrationBuilder);
        }

        /// <summary>
        /// Lo que dijo el dueño (2026-10-15, docs/propuesta-interfaz.md, «Preguntas para el dueño»): un exhibidor principal y
        /// las temporadas de cada año, con sus fechas. El mostrador empieza con dos repisas vacías y cada temporada con «Mesa de
        /// la entrada», como en el prototipo; los nombres, las fechas y los lugares se cambian en Configuración › Mostrador y
        /// temporadas, y los productos se acomodan en Ctrl+M › Acomodar. La primavera, sin fechas hasta que se definan.
        /// </summary>
        private static void SembrarLoDelDueno(MigrationBuilder migrationBuilder)
        {
            var fecha = new DateTime(2026, 10, 17, 0, 0, 0, DateTimeKind.Utc);
            static Guid Id(int n) => Guid.Parse($"0199f1e0-0000-7000-8000-{n:D12}");
            var exhibidores = new (int Id, string Nombre, string Tipo, int? DesdeDia, int? DesdeMes, int? HastaDia, int? HastaMes, int Orden)[]
            {
                (101, "Mostrador", "Exhibidor", null, null, null, null, 1),
                (201, "15 de septiembre", "Temporada", 1, 9, 16, 9, 1),
                (301, "Todos Santos", "Temporada", 16, 9, 5, 11, 2),
                (401, "Navidad", "Temporada", 20, 11, 1, 1, 3),
                (501, "Primavera", "Temporada", null, null, null, null, 4),
            };
            foreach (var e in exhibidores)
            {
                migrationBuilder.InsertData(
                    table: "Exhibidores",
                    columns: ["Id", "Nombre", "Tipo", "DesdeDia", "DesdeMes", "HastaDia", "HastaMes", "Orden", "Activo", "Version", "ModificadoUtc"],
                    values: new object[] { Id(e.Id), e.Nombre, e.Tipo, e.DesdeDia, e.DesdeMes, e.HastaDia, e.HastaMes, e.Orden, true, 0L, fecha });
            }
            var lugares = new (int Id, int Exhibidor, string Nombre, int Orden)[]
            {
                (102, 101, "Repisa de arriba", 1),
                (103, 101, "Repisa de abajo", 2),
                (202, 201, "Mesa de la entrada", 1),
                (302, 301, "Mesa de la entrada", 1),
                (402, 401, "Mesa de la entrada", 1),
                (502, 501, "Mesa de la entrada", 1),
            };
            foreach (var l in lugares)
            {
                migrationBuilder.InsertData(
                    table: "LugaresExhibidor",
                    columns: ["Id", "ExhibidorId", "Nombre", "Orden", "Version", "ModificadoUtc"],
                    values: new object[] { Id(l.Id), Id(l.Exhibidor), l.Nombre, l.Orden, 0L, fecha });
            }
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ProductosExhibidos");

            migrationBuilder.DropTable(
                name: "LugaresExhibidor");

            migrationBuilder.DropTable(
                name: "Exhibidores");
        }
    }
}
