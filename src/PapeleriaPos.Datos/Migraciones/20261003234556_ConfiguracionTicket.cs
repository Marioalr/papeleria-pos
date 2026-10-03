using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PapeleriaPos.Datos.Migraciones
{
    /// <inheritdoc />
    public partial class ConfiguracionTicket : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "ConfiguracionesTicket",
                columns: table => new
                {
                    SucursalId = table.Column<Guid>(type: "TEXT", nullable: false),
                    Encabezado = table.Column<string>(type: "TEXT", nullable: false),
                    Pie = table.Column<string>(type: "TEXT", nullable: false),
                    LogoAncho = table.Column<int>(type: "INTEGER", nullable: true),
                    LogoAlto = table.Column<int>(type: "INTEGER", nullable: true),
                    LogoBits = table.Column<byte[]>(type: "BLOB", nullable: true),
                    Version = table.Column<long>(type: "INTEGER", nullable: false),
                    ModificadoUtc = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ConfiguracionesTicket", x => x.SucursalId);
                    table.ForeignKey(
                        name: "FK_ConfiguracionesTicket_Sucursales_SucursalId",
                        column: x => x.SucursalId,
                        principalTable: "Sucursales",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ConfiguracionesTicket");
        }
    }
}
