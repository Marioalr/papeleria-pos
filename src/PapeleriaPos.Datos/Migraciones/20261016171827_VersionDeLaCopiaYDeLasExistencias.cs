using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PapeleriaPos.Datos.Migraciones
{
    /// <inheritdoc />
    public partial class VersionDeLaCopiaYDeLasExistencias : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<long>(
                name: "Version",
                table: "Existencias",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0L);

            migrationBuilder.CreateTable(
                name: "Borrados",
                columns: table => new
                {
                    Id = table.Column<long>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    Entidad = table.Column<string>(type: "TEXT", maxLength: 60, nullable: false),
                    EntidadId = table.Column<Guid>(type: "TEXT", nullable: true),
                    VarianteId = table.Column<Guid>(type: "TEXT", nullable: true),
                    Version = table.Column<long>(type: "INTEGER", nullable: false),
                    FechaUtc = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Borrados", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_Existencias_SucursalId_Version",
                table: "Existencias",
                columns: new[] { "SucursalId", "Version" });

            migrationBuilder.CreateIndex(
                name: "IX_Borrados_Version",
                table: "Borrados",
                column: "Version");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "Borrados");

            migrationBuilder.DropIndex(
                name: "IX_Existencias_SucursalId_Version",
                table: "Existencias");

            migrationBuilder.DropColumn(
                name: "Version",
                table: "Existencias");
        }
    }
}
