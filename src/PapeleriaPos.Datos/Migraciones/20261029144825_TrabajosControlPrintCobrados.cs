using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PapeleriaPos.Datos.Migraciones
{
    /// <inheritdoc />
    public partial class TrabajosControlPrintCobrados : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "TrabajosControlPrintCobrados",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    SucursalId = table.Column<Guid>(type: "TEXT", nullable: false),
                    TrabajoId = table.Column<long>(type: "INTEGER", nullable: false),
                    DetectadoUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    VentaId = table.Column<Guid>(type: "TEXT", nullable: false),
                    Documento = table.Column<string>(type: "TEXT", nullable: false),
                    Pc = table.Column<string>(type: "TEXT", nullable: false),
                    PrecioCentavos = table.Column<long>(type: "INTEGER", nullable: false),
                    CostoCentavos = table.Column<long>(type: "INTEGER", nullable: false),
                    CobradoUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    LiberadoUtc = table.Column<DateTime>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TrabajosControlPrintCobrados", x => x.Id);
                    table.ForeignKey(
                        name: "FK_TrabajosControlPrintCobrados_Sucursales_SucursalId",
                        column: x => x.SucursalId,
                        principalTable: "Sucursales",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_TrabajosControlPrintCobrados_Ventas_VentaId",
                        column: x => x.VentaId,
                        principalTable: "Ventas",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_TrabajosControlPrintCobrados_VentaId",
                table: "TrabajosControlPrintCobrados",
                column: "VentaId");

            migrationBuilder.CreateIndex(
                name: "IX_TrabajosControlPrintCobrados_Vigente",
                table: "TrabajosControlPrintCobrados",
                columns: new[] { "SucursalId", "TrabajoId", "DetectadoUtc" },
                unique: true,
                filter: "\"LiberadoUtc\" IS NULL");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "TrabajosControlPrintCobrados");
        }
    }
}
