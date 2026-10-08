using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PapeleriaPos.Datos.Migraciones
{
    /// <inheritdoc />
    public partial class CreditoClientes : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "AutorizacionCreditoId",
                table: "Ventas",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "AlCreditoCentavos",
                table: "Devoluciones",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0L);

            migrationBuilder.AddColumn<int>(
                name: "Abonos",
                table: "Cortes",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<long>(
                name: "AbonosEfectivoCentavos",
                table: "Cortes",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0L);

            migrationBuilder.AddColumn<long>(
                name: "AbonosTarjetaCentavos",
                table: "Cortes",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0L);

            migrationBuilder.AddColumn<long>(
                name: "AbonosTransferenciaCentavos",
                table: "Cortes",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0L);

            migrationBuilder.AddColumn<long>(
                name: "CreditoCentavos",
                table: "Cortes",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0L);

            migrationBuilder.AddColumn<long>(
                name: "LimiteCreditoCentavos",
                table: "ClientesRegistrados",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0L);

            migrationBuilder.AddColumn<int>(
                name: "PlazoCreditoDias",
                table: "ClientesRegistrados",
                type: "INTEGER",
                nullable: false,
                defaultValue: 30);

            migrationBuilder.CreateTable(
                name: "Abonos",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    SucursalId = table.Column<Guid>(type: "TEXT", nullable: false),
                    CajaId = table.Column<Guid>(type: "TEXT", nullable: false),
                    TurnoId = table.Column<Guid>(type: "TEXT", nullable: false),
                    ClienteId = table.Column<Guid>(type: "TEXT", nullable: false),
                    UsuarioId = table.Column<Guid>(type: "TEXT", nullable: false),
                    Forma = table.Column<string>(type: "TEXT", nullable: false),
                    MontoCentavos = table.Column<long>(type: "INTEGER", nullable: false),
                    RedondeoCentavos = table.Column<long>(type: "INTEGER", nullable: false),
                    RecibidoCentavos = table.Column<long>(type: "INTEGER", nullable: false),
                    CambioCentavos = table.Column<long>(type: "INTEGER", nullable: false),
                    Referencia = table.Column<string>(type: "TEXT", maxLength: 60, nullable: true),
                    Verificacion = table.Column<string>(type: "TEXT", nullable: false),
                    VerificadoPorId = table.Column<Guid>(type: "TEXT", nullable: true),
                    VerificadoUtc = table.Column<DateTime>(type: "TEXT", nullable: true),
                    SaldoAnteriorCentavos = table.Column<long>(type: "INTEGER", nullable: false),
                    SaldoCentavos = table.Column<long>(type: "INTEGER", nullable: false),
                    FechaUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    EnContingencia = table.Column<bool>(type: "INTEGER", nullable: false),
                    CanceladoUtc = table.Column<DateTime>(type: "TEXT", nullable: true),
                    CanceladoPorId = table.Column<Guid>(type: "TEXT", nullable: true),
                    AutorizoCancelacionId = table.Column<Guid>(type: "TEXT", nullable: true),
                    MotivoCancelacion = table.Column<string>(type: "TEXT", maxLength: 200, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Abonos", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Abonos_Cajas_CajaId",
                        column: x => x.CajaId,
                        principalTable: "Cajas",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_Abonos_ClientesRegistrados_ClienteId",
                        column: x => x.ClienteId,
                        principalTable: "ClientesRegistrados",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_Abonos_Sucursales_SucursalId",
                        column: x => x.SucursalId,
                        principalTable: "Sucursales",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_Abonos_Turnos_TurnoId",
                        column: x => x.TurnoId,
                        principalTable: "Turnos",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_Abonos_Usuarios_UsuarioId",
                        column: x => x.UsuarioId,
                        principalTable: "Usuarios",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "RevisionesCredito",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    SucursalId = table.Column<Guid>(type: "TEXT", nullable: false),
                    ClienteId = table.Column<Guid>(type: "TEXT", nullable: false),
                    MovimientoId = table.Column<Guid>(type: "TEXT", nullable: false),
                    Motivo = table.Column<string>(type: "TEXT", nullable: false),
                    Detalle = table.Column<string>(type: "TEXT", maxLength: 300, nullable: false),
                    MontoCentavos = table.Column<long>(type: "INTEGER", nullable: false),
                    CreadaUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    RevisadaUtc = table.Column<DateTime>(type: "TEXT", nullable: true),
                    RevisoId = table.Column<Guid>(type: "TEXT", nullable: true),
                    Nota = table.Column<string>(type: "TEXT", maxLength: 200, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_RevisionesCredito", x => x.Id);
                    table.ForeignKey(
                        name: "FK_RevisionesCredito_ClientesRegistrados_ClienteId",
                        column: x => x.ClienteId,
                        principalTable: "ClientesRegistrados",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_RevisionesCredito_Sucursales_SucursalId",
                        column: x => x.SucursalId,
                        principalTable: "Sucursales",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "MovimientosCredito",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    ClienteId = table.Column<Guid>(type: "TEXT", nullable: false),
                    SucursalId = table.Column<Guid>(type: "TEXT", nullable: false),
                    CajaId = table.Column<Guid>(type: "TEXT", nullable: true),
                    TurnoId = table.Column<Guid>(type: "TEXT", nullable: true),
                    UsuarioId = table.Column<Guid>(type: "TEXT", nullable: false),
                    Tipo = table.Column<string>(type: "TEXT", nullable: false),
                    MontoCentavos = table.Column<long>(type: "INTEGER", nullable: false),
                    FechaUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    Vence = table.Column<DateOnly>(type: "TEXT", nullable: true),
                    VentaId = table.Column<Guid>(type: "TEXT", nullable: true),
                    DevolucionId = table.Column<Guid>(type: "TEXT", nullable: true),
                    AbonoId = table.Column<Guid>(type: "TEXT", nullable: true),
                    SaldoCentavos = table.Column<long>(type: "INTEGER", nullable: false),
                    Motivo = table.Column<string>(type: "TEXT", maxLength: 200, nullable: true),
                    EnContingencia = table.Column<bool>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MovimientosCredito", x => x.Id);
                    table.ForeignKey(
                        name: "FK_MovimientosCredito_Abonos_AbonoId",
                        column: x => x.AbonoId,
                        principalTable: "Abonos",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_MovimientosCredito_Cajas_CajaId",
                        column: x => x.CajaId,
                        principalTable: "Cajas",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_MovimientosCredito_ClientesRegistrados_ClienteId",
                        column: x => x.ClienteId,
                        principalTable: "ClientesRegistrados",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_MovimientosCredito_Devoluciones_DevolucionId",
                        column: x => x.DevolucionId,
                        principalTable: "Devoluciones",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_MovimientosCredito_Sucursales_SucursalId",
                        column: x => x.SucursalId,
                        principalTable: "Sucursales",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_MovimientosCredito_Turnos_TurnoId",
                        column: x => x.TurnoId,
                        principalTable: "Turnos",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_MovimientosCredito_Usuarios_UsuarioId",
                        column: x => x.UsuarioId,
                        principalTable: "Usuarios",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_MovimientosCredito_Ventas_VentaId",
                        column: x => x.VentaId,
                        principalTable: "Ventas",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_Abonos_CajaId",
                table: "Abonos",
                column: "CajaId");

            migrationBuilder.CreateIndex(
                name: "IX_Abonos_ClienteId_FechaUtc",
                table: "Abonos",
                columns: new[] { "ClienteId", "FechaUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_Abonos_Forma_Verificacion",
                table: "Abonos",
                columns: new[] { "Forma", "Verificacion" });

            migrationBuilder.CreateIndex(
                name: "IX_Abonos_SucursalId",
                table: "Abonos",
                column: "SucursalId");

            migrationBuilder.CreateIndex(
                name: "IX_Abonos_TurnoId",
                table: "Abonos",
                column: "TurnoId");

            migrationBuilder.CreateIndex(
                name: "IX_Abonos_UsuarioId",
                table: "Abonos",
                column: "UsuarioId");

            migrationBuilder.CreateIndex(
                name: "IX_MovimientosCredito_AbonoId_Tipo",
                table: "MovimientosCredito",
                columns: new[] { "AbonoId", "Tipo" },
                unique: true,
                filter: "\"AbonoId\" IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_MovimientosCredito_CajaId",
                table: "MovimientosCredito",
                column: "CajaId");

            migrationBuilder.CreateIndex(
                name: "IX_MovimientosCredito_ClienteId_FechaUtc",
                table: "MovimientosCredito",
                columns: new[] { "ClienteId", "FechaUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_MovimientosCredito_DevolucionId",
                table: "MovimientosCredito",
                column: "DevolucionId");

            migrationBuilder.CreateIndex(
                name: "IX_MovimientosCredito_SucursalId",
                table: "MovimientosCredito",
                column: "SucursalId");

            migrationBuilder.CreateIndex(
                name: "IX_MovimientosCredito_TurnoId",
                table: "MovimientosCredito",
                column: "TurnoId");

            migrationBuilder.CreateIndex(
                name: "IX_MovimientosCredito_UsuarioId",
                table: "MovimientosCredito",
                column: "UsuarioId");

            migrationBuilder.CreateIndex(
                name: "IX_MovimientosCredito_VentaId",
                table: "MovimientosCredito",
                column: "VentaId");

            migrationBuilder.CreateIndex(
                name: "IX_RevisionesCredito_ClienteId",
                table: "RevisionesCredito",
                column: "ClienteId");

            migrationBuilder.CreateIndex(
                name: "IX_RevisionesCredito_SucursalId_RevisadaUtc",
                table: "RevisionesCredito",
                columns: new[] { "SucursalId", "RevisadaUtc" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "MovimientosCredito");

            migrationBuilder.DropTable(
                name: "RevisionesCredito");

            migrationBuilder.DropTable(
                name: "Abonos");

            migrationBuilder.DropColumn(
                name: "AutorizacionCreditoId",
                table: "Ventas");

            migrationBuilder.DropColumn(
                name: "AlCreditoCentavos",
                table: "Devoluciones");

            migrationBuilder.DropColumn(
                name: "Abonos",
                table: "Cortes");

            migrationBuilder.DropColumn(
                name: "AbonosEfectivoCentavos",
                table: "Cortes");

            migrationBuilder.DropColumn(
                name: "AbonosTarjetaCentavos",
                table: "Cortes");

            migrationBuilder.DropColumn(
                name: "AbonosTransferenciaCentavos",
                table: "Cortes");

            migrationBuilder.DropColumn(
                name: "CreditoCentavos",
                table: "Cortes");

            migrationBuilder.DropColumn(
                name: "LimiteCreditoCentavos",
                table: "ClientesRegistrados");

            migrationBuilder.DropColumn(
                name: "PlazoCreditoDias",
                table: "ClientesRegistrados");
        }
    }
}
