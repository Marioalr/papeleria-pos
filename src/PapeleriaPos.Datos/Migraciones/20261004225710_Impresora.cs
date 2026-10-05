using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PapeleriaPos.Datos.Migraciones
{
    /// <inheritdoc />
    public partial class Impresora : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "CarasContadas",
                table: "TrabajosImpresion",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "EnviadoUtc",
                table: "TrabajosImpresion",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "PrecioCompletoCentavos",
                table: "TrabajosImpresion",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<double>(
                name: "TintaC",
                table: "TrabajosImpresion",
                type: "REAL",
                nullable: false,
                defaultValue: 0.0);

            migrationBuilder.AddColumn<double>(
                name: "TintaK",
                table: "TrabajosImpresion",
                type: "REAL",
                nullable: false,
                defaultValue: 0.0);

            migrationBuilder.AddColumn<double>(
                name: "TintaM",
                table: "TrabajosImpresion",
                type: "REAL",
                nullable: false,
                defaultValue: 0.0);

            migrationBuilder.AddColumn<double>(
                name: "TintaY",
                table: "TrabajosImpresion",
                type: "REAL",
                nullable: false,
                defaultValue: 0.0);

            migrationBuilder.AddColumn<string>(
                name: "Tipo",
                table: "TrabajosImpresion",
                type: "TEXT",
                nullable: false,
                defaultValue: "Impresion");

            migrationBuilder.AddColumn<int>(
                name: "AvisoTintaPorcentaje",
                table: "ConfiguracionesImpresion",
                type: "INTEGER",
                nullable: false,
                defaultValue: 10);

            migrationBuilder.AddColumn<string>(
                name: "ImpresoraHost",
                table: "ConfiguracionesImpresion",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "MinutosEntreLecturas",
                table: "ConfiguracionesImpresion",
                type: "INTEGER",
                nullable: false,
                defaultValue: 5);

            migrationBuilder.CreateTable(
                name: "LecturasImpresora",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    SucursalId = table.Column<Guid>(type: "TEXT", nullable: false),
                    TomadaUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    TintaK = table.Column<int>(type: "INTEGER", nullable: true),
                    TintaC = table.Column<int>(type: "INTEGER", nullable: true),
                    TintaM = table.Column<int>(type: "INTEGER", nullable: true),
                    TintaY = table.Column<int>(type: "INTEGER", nullable: true),
                    Mantenimiento = table.Column<int>(type: "INTEGER", nullable: true),
                    Contador = table.Column<long>(type: "INTEGER", nullable: true),
                    ContadorColor = table.Column<long>(type: "INTEGER", nullable: true),
                    ContadorBN = table.Column<long>(type: "INTEGER", nullable: true),
                    Serie = table.Column<string>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_LecturasImpresora", x => x.Id);
                    table.ForeignKey(
                        name: "FK_LecturasImpresora_Sucursales_SucursalId",
                        column: x => x.SucursalId,
                        principalTable: "Sucursales",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_TrabajosImpresion_SucursalId_ImpresoUtc",
                table: "TrabajosImpresion",
                columns: new[] { "SucursalId", "ImpresoUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_LecturasImpresora_SucursalId_TomadaUtc",
                table: "LecturasImpresora",
                columns: new[] { "SucursalId", "TomadaUtc" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "LecturasImpresora");

            migrationBuilder.DropIndex(
                name: "IX_TrabajosImpresion_SucursalId_ImpresoUtc",
                table: "TrabajosImpresion");

            migrationBuilder.DropColumn(
                name: "CarasContadas",
                table: "TrabajosImpresion");

            migrationBuilder.DropColumn(
                name: "EnviadoUtc",
                table: "TrabajosImpresion");

            migrationBuilder.DropColumn(
                name: "PrecioCompletoCentavos",
                table: "TrabajosImpresion");

            migrationBuilder.DropColumn(
                name: "TintaC",
                table: "TrabajosImpresion");

            migrationBuilder.DropColumn(
                name: "TintaK",
                table: "TrabajosImpresion");

            migrationBuilder.DropColumn(
                name: "TintaM",
                table: "TrabajosImpresion");

            migrationBuilder.DropColumn(
                name: "TintaY",
                table: "TrabajosImpresion");

            migrationBuilder.DropColumn(
                name: "Tipo",
                table: "TrabajosImpresion");

            migrationBuilder.DropColumn(
                name: "AvisoTintaPorcentaje",
                table: "ConfiguracionesImpresion");

            migrationBuilder.DropColumn(
                name: "ImpresoraHost",
                table: "ConfiguracionesImpresion");

            migrationBuilder.DropColumn(
                name: "MinutosEntreLecturas",
                table: "ConfiguracionesImpresion");
        }
    }
}
