using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PapeleriaPos.Datos.Migraciones
{
    /// <inheritdoc />
    public partial class ConteosDelCelular : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "ConteosDelCelular",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    SucursalId = table.Column<Guid>(type: "TEXT", nullable: false),
                    CelularId = table.Column<Guid>(type: "TEXT", nullable: false),
                    CajaId = table.Column<Guid>(type: "TEXT", nullable: false),
                    ContoId = table.Column<Guid>(type: "TEXT", nullable: true),
                    ContadoUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    RecibidoUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    Estado = table.Column<string>(type: "TEXT", nullable: false),
                    ResolvioId = table.Column<Guid>(type: "TEXT", nullable: true),
                    ResueltoUtc = table.Column<DateTime>(type: "TEXT", nullable: true),
                    Motivo = table.Column<string>(type: "TEXT", maxLength: 500, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ConteosDelCelular", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ConteosDelCelular_Cajas_CajaId",
                        column: x => x.CajaId,
                        principalTable: "Cajas",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_ConteosDelCelular_Cajas_CelularId",
                        column: x => x.CelularId,
                        principalTable: "Cajas",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_ConteosDelCelular_Sucursales_SucursalId",
                        column: x => x.SucursalId,
                        principalTable: "Sucursales",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_ConteosDelCelular_Usuarios_ContoId",
                        column: x => x.ContoId,
                        principalTable: "Usuarios",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_ConteosDelCelular_Usuarios_ResolvioId",
                        column: x => x.ResolvioId,
                        principalTable: "Usuarios",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "ConteosDelCelularRenglones",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    ConteoId = table.Column<Guid>(type: "TEXT", nullable: false),
                    Orden = table.Column<int>(type: "INTEGER", nullable: false),
                    VarianteId = table.Column<Guid>(type: "TEXT", nullable: false),
                    Descripcion = table.Column<string>(type: "TEXT", maxLength: 300, nullable: false),
                    Detalle = table.Column<string>(type: "TEXT", maxLength: 300, nullable: true),
                    Contado = table.Column<long>(type: "INTEGER", nullable: false),
                    Aceptado = table.Column<long>(type: "INTEGER", nullable: true),
                    Desde = table.Column<long>(type: "INTEGER", nullable: true),
                    Sistema = table.Column<long>(type: "INTEGER", nullable: true),
                    CostoDiferenciaCentavos = table.Column<long>(type: "INTEGER", nullable: true),
                    Superado = table.Column<bool>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ConteosDelCelularRenglones", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ConteosDelCelularRenglones_ConteosDelCelular_ConteoId",
                        column: x => x.ConteoId,
                        principalTable: "ConteosDelCelular",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_ConteosDelCelularRenglones_Variantes_VarianteId",
                        column: x => x.VarianteId,
                        principalTable: "Variantes",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_ConteosDelCelular_CajaId_Estado",
                table: "ConteosDelCelular",
                columns: new[] { "CajaId", "Estado" });

            migrationBuilder.CreateIndex(
                name: "IX_ConteosDelCelular_CelularId_RecibidoUtc",
                table: "ConteosDelCelular",
                columns: new[] { "CelularId", "RecibidoUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_ConteosDelCelular_ContoId",
                table: "ConteosDelCelular",
                column: "ContoId");

            migrationBuilder.CreateIndex(
                name: "IX_ConteosDelCelular_ResolvioId",
                table: "ConteosDelCelular",
                column: "ResolvioId");

            migrationBuilder.CreateIndex(
                name: "IX_ConteosDelCelular_SucursalId",
                table: "ConteosDelCelular",
                column: "SucursalId");

            migrationBuilder.CreateIndex(
                name: "IX_ConteosDelCelularRenglones_ConteoId",
                table: "ConteosDelCelularRenglones",
                column: "ConteoId");

            migrationBuilder.CreateIndex(
                name: "IX_ConteosDelCelularRenglones_VarianteId",
                table: "ConteosDelCelularRenglones",
                column: "VarianteId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ConteosDelCelularRenglones");

            migrationBuilder.DropTable(
                name: "ConteosDelCelular");
        }
    }
}
