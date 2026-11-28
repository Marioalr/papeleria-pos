using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PapeleriaPos.Datos.Migraciones
{
    /// <inheritdoc />
    public partial class AvisosYPapel : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "Avisos",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    SucursalId = table.Column<Guid>(type: "TEXT", nullable: false),
                    CajaId = table.Column<Guid>(type: "TEXT", nullable: true),
                    Modulo = table.Column<string>(type: "TEXT", maxLength: 40, nullable: false),
                    Clave = table.Column<string>(type: "TEXT", maxLength: 200, nullable: false),
                    Nivel = table.Column<string>(type: "TEXT", nullable: false),
                    Titulo = table.Column<string>(type: "TEXT", nullable: false),
                    Texto = table.Column<string>(type: "TEXT", nullable: false),
                    Accion = table.Column<string>(type: "TEXT", nullable: true),
                    AccionTexto = table.Column<string>(type: "TEXT", nullable: true),
                    AccionDatos = table.Column<string>(type: "TEXT", nullable: true),
                    DesdeUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    ActualizadoUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    RetiradoUtc = table.Column<DateTime>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Avisos", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Avisos_Cajas_CajaId",
                        column: x => x.CajaId,
                        principalTable: "Cajas",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_Avisos_Sucursales_SucursalId",
                        column: x => x.SucursalId,
                        principalTable: "Sucursales",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "LlenadosPapel",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    SucursalId = table.Column<Guid>(type: "TEXT", nullable: false),
                    ImpresoraId = table.Column<Guid>(type: "TEXT", nullable: false),
                    Charola = table.Column<int>(type: "INTEGER", nullable: false),
                    Puestas = table.Column<int>(type: "INTEGER", nullable: false),
                    Quedaron = table.Column<int>(type: "INTEGER", nullable: false),
                    Tamano = table.Column<string>(type: "TEXT", maxLength: 20, nullable: false),
                    AnotadoUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    UsuarioId = table.Column<Guid>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_LlenadosPapel", x => x.Id);
                    table.ForeignKey(
                        name: "FK_LlenadosPapel_Impresoras_ImpresoraId",
                        column: x => x.ImpresoraId,
                        principalTable: "Impresoras",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_LlenadosPapel_Sucursales_SucursalId",
                        column: x => x.SucursalId,
                        principalTable: "Sucursales",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_Avisos_CajaId",
                table: "Avisos",
                column: "CajaId");

            migrationBuilder.CreateIndex(
                name: "IX_Avisos_SucursalId_RetiradoUtc",
                table: "Avisos",
                columns: new[] { "SucursalId", "RetiradoUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_Avisos_Vigente",
                table: "Avisos",
                columns: new[] { "SucursalId", "Modulo", "Clave" },
                unique: true,
                filter: "\"RetiradoUtc\" IS NULL");

            migrationBuilder.CreateIndex(
                name: "IX_LlenadosPapel_ImpresoraId_Charola_AnotadoUtc",
                table: "LlenadosPapel",
                columns: new[] { "ImpresoraId", "Charola", "AnotadoUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_LlenadosPapel_SucursalId",
                table: "LlenadosPapel",
                column: "SucursalId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "Avisos");

            migrationBuilder.DropTable(
                name: "LlenadosPapel");
        }
    }
}
