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
            migrationBuilder.AddColumn<bool>(
                name: "EsCopias",
                table: "VentasLineas",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);

            migrationBuilder.CreateTable(
                name: "HistorialCostosImpresion",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    SucursalId = table.Column<Guid>(type: "TEXT", nullable: false),
                    DesdeUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    HastaUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    PorLosDe = table.Column<string>(type: "TEXT", nullable: true),
                    CostosJson = table.Column<string>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_HistorialCostosImpresion", x => x.Id);
                    table.ForeignKey(
                        name: "FK_HistorialCostosImpresion_Sucursales_SucursalId",
                        column: x => x.SucursalId,
                        principalTable: "Sucursales",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

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
                    LiberadoUtc = table.Column<DateTime>(type: "TEXT", nullable: true),
                    EsCopia = table.Column<bool>(type: "INTEGER", nullable: false),
                    SinRevisar = table.Column<bool>(type: "INTEGER", nullable: false),
                    RevisadoUtc = table.Column<DateTime>(type: "TEXT", nullable: true),
                    CobradoEnControlPrintUtc = table.Column<DateTime>(type: "TEXT", nullable: true),
                    CobradoEnControlPrintPor = table.Column<string>(type: "TEXT", nullable: true)
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
                name: "IX_HistorialCostosImpresion_SucursalId_HastaUtc",
                table: "HistorialCostosImpresion",
                columns: new[] { "SucursalId", "HastaUtc" });

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
                name: "HistorialCostosImpresion");

            migrationBuilder.DropTable(
                name: "TrabajosControlPrintCobrados");

            // SQLite quita la columna sin rehacer la tabla: así las demás de las líneas de venta se quedan como estaban (con sus
            // valores por omisión) y la base queda igual que antes de esta migración. Con DropColumn, EF rehacía la tabla con el
            // modelo de antes y se perdían (MigracionesTests), como en Promociones.
            migrationBuilder.Sql("ALTER TABLE \"VentasLineas\" DROP COLUMN \"EsCopias\";");
        }
    }
}
