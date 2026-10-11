using PapeleriaPos.Eleventa;
using PapeleriaPos.Nucleo;
using PapeleriaPos.Nucleo.Catalogo;

namespace PapeleriaPos.Herramientas.Diagnostico;

public enum Formato
{
    Texto,
    Entero,
    Dinero,
    Porcentaje,
    Fecha,
    Decimal,
}

public sealed record FilaResumen(string Concepto, object? Valor, Formato Formato, string? Nota = null);

public sealed record FilaDineroParado(
    Articulo Articulo, decimal Existencia, decimal CostoPromedio, decimal Valor, DateTime? UltimaVenta, int? DiasSinVender, string Situacion);

public sealed record FilaAbc(
    int Rango, Articulo Articulo, decimal Unidades, decimal Venta, decimal Utilidad, decimal Margen, decimal PorcentajeVenta,
    decimal PorcentajeAcumulado, char Clase);

public sealed record FilaDepartamento(
    string Departamento, bool CobroTerceros, decimal Venta, decimal Costo, decimal Utilidad, decimal Margen, decimal PorcentajeVenta,
    int VariantesVendidas);

public sealed record FilaRevisar(string Problema, Articulo Articulo, decimal? Precio, decimal? Costo, string Detalle);

public sealed record FilaMes(DateOnly Mes, int Tickets, decimal Venta, decimal PorcentajeVenta, decimal VentaPorDia);

public sealed record FilaTemporada(Articulo Articulo, decimal VentaTemporada, decimal VentaResto, decimal PorcentajeEnTemporada, decimal Indice);

public static class Problemas
{
    public const string SinCodigo = "Sin código";
    public const string PrecioBajoCosto = "Precio debajo del costo";
    public const string Duplicado = "Posible duplicado";
}

/// <summary>
/// Los análisis del diagnóstico. Solo calculan; <see cref="LibroDiagnostico"/> los pasa a Excel.
/// Las recargas y pagos de servicios (cobros a terceros) no cuentan como venta propia.
/// </summary>
public sealed class Analisis(DatosDiagnostico datos)
{
    /// <summary>Lo que no se vende en este tiempo se considera dinero parado.</summary>
    public const int DiasSinVenta = 90;

    private readonly List<LineaVendida> _propias = datos.Lineas.Where(l => !datos.Articulos[l.VarianteId].EsCobroTerceros).ToList();

    public DateOnly PrimerDia => DateOnly.FromDateTime(datos.Ventas.Min(v => v.FechaLocal));
    public DateOnly UltimoDia => DateOnly.FromDateTime(datos.Ventas.Max(v => v.FechaLocal));

    private static decimal Pesos(long centavos) => centavos / 100m;

    private static decimal Proporcion(decimal parte, decimal total) => total == 0 ? 0 : parte / total;

    // ── Resumen ─────────────────────────────────────────────────────────────────

