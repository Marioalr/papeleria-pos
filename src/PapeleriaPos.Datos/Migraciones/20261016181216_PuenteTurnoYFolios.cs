using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PapeleriaPos.Datos.Migraciones
{
    /// <inheritdoc />
    public partial class PuenteTurnoYFolios : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "FoliosRepetidosEnEleventa",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    EnvioId = table.Column<Guid>(type: "TEXT", nullable: false),
                    Folio = table.Column<int>(type: "INTEGER", nullable: false),
                    TicketEleventa = table.Column<int>(type: "INTEGER", nullable: false),
                    Caja = table.Column<string>(type: "TEXT", maxLength: 50, nullable: false),
                    VendidoUtc = table.Column<DateTime>(type: "TEXT", nullable: true),
                    TotalCentavos = table.Column<long>(type: "INTEGER", nullable: false),
                    VistoUtc = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_FoliosRepetidosEnEleventa", x => x.Id);
                    table.ForeignKey(
                        name: "FK_FoliosRepetidosEnEleventa_EnviosAEleventa_EnvioId",
                        column: x => x.EnvioId,
                        principalTable: "EnviosAEleventa",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "TurnosEnEleventa",
                columns: table => new
                {
                    TurnoId = table.Column<Guid>(type: "TEXT", nullable: false),
                    TurnoEleventa = table.Column<int>(type: "INTEGER", nullable: false),
                    InicioUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    AdoptadoUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    CortadoUtc = table.Column<DateTime>(type: "TEXT", nullable: true),
                    ContadoCentavos = table.Column<long>(type: "INTEGER", nullable: true),
                    DiferenciaCentavos = table.Column<long>(type: "INTEGER", nullable: true),
                    TomadoUtc = table.Column<DateTime>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TurnosEnEleventa", x => x.TurnoId);
                    table.ForeignKey(
                        name: "FK_TurnosEnEleventa_Turnos_TurnoId",
                        column: x => x.TurnoId,
                        principalTable: "Turnos",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_FoliosRepetidosEnEleventa_EnvioId_TicketEleventa",
                table: "FoliosRepetidosEnEleventa",
                columns: new[] { "EnvioId", "TicketEleventa" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_FoliosRepetidosEnEleventa_Folio",
                table: "FoliosRepetidosEnEleventa",
                column: "Folio");

            migrationBuilder.CreateIndex(
                name: "IX_TurnosEnEleventa_TomadoUtc",
                table: "TurnosEnEleventa",
                column: "TomadoUtc");

            migrationBuilder.CreateIndex(
                name: "IX_TurnosEnEleventa_TurnoEleventa",
                table: "TurnosEnEleventa",
                column: "TurnoEleventa");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "FoliosRepetidosEnEleventa");

            migrationBuilder.DropTable(
                name: "TurnosEnEleventa");
        }
    }
}
