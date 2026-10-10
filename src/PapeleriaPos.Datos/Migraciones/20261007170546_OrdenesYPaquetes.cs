using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PapeleriaPos.Datos.Migraciones
{
    /// <inheritdoc />
    public partial class OrdenesYPaquetes : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "OrdenId",
                table: "Compras",
                type: "TEXT",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "AperturasPaquete",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    SucursalId = table.Column<Guid>(type: "TEXT", nullable: false),
                    OrigenId = table.Column<Guid>(type: "TEXT", nullable: false),
                    Cantidad = table.Column<long>(type: "INTEGER", nullable: false),
                    DestinoId = table.Column<Guid>(type: "TEXT", nullable: false),
                    CantidadDestino = table.Column<long>(type: "INTEGER", nullable: false),
                    CostoCentavos = table.Column<long>(type: "INTEGER", nullable: false),
                    UsuarioId = table.Column<Guid>(type: "TEXT", nullable: false),
                    FechaUtc = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AperturasPaquete", x => x.Id);
                    table.ForeignKey(
                        name: "FK_AperturasPaquete_Sucursales_SucursalId",
                        column: x => x.SucursalId,
                        principalTable: "Sucursales",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_AperturasPaquete_Usuarios_UsuarioId",
                        column: x => x.UsuarioId,
                        principalTable: "Usuarios",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_AperturasPaquete_Variantes_DestinoId",
                        column: x => x.DestinoId,
                        principalTable: "Variantes",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_AperturasPaquete_Variantes_OrigenId",
                        column: x => x.OrigenId,
                        principalTable: "Variantes",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "OrdenesCompra",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    SucursalId = table.Column<Guid>(type: "TEXT", nullable: false),
                    ProveedorId = table.Column<Guid>(type: "TEXT", nullable: false),
                    Folio = table.Column<int>(type: "INTEGER", nullable: false),
                    Estado = table.Column<string>(type: "TEXT", nullable: false),
                    Notas = table.Column<string>(type: "TEXT", nullable: true),
                    UsuarioId = table.Column<Guid>(type: "TEXT", nullable: false),
                    CreadaUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    CompraId = table.Column<Guid>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_OrdenesCompra", x => x.Id);
                    table.ForeignKey(
                        name: "FK_OrdenesCompra_Proveedores_ProveedorId",
                        column: x => x.ProveedorId,
                        principalTable: "Proveedores",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_OrdenesCompra_Sucursales_SucursalId",
                        column: x => x.SucursalId,
                        principalTable: "Sucursales",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_OrdenesCompra_Usuarios_UsuarioId",
                        column: x => x.UsuarioId,
                        principalTable: "Usuarios",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "OrdenesRenglones",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    OrdenId = table.Column<Guid>(type: "TEXT", nullable: false),
                    VarianteId = table.Column<Guid>(type: "TEXT", nullable: false),
                    PresentacionId = table.Column<Guid>(type: "TEXT", nullable: false),
                    Cantidad = table.Column<long>(type: "INTEGER", nullable: false),
                    CantidadBase = table.Column<long>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_OrdenesRenglones", x => x.Id);
                    table.ForeignKey(
                        name: "FK_OrdenesRenglones_OrdenesCompra_OrdenId",
                        column: x => x.OrdenId,
                        principalTable: "OrdenesCompra",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_OrdenesRenglones_Presentaciones_PresentacionId",
                        column: x => x.PresentacionId,
                        principalTable: "Presentaciones",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_OrdenesRenglones_Variantes_VarianteId",
                        column: x => x.VarianteId,
                        principalTable: "Variantes",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_Compras_OrdenId",
                table: "Compras",
                column: "OrdenId");

            migrationBuilder.CreateIndex(
                name: "IX_AperturasPaquete_DestinoId",
                table: "AperturasPaquete",
                column: "DestinoId");

            migrationBuilder.CreateIndex(
                name: "IX_AperturasPaquete_OrigenId_FechaUtc",
                table: "AperturasPaquete",
                columns: new[] { "OrigenId", "FechaUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_AperturasPaquete_SucursalId",
                table: "AperturasPaquete",
                column: "SucursalId");

            migrationBuilder.CreateIndex(
                name: "IX_AperturasPaquete_UsuarioId",
                table: "AperturasPaquete",
                column: "UsuarioId");

            migrationBuilder.CreateIndex(
                name: "IX_OrdenesCompra_ProveedorId",
                table: "OrdenesCompra",
                column: "ProveedorId");

            migrationBuilder.CreateIndex(
                name: "IX_OrdenesCompra_SucursalId_Estado",
                table: "OrdenesCompra",
                columns: new[] { "SucursalId", "Estado" });

            migrationBuilder.CreateIndex(
                name: "IX_OrdenesCompra_SucursalId_Folio",
                table: "OrdenesCompra",
                columns: new[] { "SucursalId", "Folio" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_OrdenesCompra_UsuarioId",
                table: "OrdenesCompra",
                column: "UsuarioId");

            migrationBuilder.CreateIndex(
                name: "IX_OrdenesRenglones_OrdenId",
                table: "OrdenesRenglones",
                column: "OrdenId");

            migrationBuilder.CreateIndex(
                name: "IX_OrdenesRenglones_PresentacionId",
                table: "OrdenesRenglones",
                column: "PresentacionId");

            migrationBuilder.CreateIndex(
                name: "IX_OrdenesRenglones_VarianteId",
                table: "OrdenesRenglones",
                column: "VarianteId");

            migrationBuilder.AddForeignKey(
                name: "FK_Compras_OrdenesCompra_OrdenId",
                table: "Compras",
                column: "OrdenId",
                principalTable: "OrdenesCompra",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_Compras_OrdenesCompra_OrdenId",
                table: "Compras");

            migrationBuilder.DropTable(
                name: "AperturasPaquete");

            migrationBuilder.DropTable(
                name: "OrdenesRenglones");

            migrationBuilder.DropTable(
                name: "OrdenesCompra");

            migrationBuilder.DropIndex(
                name: "IX_Compras_OrdenId",
                table: "Compras");

            migrationBuilder.DropColumn(
                name: "OrdenId",
                table: "Compras");
        }
    }
}
