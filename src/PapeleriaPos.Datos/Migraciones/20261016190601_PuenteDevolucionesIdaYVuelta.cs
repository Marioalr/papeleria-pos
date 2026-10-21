using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PapeleriaPos.Datos.Migraciones
{
    /// <inheritdoc />
    public partial class PuenteDevolucionesIdaYVuelta : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "DevolucionDeEleventaId",
                table: "EnviosAEleventa",
                type: "TEXT",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "DevolucionesDeEleventa",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    SucursalId = table.Column<Guid>(type: "TEXT", nullable: false),
                    CajaId = table.Column<Guid>(type: "TEXT", nullable: false),
                    TurnoId = table.Column<Guid>(type: "TEXT", nullable: false),
                    UsuarioId = table.Column<Guid>(type: "TEXT", nullable: false),
                    AutorizoId = table.Column<Guid>(type: "TEXT", nullable: true),
                    Motivo = table.Column<string>(type: "TEXT", maxLength: 500, nullable: false),
                    FechaUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    TicketEleventa = table.Column<int>(type: "INTEGER", nullable: false),
                    Folio = table.Column<int>(type: "INTEGER", nullable: false),
                    CajaEleventa = table.Column<int>(type: "INTEGER", nullable: false),
                    CajaNombre = table.Column<string>(type: "TEXT", maxLength: 50, nullable: false),
                    TurnoTicketEleventa = table.Column<int>(type: "INTEGER", nullable: true),
                    VendidoUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    TicketTotalCentavos = table.Column<long>(type: "INTEGER", nullable: false),
                    Ticket = table.Column<string>(type: "TEXT", nullable: false),
                    Articulos = table.Column<string>(type: "TEXT", nullable: false),
                    TotalCentavos = table.Column<long>(type: "INTEGER", nullable: false),
                    ReembolsoEfectivoCentavos = table.Column<long>(type: "INTEGER", nullable: false),
                    RedondeoCentavos = table.Column<long>(type: "INTEGER", nullable: false),
                    MovimientoId = table.Column<Guid>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_DevolucionesDeEleventa", x => x.Id);
                    table.ForeignKey(
                        name: "FK_DevolucionesDeEleventa_MovimientosCaja_MovimientoId",
                        column: x => x.MovimientoId,
                        principalTable: "MovimientosCaja",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_DevolucionesDeEleventa_Turnos_TurnoId",
                        column: x => x.TurnoId,
                        principalTable: "Turnos",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_DevolucionesDeEleventa_Usuarios_UsuarioId",
                        column: x => x.UsuarioId,
                        principalTable: "Usuarios",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "DevolucionesDesdeEleventa",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    DevolucionEleventa = table.Column<int>(type: "INTEGER", nullable: false),
                    TicketEleventa = table.Column<int>(type: "INTEGER", nullable: false),
                    DevueltaUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    EsCancelacion = table.Column<bool>(type: "INTEGER", nullable: false),
                    TotalCentavos = table.Column<long>(type: "INTEGER", nullable: false),
                    Cajero = table.Column<string>(type: "TEXT", maxLength: 50, nullable: true),
                    Caja = table.Column<string>(type: "TEXT", maxLength: 50, nullable: true),
                    VentaId = table.Column<Guid>(type: "TEXT", nullable: false),
                    DevolucionId = table.Column<Guid>(type: "TEXT", nullable: true),
                    PorRevisar = table.Column<string>(type: "TEXT", maxLength: 500, nullable: true),
                    RevisadaUtc = table.Column<DateTime>(type: "TEXT", nullable: true),
                    VistaUtc = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_DevolucionesDesdeEleventa", x => x.Id);
                    table.ForeignKey(
                        name: "FK_DevolucionesDesdeEleventa_Devoluciones_DevolucionId",
                        column: x => x.DevolucionId,
                        principalTable: "Devoluciones",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_DevolucionesDesdeEleventa_Ventas_VentaId",
                        column: x => x.VentaId,
                        principalTable: "Ventas",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_EnviosAEleventa_DevolucionDeEleventaId",
                table: "EnviosAEleventa",
                column: "DevolucionDeEleventaId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_DevolucionesDeEleventa_MovimientoId",
                table: "DevolucionesDeEleventa",
                column: "MovimientoId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_DevolucionesDeEleventa_TicketEleventa",
                table: "DevolucionesDeEleventa",
                column: "TicketEleventa");

            migrationBuilder.CreateIndex(
                name: "IX_DevolucionesDeEleventa_TurnoId",
                table: "DevolucionesDeEleventa",
                column: "TurnoId");

            migrationBuilder.CreateIndex(
                name: "IX_DevolucionesDeEleventa_UsuarioId",
                table: "DevolucionesDeEleventa",
                column: "UsuarioId");

            migrationBuilder.CreateIndex(
                name: "IX_DevolucionesDesdeEleventa_DevolucionEleventa_DevueltaUtc",
                table: "DevolucionesDesdeEleventa",
                columns: new[] { "DevolucionEleventa", "DevueltaUtc" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_DevolucionesDesdeEleventa_DevolucionId",
                table: "DevolucionesDesdeEleventa",
                column: "DevolucionId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_DevolucionesDesdeEleventa_TicketEleventa",
                table: "DevolucionesDesdeEleventa",
                column: "TicketEleventa");

            migrationBuilder.CreateIndex(
                name: "IX_DevolucionesDesdeEleventa_VentaId",
                table: "DevolucionesDesdeEleventa",
                column: "VentaId");

            migrationBuilder.AddForeignKey(
                name: "FK_EnviosAEleventa_DevolucionesDeEleventa_DevolucionDeEleventaId",
                table: "EnviosAEleventa",
                column: "DevolucionDeEleventaId",
                principalTable: "DevolucionesDeEleventa",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_EnviosAEleventa_DevolucionesDeEleventa_DevolucionDeEleventaId",
                table: "EnviosAEleventa");

            migrationBuilder.DropTable(
                name: "DevolucionesDeEleventa");

            migrationBuilder.DropTable(
                name: "DevolucionesDesdeEleventa");

            migrationBuilder.DropIndex(
                name: "IX_EnviosAEleventa_DevolucionDeEleventaId",
                table: "EnviosAEleventa");

            migrationBuilder.DropColumn(
                name: "DevolucionDeEleventaId",
                table: "EnviosAEleventa");
        }
    }
}
