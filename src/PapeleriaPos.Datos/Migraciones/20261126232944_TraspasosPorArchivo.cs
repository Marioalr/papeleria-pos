using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PapeleriaPos.Datos.Migraciones
{
    /// <inheritdoc />
    public partial class TraspasosPorArchivo : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "Traspasos",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    SucursalId = table.Column<Guid>(type: "TEXT", nullable: false),
                    Sentido = table.Column<string>(type: "TEXT", nullable: false),
                    Estado = table.Column<string>(type: "TEXT", nullable: false),
                    OtraSucursal = table.Column<string>(type: "TEXT", maxLength: 60, nullable: false),
                    Nota = table.Column<string>(type: "TEXT", maxLength: 200, nullable: true),
                    Huella = table.Column<string>(type: "TEXT", maxLength: 64, nullable: true),
                    Archivo = table.Column<string>(type: "TEXT", nullable: true),
                    UsuarioId = table.Column<Guid>(type: "TEXT", nullable: false),
                    CreadoUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    CerroId = table.Column<Guid>(type: "TEXT", nullable: true),
                    CerradoUtc = table.Column<DateTime>(type: "TEXT", nullable: true),
                    MarcoId = table.Column<Guid>(type: "TEXT", nullable: true),
                    MarcadoUtc = table.Column<DateTime>(type: "TEXT", nullable: true),
                    MovioExistencias = table.Column<bool>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Traspasos", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Traspasos_Sucursales_SucursalId",
                        column: x => x.SucursalId,
                        principalTable: "Sucursales",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_Traspasos_Usuarios_CerroId",
                        column: x => x.CerroId,
                        principalTable: "Usuarios",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_Traspasos_Usuarios_MarcoId",
                        column: x => x.MarcoId,
                        principalTable: "Usuarios",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_Traspasos_Usuarios_UsuarioId",
                        column: x => x.UsuarioId,
                        principalTable: "Usuarios",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "TraspasosRenglones",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    TraspasoId = table.Column<Guid>(type: "TEXT", nullable: false),
                    Orden = table.Column<int>(type: "INTEGER", nullable: false),
                    VarianteId = table.Column<Guid>(type: "TEXT", nullable: true),
                    VarianteDelArchivoId = table.Column<Guid>(type: "TEXT", nullable: true),
                    Codigo = table.Column<string>(type: "TEXT", maxLength: 100, nullable: true),
                    Nombre = table.Column<string>(type: "TEXT", maxLength: 300, nullable: false),
                    Unidad = table.Column<string>(type: "TEXT", nullable: false),
                    Enviada = table.Column<long>(type: "INTEGER", nullable: false),
                    Recibida = table.Column<long>(type: "INTEGER", nullable: false),
                    Nota = table.Column<string>(type: "TEXT", maxLength: 200, nullable: true),
                    Agregado = table.Column<bool>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TraspasosRenglones", x => x.Id);
                    table.ForeignKey(
                        name: "FK_TraspasosRenglones_Traspasos_TraspasoId",
                        column: x => x.TraspasoId,
                        principalTable: "Traspasos",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_TraspasosRenglones_Variantes_VarianteDelArchivoId",
                        column: x => x.VarianteDelArchivoId,
                        principalTable: "Variantes",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_TraspasosRenglones_Variantes_VarianteId",
                        column: x => x.VarianteId,
                        principalTable: "Variantes",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_Traspasos_CerroId",
                table: "Traspasos",
                column: "CerroId");

            migrationBuilder.CreateIndex(
                name: "IX_Traspasos_MarcoId",
                table: "Traspasos",
                column: "MarcoId");

            migrationBuilder.CreateIndex(
                name: "IX_Traspasos_SucursalId_CreadoUtc",
                table: "Traspasos",
                columns: new[] { "SucursalId", "CreadoUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_Traspasos_UsuarioId",
                table: "Traspasos",
                column: "UsuarioId");

            migrationBuilder.CreateIndex(
                name: "IX_TraspasosRenglones_TraspasoId",
                table: "TraspasosRenglones",
                column: "TraspasoId");

            migrationBuilder.CreateIndex(
                name: "IX_TraspasosRenglones_VarianteDelArchivoId",
                table: "TraspasosRenglones",
                column: "VarianteDelArchivoId");

            migrationBuilder.CreateIndex(
                name: "IX_TraspasosRenglones_VarianteId",
                table: "TraspasosRenglones",
                column: "VarianteId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "TraspasosRenglones");

            migrationBuilder.DropTable(
                name: "Traspasos");
        }
    }
}
