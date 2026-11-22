using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PapeleriaPos.Datos.Migraciones
{
    /// <summary>
    /// Los folios sin letra (2026-11-22, regla del dueño; <see cref="Nucleo.Ventas.Folios"/>): la caja principal ya no lleva letra. Su serie
    /// queda vacía; las de las cajas secundarias (y «CEL», el celular) se quedan, para lo que cobren sin conexión. Nada de lo que ya se cobró
    /// cambia: los tickets de antes siguen con su folio («A-004812») y se buscan igual.
    /// </summary>
    /// <remarks>
    /// Solo cambia datos (el modelo es el mismo). Se puede correr otra vez sin que pase nada: solo toca la principal que todavía tiene letra, y
    /// una sola por sucursal (la serie es única en la sucursal: si ya hay una caja sin letra, no se toca ninguna). El consecutivo sin letra
    /// no se crea aquí: lo crea el primer cobro en línea (o la copia de la caja principal), después del folio más alto que ya hubo en la
    /// sucursal (<c>RegistroCaja.SecuenciaDelFolioAsync</c>).
    /// </remarks>
    public partial class FoliosSinLetra : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(
                """
                UPDATE Cajas SET Serie = ''
                WHERE EsPrincipal = 1 AND Serie <> ''
                  AND NOT EXISTS (SELECT 1 FROM Cajas AS sinLetra WHERE sinLetra.SucursalId = Cajas.SucursalId AND sinLetra.Serie = '')
                  AND Id = (SELECT principal.Id FROM Cajas AS principal
                            WHERE principal.SucursalId = Cajas.SucursalId AND principal.EsPrincipal = 1
                            ORDER BY principal.Activa DESC, principal.AltaUtc IS NULL, principal.AltaUtc, principal.Id
                            LIMIT 1);
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // No se regresa: ya no se sabe qué letra tenía. Una versión de antes trabaja con la principal sin letra (sus folios salen
            // «-000123»); para volver a la letra se cambia a mano en la base.
        }
    }
}
