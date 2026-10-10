using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PapeleriaPos.Datos.Migraciones
{
    /// <inheritdoc />
    public partial class PedidoCompleto : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "Nuevo",
                table: "ComprasRenglones",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "SugerenciaId",
                table: "ComprasRenglones",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AlterColumn<Guid>(
                name: "VarianteId",
                table: "ComprasConteo",
                type: "TEXT",
                nullable: true,
                oldClrType: typeof(Guid),
                oldType: "TEXT");

            migrationBuilder.AlterColumn<Guid>(
                name: "PresentacionId",
                table: "ComprasConteo",
                type: "TEXT",
                nullable: true,
                oldClrType: typeof(Guid),
                oldType: "TEXT");

            migrationBuilder.AddColumn<string>(
                name: "Codigo",
                table: "ComprasConteo",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "DelEncargado",
                table: "ComprasConteo",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<string>(
                name: "Descripcion",
                table: "ComprasConteo",
                type: "TEXT",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "Nuevo",
                table: "ComprasRenglones");

            migrationBuilder.DropColumn(
                name: "SugerenciaId",
                table: "ComprasRenglones");

            migrationBuilder.DropColumn(
                name: "Codigo",
                table: "ComprasConteo");

            migrationBuilder.DropColumn(
                name: "DelEncargado",
                table: "ComprasConteo");

            migrationBuilder.DropColumn(
                name: "Descripcion",
                table: "ComprasConteo");

            migrationBuilder.AlterColumn<Guid>(
                name: "VarianteId",
                table: "ComprasConteo",
                type: "TEXT",
                nullable: false,
                defaultValue: new Guid("00000000-0000-0000-0000-000000000000"),
                oldClrType: typeof(Guid),
                oldType: "TEXT",
                oldNullable: true);

            migrationBuilder.AlterColumn<Guid>(
                name: "PresentacionId",
                table: "ComprasConteo",
                type: "TEXT",
                nullable: false,
                defaultValue: new Guid("00000000-0000-0000-0000-000000000000"),
                oldClrType: typeof(Guid),
                oldType: "TEXT",
                oldNullable: true);
        }
    }
}
