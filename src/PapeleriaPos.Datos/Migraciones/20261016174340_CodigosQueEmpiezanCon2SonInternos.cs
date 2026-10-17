using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PapeleriaPos.Datos.Migraciones
{
    /// <inheritdoc />
    public partial class CodigosQueEmpiezanCon2SonInternos : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Los EAN-13 que empiezan con 2 son internos (CodigoBarras.OrigenDe), pero importar o leer eleventa los guardaba
            // como de fábrica, y la lectura solo vuelve a decidir el origen cuando el código cambia: los que ya estaban se
            // corrigen aquí. Con la regla de antes eran los únicos que podían quedar de fábrica sin serlo (13 dígitos, el
            // primero un 2). La versión del catálogo no cambia: las cajas no guardan el origen.
            migrationBuilder.Sql(
                """
                UPDATE CodigosBarras SET Origen = 'Interno'
                WHERE Origen = 'Fabrica' AND length(Codigo) = 13 AND Codigo GLOB '2*' AND Codigo NOT GLOB '*[^0-9]*';
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // No se regresa: ya no se sabe cuáles estaban como de fábrica, y la versión de antes trabaja igual con ellos como
            // internos (así los daba de alta el catálogo).
        }
    }
}
