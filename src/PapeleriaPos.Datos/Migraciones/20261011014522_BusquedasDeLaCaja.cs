using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PapeleriaPos.Datos.Migraciones
{
    /// <inheritdoc />
    public partial class BusquedasDeLaCaja : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "Busquedas",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    CajaId = table.Column<Guid>(type: "TEXT", nullable: false),
                    FechaUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    Texto = table.Column<string>(type: "TEXT", nullable: false),
                    Resultados = table.Column<int>(type: "INTEGER", nullable: false),
                    VarianteId = table.Column<Guid>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Busquedas", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Busquedas_Cajas_CajaId",
                        column: x => x.CajaId,
                        principalTable: "Cajas",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_Busquedas_Variantes_VarianteId",
                        column: x => x.VarianteId,
                        principalTable: "Variantes",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_Busquedas_CajaId_Texto",
                table: "Busquedas",
                columns: new[] { "CajaId", "Texto" });

            migrationBuilder.CreateIndex(
                name: "IX_Busquedas_FechaUtc",
                table: "Busquedas",
                column: "FechaUtc");

            migrationBuilder.CreateIndex(
                name: "IX_Busquedas_VarianteId",
                table: "Busquedas",
                column: "VarianteId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "Busquedas");
        }
    }
}
