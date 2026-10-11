using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PapeleriaPos.Datos.Migraciones
{
    /// <summary>
    /// La bandeja hacia eleventa lleva también cancelaciones, devoluciones y cierres de turno (el piloto, paso 4). La
    /// tabla se hizo el mismo día y solo se usó en pruebas: se vuelve a crear en lugar de convertirla.
    /// </summary>
    public partial class BandejaEleventaCompleta : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(name: "EnviosAEleventa");

            migrationBuilder.CreateTable(
                name: "EnviosAEleventa",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    Tipo = table.Column<string>(type: "TEXT", nullable: false),
                    VentaId = table.Column<Guid>(type: "TEXT", nullable: true),
                    DevolucionId = table.Column<Guid>(type: "TEXT", nullable: true),
                    TurnoId = table.Column<Guid>(type: "TEXT", nullable: true),
                    Estado = table.Column<string>(type: "TEXT", nullable: false),
                    CreadoUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    Intentos = table.Column<int>(type: "INTEGER", nullable: false),
                    UltimoIntentoUtc = table.Column<DateTime>(type: "TEXT", nullable: true),
                    Error = table.Column<string>(type: "TEXT", maxLength: 500, nullable: true),
                    EnviadaUtc = table.Column<DateTime>(type: "TEXT", nullable: true),
                    TicketEleventa = table.Column<int>(type: "INTEGER", nullable: true),
                    FolioEleventa = table.Column<int>(type: "INTEGER", nullable: true),
                    FolioRepetido = table.Column<bool>(type: "INTEGER", nullable: false),
                    IdEleventa = table.Column<int>(type: "INTEGER", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_EnviosAEleventa", x => x.Id);
                    table.ForeignKey(
                        name: "FK_EnviosAEleventa_Devoluciones_DevolucionId",
                        column: x => x.DevolucionId,
                        principalTable: "Devoluciones",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_EnviosAEleventa_Turnos_TurnoId",
                        column: x => x.TurnoId,
                        principalTable: "Turnos",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_EnviosAEleventa_Ventas_VentaId",
                        column: x => x.VentaId,
                        principalTable: "Ventas",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_EnviosAEleventa_DevolucionId",
                table: "EnviosAEleventa",
                column: "DevolucionId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_EnviosAEleventa_Estado_CreadoUtc",
                table: "EnviosAEleventa",
                columns: new[] { "Estado", "CreadoUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_EnviosAEleventa_FolioEleventa",
                table: "EnviosAEleventa",
                column: "FolioEleventa");

            migrationBuilder.CreateIndex(
                name: "IX_EnviosAEleventa_TurnoId",
                table: "EnviosAEleventa",
                column: "TurnoId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_EnviosAEleventa_VentaId",
                table: "EnviosAEleventa",
                column: "VentaId",
                unique: true,
                filter: "\"Tipo\" = 'Venta'");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(name: "EnviosAEleventa");

            migrationBuilder.CreateTable(
                name: "EnviosAEleventa",
                columns: table => new
                {
                    VentaId = table.Column<Guid>(type: "TEXT", nullable: false),
                    Estado = table.Column<string>(type: "TEXT", nullable: false),
                    CreadoUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    Intentos = table.Column<int>(type: "INTEGER", nullable: false),
                    UltimoIntentoUtc = table.Column<DateTime>(type: "TEXT", nullable: true),
                    Error = table.Column<string>(type: "TEXT", maxLength: 500, nullable: true),
                    EnviadaUtc = table.Column<DateTime>(type: "TEXT", nullable: true),
                    TicketEleventa = table.Column<int>(type: "INTEGER", nullable: true),
                    FolioEleventa = table.Column<int>(type: "INTEGER", nullable: true),
                    FolioRepetido = table.Column<bool>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_EnviosAEleventa", x => x.VentaId);
                    table.ForeignKey(
                        name: "FK_EnviosAEleventa_Ventas_VentaId",
                        column: x => x.VentaId,
                        principalTable: "Ventas",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_EnviosAEleventa_Estado_CreadoUtc",
                table: "EnviosAEleventa",
                columns: new[] { "Estado", "CreadoUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_EnviosAEleventa_FolioEleventa",
                table: "EnviosAEleventa",
                column: "FolioEleventa");
        }
    }
}
