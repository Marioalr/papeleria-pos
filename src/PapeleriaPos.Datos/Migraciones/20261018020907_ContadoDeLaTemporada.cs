using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PapeleriaPos.Datos.Migraciones
{
    /// <inheritdoc />
    public partial class ContadoDeLaTemporada : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "TemporadasDelAnioContados",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    TemporadaId = table.Column<Guid>(type: "TEXT", nullable: false),
                    VarianteId = table.Column<Guid>(type: "TEXT", nullable: false),
                    Contado = table.Column<long>(type: "INTEGER", nullable: false),
                    Esperado = table.Column<long>(type: "INTEGER", nullable: false),
                    ContadoUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    UsuarioId = table.Column<Guid>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TemporadasDelAnioContados", x => x.Id);
                    table.ForeignKey(
                        name: "FK_TemporadasDelAnioContados_TemporadasDelAnio_TemporadaId",
                        column: x => x.TemporadaId,
                        principalTable: "TemporadasDelAnio",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_TemporadasDelAnioContados_Usuarios_UsuarioId",
                        column: x => x.UsuarioId,
                        principalTable: "Usuarios",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_TemporadasDelAnioContados_Variantes_VarianteId",
                        column: x => x.VarianteId,
                        principalTable: "Variantes",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_TemporadasDelAnioContados_TemporadaId_VarianteId",
                table: "TemporadasDelAnioContados",
                columns: new[] { "TemporadaId", "VarianteId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_TemporadasDelAnioContados_UsuarioId",
                table: "TemporadasDelAnioContados",
                column: "UsuarioId");

            migrationBuilder.CreateIndex(
                name: "IX_TemporadasDelAnioContados_VarianteId",
                table: "TemporadasDelAnioContados",
                column: "VarianteId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "TemporadasDelAnioContados");
        }
    }
}
