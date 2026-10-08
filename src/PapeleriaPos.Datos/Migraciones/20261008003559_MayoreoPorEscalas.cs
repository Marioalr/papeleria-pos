using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PapeleriaPos.Datos.Migraciones
{
    /// <inheritdoc />
    public partial class MayoreoPorEscalas : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<long>(
                name: "AhorroMayoreoCentavos",
                table: "VentasLineas",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0L);

            migrationBuilder.AddColumn<long>(
                name: "MayoreoDesde",
                table: "VentasLineas",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "MayoreoPorRevisar",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    VarianteId = table.Column<Guid>(type: "TEXT", nullable: false),
                    PrecioMayoreoCentavos = table.Column<long>(type: "INTEGER", nullable: false),
                    MenudeoCentavos = table.Column<long>(type: "INTEGER", nullable: false),
                    CostoCentavos = table.Column<long>(type: "INTEGER", nullable: false),
                    VentasConMayoreo = table.Column<int>(type: "INTEGER", nullable: false),
                    Cantidades = table.Column<string>(type: "TEXT", nullable: false),
                    Motivo = table.Column<string>(type: "TEXT", nullable: false),
                    Sugerida = table.Column<long>(type: "INTEGER", nullable: true),
                    ResueltoUtc = table.Column<DateTime>(type: "TEXT", nullable: true),
                    Resolucion = table.Column<string>(type: "TEXT", nullable: true),
                    Escala = table.Column<long>(type: "INTEGER", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MayoreoPorRevisar", x => x.Id);
                    table.ForeignKey(
                        name: "FK_MayoreoPorRevisar_Variantes_VarianteId",
                        column: x => x.VarianteId,
                        principalTable: "Variantes",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_MayoreoPorRevisar_VarianteId",
                table: "MayoreoPorRevisar",
                column: "VarianteId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "MayoreoPorRevisar");

            migrationBuilder.DropColumn(
                name: "AhorroMayoreoCentavos",
                table: "VentasLineas");

            migrationBuilder.DropColumn(
                name: "MayoreoDesde",
                table: "VentasLineas");
        }
    }
}