    public List<FilaResumen> Resumen()
    {
        var validas = datos.Ventas.Where(v => !v.Cancelada).ToList();
        var dias = validas.GroupBy(v => DateOnly.FromDateTime(v.FechaLocal)).ToList();
        var pico = dias.MaxBy(d => d.Count())!;
        var venta = Pesos(_propias.Sum(l => l.ImporteCentavos));
        var costo = Pesos(_propias.Sum(l => l.CostoCentavos));
        var terceros = Pesos(datos.Lineas.Sum(l => l.ImporteCentavos)) - venta;
        var inventario = Pesos(datos.Existencias.Values.Where(e => e.Cantidad.EsPositiva).Sum(e => e.CostoCentavos));
        var diasPeriodo = UltimoDia.DayNumber - PrimerDia.DayNumber + 1;
        var costoAnual = costo * 365 / diasPeriodo;
        var rotacion = inventario == 0 ? 0 : costoAnual / inventario;

        var parado = DineroParado();
        var abc = Abc();
        var revisar = RevisarCatalogo();
        var temporada = Pesos(_propias.Where(l => EsRegresoAClases(l.FechaLocal)).Sum(l => l.ImporteCentavos));

        return
        [
            new("Periodo analizado", $"{PrimerDia:dd/MM/yyyy} a {UltimoDia:dd/MM/yyyy}", Formato.Texto, $"{diasPeriodo} días"),
            new("Tickets", validas.Count, Formato.Entero, $"Sin contar {datos.Ventas.Count - validas.Count} cancelados"),
            new("Tickets por día abierto", (decimal)validas.Count / dias.Count, Formato.Decimal, $"{dias.Count} días con venta"),
            new("Día con más tickets", pico.Key.ToDateTime(TimeOnly.MinValue), Formato.Fecha, $"{pico.Count()} tickets"),
            new("Ticket promedio", Proporcion(venta + terceros, validas.Count), Formato.Dinero),
            new("Venta propia", venta, Formato.Dinero, "Con IVA, sin recargas ni pagos de servicios"),
            new("Costo de lo vendido", costo, Formato.Dinero),
            new("Utilidad bruta", venta - costo, Formato.Dinero),
            new("Margen bruto", Proporcion(venta - costo, venta), Formato.Porcentaje, "Utilidad ÷ venta"),
            new("Cobros a terceros", terceros, Formato.Dinero, "Recargas y pagos: el dinero es del proveedor"),
            new("Inventario a costo", inventario, Formato.Dinero, $"{datos.Existencias.Values.Count(e => e.Cantidad.EsPositiva)} variantes con existencia"),
            new("Rotación del inventario", rotacion, Formato.Decimal, "Veces al año que se vende el inventario completo"),
            new("Días de inventario", rotacion == 0 ? null : 365 / rotacion, Formato.Decimal, "Días que alcanzaría el inventario sin comprar"),
            new("", null, Formato.Texto),
            new("Dinero parado", parado.Sum(p => p.Valor), Formato.Dinero, $"{parado.Count} variantes sin venta en {DiasSinVenta} días (hoja «Dinero parado»)"),
            new("Productos que hacen el 80 % de la venta", Proporcion(abc.Count(a => a.Clase == 'A'), abc.Count), Formato.Porcentaje,
                $"{abc.Count(a => a.Clase == 'A')} de {abc.Count} variantes vendidas (hoja «80-20»)"),
            new("Regreso a clases", Proporcion(temporada, venta), Formato.Porcentaje, "De la venta propia, del 15 de julio al 15 de septiembre (hoja «Temporada»)"),
            new("Sin código", revisar.Count(r => r.Problema == Problemas.SinCodigo), Formato.Entero, "Hoja «Revisar catálogo»"),
            new("Precio debajo del costo", revisar.Count(r => r.Problema == Problemas.PrecioBajoCosto), Formato.Entero, "Hoja «Revisar catálogo»"),
            new("Posibles duplicados", revisar.Count(r => r.Problema == Problemas.Duplicado), Formato.Entero, "Hoja «Revisar catálogo»"),
        ];
    }

    // ── Dinero parado ───────────────────────────────────────────────────────────

    /// <summary>Lo que tiene existencia pero no se ha vendido en los últimos <see cref="DiasSinVenta"/> días.</summary>
    public List<FilaDineroParado> DineroParado()
    {
        var ultimaVenta = _propias.GroupBy(l => l.VarianteId).ToDictionary(g => g.Key, g => g.Max(l => l.FechaLocal));
        var limite = UltimoDia.AddDays(-DiasSinVenta).ToDateTime(TimeOnly.MaxValue);

        return datos.Existencias.Values
            .Where(e => e.Cantidad.EsPositiva && e.CostoCentavos > 0)
            .Select(e => (Existencia: e, Ultima: ultimaVenta.TryGetValue(e.VarianteId, out var f) ? f : (DateTime?)null))
            .Where(x => x.Ultima is null || x.Ultima <= limite)
            .Select(x => new FilaDineroParado(
                datos.Articulos[x.Existencia.VarianteId],
                x.Existencia.Cantidad.EnUnidades,
                Math.Round(x.Existencia.CostoPromedio!.Value / 100, 2),
                Pesos(x.Existencia.CostoCentavos),
                x.Ultima,
                x.Ultima is { } u ? UltimoDia.DayNumber - DateOnly.FromDateTime(u).DayNumber : null,
                x.Ultima is null ? "Sin ventas en todo el periodo" : $"Sin ventas en {DiasSinVenta} días o más"))
            .OrderByDescending(f => f.Valor)
            .ToList();
    }

