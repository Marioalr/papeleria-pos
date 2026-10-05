using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PapeleriaPos.Datos.Migraciones
{
    /// <inheritdoc />
    public partial class Descuentos : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<long>(
                name: "DescuentoCentavos",
                table: "VentasLineas",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0L);

            migrationBuilder.AddColumn<int>(
                name: "DescuentoCentesimas",
                table: "VentasLineas",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "MotivoDescuento",
                table: "VentasLineas",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "AutorizacionBajoCostoId",
                table: "Ventas",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "AutorizacionDescuentoId",
                table: "Ventas",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "ClienteEspecialId",
                table: "Ventas",
                type: "TEXT",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "ClientesEspeciales",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    SucursalId = table.Column<Guid>(type: "TEXT", nullable: true),
                    Nombre = table.Column<string>(type: "TEXT", maxLength: 80, nullable: false),
                    DescuentoCentesimas = table.Column<int>(type: "INTEGER", nullable: false),
                    Nota = table.Column<string>(type: "TEXT", nullable: true),
                    Activo = table.Column<bool>(type: "INTEGER", nullable: false),
                    Version = table.Column<long>(type: "INTEGER", nullable: false),
                    ModificadoUtc = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ClientesEspeciales", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ClientesEspeciales_Sucursales_SucursalId",
                        column: x => x.SucursalId,
                        principalTable: "Sucursales",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_Ventas_ClienteEspecialId",
                table: "Ventas",
                column: "ClienteEspecialId");

            migrationBuilder.CreateIndex(
                name: "IX_ClientesEspeciales_SucursalId",
                table: "ClientesEspeciales",
                column: "SucursalId");

            migrationBuilder.AddForeignKey(
                name: "FK_Ventas_ClientesEspeciales_ClienteEspecialId",
                table: "Ventas",
                column: "ClienteEspecialId",
                principalTable: "ClientesEspeciales",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_Ventas_ClientesEspeciales_ClienteEspecialId",
                table: "Ventas");

            migrationBuilder.DropTable(
                name: "ClientesEspeciales");

            migrationBuilder.DropIndex(
                name: "IX_Ventas_ClienteEspecialId",
                table: "Ventas");

            migrationBuilder.DropColumn(
                name: "DescuentoCentavos",
                table: "VentasLineas");

            migrationBuilder.DropColumn(
                name: "DescuentoCentesimas",
                table: "VentasLineas");

            migrationBuilder.DropColumn(
                name: "MotivoDescuento",
                table: "VentasLineas");

            migrationBuilder.DropColumn(
                name: "AutorizacionBajoCostoId",
                table: "Ventas");

            migrationBuilder.DropColumn(
                name: "AutorizacionDescuentoId",
                table: "Ventas");

            migrationBuilder.DropColumn(
                name: "ClienteEspecialId",
                table: "Ventas");
        }
    }
}
