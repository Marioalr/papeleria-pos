using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PapeleriaPos.Datos.Migraciones
{
    /// <inheritdoc />
    public partial class BandejaEleventa : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
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

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "EnviosAEleventa");
        }
    }
}
