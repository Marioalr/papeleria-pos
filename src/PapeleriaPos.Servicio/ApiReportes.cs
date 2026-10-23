using PapeleriaPos.Datos;
using PapeleriaPos.Nucleo.Contratos;
using PapeleriaPos.Servicio.Excel;

namespace PapeleriaPos.Servicio;

/// <summary>
/// Los reportes de ventas (entrega 9, parte 1, 2026-10-22): solo leen, con el usuario que entró a la caja. El servicio revisa
/// sus permisos (<see cref="ReportesDeVentas"/>): «Ver reportes de ventas» para verlos y «Ver costos, utilidad y margen» para
/// la utilidad. En el piloto con eleventa (<see cref="OpcionesEleventa.Piloto"/>: la lectura o el envío prendidos), solo lo de
/// la caja 2 (lo de la principal está en eleventa). Los errores van en palabras de la tienda (ErroresApi): 403 sin el permiso,
/// 400 lo demás.
/// <para>
/// La parte 2 (2026-10-22): «Ventas por período» (<see cref="ReporteVentasPorPeriodo"/>) y el Excel de los dos, que arma este
/// servicio con los mismos permisos que la pantalla (el reporte que lo llena es el mismo que ve quien lo exporta).
/// </para>
/// </summary>
public static class ApiReportes
{
    public static void MapearApiReportes(this IEndpointRouteBuilder app)
    {
        var api = app.MapGroup("/api").ConErrores().SoloSuCaja();
        // Cada uno con el CancellationToken de la petición (RequestAborted): si la caja deja de esperar, se deja de leer y de armar el
        // Excel (revisión del PR #134).

        // Hoy, la semana, el mes o el año (sin periodo: hoy) del día pedido (sin fecha: hoy), contra el mismo periodo del año pasado.
        api.MapGet("/cajas/{cajaId:guid}/reportes/ventas", (Guid cajaId, Guid usuarioId, string? periodo, DateOnly? fecha, PosDbContext db,
            OpcionesEleventa opcionesEleventa, CancellationToken ct) =>
            new ReportesDeVentas(db).ArmarAsync(cajaId, usuarioId, Periodo(periodo), fecha, opcionesEleventa.Piloto, ct));

        // El mismo reporte en Excel: una hoja por tabla.
        api.MapGet("/cajas/{cajaId:guid}/reportes/ventas/excel", async (Guid cajaId, Guid usuarioId, string? periodo, DateOnly? fecha, PosDbContext db,
            OpcionesEleventa opcionesEleventa, CancellationToken ct) =>
        {
            var reporte = await new ReportesDeVentas(db).ArmarAsync(cajaId, usuarioId, Periodo(periodo), fecha, opcionesEleventa.Piloto, ct);
            var quien = await QuienExporta.DeAsync(db, await SucursalDeAsync(db, cajaId), usuarioId, ct);
            return Excel(ExcelDeReportes.Armar(reporte, quien), ArchivosDeReportes.ReporteDeVentas(reporte.Periodo, reporte.Desde));
        });

        // Ventas por período: los tickets de unos días (sin fechas: hoy) de una caja o de todas, de 100 en 100, con sus totales.
        api.MapGet("/cajas/{cajaId:guid}/reportes/ventas-por-periodo", (Guid cajaId, Guid usuarioId, DateOnly? desde, DateOnly? hasta, Guid? caja,
            int? pagina, PosDbContext db, OpcionesEleventa opcionesEleventa, CancellationToken ct) =>
            new ReporteVentasPorPeriodo(db).ArmarAsync(cajaId, usuarioId, desde, hasta, caja, pagina ?? 0, opcionesEleventa.Piloto, ct));

        // Todos los tickets de esos días en Excel, con los totales y cada día.
        api.MapGet("/cajas/{cajaId:guid}/reportes/ventas-por-periodo/excel", async (Guid cajaId, Guid usuarioId, DateOnly? desde, DateOnly? hasta,
            Guid? caja, PosDbContext db, OpcionesEleventa opcionesEleventa, CancellationToken ct) =>
        {
            var ventas = await new ReporteVentasPorPeriodo(db).TodoAsync(cajaId, usuarioId, desde, hasta, caja, opcionesEleventa.Piloto, ct: ct);
            var quien = await QuienExporta.DeAsync(db, await SucursalDeAsync(db, cajaId), usuarioId, ct);
            var deCaja = caja is { } elegida ? ventas.Cajas.FirstOrDefault(c => c.Id == elegida)?.Nombre : null;
            return Excel(ExcelDeVentasPorPeriodo.Armar(ventas, quien, ct), ArchivosDeReportes.VentasPorPeriodo(ventas.Desde, ventas.Hasta, deCaja));
        });
    }

    private static PeriodoReporte Periodo(string? periodo)
    {
        var tipo = PeriodoReporte.Hoy;
        if (!string.IsNullOrWhiteSpace(periodo) && !(Enum.TryParse(periodo, ignoreCase: true, out tipo) && Enum.IsDefined(tipo)))
            throw new ArgumentException($"«{periodo}» no es un periodo: hoy, la semana, el mes o el año.");
        return tipo;
    }

    private static async Task<Guid> SucursalDeAsync(PosDbContext db, Guid cajaId) =>
        (await db.Cajas.FindAsync(cajaId))?.SucursalId ?? throw new InvalidOperationException("La caja no existe.");

    /// <summary>Un libro de Excel con su nombre: la caja lo propone al guardarlo (y es el de la descarga en el navegador).</summary>
    internal static IResult Excel(byte[] libro, string nombre) => Results.File(libro, ArchivosDeReportes.TipoExcel, nombre);
}
