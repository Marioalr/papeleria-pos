using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PapeleriaPos.Datos.Migraciones
{
    /// <inheritdoc />
    public partial class FotosDeLosProductos : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "FotosProducto",
                columns: table => new
                {
                    VarianteId = table.Column<Guid>(type: "TEXT", nullable: false),
                    Datos = table.Column<byte[]>(type: "BLOB", nullable: false),
                    Huella = table.Column<string>(type: "TEXT", maxLength: 16, nullable: false),
                    Ancho = table.Column<int>(type: "INTEGER", nullable: false),
                    Alto = table.Column<int>(type: "INTEGER", nullable: false),
                    Version = table.Column<long>(type: "INTEGER", nullable: false),
                    ModificadoUtc = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_FotosProducto", x => x.VarianteId);
                    table.ForeignKey(
                        name: "FK_FotosProducto_Variantes_VarianteId",
                        column: x => x.VarianteId,
                        principalTable: "Variantes",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_FotosProducto_Version",
                table: "FotosProducto",
                column: "Version");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "FotosProducto");
        }
    }
}
