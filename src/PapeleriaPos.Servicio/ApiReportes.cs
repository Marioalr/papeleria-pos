using PapeleriaPos.Datos;
using PapeleriaPos.Nucleo.Contratos;

namespace PapeleriaPos.Servicio;

/// <summary>
/// Los reportes de ventas (entrega 9, parte 1, 2026-10-22): solo leen, con el usuario que entró a la caja. El servicio revisa
/// sus permisos (<see cref="ReportesDeVentas"/>): «Ver reportes de ventas» para verlos y «Ver costos, utilidad y margen» para
/// la utilidad. En el piloto con eleventa, solo lo de la caja 2 (lo de la principal está en eleventa). Los errores van en
/// palabras de la tienda (ErroresApi): 403 sin el permiso, 400 lo demás.
/// </summary>
public static class ApiReportes
{
    public static void MapearApiReportes(this IEndpointRouteBuilder app)
    {
        var api = app.MapGroup("/api").ConErrores().SoloSuCaja();

        // Hoy, la semana, el mes o el año (sin periodo: hoy) del día pedido (sin fecha: hoy), contra el mismo periodo del año pasado.
        api.MapGet("/cajas/{cajaId:guid}/reportes/ventas", (Guid cajaId, Guid usuarioId, string? periodo, DateOnly? fecha, PosDbContext db,
            OpcionesEleventa opcionesEleventa) =>
        {
            var tipo = PeriodoReporte.Hoy;
            if (!string.IsNullOrWhiteSpace(periodo) && !(Enum.TryParse(periodo, ignoreCase: true, out tipo) && Enum.IsDefined(tipo)))
                throw new ArgumentException($"«{periodo}» no es un periodo: hoy, la semana, el mes o el año.");
            return new ReportesDeVentas(db).ArmarAsync(cajaId, usuarioId, tipo, fecha, opcionesEleventa.CorteEnEleventa);
        });
    }
}
