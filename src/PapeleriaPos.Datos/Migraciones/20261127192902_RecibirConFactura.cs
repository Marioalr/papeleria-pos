using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PapeleriaPos.Datos.Migraciones
{
    /// <inheritdoc />
    public partial class RecibirConFactura : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "Agregado",
                table: "ComprasRenglones",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<long>(
                name: "Llego",
                table: "ComprasRenglones",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Nota",
                table: "ComprasRenglones",
                type: "TEXT",
                maxLength: 200,
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "PresentacionReconocidaId",
                table: "ComprasRenglones",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "ConFactura",
                table: "Compras",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<DateTime>(
                name: "MarcadoUtc",
                table: "Compras",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "MarcoId",
                table: "Compras",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "MovioExistencias",
                table: "Compras",
                type: "INTEGER",
                nullable: false,
                defaultValue: true);

            migrationBuilder.CreateIndex(
                name: "IX_ComprasRenglones_PresentacionReconocidaId",
                table: "ComprasRenglones",
                column: "PresentacionReconocidaId");

            migrationBuilder.CreateIndex(
                name: "IX_Compras_MarcoId",
                table: "Compras",
                column: "MarcoId");

            migrationBuilder.AddForeignKey(
                name: "FK_Compras_Usuarios_MarcoId",
                table: "Compras",
                column: "MarcoId",
                principalTable: "Usuarios",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_ComprasRenglones_Presentaciones_PresentacionReconocidaId",
                table: "ComprasRenglones",
                column: "PresentacionReconocidaId",
                principalTable: "Presentaciones",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_Compras_Usuarios_MarcoId",
                table: "Compras");

            migrationBuilder.DropForeignKey(
                name: "FK_ComprasRenglones_Presentaciones_PresentacionReconocidaId",
                table: "ComprasRenglones");

            migrationBuilder.DropIndex(
                name: "IX_ComprasRenglones_PresentacionReconocidaId",
                table: "ComprasRenglones");

            migrationBuilder.DropIndex(
                name: "IX_Compras_MarcoId",
                table: "Compras");

            migrationBuilder.DropColumn(
                name: "Agregado",
                table: "ComprasRenglones");

            migrationBuilder.DropColumn(
                name: "Llego",
                table: "ComprasRenglones");

            migrationBuilder.DropColumn(
                name: "Nota",
                table: "ComprasRenglones");

            migrationBuilder.DropColumn(
                name: "PresentacionReconocidaId",
                table: "ComprasRenglones");

            migrationBuilder.DropColumn(
                name: "ConFactura",
                table: "Compras");

            migrationBuilder.DropColumn(
                name: "MarcadoUtc",
                table: "Compras");

            migrationBuilder.DropColumn(
                name: "MarcoId",
                table: "Compras");

            migrationBuilder.DropColumn(
                name: "MovioExistencias",
                table: "Compras");
        }
    }
}
