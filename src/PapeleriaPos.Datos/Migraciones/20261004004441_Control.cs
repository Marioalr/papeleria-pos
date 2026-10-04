using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PapeleriaPos.Datos.Migraciones
{
    /// <inheritdoc />
    public partial class Control : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "Autorizaciones",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    Accion = table.Column<string>(type: "TEXT", nullable: false),
                    SucursalId = table.Column<Guid>(type: "TEXT", nullable: false),
                    CajaId = table.Column<Guid>(type: "TEXT", nullable: true),
                    SolicitanteId = table.Column<Guid>(type: "TEXT", nullable: false),
                    AutorizadorId = table.Column<Guid>(type: "TEXT", nullable: false),
                    MontoCentavos = table.Column<long>(type: "INTEGER", nullable: true),
                    Detalle = table.Column<string>(type: "TEXT", nullable: false),
                    CreadaUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    VenceUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    UsadaUtc = table.Column<DateTime>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Autorizaciones", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Autorizaciones_Usuarios_AutorizadorId",
                        column: x => x.AutorizadorId,
                        principalTable: "Usuarios",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_Autorizaciones_Usuarios_SolicitanteId",
                        column: x => x.SolicitanteId,
                        principalTable: "Usuarios",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "Bitacora",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    SucursalId = table.Column<Guid>(type: "TEXT", nullable: false),
                    CajaId = table.Column<Guid>(type: "TEXT", nullable: true),
                    UsuarioId = table.Column<Guid>(type: "TEXT", nullable: true),
                    Accion = table.Column<string>(type: "TEXT", nullable: false),
                    Detalle = table.Column<string>(type: "TEXT", nullable: false),
                    MontoCentavos = table.Column<long>(type: "INTEGER", nullable: true),
                    VentaId = table.Column<Guid>(type: "TEXT", nullable: true),
                    AutorizoId = table.Column<Guid>(type: "TEXT", nullable: true),
                    Resultado = table.Column<string>(type: "TEXT", nullable: false),
                    FechaUtc = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Bitacora", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Bitacora_Sucursales_SucursalId",
                        column: x => x.SucursalId,
                        principalTable: "Sucursales",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_Bitacora_Usuarios_UsuarioId",
                        column: x => x.UsuarioId,
                        principalTable: "Usuarios",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "ConfiguracionesSucursal",
                columns: table => new
                {
                    SucursalId = table.Column<Guid>(type: "TEXT", nullable: false),
                    LimiteEfectivoCentavos = table.Column<long>(type: "INTEGER", nullable: true),
                    Version = table.Column<long>(type: "INTEGER", nullable: false),
                    ModificadoUtc = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ConfiguracionesSucursal", x => x.SucursalId);
                    table.ForeignKey(
                        name: "FK_ConfiguracionesSucursal_Sucursales_SucursalId",
                        column: x => x.SucursalId,
                        principalTable: "Sucursales",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "ReglasPermiso",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    Accion = table.Column<string>(type: "TEXT", nullable: false),
                    Rol = table.Column<string>(type: "TEXT", nullable: false),
                    SucursalId = table.Column<Guid>(type: "TEXT", nullable: true),
                    Nivel = table.Column<string>(type: "TEXT", nullable: false),
                    UmbralCentavos = table.Column<long>(type: "INTEGER", nullable: true),
                    PideMotivo = table.Column<bool>(type: "INTEGER", nullable: false),
                    Version = table.Column<long>(type: "INTEGER", nullable: false),
                    ModificadoUtc = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ReglasPermiso", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ReglasPermiso_Sucursales_SucursalId",
                        column: x => x.SucursalId,
                        principalTable: "Sucursales",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_Autorizaciones_AutorizadorId",
                table: "Autorizaciones",
                column: "AutorizadorId");

            migrationBuilder.CreateIndex(
                name: "IX_Autorizaciones_SolicitanteId",
                table: "Autorizaciones",
                column: "SolicitanteId");

            migrationBuilder.CreateIndex(
                name: "IX_Bitacora_SucursalId_FechaUtc",
                table: "Bitacora",
                columns: new[] { "SucursalId", "FechaUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_Bitacora_UsuarioId",
                table: "Bitacora",
                column: "UsuarioId");

            migrationBuilder.CreateIndex(
                name: "IX_Bitacora_VentaId",
                table: "Bitacora",
                column: "VentaId");

            migrationBuilder.CreateIndex(
                name: "IX_ReglasPermiso_Accion_Rol_SucursalId",
                table: "ReglasPermiso",
                columns: new[] { "Accion", "Rol", "SucursalId" });

            migrationBuilder.CreateIndex(
                name: "IX_ReglasPermiso_SucursalId",
                table: "ReglasPermiso",
                column: "SucursalId");

            migrationBuilder.CreateIndex(
                name: "IX_ReglasPermiso_Version",
                table: "ReglasPermiso",
                column: "Version");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "Autorizaciones");

            migrationBuilder.DropTable(
                name: "Bitacora");

            migrationBuilder.DropTable(
                name: "ConfiguracionesSucursal");

            migrationBuilder.DropTable(
                name: "ReglasPermiso");
        }
    }
}
