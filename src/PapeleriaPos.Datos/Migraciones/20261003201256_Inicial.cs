using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PapeleriaPos.Datos.Migraciones
{
    /// <inheritdoc />
    public partial class Inicial : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "Departamentos",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    Nombre = table.Column<string>(type: "TEXT", nullable: false),
                    Activo = table.Column<bool>(type: "INTEGER", nullable: false),
                    Version = table.Column<long>(type: "INTEGER", nullable: false),
                    ModificadoUtc = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Departamentos", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "MapeosImportacion",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    Origen = table.Column<string>(type: "TEXT", nullable: false),
                    Entidad = table.Column<string>(type: "TEXT", nullable: false),
                    IdOrigen = table.Column<string>(type: "TEXT", nullable: false),
                    IdNuevo = table.Column<Guid>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MapeosImportacion", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "Marcas",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    Nombre = table.Column<string>(type: "TEXT", nullable: false),
                    NombreNormalizado = table.Column<string>(type: "TEXT", nullable: false),
                    Activa = table.Column<bool>(type: "INTEGER", nullable: false),
                    Version = table.Column<long>(type: "INTEGER", nullable: false),
                    ModificadoUtc = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Marcas", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "PreciosHistorial",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    PrecioId = table.Column<Guid>(type: "TEXT", nullable: false),
                    VarianteId = table.Column<Guid>(type: "TEXT", nullable: false),
                    PresentacionId = table.Column<Guid>(type: "TEXT", nullable: true),
                    SucursalId = table.Column<Guid>(type: "TEXT", nullable: true),
                    CantidadMinima = table.Column<long>(type: "INTEGER", nullable: false),
                    PrecioAnteriorCentavos = table.Column<long>(type: "INTEGER", nullable: true),
                    PrecioNuevoCentavos = table.Column<long>(type: "INTEGER", nullable: true),
                    FechaUtc = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PreciosHistorial", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "Proveedores",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    Nombre = table.Column<string>(type: "TEXT", nullable: false),
                    Telefono = table.Column<string>(type: "TEXT", nullable: true),
                    Rfc = table.Column<string>(type: "TEXT", nullable: true),
                    Notas = table.Column<string>(type: "TEXT", nullable: true),
                    Activo = table.Column<bool>(type: "INTEGER", nullable: false),
                    Version = table.Column<long>(type: "INTEGER", nullable: false),
                    ModificadoUtc = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Proveedores", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "Secuencias",
                columns: table => new
                {
                    Nombre = table.Column<string>(type: "TEXT", nullable: false),
                    Valor = table.Column<long>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Secuencias", x => x.Nombre);
                });

            migrationBuilder.CreateTable(
                name: "Sucursales",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    Clave = table.Column<string>(type: "TEXT", nullable: false),
                    Nombre = table.Column<string>(type: "TEXT", nullable: false),
                    Activa = table.Column<bool>(type: "INTEGER", nullable: false),
                    Version = table.Column<long>(type: "INTEGER", nullable: false),
                    ModificadoUtc = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Sucursales", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "Productos",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    Nombre = table.Column<string>(type: "TEXT", nullable: false),
                    MarcaId = table.Column<Guid>(type: "TEXT", nullable: true),
                    DepartamentoId = table.Column<Guid>(type: "TEXT", nullable: false),
                    Tipo = table.Column<string>(type: "TEXT", nullable: false),
                    ClaveSat = table.Column<string>(type: "TEXT", nullable: true),
                    ClaveUnidadSat = table.Column<string>(type: "TEXT", nullable: true),
                    Impuesto = table.Column<string>(type: "TEXT", nullable: false),
                    Activo = table.Column<bool>(type: "INTEGER", nullable: false),
                    Version = table.Column<long>(type: "INTEGER", nullable: false),
                    ModificadoUtc = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Productos", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Productos_Departamentos_DepartamentoId",
                        column: x => x.DepartamentoId,
                        principalTable: "Departamentos",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_Productos_Marcas_MarcaId",
                        column: x => x.MarcaId,
                        principalTable: "Marcas",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "VentasHistoricas",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    SucursalId = table.Column<Guid>(type: "TEXT", nullable: false),
                    FechaUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    FolioOrigen = table.Column<string>(type: "TEXT", nullable: false),
                    TotalCentavos = table.Column<long>(type: "INTEGER", nullable: false),
                    Cancelada = table.Column<bool>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_VentasHistoricas", x => x.Id);
                    table.ForeignKey(
                        name: "FK_VentasHistoricas_Sucursales_SucursalId",
                        column: x => x.SucursalId,
                        principalTable: "Sucursales",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "Variantes",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    ProductoId = table.Column<Guid>(type: "TEXT", nullable: false),
                    Descripcion = table.Column<string>(type: "TEXT", nullable: false),
                    UnidadBase = table.Column<string>(type: "TEXT", nullable: false),
                    ManejaCaducidad = table.Column<bool>(type: "INTEGER", nullable: false),
                    Activa = table.Column<bool>(type: "INTEGER", nullable: false),
                    Version = table.Column<long>(type: "INTEGER", nullable: false),
                    ModificadoUtc = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Variantes", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Variantes_Productos_ProductoId",
                        column: x => x.ProductoId,
                        principalTable: "Productos",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "Existencias",
                columns: table => new
                {
                    SucursalId = table.Column<Guid>(type: "TEXT", nullable: false),
                    VarianteId = table.Column<Guid>(type: "TEXT", nullable: false),
                    Cantidad = table.Column<long>(type: "INTEGER", nullable: false),
                    CostoCentavos = table.Column<long>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Existencias", x => new { x.SucursalId, x.VarianteId });
                    table.ForeignKey(
                        name: "FK_Existencias_Sucursales_SucursalId",
                        column: x => x.SucursalId,
                        principalTable: "Sucursales",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_Existencias_Variantes_VarianteId",
                        column: x => x.VarianteId,
                        principalTable: "Variantes",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "MovimientosInventario",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    SucursalId = table.Column<Guid>(type: "TEXT", nullable: false),
                    VarianteId = table.Column<Guid>(type: "TEXT", nullable: false),
                    FechaUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    Tipo = table.Column<string>(type: "TEXT", nullable: false),
                    Cantidad = table.Column<long>(type: "INTEGER", nullable: false),
                    CostoCentavos = table.Column<long>(type: "INTEGER", nullable: false),
                    DocumentoTipo = table.Column<string>(type: "TEXT", nullable: true),
                    DocumentoId = table.Column<Guid>(type: "TEXT", nullable: true),
                    Motivo = table.Column<string>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MovimientosInventario", x => x.Id);
                    table.ForeignKey(
                        name: "FK_MovimientosInventario_Sucursales_SucursalId",
                        column: x => x.SucursalId,
                        principalTable: "Sucursales",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_MovimientosInventario_Variantes_VarianteId",
                        column: x => x.VarianteId,
                        principalTable: "Variantes",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "Presentaciones",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    VarianteId = table.Column<Guid>(type: "TEXT", nullable: false),
                    Nombre = table.Column<string>(type: "TEXT", nullable: false),
                    Factor = table.Column<long>(type: "INTEGER", nullable: false),
                    EsBase = table.Column<bool>(type: "INTEGER", nullable: false),
                    Version = table.Column<long>(type: "INTEGER", nullable: false),
                    ModificadoUtc = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Presentaciones", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Presentaciones_Variantes_VarianteId",
                        column: x => x.VarianteId,
                        principalTable: "Variantes",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "ProveedoresVariantes",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    ProveedorId = table.Column<Guid>(type: "TEXT", nullable: false),
                    VarianteId = table.Column<Guid>(type: "TEXT", nullable: false),
                    CodigoProveedor = table.Column<string>(type: "TEXT", nullable: true),
                    UltimoCostoCentavos = table.Column<long>(type: "INTEGER", nullable: true),
                    UltimaCompraUtc = table.Column<DateTime>(type: "TEXT", nullable: true),
                    Version = table.Column<long>(type: "INTEGER", nullable: false),
                    ModificadoUtc = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ProveedoresVariantes", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ProveedoresVariantes_Proveedores_ProveedorId",
                        column: x => x.ProveedorId,
                        principalTable: "Proveedores",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_ProveedoresVariantes_Variantes_VarianteId",
                        column: x => x.VarianteId,
                        principalTable: "Variantes",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "VentasHistoricasLineas",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    VentaHistoricaId = table.Column<Guid>(type: "TEXT", nullable: false),
                    VarianteId = table.Column<Guid>(type: "TEXT", nullable: false),
                    Cantidad = table.Column<long>(type: "INTEGER", nullable: false),
                    PrecioCentavos = table.Column<long>(type: "INTEGER", nullable: false),
                    CostoCentavos = table.Column<long>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_VentasHistoricasLineas", x => x.Id);
                    table.ForeignKey(
                        name: "FK_VentasHistoricasLineas_Variantes_VarianteId",
                        column: x => x.VarianteId,
                        principalTable: "Variantes",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_VentasHistoricasLineas_VentasHistoricas_VentaHistoricaId",
                        column: x => x.VentaHistoricaId,
                        principalTable: "VentasHistoricas",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "CodigosBarras",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    Codigo = table.Column<string>(type: "TEXT", nullable: false),
                    PresentacionId = table.Column<Guid>(type: "TEXT", nullable: false),
                    Origen = table.Column<string>(type: "TEXT", nullable: false),
                    Version = table.Column<long>(type: "INTEGER", nullable: false),
                    ModificadoUtc = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CodigosBarras", x => x.Id);
                    table.ForeignKey(
                        name: "FK_CodigosBarras_Presentaciones_PresentacionId",
                        column: x => x.PresentacionId,
                        principalTable: "Presentaciones",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "Precios",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    VarianteId = table.Column<Guid>(type: "TEXT", nullable: false),
                    PresentacionId = table.Column<Guid>(type: "TEXT", nullable: true),
                    SucursalId = table.Column<Guid>(type: "TEXT", nullable: true),
                    CantidadMinima = table.Column<long>(type: "INTEGER", nullable: false),
                    PrecioCentavos = table.Column<long>(type: "INTEGER", nullable: false),
                    Version = table.Column<long>(type: "INTEGER", nullable: false),
                    ModificadoUtc = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Precios", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Precios_Presentaciones_PresentacionId",
                        column: x => x.PresentacionId,
                        principalTable: "Presentaciones",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_Precios_Sucursales_SucursalId",
                        column: x => x.SucursalId,
                        principalTable: "Sucursales",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_Precios_Variantes_VarianteId",
                        column: x => x.VarianteId,
                        principalTable: "Variantes",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_CodigosBarras_Codigo",
                table: "CodigosBarras",
                column: "Codigo",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_CodigosBarras_PresentacionId",
                table: "CodigosBarras",
                column: "PresentacionId");

            migrationBuilder.CreateIndex(
                name: "IX_CodigosBarras_Version",
                table: "CodigosBarras",
                column: "Version");

            migrationBuilder.CreateIndex(
                name: "IX_Departamentos_Version",
                table: "Departamentos",
                column: "Version");

            migrationBuilder.CreateIndex(
                name: "IX_Existencias_VarianteId",
                table: "Existencias",
                column: "VarianteId");

            migrationBuilder.CreateIndex(
                name: "IX_MapeosImportacion_Origen_Entidad_IdOrigen",
                table: "MapeosImportacion",
                columns: new[] { "Origen", "Entidad", "IdOrigen" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Marcas_NombreNormalizado",
                table: "Marcas",
                column: "NombreNormalizado",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Marcas_Version",
                table: "Marcas",
                column: "Version");

            migrationBuilder.CreateIndex(
                name: "IX_MovimientosInventario_DocumentoTipo_DocumentoId",
                table: "MovimientosInventario",
                columns: new[] { "DocumentoTipo", "DocumentoId" });

            migrationBuilder.CreateIndex(
                name: "IX_MovimientosInventario_SucursalId_VarianteId_FechaUtc",
                table: "MovimientosInventario",
                columns: new[] { "SucursalId", "VarianteId", "FechaUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_MovimientosInventario_VarianteId",
                table: "MovimientosInventario",
                column: "VarianteId");

            migrationBuilder.CreateIndex(
                name: "IX_Precios_PresentacionId",
                table: "Precios",
                column: "PresentacionId");

            migrationBuilder.CreateIndex(
                name: "IX_Precios_SucursalId",
                table: "Precios",
                column: "SucursalId");

            migrationBuilder.CreateIndex(
                name: "IX_Precios_VarianteId",
                table: "Precios",
                column: "VarianteId");

            migrationBuilder.CreateIndex(
                name: "IX_Precios_Version",
                table: "Precios",
                column: "Version");

            migrationBuilder.CreateIndex(
                name: "IX_PreciosHistorial_VarianteId_FechaUtc",
                table: "PreciosHistorial",
                columns: new[] { "VarianteId", "FechaUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_Presentaciones_VarianteId",
                table: "Presentaciones",
                column: "VarianteId");

            migrationBuilder.CreateIndex(
                name: "IX_Presentaciones_Version",
                table: "Presentaciones",
                column: "Version");

            migrationBuilder.CreateIndex(
                name: "IX_Productos_DepartamentoId",
                table: "Productos",
                column: "DepartamentoId");

            migrationBuilder.CreateIndex(
                name: "IX_Productos_MarcaId",
                table: "Productos",
                column: "MarcaId");

            migrationBuilder.CreateIndex(
                name: "IX_Productos_Version",
                table: "Productos",
                column: "Version");

            migrationBuilder.CreateIndex(
                name: "IX_Proveedores_Version",
                table: "Proveedores",
                column: "Version");

            migrationBuilder.CreateIndex(
                name: "IX_ProveedoresVariantes_ProveedorId_VarianteId",
                table: "ProveedoresVariantes",
                columns: new[] { "ProveedorId", "VarianteId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ProveedoresVariantes_VarianteId",
                table: "ProveedoresVariantes",
                column: "VarianteId");

            migrationBuilder.CreateIndex(
                name: "IX_ProveedoresVariantes_Version",
                table: "ProveedoresVariantes",
                column: "Version");

            migrationBuilder.CreateIndex(
                name: "IX_Sucursales_Clave",
                table: "Sucursales",
                column: "Clave",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Sucursales_Version",
                table: "Sucursales",
                column: "Version");

            migrationBuilder.CreateIndex(
                name: "IX_Variantes_ProductoId",
                table: "Variantes",
                column: "ProductoId");

            migrationBuilder.CreateIndex(
                name: "IX_Variantes_Version",
                table: "Variantes",
                column: "Version");

            migrationBuilder.CreateIndex(
                name: "IX_VentasHistoricas_FechaUtc",
                table: "VentasHistoricas",
                column: "FechaUtc");

            migrationBuilder.CreateIndex(
                name: "IX_VentasHistoricas_SucursalId",
                table: "VentasHistoricas",
                column: "SucursalId");

            migrationBuilder.CreateIndex(
                name: "IX_VentasHistoricasLineas_VarianteId",
                table: "VentasHistoricasLineas",
                column: "VarianteId");

            migrationBuilder.CreateIndex(
                name: "IX_VentasHistoricasLineas_VentaHistoricaId",
                table: "VentasHistoricasLineas",
                column: "VentaHistoricaId");

            // Una sola fila por escala. IFNULL porque SQLite considera distintos dos NULL en un índice único.
            migrationBuilder.Sql(
                """
                CREATE UNIQUE INDEX "UX_Precios_Escala" ON "Precios"
                ("VarianteId", IFNULL("PresentacionId", ''), IFNULL("SucursalId", ''), "CantidadMinima");
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""DROP INDEX "UX_Precios_Escala";""");

            migrationBuilder.DropTable(
                name: "CodigosBarras");

            migrationBuilder.DropTable(
                name: "Existencias");

            migrationBuilder.DropTable(
                name: "MapeosImportacion");

            migrationBuilder.DropTable(
                name: "MovimientosInventario");

            migrationBuilder.DropTable(
                name: "Precios");

            migrationBuilder.DropTable(
                name: "PreciosHistorial");

            migrationBuilder.DropTable(
                name: "ProveedoresVariantes");

            migrationBuilder.DropTable(
                name: "Secuencias");

            migrationBuilder.DropTable(
                name: "VentasHistoricasLineas");

            migrationBuilder.DropTable(
                name: "Presentaciones");

            migrationBuilder.DropTable(
                name: "Proveedores");

            migrationBuilder.DropTable(
                name: "VentasHistoricas");

            migrationBuilder.DropTable(
                name: "Variantes");

            migrationBuilder.DropTable(
                name: "Sucursales");

            migrationBuilder.DropTable(
                name: "Productos");

            migrationBuilder.DropTable(
                name: "Departamentos");

            migrationBuilder.DropTable(
                name: "Marcas");
        }
    }
}
