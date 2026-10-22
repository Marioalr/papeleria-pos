using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PapeleriaPos.Datos.Migraciones
{
    /// <inheritdoc />
    public partial class ImpresionesYCancelacionesSinRepetir : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Revisión del PR #133. Cuántas veces salió el ticket de una venta después del cobro (su primera impresión y las
            // reimpresiones): «No imprimirlo» ya no lo marca sin imprimir si después salió otro. Y el Id de la solicitud que canceló
            // una entrada o salida: si la caja la manda otra vez (se perdió la respuesta), es la misma y no es un error. Lo de
            // antes empieza en cero y sin Id.
            migrationBuilder.AddColumn<int>(
                name: "TicketImpresiones",
                table: "Ventas",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<Guid>(
                name: "CancelacionId",
                table: "MovimientosCaja",
                type: "TEXT",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Sin rehacer las tablas (ALTER TABLE … DROP COLUMN), como en CancelarMovimientos: lo demás se queda con sus valores
            // por omisión y la base regresada queda igual (MigracionesTests).
            migrationBuilder.Sql("""
                ALTER TABLE "Ventas" DROP COLUMN "TicketImpresiones";
                ALTER TABLE "MovimientosCaja" DROP COLUMN "CancelacionId";
                """);
        }
    }
}
