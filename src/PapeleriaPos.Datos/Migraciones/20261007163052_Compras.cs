using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PapeleriaPos.Datos.Migraciones
{
    /// <inheritdoc />
    public partial class Compras : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "MargenMinimoCentesimas",
                table: "Departamentos",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "MargenMinimoCentesimas",
                table: "ConfiguracionesSucursal",
                type: "INTEGER",
                nullable: false,
                defaultValue: 3000);

            migrationBuilder.CreateTable(
                name: "Compras",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    SucursalId = table.Column<Guid>(type: "TEXT", nullable: false),
                    ProveedorId = table.Column<Guid>(type: "TEXT", nullable: false),
                    Folio = table.Column<int>(type: "INTEGER", nullable: false),
                    Documento = table.Column<string>(type: "TEXT", nullable: true),
                    Estado = table.Column<string>(type: "TEXT", nullable: false),
                    RecibioId = table.Column<Guid>(type: "TEXT", nullable: false),
                    CosteoId = table.Column<Guid>(type: "TEXT", nullable: true),
                    AbiertaUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    ContadaUtc = table.Column<DateTime>(type: "TEXT", nullable: true),
                    CerradaUtc = table.Column<DateTime>(type: "TEXT", nullable: true),
                    Motivo = table.Column<string>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Compras", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Compras_Proveedores_ProveedorId",
                        column: x => x.ProveedorId,
                        principalTable: "Proveedores",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_Compras_Sucursales_SucursalId",
                        column: x => x.SucursalId,
                        principalTable: "Sucursales",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_Compras_Usuarios_CosteoId",
                        column: x => x.CosteoId,
                        principalTable: "Usuarios",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_Compras_Usuarios_RecibioId",
                        column: x => x.RecibioId,
                        principalTable: "Usuarios",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "ComprasConteo",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    CompraId = table.Column<Guid>(type: "TEXT", nullable: false),
                    VarianteId = table.Column<Guid>(type: "TEXT", nullable: false),
                    PresentacionId = table.Column<Guid>(type: "TEXT", nullable: false),
                    Cantidad = table.Column<long>(type: "INTEGER", nullable: false),
                    CantidadBase = table.Column<long>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ComprasConteo", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ComprasConteo_Compras_CompraId",
                        column: x => x.CompraId,
                        principalTable: "Compras",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_ComprasConteo_Presentaciones_PresentacionId",
                        column: x => x.PresentacionId,
                        principalTable: "Presentaciones",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_ComprasConteo_Variantes_VarianteId",
                        column: x => x.VarianteId,
                        principalTable: "Variantes",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "ComprasRenglones",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    CompraId = table.Column<Guid>(type: "TEXT", nullable: false),
                    Orden = table.Column<int>(type: "INTEGER", nullable: false),
                    CodigoProveedor = table.Column<string>(type: "TEXT", nullable: true),
                    Descripcion = table.Column<string>(type: "TEXT", nullable: false),
                    VarianteId = table.Column<Guid>(type: "TEXT", nullable: false),
                    PresentacionId = table.Column<Guid>(type: "TEXT", nullable: false),
                    Cantidad = table.Column<long>(type: "INTEGER", nullable: false),
                    CantidadBase = table.Column<long>(type: "INTEGER", nullable: false),
                    ImporteCentavos = table.Column<long>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ComprasRenglones", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ComprasRenglones_Compras_CompraId",
                        column: x => x.CompraId,
                        principalTable: "Compras",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_ComprasRenglones_Presentaciones_PresentacionId",
                        column: x => x.PresentacionId,
                        principalTable: "Presentaciones",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_ComprasRenglones_Variantes_VarianteId",
                        column: x => x.VarianteId,
                        principalTable: "Variantes",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_Compras_CosteoId",
                table: "Compras",
                column: "CosteoId");

            migrationBuilder.CreateIndex(
                name: "IX_Compras_ProveedorId",
                table: "Compras",
                column: "ProveedorId");

            migrationBuilder.CreateIndex(
                name: "IX_Compras_RecibioId",
                table: "Compras",
                column: "RecibioId");

            migrationBuilder.CreateIndex(
                name: "IX_Compras_SucursalId_Estado",
                table: "Compras",
                columns: new[] { "SucursalId", "Estado" });

            migrationBuilder.CreateIndex(
                name: "IX_Compras_SucursalId_Folio",
                table: "Compras",
                columns: new[] { "SucursalId", "Folio" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ComprasConteo_CompraId",
                table: "ComprasConteo",
                column: "CompraId");

            migrationBuilder.CreateIndex(
                name: "IX_ComprasConteo_PresentacionId",
                table: "ComprasConteo",
                column: "PresentacionId");

            migrationBuilder.CreateIndex(
                name: "IX_ComprasConteo_VarianteId",
                table: "ComprasConteo",
                column: "VarianteId");

            migrationBuilder.CreateIndex(
                name: "IX_ComprasRenglones_CompraId",
                table: "ComprasRenglones",
                column: "CompraId");

            migrationBuilder.CreateIndex(
                name: "IX_ComprasRenglones_PresentacionId",
                table: "ComprasRenglones",
                column: "PresentacionId");

            migrationBuilder.CreateIndex(
                name: "IX_ComprasRenglones_VarianteId",
                table: "ComprasRenglones",
                column: "VarianteId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ComprasConteo");

            migrationBuilder.DropTable(
                name: "ComprasRenglones");

            migrationBuilder.DropTable(
                name: "Compras");

            migrationBuilder.DropColumn(
                name: "MargenMinimoCentesimas",
                table: "Departamentos");

            migrationBuilder.DropColumn(
                name: "MargenMinimoCentesimas",
                table: "ConfiguracionesSucursal");
        }
    }
}
