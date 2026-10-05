using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PapeleriaPos.Datos.Migraciones
{
    /// <inheritdoc />
    public partial class Depuracion : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "JuntadaEnId",
                table: "Variantes",
                type: "TEXT",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "DecisionesDepuracion",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    Tipo = table.Column<string>(type: "TEXT", nullable: false),
                    VarianteId = table.Column<Guid>(type: "TEXT", nullable: false),
                    DestinoId = table.Column<Guid>(type: "TEXT", nullable: true),
                    OrigenEleventa = table.Column<string>(type: "TEXT", nullable: true),
                    DestinoEleventa = table.Column<string>(type: "TEXT", nullable: true),
                    Unidad = table.Column<string>(type: "TEXT", nullable: true),
                    FactorMilesimas = table.Column<long>(type: "INTEGER", nullable: true),
                    PrecioCentavos = table.Column<long>(type: "INTEGER", nullable: true),
                    Descripcion = table.Column<string>(type: "TEXT", nullable: false),
                    UsuarioId = table.Column<Guid>(type: "TEXT", nullable: true),
                    FechaUtc = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_DecisionesDepuracion", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_DecisionesDepuracion_VarianteId",
                table: "DecisionesDepuracion",
                column: "VarianteId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "DecisionesDepuracion");

            migrationBuilder.DropColumn(
                name: "JuntadaEnId",
                table: "Variantes");
        }
    }
}
