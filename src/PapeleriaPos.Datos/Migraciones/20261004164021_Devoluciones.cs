using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PapeleriaPos.Datos.Migraciones
{
    /// <inheritdoc />
    public partial class Devoluciones : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "AutorizacionPrecioId",
                table: "VentasLineas",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "CantidadDevuelta",
                table: "VentasLineas",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0L);

            migrationBuilder.AddColumn<long>(
                name: "PrecioOriginalCentavos",
                table: "VentasLineas",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "CanceladaPorId",
                table: "Ventas",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "CanceladaUtc",
                table: "Ventas",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "MotivoCancelacion",
                table: "Ventas",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "Cancelaciones",
                table: "Cortes",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<long>(
                name: "DevolucionesCentavos",
                table: "Cortes",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0L);

            migrationBuilder.CreateTable(
                name: "Devoluciones",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    VentaId = table.Column<Guid>(type: "TEXT", nullable: false),
                    SucursalId = table.Column<Guid>(type: "TEXT", nullable: false),
                    CajaId = table.Column<Guid>(type: "TEXT", nullable: false),
                    TurnoId = table.Column<Guid>(type: "TEXT", nullable: false),
                    UsuarioId = table.Column<Guid>(type: "TEXT", nullable: false),
                    AutorizoId = table.Column<Guid>(type: "TEXT", nullable: true),
                    EsCancelacion = table.Column<bool>(type: "INTEGER", nullable: false),
                    Motivo = table.Column<string>(type: "TEXT", nullable: false),
                    TotalCentavos = table.Column<long>(type: "INTEGER", nullable: false),
                    ReembolsoEfectivoCentavos = table.Column<long>(type: "INTEGER", nullable: false),
                    FechaUtc = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Devoluciones", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Devoluciones_Turnos_TurnoId",
                        column: x => x.TurnoId,
                        principalTable: "Turnos",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_Devoluciones_Usuarios_UsuarioId",
                        column: x => x.UsuarioId,
                        principalTable: "Usuarios",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_Devoluciones_Ventas_VentaId",
                        column: x => x.VentaId,
                        principalTable: "Ventas",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "DevolucionesLineas",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    DevolucionId = table.Column<Guid>(type: "TEXT", nullable: false),
                    VentaLineaId = table.Column<Guid>(type: "TEXT", nullable: false),
                    Cantidad = table.Column<long>(type: "INTEGER", nullable: false),
                    ImporteCentavos = table.Column<long>(type: "INTEGER", nullable: false),
                    CostoCentavos = table.Column<long>(type: "INTEGER", nullable: false),
                    RegresaAlInventario = table.Column<bool>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_DevolucionesLineas", x => x.Id);
                    table.ForeignKey(
                        name: "FK_DevolucionesLineas_Devoluciones_DevolucionId",
                        column: x => x.DevolucionId,
                        principalTable: "Devoluciones",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_DevolucionesLineas_VentasLineas_VentaLineaId",
                        column: x => x.VentaLineaId,
                        principalTable: "VentasLineas",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_Devoluciones_TurnoId",
                table: "Devoluciones",
                column: "TurnoId");

            migrationBuilder.CreateIndex(
                name: "IX_Devoluciones_UsuarioId",
                table: "Devoluciones",
                column: "UsuarioId");

            migrationBuilder.CreateIndex(
                name: "IX_Devoluciones_VentaId",
                table: "Devoluciones",
                column: "VentaId");

            migrationBuilder.CreateIndex(
                name: "IX_DevolucionesLineas_DevolucionId",
                table: "DevolucionesLineas",
                column: "DevolucionId");

            migrationBuilder.CreateIndex(
                name: "IX_DevolucionesLineas_VentaLineaId",
                table: "DevolucionesLineas",
                column: "VentaLineaId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "DevolucionesLineas");

            migrationBuilder.DropTable(
                name: "Devoluciones");

            migrationBuilder.DropColumn(
                name: "AutorizacionPrecioId",
                table: "VentasLineas");

            migrationBuilder.DropColumn(
                name: "CantidadDevuelta",
                table: "VentasLineas");

            migrationBuilder.DropColumn(
                name: "PrecioOriginalCentavos",
                table: "VentasLineas");

            migrationBuilder.DropColumn(
                name: "CanceladaPorId",
                table: "Ventas");

            migrationBuilder.DropColumn(
                name: "CanceladaUtc",
                table: "Ventas");

            migrationBuilder.DropColumn(
                name: "MotivoCancelacion",
                table: "Ventas");

            migrationBuilder.DropColumn(
                name: "Cancelaciones",
                table: "Cortes");

            migrationBuilder.DropColumn(
                name: "DevolucionesCentavos",
                table: "Cortes");
        }
    }
}
