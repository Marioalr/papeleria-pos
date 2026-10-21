using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PapeleriaPos.Datos.Migraciones
{
    /// <inheritdoc />
    public partial class TemporadaTarjetasYConsultas : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "ConsultasDePrecio",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    SucursalId = table.Column<Guid>(type: "TEXT", nullable: false),
                    CajaId = table.Column<Guid>(type: "TEXT", nullable: true),
                    VarianteId = table.Column<Guid>(type: "TEXT", nullable: false),
                    FechaUtc = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ConsultasDePrecio", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ConsultasDePrecio_Cajas_CajaId",
                        column: x => x.CajaId,
                        principalTable: "Cajas",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_ConsultasDePrecio_Sucursales_SucursalId",
                        column: x => x.SucursalId,
                        principalTable: "Sucursales",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_ConsultasDePrecio_Variantes_VarianteId",
                        column: x => x.VarianteId,
                        principalTable: "Variantes",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "TarjetasDePrecio",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    SucursalId = table.Column<Guid>(type: "TEXT", nullable: false),
                    VarianteId = table.Column<Guid>(type: "TEXT", nullable: false),
                    PrecioCentavos = table.Column<long>(type: "INTEGER", nullable: false),
                    Codigo = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    ImpresaUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    ImpresaPorId = table.Column<Guid>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TarjetasDePrecio", x => x.Id);
                    table.ForeignKey(
                        name: "FK_TarjetasDePrecio_Sucursales_SucursalId",
                        column: x => x.SucursalId,
                        principalTable: "Sucursales",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_TarjetasDePrecio_Usuarios_ImpresaPorId",
                        column: x => x.ImpresaPorId,
                        principalTable: "Usuarios",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_TarjetasDePrecio_Variantes_VarianteId",
                        column: x => x.VarianteId,
                        principalTable: "Variantes",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "TemporadasDelAnio",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    ExhibidorId = table.Column<Guid>(type: "TEXT", nullable: false),
                    SucursalId = table.Column<Guid>(type: "TEXT", nullable: false),
                    Inicio = table.Column<DateOnly>(type: "TEXT", nullable: false),
                    Fin = table.Column<DateOnly>(type: "TEXT", nullable: false),
                    PreciosConfirmadosUtc = table.Column<DateTime>(type: "TEXT", nullable: true),
                    PreciosConfirmadosPorId = table.Column<Guid>(type: "TEXT", nullable: true),
                    TerminadaUtc = table.Column<DateTime>(type: "TEXT", nullable: true),
                    TerminadaPorId = table.Column<Guid>(type: "TEXT", nullable: true),
                    Contada = table.Column<bool>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TemporadasDelAnio", x => x.Id);
                    table.ForeignKey(
                        name: "FK_TemporadasDelAnio_Exhibidores_ExhibidorId",
                        column: x => x.ExhibidorId,
                        principalTable: "Exhibidores",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_TemporadasDelAnio_Sucursales_SucursalId",
                        column: x => x.SucursalId,
                        principalTable: "Sucursales",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_TemporadasDelAnio_Usuarios_PreciosConfirmadosPorId",
                        column: x => x.PreciosConfirmadosPorId,
                        principalTable: "Usuarios",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_TemporadasDelAnio_Usuarios_TerminadaPorId",
                        column: x => x.TerminadaPorId,
                        principalTable: "Usuarios",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "TemporadasDelAnioRenglones",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    TemporadaId = table.Column<Guid>(type: "TEXT", nullable: false),
                    VarianteId = table.Column<Guid>(type: "TEXT", nullable: false),
                    Posicion = table.Column<int>(type: "INTEGER", nullable: false),
                    Inicial = table.Column<long>(type: "INTEGER", nullable: false),
                    Vendido = table.Column<long>(type: "INTEGER", nullable: false),
                    Quedo = table.Column<long>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TemporadasDelAnioRenglones", x => x.Id);
                    table.ForeignKey(
                        name: "FK_TemporadasDelAnioRenglones_TemporadasDelAnio_TemporadaId",
                        column: x => x.TemporadaId,
                        principalTable: "TemporadasDelAnio",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_TemporadasDelAnioRenglones_Variantes_VarianteId",
                        column: x => x.VarianteId,
                        principalTable: "Variantes",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_ConsultasDePrecio_CajaId",
                table: "ConsultasDePrecio",
                column: "CajaId");

            migrationBuilder.CreateIndex(
                name: "IX_ConsultasDePrecio_SucursalId_FechaUtc",
                table: "ConsultasDePrecio",
                columns: new[] { "SucursalId", "FechaUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_ConsultasDePrecio_VarianteId",
                table: "ConsultasDePrecio",
                column: "VarianteId");

            migrationBuilder.CreateIndex(
                name: "IX_TarjetasDePrecio_ImpresaPorId",
                table: "TarjetasDePrecio",
                column: "ImpresaPorId");

            migrationBuilder.CreateIndex(
                name: "IX_TarjetasDePrecio_SucursalId_VarianteId",
                table: "TarjetasDePrecio",
                columns: new[] { "SucursalId", "VarianteId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_TarjetasDePrecio_VarianteId",
                table: "TarjetasDePrecio",
                column: "VarianteId");

            migrationBuilder.CreateIndex(
                name: "IX_TemporadasDelAnio_ExhibidorId_SucursalId_Inicio",
                table: "TemporadasDelAnio",
                columns: new[] { "ExhibidorId", "SucursalId", "Inicio" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_TemporadasDelAnio_PreciosConfirmadosPorId",
                table: "TemporadasDelAnio",
                column: "PreciosConfirmadosPorId");

            migrationBuilder.CreateIndex(
                name: "IX_TemporadasDelAnio_SucursalId",
                table: "TemporadasDelAnio",
                column: "SucursalId");

            migrationBuilder.CreateIndex(
                name: "IX_TemporadasDelAnio_TerminadaPorId",
                table: "TemporadasDelAnio",
                column: "TerminadaPorId");

            migrationBuilder.CreateIndex(
                name: "IX_TemporadasDelAnioRenglones_TemporadaId_VarianteId",
                table: "TemporadasDelAnioRenglones",
                columns: new[] { "TemporadaId", "VarianteId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_TemporadasDelAnioRenglones_VarianteId",
                table: "TemporadasDelAnioRenglones",
                column: "VarianteId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ConsultasDePrecio");

            migrationBuilder.DropTable(
                name: "TarjetasDePrecio");

            migrationBuilder.DropTable(
                name: "TemporadasDelAnioRenglones");

            migrationBuilder.DropTable(
                name: "TemporadasDelAnio");
        }
    }
}
