using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PapeleriaPos.Datos.Migraciones
{
    /// <inheritdoc />
    public partial class DocumentoProveedor : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AlterColumn<Guid>(
                name: "VarianteId",
                table: "ComprasRenglones",
                type: "TEXT",
                nullable: true,
                oldClrType: typeof(Guid),
                oldType: "TEXT");

            migrationBuilder.AlterColumn<Guid>(
                name: "PresentacionId",
                table: "ComprasRenglones",
                type: "TEXT",
                nullable: true,
                oldClrType: typeof(Guid),
                oldType: "TEXT");

            migrationBuilder.AddColumn<bool>(
                name: "DelDocumento",
                table: "ComprasRenglones",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<string>(
                name: "Unidad",
                table: "ComprasRenglones",
                type: "TEXT",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "ComprasDocumentos",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    CompraId = table.Column<Guid>(type: "TEXT", nullable: false),
                    Nombre = table.Column<string>(type: "TEXT", nullable: false),
                    Tipo = table.Column<string>(type: "TEXT", nullable: false),
                    Contenido = table.Column<byte[]>(type: "BLOB", nullable: false),
                    Folio = table.Column<string>(type: "TEXT", nullable: true),
                    Uuid = table.Column<string>(type: "TEXT", nullable: true),
                    RfcEmisor = table.Column<string>(type: "TEXT", nullable: true),
                    TotalCentavos = table.Column<long>(type: "INTEGER", nullable: true),
                    Avisos = table.Column<string>(type: "TEXT", nullable: true),
                    UsuarioId = table.Column<Guid>(type: "TEXT", nullable: false),
                    CargadoUtc = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ComprasDocumentos", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ComprasDocumentos_Compras_CompraId",
                        column: x => x.CompraId,
                        principalTable: "Compras",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_ComprasDocumentos_Usuarios_UsuarioId",
                        column: x => x.UsuarioId,
                        principalTable: "Usuarios",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "LigasProveedor",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    ProveedorId = table.Column<Guid>(type: "TEXT", nullable: false),
                    Clave = table.Column<string>(type: "TEXT", nullable: false),
                    PresentacionId = table.Column<Guid>(type: "TEXT", nullable: false),
                    ModificadaUtc = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_LigasProveedor", x => x.Id);
                    table.ForeignKey(
                        name: "FK_LigasProveedor_Presentaciones_PresentacionId",
                        column: x => x.PresentacionId,
                        principalTable: "Presentaciones",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_LigasProveedor_Proveedores_ProveedorId",
                        column: x => x.ProveedorId,
                        principalTable: "Proveedores",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_ComprasDocumentos_CompraId",
                table: "ComprasDocumentos",
                column: "CompraId");

            migrationBuilder.CreateIndex(
                name: "IX_ComprasDocumentos_UsuarioId",
                table: "ComprasDocumentos",
                column: "UsuarioId");

            migrationBuilder.CreateIndex(
                name: "IX_ComprasDocumentos_Uuid",
                table: "ComprasDocumentos",
                column: "Uuid");

            migrationBuilder.CreateIndex(
                name: "IX_LigasProveedor_PresentacionId",
                table: "LigasProveedor",
                column: "PresentacionId");

            migrationBuilder.CreateIndex(
                name: "IX_LigasProveedor_ProveedorId_Clave",
                table: "LigasProveedor",
                columns: new[] { "ProveedorId", "Clave" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ComprasDocumentos");

            migrationBuilder.DropTable(
                name: "LigasProveedor");

            migrationBuilder.DropColumn(
                name: "DelDocumento",
                table: "ComprasRenglones");

            migrationBuilder.DropColumn(
                name: "Unidad",
                table: "ComprasRenglones");

            migrationBuilder.AlterColumn<Guid>(
                name: "VarianteId",
                table: "ComprasRenglones",
                type: "TEXT",
                nullable: false,
                defaultValue: new Guid("00000000-0000-0000-0000-000000000000"),
                oldClrType: typeof(Guid),
                oldType: "TEXT",
                oldNullable: true);

            migrationBuilder.AlterColumn<Guid>(
                name: "PresentacionId",
                table: "ComprasRenglones",
                type: "TEXT",
                nullable: false,
                defaultValue: new Guid("00000000-0000-0000-0000-000000000000"),
                oldClrType: typeof(Guid),
                oldType: "TEXT",
                oldNullable: true);
        }
    }
}
