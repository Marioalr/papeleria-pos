using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PapeleriaPos.Datos.Migraciones
{
    /// <inheritdoc />
    public partial class Telegram : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "AlertasApagadas",
                table: "ConfiguracionesSucursal",
                type: "TEXT",
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<TimeOnly>(
                name: "HoraApertura",
                table: "ConfiguracionesSucursal",
                type: "TEXT",
                nullable: false,
                defaultValue: new TimeOnly(8, 0, 0));

            migrationBuilder.AddColumn<TimeOnly>(
                name: "HoraCierre",
                table: "ConfiguracionesSucursal",
                type: "TEXT",
                nullable: false,
                defaultValue: new TimeOnly(21, 0, 0));

            migrationBuilder.AddColumn<int>(
                name: "MinutosSinConexion",
                table: "ConfiguracionesSucursal",
                type: "INTEGER",
                nullable: false,
                defaultValue: 10);

            migrationBuilder.AddColumn<long>(
                name: "UmbralCorteCentavos",
                table: "ConfiguracionesSucursal",
                type: "INTEGER",
                nullable: false,
                defaultValue: 5000L);

            migrationBuilder.AddColumn<long>(
                name: "UmbralDevolucionCentavos",
                table: "ConfiguracionesSucursal",
                type: "INTEGER",
                nullable: false,
                defaultValue: 20000L);

            migrationBuilder.AddColumn<long>(
                name: "UmbralSalidaCentavos",
                table: "ConfiguracionesSucursal",
                type: "INTEGER",
                nullable: false,
                defaultValue: 50000L);

            migrationBuilder.AddColumn<bool>(
                name: "PorTelegram",
                table: "Autorizaciones",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);

            migrationBuilder.CreateTable(
                name: "ChatsTelegram",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    UsuarioId = table.Column<Guid>(type: "TEXT", nullable: false),
                    ChatId = table.Column<long>(type: "INTEGER", nullable: false),
                    Nombre = table.Column<string>(type: "TEXT", nullable: true),
                    VinculadoUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    Activo = table.Column<bool>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ChatsTelegram", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ChatsTelegram_Usuarios_UsuarioId",
                        column: x => x.UsuarioId,
                        principalTable: "Usuarios",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "CodigosVinculacion",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    UsuarioId = table.Column<Guid>(type: "TEXT", nullable: false),
                    Codigo = table.Column<string>(type: "TEXT", nullable: false),
                    VenceUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    UsadoUtc = table.Column<DateTime>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CodigosVinculacion", x => x.Id);
                    table.ForeignKey(
                        name: "FK_CodigosVinculacion_Usuarios_UsuarioId",
                        column: x => x.UsuarioId,
                        principalTable: "Usuarios",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "MensajesTelegram",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    ChatId = table.Column<long>(type: "INTEGER", nullable: false),
                    Texto = table.Column<string>(type: "TEXT", nullable: false),
                    Tipo = table.Column<string>(type: "TEXT", nullable: true),
                    Clave = table.Column<string>(type: "TEXT", nullable: true),
                    SolicitudId = table.Column<Guid>(type: "TEXT", nullable: true),
                    CreadoUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    EnviadoUtc = table.Column<DateTime>(type: "TEXT", nullable: true),
                    MensajeId = table.Column<long>(type: "INTEGER", nullable: true),
                    TextoNuevo = table.Column<string>(type: "TEXT", nullable: true),
                    Intentos = table.Column<int>(type: "INTEGER", nullable: false),
                    Error = table.Column<string>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MensajesTelegram", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "SolicitudesRemotas",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    Accion = table.Column<string>(type: "TEXT", nullable: false),
                    SucursalId = table.Column<Guid>(type: "TEXT", nullable: false),
                    CajaId = table.Column<Guid>(type: "TEXT", nullable: true),
                    SolicitanteId = table.Column<Guid>(type: "TEXT", nullable: false),
                    MontoCentavos = table.Column<long>(type: "INTEGER", nullable: true),
                    Detalle = table.Column<string>(type: "TEXT", nullable: false),
                    CreadaUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    VenceUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    Estado = table.Column<string>(type: "TEXT", nullable: false),
                    RespondioId = table.Column<Guid>(type: "TEXT", nullable: true),
                    RespondidaUtc = table.Column<DateTime>(type: "TEXT", nullable: true),
                    AutorizacionId = table.Column<Guid>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SolicitudesRemotas", x => x.Id);
                    table.ForeignKey(
                        name: "FK_SolicitudesRemotas_Sucursales_SucursalId",
                        column: x => x.SucursalId,
                        principalTable: "Sucursales",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_SolicitudesRemotas_Usuarios_SolicitanteId",
                        column: x => x.SolicitanteId,
                        principalTable: "Usuarios",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_ChatsTelegram_ChatId",
                table: "ChatsTelegram",
                column: "ChatId");

            migrationBuilder.CreateIndex(
                name: "IX_ChatsTelegram_UsuarioId",
                table: "ChatsTelegram",
                column: "UsuarioId");

            migrationBuilder.CreateIndex(
                name: "IX_CodigosVinculacion_Codigo",
                table: "CodigosVinculacion",
                column: "Codigo");

            migrationBuilder.CreateIndex(
                name: "IX_CodigosVinculacion_UsuarioId",
                table: "CodigosVinculacion",
                column: "UsuarioId");

            migrationBuilder.CreateIndex(
                name: "IX_MensajesTelegram_Clave",
                table: "MensajesTelegram",
                column: "Clave");

            migrationBuilder.CreateIndex(
                name: "IX_MensajesTelegram_EnviadoUtc",
                table: "MensajesTelegram",
                column: "EnviadoUtc");

            migrationBuilder.CreateIndex(
                name: "IX_MensajesTelegram_SolicitudId",
                table: "MensajesTelegram",
                column: "SolicitudId");

            migrationBuilder.CreateIndex(
                name: "IX_SolicitudesRemotas_Estado",
                table: "SolicitudesRemotas",
                column: "Estado");

            migrationBuilder.CreateIndex(
                name: "IX_SolicitudesRemotas_SolicitanteId",
                table: "SolicitudesRemotas",
                column: "SolicitanteId");

            migrationBuilder.CreateIndex(
                name: "IX_SolicitudesRemotas_SucursalId",
                table: "SolicitudesRemotas",
                column: "SucursalId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ChatsTelegram");

            migrationBuilder.DropTable(
                name: "CodigosVinculacion");

            migrationBuilder.DropTable(
                name: "MensajesTelegram");

            migrationBuilder.DropTable(
                name: "SolicitudesRemotas");

            migrationBuilder.DropColumn(
                name: "AlertasApagadas",
                table: "ConfiguracionesSucursal");

            migrationBuilder.DropColumn(
                name: "HoraApertura",
                table: "ConfiguracionesSucursal");

            migrationBuilder.DropColumn(
                name: "HoraCierre",
                table: "ConfiguracionesSucursal");

            migrationBuilder.DropColumn(
                name: "MinutosSinConexion",
                table: "ConfiguracionesSucursal");

            migrationBuilder.DropColumn(
                name: "UmbralCorteCentavos",
                table: "ConfiguracionesSucursal");

            migrationBuilder.DropColumn(
                name: "UmbralDevolucionCentavos",
                table: "ConfiguracionesSucursal");

            migrationBuilder.DropColumn(
                name: "UmbralSalidaCentavos",
                table: "ConfiguracionesSucursal");

            migrationBuilder.DropColumn(
                name: "PorTelegram",
                table: "Autorizaciones");
        }
    }
}
