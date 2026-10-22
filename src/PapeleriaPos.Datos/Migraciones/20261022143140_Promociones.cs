using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PapeleriaPos.Datos.Migraciones
{
    /// <inheritdoc />
    public partial class Promociones : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<long>(
                name: "AhorroPromocionCentavos",
                table: "VentasLineas",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0L);

            migrationBuilder.AddColumn<string>(
                name: "Promocion",
                table: "VentasLineas",
                type: "TEXT",
                maxLength: 40,
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "PromocionId",
                table: "VentasLineas",
                type: "TEXT",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "Promociones",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    Nombre = table.Column<string>(type: "TEXT", maxLength: 40, nullable: false),
                    Tipo = table.Column<string>(type: "TEXT", nullable: false),
                    Lleva = table.Column<int>(type: "INTEGER", nullable: false),
                    Paga = table.Column<int>(type: "INTEGER", nullable: false),
                    Centesimas = table.Column<int>(type: "INTEGER", nullable: false),
                    Desde = table.Column<DateOnly>(type: "TEXT", nullable: false),
                    Hasta = table.Column<DateOnly>(type: "TEXT", nullable: true),
                    Dias = table.Column<int>(type: "INTEGER", nullable: false),
                    SucursalId = table.Column<Guid>(type: "TEXT", nullable: true),
                    Activa = table.Column<bool>(type: "INTEGER", nullable: false),
                    CreadaPorId = table.Column<Guid>(type: "TEXT", nullable: false),
                    CreadaUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    TerminadaUtc = table.Column<DateTime>(type: "TEXT", nullable: true),
                    TerminadaPorId = table.Column<Guid>(type: "TEXT", nullable: true),
                    Version = table.Column<long>(type: "INTEGER", nullable: false),
                    ModificadoUtc = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Promociones", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Promociones_Sucursales_SucursalId",
                        column: x => x.SucursalId,
                        principalTable: "Sucursales",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_Promociones_Usuarios_CreadaPorId",
                        column: x => x.CreadaPorId,
                        principalTable: "Usuarios",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_Promociones_Usuarios_TerminadaPorId",
                        column: x => x.TerminadaPorId,
                        principalTable: "Usuarios",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "AlcancesPromocion",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    PromocionId = table.Column<Guid>(type: "TEXT", nullable: false),
                    VarianteId = table.Column<Guid>(type: "TEXT", nullable: true),
                    DepartamentoId = table.Column<Guid>(type: "TEXT", nullable: true),
                    MarcaId = table.Column<Guid>(type: "TEXT", nullable: true),
                    PrecioCentavos = table.Column<long>(type: "INTEGER", nullable: true),
                    Version = table.Column<long>(type: "INTEGER", nullable: false),
                    ModificadoUtc = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AlcancesPromocion", x => x.Id);
                    table.ForeignKey(
                        name: "FK_AlcancesPromocion_Departamentos_DepartamentoId",
                        column: x => x.DepartamentoId,
                        principalTable: "Departamentos",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_AlcancesPromocion_Marcas_MarcaId",
                        column: x => x.MarcaId,
                        principalTable: "Marcas",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_AlcancesPromocion_Promociones_PromocionId",
                        column: x => x.PromocionId,
                        principalTable: "Promociones",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_AlcancesPromocion_Variantes_VarianteId",
                        column: x => x.VarianteId,
                        principalTable: "Variantes",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_VentasLineas_PromocionId",
                table: "VentasLineas",
                column: "PromocionId");

            migrationBuilder.CreateIndex(
                name: "IX_AlcancesPromocion_DepartamentoId",
                table: "AlcancesPromocion",
                column: "DepartamentoId");

            migrationBuilder.CreateIndex(
                name: "IX_AlcancesPromocion_MarcaId",
                table: "AlcancesPromocion",
                column: "MarcaId");

            migrationBuilder.CreateIndex(
                name: "IX_AlcancesPromocion_PromocionId",
                table: "AlcancesPromocion",
                column: "PromocionId");

            migrationBuilder.CreateIndex(
                name: "IX_AlcancesPromocion_VarianteId",
                table: "AlcancesPromocion",
                column: "VarianteId");

            migrationBuilder.CreateIndex(
                name: "IX_AlcancesPromocion_Version",
                table: "AlcancesPromocion",
                column: "Version");

            migrationBuilder.CreateIndex(
                name: "IX_Promociones_Activa_Hasta",
                table: "Promociones",
                columns: new[] { "Activa", "Hasta" });

            migrationBuilder.CreateIndex(
                name: "IX_Promociones_CreadaPorId",
                table: "Promociones",
                column: "CreadaPorId");

            migrationBuilder.CreateIndex(
                name: "IX_Promociones_SucursalId",
                table: "Promociones",
                column: "SucursalId");

            migrationBuilder.CreateIndex(
                name: "IX_Promociones_TerminadaPorId",
                table: "Promociones",
                column: "TerminadaPorId");

            migrationBuilder.CreateIndex(
                name: "IX_Promociones_Version",
                table: "Promociones",
                column: "Version");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "AlcancesPromocion");

            migrationBuilder.DropTable(
                name: "Promociones");

            migrationBuilder.DropIndex(
                name: "IX_VentasLineas_PromocionId",
                table: "VentasLineas");

            // SQLite quita las columnas sin rehacer la tabla: así las demás de las líneas de venta se quedan como estaban (con sus
            // valores por omisión) y la base queda igual que antes de esta migración. Con DropColumn, EF rehacía la tabla con el
            // modelo de antes y se perdían (MigracionesTests).
            migrationBuilder.Sql("ALTER TABLE \"VentasLineas\" DROP COLUMN \"AhorroPromocionCentavos\";");
            migrationBuilder.Sql("ALTER TABLE \"VentasLineas\" DROP COLUMN \"Promocion\";");
            migrationBuilder.Sql("ALTER TABLE \"VentasLineas\" DROP COLUMN \"PromocionId\";");
        }
    }
}
