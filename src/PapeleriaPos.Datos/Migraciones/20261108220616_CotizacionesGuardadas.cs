using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PapeleriaPos.Datos.Migraciones
{
    /// <summary>
    /// Cotizar (2026-11-08): las cotizaciones guardadas (<c>CotizacionesImpresion</c>, sin miniatura ni texto del documento), lo cotizado de
    /// cada impresión (<c>TrabajosImpresion</c>: su cotización, lo cotizado, lo impreso y qué se cobra) y la corrección de la tinta al cotizar
    /// de cada impresora (<c>Impresoras.FactorCotizacion</c>, 1 en las que ya están).
    /// </summary>
    public partial class CotizacionesGuardadas : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "CobraLoCotizado",
                table: "TrabajosImpresion",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<Guid>(
                name: "CotizacionId",
                table: "TrabajosImpresion",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "PrecioCotizadoCentavos",
                table: "TrabajosImpresion",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "PrecioImpresoCentavos",
                table: "TrabajosImpresion",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<double>(
                name: "FactorCotizacion",
                table: "Impresoras",
                type: "REAL",
                nullable: false,
                // Las impresoras que ya están empiezan sin corregir (1), no en cero (la tinta de lo cotizado sería cero).
                defaultValue: 1.0);

            migrationBuilder.CreateTable(
                name: "CotizacionesImpresion",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    SucursalId = table.Column<Guid>(type: "TEXT", nullable: false),
                    ImpresoraId = table.Column<Guid>(type: "TEXT", nullable: true),
                    CajaId = table.Column<Guid>(type: "TEXT", nullable: true),
                    UsuarioId = table.Column<Guid>(type: "TEXT", nullable: true),
                    CreadaUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    ModificadaUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    VenceUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    Documento = table.Column<string>(type: "TEXT", nullable: false),
                    PaginasDocumento = table.Column<int>(type: "INTEGER", nullable: false),
                    Rango = table.Column<string>(type: "TEXT", nullable: true),
                    Paginas = table.Column<int>(type: "INTEGER", nullable: false),
                    Copias = table.Column<int>(type: "INTEGER", nullable: false),
                    Caras = table.Column<int>(type: "INTEGER", nullable: false),
                    Hojas = table.Column<int>(type: "INTEGER", nullable: false),
                    CarasColor = table.Column<int>(type: "INTEGER", nullable: false),
                    Color = table.Column<bool>(type: "INTEGER", nullable: false),
                    Duplex = table.Column<bool>(type: "INTEGER", nullable: false),
                    PaginasPorHoja = table.Column<int>(type: "INTEGER", nullable: false),
                    Tamano = table.Column<string>(type: "TEXT", nullable: false),
                    TipoPapel = table.Column<string>(type: "TEXT", nullable: false),
                    Calidad = table.Column<string>(type: "TEXT", nullable: false),
                    PrecioCentavos = table.Column<long>(type: "INTEGER", nullable: false),
                    CostoCentavos = table.Column<long>(type: "INTEGER", nullable: false),
                    TintaSinCorregir = table.Column<decimal>(type: "TEXT", precision: 18, scale: 6, nullable: false),
                    FactorCotizacion = table.Column<double>(type: "REAL", nullable: false),
                    TrabajoId = table.Column<Guid>(type: "TEXT", nullable: true),
                    LigadaUtc = table.Column<DateTime>(type: "TEXT", nullable: true),
                    PrecioImpresoCentavos = table.Column<long>(type: "INTEGER", nullable: true),
                    TintaImpresa = table.Column<decimal>(type: "TEXT", precision: 18, scale: 6, nullable: true),
                    MismosAjustes = table.Column<bool>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CotizacionesImpresion", x => x.Id);
                    table.ForeignKey(
                        name: "FK_CotizacionesImpresion_Sucursales_SucursalId",
                        column: x => x.SucursalId,
                        principalTable: "Sucursales",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_CotizacionesImpresion_SucursalId_VenceUtc",
                table: "CotizacionesImpresion",
                columns: new[] { "SucursalId", "VenceUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_CotizacionesImpresion_TrabajoId",
                table: "CotizacionesImpresion",
                column: "TrabajoId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "CotizacionesImpresion");

            // SQLite no las quita con DropColumn (pide rehacer la tabla, con sus miniaturas): como en «VariasImpresoras». Lo cobrado a lo
            // cotizado se queda con ese precio.
            migrationBuilder.Sql("ALTER TABLE \"TrabajosImpresion\" DROP COLUMN \"CobraLoCotizado\";");
            migrationBuilder.Sql("ALTER TABLE \"TrabajosImpresion\" DROP COLUMN \"CotizacionId\";");
            migrationBuilder.Sql("ALTER TABLE \"TrabajosImpresion\" DROP COLUMN \"PrecioCotizadoCentavos\";");
            migrationBuilder.Sql("ALTER TABLE \"TrabajosImpresion\" DROP COLUMN \"PrecioImpresoCentavos\";");
            migrationBuilder.Sql("ALTER TABLE \"Impresoras\" DROP COLUMN \"FactorCotizacion\";");
        }
    }
}
