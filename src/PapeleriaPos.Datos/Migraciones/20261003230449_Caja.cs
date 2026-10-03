using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PapeleriaPos.Datos.Migraciones
{
    /// <inheritdoc />
    public partial class Caja : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "Cajas",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    SucursalId = table.Column<Guid>(type: "TEXT", nullable: false),
                    Nombre = table.Column<string>(type: "TEXT", nullable: false),
                    Serie = table.Column<string>(type: "TEXT", nullable: false),
                    EsPrincipal = table.Column<bool>(type: "INTEGER", nullable: false),
                    Activa = table.Column<bool>(type: "INTEGER", nullable: false),
                    Version = table.Column<long>(type: "INTEGER", nullable: false),
                    ModificadoUtc = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Cajas", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Cajas_Sucursales_SucursalId",
                        column: x => x.SucursalId,
                        principalTable: "Sucursales",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "Usuarios",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    Nombre = table.Column<string>(type: "TEXT", nullable: false),
                    NombreUsuario = table.Column<string>(type: "TEXT", nullable: false),
                    PinHash = table.Column<string>(type: "TEXT", nullable: false),
                    Rol = table.Column<string>(type: "TEXT", nullable: false),
                    SucursalId = table.Column<Guid>(type: "TEXT", nullable: true),
                    Activo = table.Column<bool>(type: "INTEGER", nullable: false),
                    IntentosFallidos = table.Column<int>(type: "INTEGER", nullable: false),
                    BloqueadoHastaUtc = table.Column<DateTime>(type: "TEXT", nullable: true),
                    Version = table.Column<long>(type: "INTEGER", nullable: false),
                    ModificadoUtc = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Usuarios", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Usuarios_Sucursales_SucursalId",
                        column: x => x.SucursalId,
                        principalTable: "Sucursales",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "Turnos",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    SucursalId = table.Column<Guid>(type: "TEXT", nullable: false),
                    CajaId = table.Column<Guid>(type: "TEXT", nullable: false),
                    CajeroId = table.Column<Guid>(type: "TEXT", nullable: false),
                    AbiertoUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    FondoCentavos = table.Column<long>(type: "INTEGER", nullable: false),
                    CerradoUtc = table.Column<DateTime>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Turnos", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Turnos_Cajas_CajaId",
                        column: x => x.CajaId,
                        principalTable: "Cajas",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_Turnos_Sucursales_SucursalId",
                        column: x => x.SucursalId,
                        principalTable: "Sucursales",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_Turnos_Usuarios_CajeroId",
                        column: x => x.CajeroId,
                        principalTable: "Usuarios",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "AperturasCajon",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    CajaId = table.Column<Guid>(type: "TEXT", nullable: false),
                    TurnoId = table.Column<Guid>(type: "TEXT", nullable: true),
                    UsuarioId = table.Column<Guid>(type: "TEXT", nullable: false),
                    Motivo = table.Column<string>(type: "TEXT", nullable: false),
                    VentaId = table.Column<Guid>(type: "TEXT", nullable: true),
                    Nota = table.Column<string>(type: "TEXT", nullable: true),
                    FechaUtc = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AperturasCajon", x => x.Id);
                    table.ForeignKey(
                        name: "FK_AperturasCajon_Cajas_CajaId",
                        column: x => x.CajaId,
                        principalTable: "Cajas",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_AperturasCajon_Turnos_TurnoId",
                        column: x => x.TurnoId,
                        principalTable: "Turnos",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_AperturasCajon_Usuarios_UsuarioId",
                        column: x => x.UsuarioId,
                        principalTable: "Usuarios",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "Cortes",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    TurnoId = table.Column<Guid>(type: "TEXT", nullable: false),
                    Tipo = table.Column<string>(type: "TEXT", nullable: false),
                    FechaUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    RealizadoPorId = table.Column<Guid>(type: "TEXT", nullable: false),
                    Ventas = table.Column<int>(type: "INTEGER", nullable: false),
                    FondoCentavos = table.Column<long>(type: "INTEGER", nullable: false),
                    EfectivoVentasCentavos = table.Column<long>(type: "INTEGER", nullable: false),
                    EntradasCentavos = table.Column<long>(type: "INTEGER", nullable: false),
                    SalidasCentavos = table.Column<long>(type: "INTEGER", nullable: false),
                    TarjetaCentavos = table.Column<long>(type: "INTEGER", nullable: false),
                    TransferenciaCentavos = table.Column<long>(type: "INTEGER", nullable: false),
                    EsperadoCentavos = table.Column<long>(type: "INTEGER", nullable: false),
                    ContadoCentavos = table.Column<long>(type: "INTEGER", nullable: true),
                    DiferenciaCentavos = table.Column<long>(type: "INTEGER", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Cortes", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Cortes_Turnos_TurnoId",
                        column: x => x.TurnoId,
                        principalTable: "Turnos",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_Cortes_Usuarios_RealizadoPorId",
                        column: x => x.RealizadoPorId,
                        principalTable: "Usuarios",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "MovimientosCaja",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    TurnoId = table.Column<Guid>(type: "TEXT", nullable: false),
                    Tipo = table.Column<string>(type: "TEXT", nullable: false),
                    MontoCentavos = table.Column<long>(type: "INTEGER", nullable: false),
                    Motivo = table.Column<string>(type: "TEXT", nullable: false),
                    UsuarioId = table.Column<Guid>(type: "TEXT", nullable: false),
                    FechaUtc = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MovimientosCaja", x => x.Id);
                    table.ForeignKey(
                        name: "FK_MovimientosCaja_Turnos_TurnoId",
                        column: x => x.TurnoId,
                        principalTable: "Turnos",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_MovimientosCaja_Usuarios_UsuarioId",
                        column: x => x.UsuarioId,
                        principalTable: "Usuarios",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "Ventas",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    SucursalId = table.Column<Guid>(type: "TEXT", nullable: false),
                    CajaId = table.Column<Guid>(type: "TEXT", nullable: false),
                    TurnoId = table.Column<Guid>(type: "TEXT", nullable: false),
                    CajeroId = table.Column<Guid>(type: "TEXT", nullable: false),
                    Serie = table.Column<string>(type: "TEXT", nullable: false),
                    Folio = table.Column<int>(type: "INTEGER", nullable: true),
                    Estado = table.Column<string>(type: "TEXT", nullable: false),
                    CreadaUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    PagadaUtc = table.Column<DateTime>(type: "TEXT", nullable: true),
                    TotalCentavos = table.Column<long>(type: "INTEGER", nullable: false),
                    IvaCentavos = table.Column<long>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Ventas", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Ventas_Cajas_CajaId",
                        column: x => x.CajaId,
                        principalTable: "Cajas",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_Ventas_Sucursales_SucursalId",
                        column: x => x.SucursalId,
                        principalTable: "Sucursales",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_Ventas_Turnos_TurnoId",
                        column: x => x.TurnoId,
                        principalTable: "Turnos",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_Ventas_Usuarios_CajeroId",
                        column: x => x.CajeroId,
                        principalTable: "Usuarios",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "CortesConteo",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    CorteId = table.Column<Guid>(type: "TEXT", nullable: false),
                    DenominacionCentavos = table.Column<long>(type: "INTEGER", nullable: false),
                    Cantidad = table.Column<int>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CortesConteo", x => x.Id);
                    table.ForeignKey(
                        name: "FK_CortesConteo_Cortes_CorteId",
                        column: x => x.CorteId,
                        principalTable: "Cortes",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "Cobros",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    VentaId = table.Column<Guid>(type: "TEXT", nullable: false),
                    Forma = table.Column<string>(type: "TEXT", nullable: false),
                    MontoCentavos = table.Column<long>(type: "INTEGER", nullable: false),
                    RecibidoCentavos = table.Column<long>(type: "INTEGER", nullable: false),
                    CambioCentavos = table.Column<long>(type: "INTEGER", nullable: false),
                    Referencia = table.Column<string>(type: "TEXT", nullable: true),
                    Verificacion = table.Column<string>(type: "TEXT", nullable: false),
                    VerificadoPorId = table.Column<Guid>(type: "TEXT", nullable: true),
                    VerificadoUtc = table.Column<DateTime>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Cobros", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Cobros_Usuarios_VerificadoPorId",
                        column: x => x.VerificadoPorId,
                        principalTable: "Usuarios",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_Cobros_Ventas_VentaId",
                        column: x => x.VentaId,
                        principalTable: "Ventas",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "VentasLineas",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    VentaId = table.Column<Guid>(type: "TEXT", nullable: false),
                    VarianteId = table.Column<Guid>(type: "TEXT", nullable: true),
                    PresentacionId = table.Column<Guid>(type: "TEXT", nullable: true),
                    Descripcion = table.Column<string>(type: "TEXT", nullable: false),
                    Impuesto = table.Column<string>(type: "TEXT", nullable: false),
                    Unidad = table.Column<string>(type: "TEXT", nullable: false),
                    LlevaInventario = table.Column<bool>(type: "INTEGER", nullable: false),
                    Cantidad = table.Column<long>(type: "INTEGER", nullable: false),
                    CantidadBase = table.Column<long>(type: "INTEGER", nullable: false),
                    PrecioCentavos = table.Column<long>(type: "INTEGER", nullable: false),
                    ImporteCentavos = table.Column<long>(type: "INTEGER", nullable: false),
                    IvaCentavos = table.Column<long>(type: "INTEGER", nullable: false),
                    CostoCentavos = table.Column<long>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_VentasLineas", x => x.Id);
                    table.ForeignKey(
                        name: "FK_VentasLineas_Presentaciones_PresentacionId",
                        column: x => x.PresentacionId,
                        principalTable: "Presentaciones",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_VentasLineas_Variantes_VarianteId",
                        column: x => x.VarianteId,
                        principalTable: "Variantes",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_VentasLineas_Ventas_VentaId",
                        column: x => x.VentaId,
                        principalTable: "Ventas",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_AperturasCajon_CajaId_FechaUtc",
                table: "AperturasCajon",
                columns: new[] { "CajaId", "FechaUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_AperturasCajon_TurnoId",
                table: "AperturasCajon",
                column: "TurnoId");

            migrationBuilder.CreateIndex(
                name: "IX_AperturasCajon_UsuarioId",
                table: "AperturasCajon",
                column: "UsuarioId");

            migrationBuilder.CreateIndex(
                name: "IX_Cajas_SucursalId_Serie",
                table: "Cajas",
                columns: new[] { "SucursalId", "Serie" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Cajas_Version",
                table: "Cajas",
                column: "Version");

            migrationBuilder.CreateIndex(
                name: "IX_Cobros_VentaId",
                table: "Cobros",
                column: "VentaId");

            migrationBuilder.CreateIndex(
                name: "IX_Cobros_Verificacion",
                table: "Cobros",
                column: "Verificacion");

            migrationBuilder.CreateIndex(
                name: "IX_Cobros_VerificadoPorId",
                table: "Cobros",
                column: "VerificadoPorId");

            migrationBuilder.CreateIndex(
                name: "IX_Cortes_RealizadoPorId",
                table: "Cortes",
                column: "RealizadoPorId");

            migrationBuilder.CreateIndex(
                name: "IX_Cortes_TurnoId",
                table: "Cortes",
                column: "TurnoId");

            migrationBuilder.CreateIndex(
                name: "IX_CortesConteo_CorteId",
                table: "CortesConteo",
                column: "CorteId");

            migrationBuilder.CreateIndex(
                name: "IX_MovimientosCaja_TurnoId",
                table: "MovimientosCaja",
                column: "TurnoId");

            migrationBuilder.CreateIndex(
                name: "IX_MovimientosCaja_UsuarioId",
                table: "MovimientosCaja",
                column: "UsuarioId");

            migrationBuilder.CreateIndex(
                name: "IX_Turnos_CajaId_CerradoUtc",
                table: "Turnos",
                columns: new[] { "CajaId", "CerradoUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_Turnos_CajeroId",
                table: "Turnos",
                column: "CajeroId");

            migrationBuilder.CreateIndex(
                name: "IX_Turnos_SucursalId",
                table: "Turnos",
                column: "SucursalId");

            migrationBuilder.CreateIndex(
                name: "IX_Usuarios_NombreUsuario",
                table: "Usuarios",
                column: "NombreUsuario",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Usuarios_SucursalId",
                table: "Usuarios",
                column: "SucursalId");

            migrationBuilder.CreateIndex(
                name: "IX_Usuarios_Version",
                table: "Usuarios",
                column: "Version");

            migrationBuilder.CreateIndex(
                name: "IX_Ventas_CajaId",
                table: "Ventas",
                column: "CajaId");

            migrationBuilder.CreateIndex(
                name: "IX_Ventas_CajeroId",
                table: "Ventas",
                column: "CajeroId");

            migrationBuilder.CreateIndex(
                name: "IX_Ventas_PagadaUtc",
                table: "Ventas",
                column: "PagadaUtc");

            migrationBuilder.CreateIndex(
                name: "IX_Ventas_SucursalId_Serie_Folio",
                table: "Ventas",
                columns: new[] { "SucursalId", "Serie", "Folio" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Ventas_TurnoId",
                table: "Ventas",
                column: "TurnoId");

            migrationBuilder.CreateIndex(
                name: "IX_VentasLineas_PresentacionId",
                table: "VentasLineas",
                column: "PresentacionId");

            migrationBuilder.CreateIndex(
                name: "IX_VentasLineas_VarianteId",
                table: "VentasLineas",
                column: "VarianteId");

            migrationBuilder.CreateIndex(
                name: "IX_VentasLineas_VentaId",
                table: "VentasLineas",
                column: "VentaId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "AperturasCajon");

            migrationBuilder.DropTable(
                name: "Cobros");

            migrationBuilder.DropTable(
                name: "CortesConteo");

            migrationBuilder.DropTable(
                name: "MovimientosCaja");

            migrationBuilder.DropTable(
                name: "VentasLineas");

            migrationBuilder.DropTable(
                name: "Cortes");

            migrationBuilder.DropTable(
                name: "Ventas");

            migrationBuilder.DropTable(
                name: "Turnos");

            migrationBuilder.DropTable(
                name: "Cajas");

            migrationBuilder.DropTable(
                name: "Usuarios");
        }
    }
}