    // ── 80-20 ───────────────────────────────────────────────────────────────────

    /// <summary>Variantes de la que más vende a la que menos. A: hacen el 80 % de la venta; B: el siguiente 15 %; C: el resto.</summary>
    public List<FilaAbc> Abc()
    {
        var porVariante = _propias
            .GroupBy(l => l.VarianteId)
            .Select(g => (Id: g.Key, Unidades: g.Sum(l => l.Cantidad.EnUnidades), Venta: Pesos(g.Sum(l => l.ImporteCentavos)), Costo: Pesos(g.Sum(l => l.CostoCentavos))))
            .Where(x => x.Venta > 0)
            .OrderByDescending(x => x.Venta)
            .ToList();
        var total = porVariante.Sum(x => x.Venta);

        var filas = new List<FilaAbc>(porVariante.Count);
        var acumulado = 0m;
        foreach (var x in porVariante)
        {
            var clase = acumulado < 0.80m ? 'A' : acumulado < 0.95m ? 'B' : 'C';
            acumulado += x.Venta / total;
            filas.Add(new FilaAbc(filas.Count + 1, datos.Articulos[x.Id], x.Unidades, x.Venta, x.Venta - x.Costo,
                Proporcion(x.Venta - x.Costo, x.Venta), x.Venta / total, acumulado, clase));
        }
        return filas;
    }

    // ── Margen por departamento ─────────────────────────────────────────────────

    public List<FilaDepartamento> MargenPorDepartamento()
    {
        var totalPropio = Pesos(_propias.Sum(l => l.ImporteCentavos));
        return datos.Lineas
            .GroupBy(l => (datos.Articulos[l.VarianteId].Departamento, datos.Articulos[l.VarianteId].EsCobroTerceros))
            .Select(g =>
            {
                var venta = Pesos(g.Sum(l => l.ImporteCentavos));
                var costo = Pesos(g.Sum(l => l.CostoCentavos));
                return new FilaDepartamento(g.Key.Departamento, g.Key.EsCobroTerceros, venta, costo, venta - costo,
                    Proporcion(venta - costo, venta), g.Key.EsCobroTerceros ? 0 : Proporcion(venta, totalPropio),
                    g.Select(l => l.VarianteId).Distinct().Count());
            })
            .OrderBy(f => f.CobroTerceros)
            .ThenByDescending(f => f.Venta)
            .ToList();
    }

    // ── Revisar catálogo ────────────────────────────────────────────────────────

    public List<FilaRevisar> RevisarCatalogo()
    {
        var filas = new List<FilaRevisar>();
        var mercancia = datos.Articulos.Values.Where(a => a.Tipo == TipoProducto.Mercancia).ToList();

        foreach (var a in mercancia.Where(a => a.Codigo is null).OrderBy(a => a.Departamento).ThenBy(a => a.Nombre))
            filas.Add(new FilaRevisar(Problemas.SinCodigo, a, PrecioMenudeo(a.VarianteId), CostoUnitario(a.VarianteId), "No se puede escanear"));

        foreach (var a in mercancia.OrderBy(a => a.Departamento).ThenBy(a => a.Nombre))
        {
            if (CostoUnitario(a.VarianteId) is not { } costo)
                continue;
            foreach (var p in datos.Precios.Where(p => p.VarianteId == a.VarianteId).OrderBy(p => p.CantidadMinima))
            {
                var porUnidad = p.Presentacion is null ? p.PrecioCentavos / 100m : p.PrecioCentavos / 100m / p.Factor.EnUnidades;
                if (porUnidad >= costo)
                    continue;
                var cual = p.Presentacion is not null ? $"Precio propio de «{p.Presentacion}»"
                    : p.CantidadMinima > Cantidad.Unidades(1) ? $"Mayoreo desde {p.CantidadMinima}" : "Menudeo";
                filas.Add(new FilaRevisar(Problemas.PrecioBajoCosto, a, Math.Round(porUnidad, 2), costo,
                    $"{cual}: se pierden {costo - porUnidad:C2} por {a.Unidad.ToString().ToLowerInvariant()}"));
            }
        }

        foreach (var grupo in NombresParecidos.Agrupar(datos.Articulos.Values, a => a.Nombre))
        {
            foreach (var a in grupo)
            {
                var otros = string.Join("; ", grupo.Where(o => o != a).Select(o => o.Codigo is null ? o.Nombre : $"{o.Nombre} ({o.Codigo})"));
                filas.Add(new FilaRevisar(Problemas.Duplicado, a, PrecioMenudeo(a.VarianteId), CostoUnitario(a.VarianteId), $"Parecido a: {otros}"));
            }
        }
        return filas;
    }

