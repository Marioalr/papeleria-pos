using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PapeleriaPos.Datos.Migraciones
{
    /// <inheritdoc />
    public partial class AgregarInventario : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "EntradasInventario",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    SucursalId = table.Column<Guid>(type: "TEXT", nullable: false),
                    VarianteId = table.Column<Guid>(type: "TEXT", nullable: false),
                    Cantidad = table.Column<long>(type: "INTEGER", nullable: false),
                    CostoUnitarioCentavos = table.Column<long>(type: "INTEGER", nullable: false),
                    CostoCentavos = table.Column<long>(type: "INTEGER", nullable: false),
                    CostoEscrito = table.Column<bool>(type: "INTEGER", nullable: false),
                    UsuarioId = table.Column<Guid>(type: "TEXT", nullable: false),
                    FechaUtc = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_EntradasInventario", x => x.Id);
                    table.ForeignKey(
                        name: "FK_EntradasInventario_Sucursales_SucursalId",
                        column: x => x.SucursalId,
                        principalTable: "Sucursales",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_EntradasInventario_Usuarios_UsuarioId",
                        column: x => x.UsuarioId,
                        principalTable: "Usuarios",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_EntradasInventario_Variantes_VarianteId",
                        column: x => x.VarianteId,
                        principalTable: "Variantes",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_EntradasInventario_SucursalId_FechaUtc",
                table: "EntradasInventario",
                columns: new[] { "SucursalId", "FechaUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_EntradasInventario_UsuarioId",
                table: "EntradasInventario",
                column: "UsuarioId");

            migrationBuilder.CreateIndex(
                name: "IX_EntradasInventario_VarianteId_FechaUtc",
                table: "EntradasInventario",
                columns: new[] { "VarianteId", "FechaUtc" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "EntradasInventario");
        }
    }
}
