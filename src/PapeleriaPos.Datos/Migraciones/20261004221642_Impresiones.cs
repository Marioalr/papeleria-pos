using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PapeleriaPos.Datos.Migraciones
{
    /// <inheritdoc />
    public partial class Impresiones : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "EsImpresiones",
                table: "VentasLineas",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);

            migrationBuilder.CreateTable(
                name: "ConfiguracionesImpresion",
                columns: table => new
                {
                    SucursalId = table.Column<Guid>(type: "TEXT", nullable: false),
                    CostosJson = table.Column<string>(type: "TEXT", nullable: false),
                    ModificadoUtc = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ConfiguracionesImpresion", x => x.SucursalId);
                    table.ForeignKey(
                        name: "FK_ConfiguracionesImpresion_Sucursales_SucursalId",
                        column: x => x.SucursalId,
                        principalTable: "Sucursales",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "TrabajosImpresion",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    SucursalId = table.Column<Guid>(type: "TEXT", nullable: false),
                    CajaId = table.Column<Guid>(type: "TEXT", nullable: false),
                    AtendioId = table.Column<Guid>(type: "TEXT", nullable: true),
                    Pc = table.Column<string>(type: "TEXT", nullable: false),
                    Cola = table.Column<string>(type: "TEXT", nullable: false),
                    SpoolId = table.Column<long>(type: "INTEGER", nullable: false),
                    Documento = table.Column<string>(type: "TEXT", nullable: false),
                    UsuarioWindows = table.Column<string>(type: "TEXT", nullable: true),
                    ImpresoUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    Estado = table.Column<string>(type: "TEXT", nullable: false),
                    Paginas = table.Column<int>(type: "INTEGER", nullable: false),
                    Copias = table.Column<int>(type: "INTEGER", nullable: false),
                    Caras = table.Column<int>(type: "INTEGER", nullable: false),
                    CarasColor = table.Column<int>(type: "INTEGER", nullable: false),
                    Hojas = table.Column<int>(type: "INTEGER", nullable: false),
                    Color = table.Column<bool>(type: "INTEGER", nullable: false),
                    Duplex = table.Column<bool>(type: "INTEGER", nullable: false),
                    Tamano = table.Column<string>(type: "TEXT", nullable: false),
                    TipoPapel = table.Column<string>(type: "TEXT", nullable: false),
                    Calidad = table.Column<string>(type: "TEXT", nullable: false),
                    Medido = table.Column<bool>(type: "INTEGER", nullable: false),
                    Nota = table.Column<string>(type: "TEXT", nullable: true),
                    Miniatura = table.Column<byte[]>(type: "BLOB", nullable: true),
                    CostoTintaCentavos = table.Column<long>(type: "INTEGER", nullable: false),
                    CostoPapelCentavos = table.Column<long>(type: "INTEGER", nullable: false),
                    CostoOtrosCentavos = table.Column<long>(type: "INTEGER", nullable: false),
                    CostoCentavos = table.Column<long>(type: "INTEGER", nullable: false),
                    MargenPorcentaje = table.Column<decimal>(type: "TEXT", precision: 9, scale: 2, nullable: false),
                    PrecioCentavos = table.Column<long>(type: "INTEGER", nullable: false),
                    DetalleJson = table.Column<string>(type: "TEXT", nullable: false),
                    Cobro = table.Column<string>(type: "TEXT", nullable: false),
                    VentaId = table.Column<Guid>(type: "TEXT", nullable: true),
                    CobroUtc = table.Column<DateTime>(type: "TEXT", nullable: true),
                    MotivoNoVendida = table.Column<string>(type: "TEXT", nullable: true),
                    NotaNoVendida = table.Column<string>(type: "TEXT", nullable: true),
                    MarcoId = table.Column<Guid>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TrabajosImpresion", x => x.Id);
                    table.ForeignKey(
                        name: "FK_TrabajosImpresion_Cajas_CajaId",
                        column: x => x.CajaId,
                        principalTable: "Cajas",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_TrabajosImpresion_Sucursales_SucursalId",
                        column: x => x.SucursalId,
                        principalTable: "Sucursales",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_TrabajosImpresion_Ventas_VentaId",
                        column: x => x.VentaId,
                        principalTable: "Ventas",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_TrabajosImpresion_CajaId",
                table: "TrabajosImpresion",
                column: "CajaId");

            migrationBuilder.CreateIndex(
                name: "IX_TrabajosImpresion_SucursalId_Cobro",
                table: "TrabajosImpresion",
                columns: new[] { "SucursalId", "Cobro" });

            migrationBuilder.CreateIndex(
                name: "IX_TrabajosImpresion_VentaId",
                table: "TrabajosImpresion",
                column: "VentaId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ConfiguracionesImpresion");

            migrationBuilder.DropTable(
                name: "TrabajosImpresion");

            migrationBuilder.DropColumn(
                name: "EsImpresiones",
                table: "VentasLineas");
        }
    }
}