    private decimal? PrecioMenudeo(Guid varianteId) =>
        datos.Precios.Where(p => p.VarianteId == varianteId && p.Presentacion is null).MinBy(p => p.CantidadMinima)?.PrecioCentavos / 100m;

    /// <summary>Pesos por unidad base: el costo promedio si hay existencia, si no el último costo de compra.</summary>
    private decimal? CostoUnitario(Guid varianteId)
    {
        if (datos.Existencias.TryGetValue(varianteId, out var e) && e.CostoPromedio is { } promedio)
            return Math.Round(promedio / 100, 2);
        return datos.UltimoCosto.TryGetValue(varianteId, out var ultimo) ? ultimo / 100m : null;
    }

    // ── Temporada ───────────────────────────────────────────────────────────────

    public static bool EsRegresoAClases(DateTime fecha) =>
        (fecha.Month, fecha.Day) is (7, >= 15) or (8, _) or (9, <= 15);

    public List<FilaMes> Meses()
    {
        var total = Pesos(_propias.Sum(l => l.ImporteCentavos));
        var ticketsPorMes = datos.Ventas.Where(v => !v.Cancelada)
            .GroupBy(v => new DateOnly(v.FechaLocal.Year, v.FechaLocal.Month, 1))
            .ToDictionary(g => g.Key, g => (Tickets: g.Count(), Dias: g.Select(v => v.FechaLocal.Date).Distinct().Count()));

        return _propias
            .GroupBy(l => new DateOnly(l.FechaLocal.Year, l.FechaLocal.Month, 1))
            .OrderBy(g => g.Key)
            .Select(g =>
            {
                var venta = Pesos(g.Sum(l => l.ImporteCentavos));
                var (tickets, dias) = ticketsPorMes[g.Key];
                return new FilaMes(g.Key, tickets, venta, Proporcion(venta, total), Proporcion(venta, dias));
            })
            .ToList();
    }

    /// <summary>
    /// Lo que más se vende en regreso a clases comparado con el resto del año. Índice: venta diaria en temporada ÷
    /// venta diaria fuera de ella (2 = se vende el doble).
    /// </summary>
    public List<FilaTemporada> ProductosDeTemporada(int maximo = 100)
    {
        var diasTemporada = 0;
        var diasResto = 0;
        for (var d = PrimerDia; d <= UltimoDia; d = d.AddDays(1))
        {
            if (EsRegresoAClases(d.ToDateTime(TimeOnly.MinValue)))
                diasTemporada++;
            else
                diasResto++;
        }
        if (diasTemporada == 0)
            return [];

        return _propias
            .GroupBy(l => l.VarianteId)
            .Select(g =>
            {
                var temporada = Pesos(g.Where(l => EsRegresoAClases(l.FechaLocal)).Sum(l => l.ImporteCentavos));
                var resto = Pesos(g.Where(l => !EsRegresoAClases(l.FechaLocal)).Sum(l => l.ImporteCentavos));
                var indice = resto == 0 || diasResto == 0 ? 99m : temporada / diasTemporada / (resto / diasResto);
                return new FilaTemporada(datos.Articulos[g.Key], temporada, resto, Proporcion(temporada, temporada + resto), Math.Round(indice, 2));
            })
            .Where(f => f.VentaTemporada > 0 && f.Indice >= 1.5m)
            .OrderByDescending(f => f.VentaTemporada)
            .Take(maximo)
            .ToList();
    }
}
