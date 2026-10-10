using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PapeleriaPos.Datos.Migraciones
{
    /// <inheritdoc />
    public partial class ConteoCiclico : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "ConteosCiclicos",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    SucursalId = table.Column<Guid>(type: "TEXT", nullable: false),
                    Fecha = table.Column<DateOnly>(type: "TEXT", nullable: false),
                    CreadoUtc = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ConteosCiclicos", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ConteosCiclicos_Sucursales_SucursalId",
                        column: x => x.SucursalId,
                        principalTable: "Sucursales",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "ConteosCiclicosRenglones",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    ConteoId = table.Column<Guid>(type: "TEXT", nullable: false),
                    Orden = table.Column<int>(type: "INTEGER", nullable: false),
                    VarianteId = table.Column<Guid>(type: "TEXT", nullable: false),
                    Contado = table.Column<long>(type: "INTEGER", nullable: true),
                    Esperado = table.Column<long>(type: "INTEGER", nullable: true),
                    CostoDiferenciaCentavos = table.Column<long>(type: "INTEGER", nullable: true),
                    ContadoPorId = table.Column<Guid>(type: "TEXT", nullable: true),
                    ContadoUtc = table.Column<DateTime>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ConteosCiclicosRenglones", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ConteosCiclicosRenglones_ConteosCiclicos_ConteoId",
                        column: x => x.ConteoId,
                        principalTable: "ConteosCiclicos",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_ConteosCiclicosRenglones_Usuarios_ContadoPorId",
                        column: x => x.ContadoPorId,
                        principalTable: "Usuarios",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_ConteosCiclicosRenglones_Variantes_VarianteId",
                        column: x => x.VarianteId,
                        principalTable: "Variantes",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_ConteosCiclicos_SucursalId_Fecha",
                table: "ConteosCiclicos",
                columns: new[] { "SucursalId", "Fecha" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ConteosCiclicosRenglones_ContadoPorId",
                table: "ConteosCiclicosRenglones",
                column: "ContadoPorId");

            migrationBuilder.CreateIndex(
                name: "IX_ConteosCiclicosRenglones_ConteoId",
                table: "ConteosCiclicosRenglones",
                column: "ConteoId");

            migrationBuilder.CreateIndex(
                name: "IX_ConteosCiclicosRenglones_VarianteId",
                table: "ConteosCiclicosRenglones",
                column: "VarianteId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ConteosCiclicosRenglones");

            migrationBuilder.DropTable(
                name: "ConteosCiclicos");
        }
    }
}
