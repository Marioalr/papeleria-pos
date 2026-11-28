using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PapeleriaPos.Datos.Migraciones
{
    /// <inheritdoc />
    public partial class ConteoPorCapturarEnEleventa : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // El conteo del celular en el piloto con eleventa (2026-11-27; docs/celular-como-caja.md, «En el piloto con eleventa: la lista
            // por capturar»): aceptarlo no cambia existencias; cada producto con diferencia queda «por capturar» en eleventa (Eleventa), y
            // la lista se cierra cuando alguien dice que ya la capturó (CapturoId, CapturadoUtc). Y la hora en que se contó cada producto,
            // si el celular la manda (ContadoUtc; vacía: la de la lista).
            migrationBuilder.AddColumn<DateTime>(
                name: "ContadoUtc",
                table: "ConteosDelCelularRenglones",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Eleventa",
                table: "ConteosDelCelularRenglones",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "CapturadoUtc",
                table: "ConteosDelCelular",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "CapturoId",
                table: "ConteosDelCelular",
                type: "TEXT",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_ConteosDelCelular_CapturoId",
                table: "ConteosDelCelular",
                column: "CapturoId");

            migrationBuilder.AddForeignKey(
                name: "FK_ConteosDelCelular_Usuarios_CapturoId",
                table: "ConteosDelCelular",
                column: "CapturoId",
                principalTable: "Usuarios",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_ConteosDelCelular_Usuarios_CapturoId",
                table: "ConteosDelCelular");

            migrationBuilder.DropIndex(
                name: "IX_ConteosDelCelular_CapturoId",
                table: "ConteosDelCelular");

            migrationBuilder.DropColumn(
                name: "ContadoUtc",
                table: "ConteosDelCelularRenglones");

            migrationBuilder.DropColumn(
                name: "Eleventa",
                table: "ConteosDelCelularRenglones");

            migrationBuilder.DropColumn(
                name: "CapturadoUtc",
                table: "ConteosDelCelular");

            migrationBuilder.DropColumn(
                name: "CapturoId",
                table: "ConteosDelCelular");
        }
    }
}
