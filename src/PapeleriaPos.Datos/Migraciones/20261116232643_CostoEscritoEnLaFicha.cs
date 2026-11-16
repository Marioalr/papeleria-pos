using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PapeleriaPos.Datos.Migraciones
{
    /// <inheritdoc />
    public partial class CostoEscritoEnLaFicha : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "CostosCapturados",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    VarianteId = table.Column<Guid>(type: "TEXT", nullable: false),
                    CostoUnitarioCentavos = table.Column<long>(type: "INTEGER", nullable: false),
                    UsuarioId = table.Column<Guid>(type: "TEXT", nullable: false),
                    FechaUtc = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CostosCapturados", x => x.Id);
                    table.ForeignKey(
                        name: "FK_CostosCapturados_Usuarios_UsuarioId",
                        column: x => x.UsuarioId,
                        principalTable: "Usuarios",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_CostosCapturados_Variantes_VarianteId",
                        column: x => x.VarianteId,
                        principalTable: "Variantes",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_CostosCapturados_UsuarioId",
                table: "CostosCapturados",
                column: "UsuarioId");

            migrationBuilder.CreateIndex(
                name: "IX_CostosCapturados_VarianteId_FechaUtc",
                table: "CostosCapturados",
                columns: new[] { "VarianteId", "FechaUtc" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "CostosCapturados");
        }
    }
}
