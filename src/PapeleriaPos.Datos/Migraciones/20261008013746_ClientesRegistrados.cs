using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PapeleriaPos.Datos.Migraciones
{
    /// <inheritdoc />
    public partial class ClientesRegistrados : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Los clientes especiales pasan a ser clientes registrados con su mismo Id: sus ventas los siguen nombrando.
            // Quedan marcados para revisar sus datos (teléfono, privilegios). La tabla vieja se borra después de copiarla.
            // En SQLite la llave de Ventas se cambia reconstruyendo la tabla al final de la migración: mientras tanto
            // Ventas.ClienteId sigue apuntando a la tabla vieja, y borrarla con las llaves activas truena. EF las vuelve a
            // activar al terminar la reconstrucción.
            migrationBuilder.Sql("PRAGMA foreign_keys = 0;", suppressTransaction: true);

            migrationBuilder.DropForeignKey(
                name: "FK_Ventas_ClientesEspeciales_ClienteEspecialId",
                table: "Ventas");

            migrationBuilder.DropIndex(
                name: "IX_Ventas_ClienteEspecialId",
                table: "Ventas");

            migrationBuilder.RenameColumn(
                name: "ClienteEspecialId",
                table: "Ventas",
                newName: "ClienteId");

            migrationBuilder.CreateTable(
                name: "ClientesRegistrados",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    SucursalId = table.Column<Guid>(type: "TEXT", nullable: true),
                    Nombre = table.Column<string>(type: "TEXT", maxLength: 80, nullable: false),
                    Telefono = table.Column<string>(type: "TEXT", maxLength: 20, nullable: true),
                    Nota = table.Column<string>(type: "TEXT", maxLength: 200, nullable: true),
                    DescuentoCentesimas = table.Column<int>(type: "INTEGER", nullable: false),
                    MayoreoSiempre = table.Column<bool>(type: "INTEGER", nullable: false),
                    PedidoSinAnticipo = table.Column<bool>(type: "INTEGER", nullable: false),
                    ApartarSinPagar = table.Column<bool>(type: "INTEGER", nullable: false),
                    Activo = table.Column<bool>(type: "INTEGER", nullable: false),
                    PorRevisar = table.Column<bool>(type: "INTEGER", nullable: false),
                    Version = table.Column<long>(type: "INTEGER", nullable: false),
                    ModificadoUtc = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ClientesRegistrados", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ClientesRegistrados_Sucursales_SucursalId",
                        column: x => x.SucursalId,
                        principalTable: "Sucursales",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.Sql(
                """
                INSERT INTO ClientesRegistrados (Id, SucursalId, Nombre, Telefono, Nota, DescuentoCentesimas, MayoreoSiempre, PedidoSinAnticipo,
                    ApartarSinPagar, Activo, PorRevisar, Version, ModificadoUtc)
                SELECT Id, SucursalId, Nombre, NULL, Nota, DescuentoCentesimas, 0, 0, 0, Activo, 1, Version, ModificadoUtc
                FROM ClientesEspeciales;
                """);

            migrationBuilder.DropTable(
                name: "ClientesEspeciales");

            migrationBuilder.CreateIndex(
                name: "IX_Ventas_ClienteId_PagadaUtc",
                table: "Ventas",
                columns: new[] { "ClienteId", "PagadaUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_ClientesRegistrados_SucursalId",
                table: "ClientesRegistrados",
                column: "SucursalId");

            migrationBuilder.AddForeignKey(
                name: "FK_Ventas_ClientesRegistrados_ClienteId",
                table: "Ventas",
                column: "ClienteId",
                principalTable: "ClientesRegistrados",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Igual que al subir: la tabla que se borra sigue nombrada por Ventas hasta la reconstrucción del final.
            migrationBuilder.Sql("PRAGMA foreign_keys = 0;", suppressTransaction: true);

            migrationBuilder.DropForeignKey(
                name: "FK_Ventas_ClientesRegistrados_ClienteId",
                table: "Ventas");

            migrationBuilder.DropIndex(
                name: "IX_Ventas_ClienteId_PagadaUtc",
                table: "Ventas");

            migrationBuilder.RenameColumn(
                name: "ClienteId",
                table: "Ventas",
                newName: "ClienteEspecialId");

            migrationBuilder.CreateTable(
                name: "ClientesEspeciales",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    Activo = table.Column<bool>(type: "INTEGER", nullable: false),
                    DescuentoCentesimas = table.Column<int>(type: "INTEGER", nullable: false),
                    ModificadoUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    Nombre = table.Column<string>(type: "TEXT", maxLength: 80, nullable: false),
                    Nota = table.Column<string>(type: "TEXT", nullable: true),
                    SucursalId = table.Column<Guid>(type: "TEXT", nullable: true),
                    Version = table.Column<long>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ClientesEspeciales", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ClientesEspeciales_Sucursales_SucursalId",
                        column: x => x.SucursalId,
                        principalTable: "Sucursales",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.Sql(
                """
                INSERT INTO ClientesEspeciales (Id, Activo, DescuentoCentesimas, ModificadoUtc, Nombre, Nota, SucursalId, Version)
                SELECT Id, Activo, DescuentoCentesimas, ModificadoUtc, Nombre, Nota, SucursalId, Version FROM ClientesRegistrados;
                """);

            migrationBuilder.DropTable(
                name: "ClientesRegistrados");

            migrationBuilder.CreateIndex(
                name: "IX_Ventas_ClienteEspecialId",
                table: "Ventas",
                column: "ClienteEspecialId");

            migrationBuilder.CreateIndex(
                name: "IX_ClientesEspeciales_SucursalId",
                table: "ClientesEspeciales",
                column: "SucursalId");

            migrationBuilder.AddForeignKey(
                name: "FK_Ventas_ClientesEspeciales_ClienteEspecialId",
                table: "Ventas",
                column: "ClienteEspecialId",
                principalTable: "ClientesEspeciales",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);
        }
    }
}
