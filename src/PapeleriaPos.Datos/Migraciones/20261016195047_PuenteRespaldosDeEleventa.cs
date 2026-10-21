using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PapeleriaPos.Datos.Migraciones
{
    /// <inheritdoc />
    public partial class PuenteRespaldosDeEleventa : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "DetencionesDelPuente",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    DetectadaUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    Pruebas = table.Column<string>(type: "TEXT", maxLength: 2000, nullable: false),
                    DesdeUtc = table.Column<DateTime>(type: "TEXT", nullable: true),
                    ReanudadoUtc = table.Column<DateTime>(type: "TEXT", nullable: true),
                    ReanudoId = table.Column<Guid>(type: "TEXT", nullable: true),
                    Nota = table.Column<string>(type: "TEXT", maxLength: 500, nullable: true),
                    Perdidos = table.Column<int>(type: "INTEGER", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_DetencionesDelPuente", x => x.Id);
                    table.ForeignKey(
                        name: "FK_DetencionesDelPuente_Usuarios_ReanudoId",
                        column: x => x.ReanudoId,
                        principalTable: "Usuarios",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_DetencionesDelPuente_ReanudadoUtc",
                table: "DetencionesDelPuente",
                column: "ReanudadoUtc");

            migrationBuilder.CreateIndex(
                name: "IX_DetencionesDelPuente_ReanudoId",
                table: "DetencionesDelPuente",
                column: "ReanudoId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "DetencionesDelPuente");
        }
    }
}
