using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PapeleriaPos.Datos.Migraciones
{
    /// <inheritdoc />
    public partial class ConteoCompleto : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "ConteosDepartamento",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    SucursalId = table.Column<Guid>(type: "TEXT", nullable: false),
                    DepartamentoId = table.Column<Guid>(type: "TEXT", nullable: false),
                    Estado = table.Column<string>(type: "TEXT", nullable: false),
                    AbiertoPorId = table.Column<Guid>(type: "TEXT", nullable: false),
                    AbiertoUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    TerminadoPorId = table.Column<Guid>(type: "TEXT", nullable: true),
                    TerminadoUtc = table.Column<DateTime>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ConteosDepartamento", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ConteosDepartamento_Departamentos_DepartamentoId",
                        column: x => x.DepartamentoId,
                        principalTable: "Departamentos",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_ConteosDepartamento_Sucursales_SucursalId",
                        column: x => x.SucursalId,
                        principalTable: "Sucursales",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_ConteosDepartamento_Usuarios_AbiertoPorId",
                        column: x => x.AbiertoPorId,
                        principalTable: "Usuarios",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_ConteosDepartamento_Usuarios_TerminadoPorId",
                        column: x => x.TerminadoPorId,
                        principalTable: "Usuarios",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "ConteosDepartamentoCapturas",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    ConteoId = table.Column<Guid>(type: "TEXT", nullable: false),
                    VarianteId = table.Column<Guid>(type: "TEXT", nullable: false),
                    Cantidad = table.Column<long>(type: "INTEGER", nullable: false),
                    Esperado = table.Column<long>(type: "INTEGER", nullable: false),
                    UsuarioId = table.Column<Guid>(type: "TEXT", nullable: false),
                    CapturadoUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    QuitadaPorId = table.Column<Guid>(type: "TEXT", nullable: true),
                    QuitadaUtc = table.Column<DateTime>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ConteosDepartamentoCapturas", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ConteosDepartamentoCapturas_ConteosDepartamento_ConteoId",
                        column: x => x.ConteoId,
                        principalTable: "ConteosDepartamento",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_ConteosDepartamentoCapturas_Usuarios_QuitadaPorId",
                        column: x => x.QuitadaPorId,
                        principalTable: "Usuarios",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_ConteosDepartamentoCapturas_Usuarios_UsuarioId",
                        column: x => x.UsuarioId,
                        principalTable: "Usuarios",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_ConteosDepartamentoCapturas_Variantes_VarianteId",
                        column: x => x.VarianteId,
                        principalTable: "Variantes",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "ConteosDepartamentoRenglones",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    ConteoId = table.Column<Guid>(type: "TEXT", nullable: false),
                    VarianteId = table.Column<Guid>(type: "TEXT", nullable: false),
                    Contado = table.Column<long>(type: "INTEGER", nullable: true),
                    Esperado = table.Column<long>(type: "INTEGER", nullable: false),
                    CostoDiferenciaCentavos = table.Column<long>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ConteosDepartamentoRenglones", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ConteosDepartamentoRenglones_ConteosDepartamento_ConteoId",
                        column: x => x.ConteoId,
                        principalTable: "ConteosDepartamento",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_ConteosDepartamentoRenglones_Variantes_VarianteId",
                        column: x => x.VarianteId,
                        principalTable: "Variantes",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_ConteosDepartamento_AbiertoPorId",
                table: "ConteosDepartamento",
                column: "AbiertoPorId");

            migrationBuilder.CreateIndex(
                name: "IX_ConteosDepartamento_DepartamentoId",
                table: "ConteosDepartamento",
                column: "DepartamentoId");

            migrationBuilder.CreateIndex(
                name: "IX_ConteosDepartamento_SucursalId_DepartamentoId",
                table: "ConteosDepartamento",
                columns: new[] { "SucursalId", "DepartamentoId" },
                unique: true,
                filter: "\"Estado\" = 'Abierto'");

            migrationBuilder.CreateIndex(
                name: "IX_ConteosDepartamento_TerminadoPorId",
                table: "ConteosDepartamento",
                column: "TerminadoPorId");

            migrationBuilder.CreateIndex(
                name: "IX_ConteosDepartamentoCapturas_ConteoId",
                table: "ConteosDepartamentoCapturas",
                column: "ConteoId");

            migrationBuilder.CreateIndex(
                name: "IX_ConteosDepartamentoCapturas_QuitadaPorId",
                table: "ConteosDepartamentoCapturas",
                column: "QuitadaPorId");

            migrationBuilder.CreateIndex(
                name: "IX_ConteosDepartamentoCapturas_UsuarioId",
                table: "ConteosDepartamentoCapturas",
                column: "UsuarioId");

            migrationBuilder.CreateIndex(
                name: "IX_ConteosDepartamentoCapturas_VarianteId",
                table: "ConteosDepartamentoCapturas",
                column: "VarianteId");

            migrationBuilder.CreateIndex(
                name: "IX_ConteosDepartamentoRenglones_ConteoId",
                table: "ConteosDepartamentoRenglones",
                column: "ConteoId");

            migrationBuilder.CreateIndex(
                name: "IX_ConteosDepartamentoRenglones_VarianteId",
                table: "ConteosDepartamentoRenglones",
                column: "VarianteId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ConteosDepartamentoCapturas");

            migrationBuilder.DropTable(
                name: "ConteosDepartamentoRenglones");

            migrationBuilder.DropTable(
                name: "ConteosDepartamento");
        }
    }
}
