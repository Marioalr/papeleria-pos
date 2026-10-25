using System.Globalization;
using Microsoft.EntityFrameworkCore;
using PapeleriaPos.Datos;
using PapeleriaPos.Eleventa;
using PapeleriaPos.Herramientas.Importacion;
using PapeleriaPos.Nucleo;
using PapeleriaPos.Nucleo.Catalogo;
using PapeleriaPos.Nucleo.Historico;
using PapeleriaPos.Nucleo.Importacion;

namespace PapeleriaPos.Herramientas.Eleventa;

/// <summary>Lo que hizo la importación final (o lo que haría, si se simula).</summary>
public sealed class ResultadoImportacionFinal
{
    /// <summary>Tickets de eleventa que no estaban en el historial.</summary>
    public int Nuevos { get; internal set; }

    /// <summary>
    /// Tickets que ya estaban, pero distintos: se les devolvió o se cancelaron después de la importación anterior (o se
    /// importaron con una versión de antes). Se escriben como están ahora en eleventa.
    /// </summary>
    public int Actualizados { get; internal set; }

    /// <summary>
    /// Tickets que ya estaban y que eleventa no cambió, pero que una importación de antes del 2026-10-24 leyó mal: sus líneas
    /// viejas (sin <c>PRECIO_FINAL</c> ni <c>TOTAL_ARTICULO</c>, de antes del 2024-10-05) entraron en $0 y con el costo en
    /// negativo. Se escriben otra vez con su <c>PRECIO_USADO</c>. No cuentan en <see cref="Actualizados"/>: allá no cambió nada.
    /// </summary>
    public int Corregidos { get; internal set; }

    /// <summary>Tickets que ya estaban igual.</summary>
    public int Iguales { get; internal set; }

    /// <summary>Tickets que estaban en el historial y son ventas de la caja 2: se quitaron.</summary>
    public int Quitados { get; internal set; }

    /// <summary>Tickets del historial que ya no están en eleventa: se dejaron como estaban.</summary>
    public int YaNoEstan { get; internal set; }

    /// <summary>Los tickets de eleventa que quedan en el historial (también los cancelados).</summary>
    public int Tickets { get; internal set; }

    /// <summary>Lo que suman, sin los cancelados.</summary>
    public long TotalCentavos { get; internal set; }

    public DateTime? DesdeUtc { get; internal set; }
    public DateTime? HastaUtc { get; internal set; }

    /// <summary>
    /// Productos de eleventa que aquí no tenían mínimo ni máximo de inventario y tomaron los de eleventa (una base que se armó
    /// antes de que la importación los trajera, sin la lectura del piloto). Lo que ya tenían no se cambia.
    /// </summary>
    public int MinimosYMaximos { get; internal set; }

    /// <summary>
    /// Los mínimos y máximos de eleventa ya se habían traído a esta base (con ella, con la lectura del piloto o en otra importación
    /// final): no se volvieron a poner (<see cref="ReglasEleventa.EntidadMinimosTraidos"/>, revisión del PR #139).
    /// </summary>
    public bool MinimosYaTraidos { get; internal set; }

    /// <summary>Lo de la caja 2 que no se trajo porque ya está en la base, y lo que no se pudo decidir solo.</summary>
    public required LoDelPuenteEnEleventa DelPuente { get; init; }

    /// <summary>Texto del aviso → cuántas veces pasó.</summary>
    public SortedDictionary<string, int> Avisos { get; } = new(StringComparer.Ordinal);

    internal void Avisar(string aviso) => Avisos[aviso] = Avisos.GetValueOrDefault(aviso) + 1;
}

/// <summary>
/// La importación final (docs/migracion-eleventa.md, «La importación final»): al dejar eleventa, su historial de ventas entra a la
/// base de la tienda, la que ya tiene su catálogo (se armó con importar-eleventa y la siguió la lectura de cada pocos minutos) y
/// donde corrió el piloto. Solo el historial: el catálogo ya está, y las existencias salen del conteo físico. Del catálogo, solo
/// el mínimo y el máximo de inventario de los productos de eleventa que aquí no tienen ninguno (decidido el 2026-10-24, por
/// revisar): lo que ya tienen lo puso la lectura del piloto (eleventa manda) o alguien aquí, y no se pisa.
/// <list type="bullet">
/// <item>No trae los tickets que escribió el puente (<see cref="LoQueMandoElPuente"/>): ya están como ventas, con sus
/// devoluciones y cancelaciones, y contarían dos veces. Lo de eleventa entra como en una base nueva: cada ticket con lo que
/// eleventa ya le bajó al devolver, y los cancelados marcados.</item>
/// <item>Se puede correr las veces que haga falta: lo que ya estaba igual no se toca, lo que cambió en eleventa se vuelve a
/// escribir, lo que resultó ser de la caja 2 se quita y lo nuevo entra una vez (los mapeos dicen qué ticket es cada venta del
/// historial). Qué ticket de eleventa es cada venta de la caja 2 también queda en los mapeos: así, si después se quita el
/// puente, otra importación no los vuelve a traer.</item>
/// <item>Todo o nada: en una sola transacción.</item>
/// </list>
/// Lo que se vendió con un producto que la base no tiene se da de alta inactivo, como al importar una base nueva. No lee
/// eleventa: recibe las filas.
/// </summary>
public static class ImportacionFinal
{
    private const string Origen = ImportadorEleventa.Origen;

    /// <summary>Cuántos renglones se borran por vuelta.</summary>
    private const int Grupo = 500;

    public const string NoEsDeEleventa =
        "Esta base no se armó con importar-eleventa: la importación final trae el historial a la base de la tienda, que ya tiene el "
        + "catálogo de eleventa. Para una base nueva, usa --salida.";

    public const string AvisoProductoSinLeer =
        "Vendido con un producto de eleventa que esta base no tenía (no lo alcanzó a leer la caja): se creó inactivo para su historial";

    /// <summary>Por qué no se usa una copia de eleventa más vieja que lo que ya se importó: dejaría el historial como estaba antes.</summary>
    public static string CopiaVieja(DateTime ultimaCopiaUtc, DateTime ultimaAquiUtc) =>
        $"La copia de eleventa es de antes de lo que ya se importó: su último ticket es del {Fecha(ultimaCopiaUtc)} y en esta base ya hay "
        + $"del {Fecha(ultimaAquiUtc)}. Saca una copia nueva de eleventa.";

    /// <summary>Una venta del historial como está guardada.</summary>
    private sealed record Guardada(Guid Id, Guid SucursalId, DateTime FechaUtc, string FolioOrigen, long TotalCentavos, bool Cancelada);

    /// <summary>
    /// Una línea del historial: lo que se compara para saber si el ticket cambió en eleventa (milésimas, precio, importe, costo
    /// y si es un artículo común: lo importado antes de marcarlos se vuelve a escribir).
    /// </summary>
    private static (long, long, long, long, bool) Renglon(VentaHistoricaLinea l) =>
        (l.Cantidad.Milesimas, l.PrecioCentavos, l.ImporteCentavos, l.CostoCentavos, l.EsArticuloComun);

    /// <summary>
    /// Pone el historial de eleventa de la base al día con lo que dice eleventa, sin las ventas de la caja 2. Si no hay una
    /// transacción abierta, abre la suya (quien simula abre una y la deshace). La base queda apartada para escribir desde antes
    /// de leerla (SQLite: BEGIN IMMEDIATE): si la caja estuviera abierta, esperaría a que termine.
    /// </summary>
    public static async Task<ResultadoImportacionFinal> AplicarAsync(PosDbContext db, DatosEleventa e, CancellationToken ct = default)
    {
        await using var propia = db.Database.CurrentTransaction is null ? await db.Database.BeginTransactionAsync(ct) : null;
        var r = await ImportarAsync(db, e, ct);
        if (propia is not null)
            await propia.CommitAsync(ct);
        return r;
    }

    private static async Task<ResultadoImportacionFinal> ImportarAsync(PosDbContext db, DatosEleventa e, CancellationToken ct)
    {
        var mapeos = await db.MapeosImportacion.AsNoTracking().Where(m => m.Origen == Origen).ToListAsync(ct);
        if (!mapeos.Any(m => m.Entidad == nameof(Variante)))
            throw new InvalidOperationException(NoEsDeEleventa);

        var puente = await LoQueMandoElPuente.LeerAsync(db, ct);
        var delPuente = LoQueMandoElPuente.Reconocer(puente, e);
        var r = new ResultadoImportacionFinal { DelPuente = delPuente };

        // Lo que ya está en el historial, por ticket de eleventa.
        var historial = new Dictionary<int, MapeoImportacion>();
        foreach (var m in mapeos.Where(m => m.Entidad == nameof(VentaHistorica)))
            if (Numero(m.IdOrigen) is { } ticket)
                historial.TryAdd(ticket, m);
        var guardadas = await db.VentasHistoricas.AsNoTracking()
            .Select(v => new Guardada(v.Id, v.SucursalId, v.FechaUtc, v.FolioOrigen, v.TotalCentavos, v.Cancelada))
            .ToDictionaryAsync(v => v.Id, ct);
        var renglones = (await db.VentasHistoricasLineas.AsNoTracking()
                .Select(l => new { l.VentaHistoricaId, l.Cantidad, l.PrecioCentavos, l.ImporteCentavos, l.CostoCentavos, l.EsArticuloComun })
                .ToListAsync(ct))
            .ToLookup(l => l.VentaHistoricaId, l => (l.Cantidad.Milesimas, l.PrecioCentavos, l.ImporteCentavos, l.CostoCentavos, l.EsArticuloComun));
        var deEleventa = historial.Values.Where(m => guardadas.ContainsKey(m.IdNuevo)).Select(m => guardadas[m.IdNuevo]).ToList();

        // Una copia más vieja que lo ya importado regresaría el historial a como estaba: no se usa.
        var cobrados = e.Tickets.Where(t => t.VendidoEn is not null).ToList();
        if (deEleventa.Count > 0)
        {
            var ultimaAqui = deEleventa.Max(v => v.FechaUtc);
            var ultimaCopia = cobrados.Count > 0 ? HoraMexico.AUtc(cobrados.Max(t => t.VendidoEn!.Value)) : DateTime.MinValue;
            if (ultimaCopia < ultimaAqui)
                throw new InvalidOperationException(CopiaVieja(ultimaCopia, ultimaAqui));
        }
        var sucursalId = deEleventa.Select(v => (Guid?)v.SucursalId).FirstOrDefault()
            ?? await db.Sucursales.OrderBy(s => s.Clave != "M").ThenBy(s => s.Nombre).Select(s => s.Id).FirstAsync(ct);

        var lote = new ResultadoImportacion();
        var variante = await VariantesAsync(db, e, mapeos, lote, r, ct);
        var esComun = ImportadorEleventa.Comunes(e);
        var lineasPorTicket = e.Lineas.ToLookup(l => l.TicketId);
        var aBorrar = new List<Guid>();
        var mapeosABorrar = new List<Guid>();
        foreach (var t in e.Tickets.OrderBy(t => t.Id))
        {
            if (t.VendidoEn is null)
            {
                r.Avisar(ImportadorEleventa.AvisoTicketAbierto);
                continue;
            }
            // Una venta de la caja 2: ya está en la base.
            if (delPuente.Tickets.ContainsKey(t.Id))
                continue;

            // Para comparar con lo guardado no hace falta saber de qué producto es cada línea (ni darlo de alta).
            var como = ImportadorEleventa.Venta(t, lineasPorTicket[t.Id], sucursalId, _ => Guid.Empty, esComun, _ => { });
            r.Tickets++;
            if (!como.Cancelada)
                r.TotalCentavos += como.TotalCentavos;
            (r.DesdeUtc, r.HastaUtc) = (Min(r.DesdeUtc, como.FechaUtc), Max(r.HastaUtc, como.FechaUtc));

            var mapeo = historial.GetValueOrDefault(t.Id);
            if (mapeo is not null && guardadas.TryGetValue(mapeo.IdNuevo, out var guardada))
            {
                if (Igual(guardada, renglones[guardada.Id], como))
                {
                    r.Iguales++;
                    continue;
                }
                // Cambió en eleventa (se devolvió algo o se canceló), o se había leído mal (2026-10-24: las líneas viejas, sin
                // PRECIO_FINAL, entraban en $0): se vuelve a escribir como está, con el mismo Id. Si leído como antes queda igual a
                // lo guardado, allá no cambió nada: se dice que se corrigió, no que cambió.
                if (SeLeiaMal(t, lineasPorTicket[t.Id], sucursalId, esComun) is { } comoAntes && Igual(guardada, renglones[guardada.Id], comoAntes))
                    r.Corregidos++;
                else
                    r.Actualizados++;
                aBorrar.Add(guardada.Id);
                lote.Ventas.Add(ImportadorEleventa.Venta(t, lineasPorTicket[t.Id], sucursalId, variante, esComun, r.Avisar, guardada.Id));
                continue;
            }

            if (mapeo is not null)
                mapeosABorrar.Add(mapeo.Id);
            r.Nuevos++;
            var nueva = ImportadorEleventa.Venta(t, lineasPorTicket[t.Id], sucursalId, variante, esComun, r.Avisar);
            lote.Ventas.Add(nueva);
            ImportadorEleventa.Mapear(lote, nameof(VentaHistorica), t.Id.ToString(CultureInfo.InvariantCulture), nueva.Id);
        }

        // Lo que ya estaba y resultó ser de la caja 2 se quita; lo que ya no está en eleventa se deja.
        var enEleventa = cobrados.Select(t => t.Id).ToHashSet();
        foreach (var (ticket, mapeo) in historial)
        {
            if (delPuente.Tickets.ContainsKey(ticket))
            {
                if (guardadas.ContainsKey(mapeo.IdNuevo))
                {
                    aBorrar.Add(mapeo.IdNuevo);
                    r.Quitados++;
                }
                mapeosABorrar.Add(mapeo.Id);
            }
            else if (!enEleventa.Contains(ticket))
                r.YaNoEstan++;
        }

        // Qué ticket de eleventa es cada venta de la caja 2. Con la bandeja, como se acaba de reconocer; sin ella (se quitó el
        // puente) se queda lo de la importación anterior.
        if (puente.Ventas.Count > 0)
        {
            var ligados = new HashSet<int>();
            foreach (var m in mapeos.Where(m => m.Entidad == LoQueMandoElPuente.EntidadMapeo))
            {
                if (Numero(m.IdOrigen) is { } ticket && delPuente.Tickets.TryGetValue(ticket, out var venta) && venta == m.IdNuevo && ligados.Add(ticket))
                    continue;
                mapeosABorrar.Add(m.Id);
            }
            foreach (var (ticket, venta) in delPuente.Tickets.Where(x => !ligados.Contains(x.Key)))
                ImportadorEleventa.Mapear(lote, LoQueMandoElPuente.EntidadMapeo, ticket.ToString(CultureInfo.InvariantCulture), venta);
        }

        // El mínimo y el máximo de eleventa, a sus productos que aquí no tienen ninguno, una sola vez: si la base ya los trajo, son del
        // POS (también el que se quitó en F3). Antes de guardar el historial, que ya no sigue los cambios.
        r.MinimosYaTraidos = mapeos.Any(m => m.Entidad == ReglasEleventa.EntidadMinimosTraidos);
        if (!r.MinimosYaTraidos)
        {
            r.MinimosYMaximos = await MinimosYMaximosAsync(db, e, mapeos, r, ct);
            ImportadorEleventa.Mapear(lote, ReglasEleventa.EntidadMinimosTraidos, ReglasEleventa.IdMinimosTraidos, Guid.Empty);
        }

        // Primero lo que se va: sus líneas, las ventas y sus mapeos (las que se vuelven a escribir conservan su mapeo).
        foreach (var grupo in aBorrar.Chunk(Grupo))
        {
            await db.VentasHistoricasLineas.Where(l => grupo.Contains(l.VentaHistoricaId)).ExecuteDeleteAsync(ct);
            await db.VentasHistoricas.Where(v => grupo.Contains(v.Id)).ExecuteDeleteAsync(ct);
        }
        foreach (var grupo in mapeosABorrar.Chunk(Grupo))
            await db.MapeosImportacion.Where(m => grupo.Contains(m.Id)).ExecuteDeleteAsync(ct);
        await GuardadoLote.GuardarAsync(lote, db, ct);
        return r;
    }

    /// <summary>
    /// El mínimo y el máximo de eleventa (<see cref="ReglasEleventa.PonerMinimoYMaximo"/>) en las variantes de sus productos que
    /// aquí no tienen ninguno: así llegan a una base que se armó antes de que la importación los trajera y que no siguió la
    /// lectura del piloto. Lo que ya tienen, de esa lectura o de F3, no se pisa, y repetirla no cambia nada. Ni lo juntado en
    /// la depuración ni los servicios. Si dos productos de eleventa quedaron en una variante, el primero (los activos y el más
    /// viejo), como la lectura. Regresa cuántas tomaron los de eleventa.
    /// </summary>
    private static async Task<int> MinimosYMaximosAsync(PosDbContext db, DatosEleventa e, List<MapeoImportacion> mapeos, ResultadoImportacionFinal r,
        CancellationToken ct)
    {
        var varianteDe = mapeos.Where(m => m.Entidad == nameof(Variante) && Numero(m.IdOrigen) is not null)
            .GroupBy(m => Numero(m.IdOrigen)!.Value).ToDictionary(g => g.Key, g => g.First().IdNuevo);
        var sinMinimo = await db.Variantes.Include(v => v.Producto)
            .Where(v => v.JuntadaEnId == null && v.InventarioMinimo == null && v.InventarioMaximo == null)
            .ToDictionaryAsync(v => v.Id, ct);
        var puestos = 0;
        foreach (var fila in e.Productos.OrderBy(p => p.Eliminado).ThenBy(p => p.Id))
        {
            if (varianteDe.TryGetValue(fila.Id, out var id) && sinMinimo.Remove(id, out var variante)
                && ReglasEleventa.PonerMinimoYMaximo(variante, variante.Producto!.LlevaInventario, fila, r.Avisar))
                puestos++;
        }
        await db.SaveChangesAsync(ct);
        return puestos;
    }

    /// <summary>
    /// La variante de cada línea, por su código: la del producto de eleventa que tiene ese código (los activos primero y luego
    /// el más viejo, como al importar), por su Id de eleventa. Si la base no tiene ese producto (se dio de alta allá después de
    /// la última lectura), se crea inactivo con su Id de eleventa; si el código ya no es de ningún producto, se usa el producto
    /// inactivo que se creó para él en otra importación, o se crea uno (como al importar una base nueva). Un artículo común va
    /// al producto de los comunes de eleventa, uno solo para todos (el de otra importación, o se crea; 2026-10-24).
    /// </summary>
    private static async Task<Func<FilaLinea, Guid>> VariantesAsync(PosDbContext db, DatosEleventa e, List<MapeoImportacion> mapeos,
        ResultadoImportacion lote, ResultadoImportacionFinal r, CancellationToken ct)
    {
        var existentes = (await db.Variantes.AsNoTracking().Select(v => v.Id).ToListAsync(ct)).ToHashSet();
        var mapeadas = mapeos.Where(m => m.Entidad == nameof(Variante) && existentes.Contains(m.IdNuevo)).ToList();
        var porId = mapeadas.Where(m => Numero(m.IdOrigen) is not null).GroupBy(m => Numero(m.IdOrigen)!.Value)
            .ToDictionary(g => g.Key, g => g.First().IdNuevo);
        var porCodigo = mapeadas.Where(m => m.IdOrigen.StartsWith(ImportadorEleventa.IdPorCodigo, StringComparison.Ordinal))
            .GroupBy(m => m.IdOrigen[ImportadorEleventa.IdPorCodigo.Length..], StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First().IdNuevo, StringComparer.OrdinalIgnoreCase);
        var duenoDelCodigo = new Dictionary<string, FilaProducto>(StringComparer.OrdinalIgnoreCase);
        foreach (var p in e.Productos.OrderBy(p => p.Eliminado).ThenBy(p => p.Id))
            duenoDelCodigo.TryAdd(p.Codigo.Trim(), p);
        var ocupados = (await db.CodigosBarras.AsNoTracking().Select(c => c.Codigo).ToListAsync(ct)).ToHashSet(StringComparer.OrdinalIgnoreCase);

        // Lo vendido o contado con fracciones dice si un producto «por pieza» es a granel, como al importar.
        var conFracciones = e.Lineas.Where(l => !ReglasEleventa.EsEntero(l.Cantidad)).Select(l => l.Codigo.Trim()).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var fraccionadas = e.Existencias.GroupBy(x => x.ProductoId).Where(g => !ReglasEleventa.EsEntero(g.Sum(x => x.Cantidad))).Select(g => g.Key).ToHashSet();

        var departamentos = await db.Departamentos.AsNoTracking().ToDictionaryAsync(d => d.Id, ct);
        var deptoDe = mapeos.Where(m => m.Entidad == nameof(Departamento) && Numero(m.IdOrigen) is not null && departamentos.ContainsKey(m.IdNuevo))
            .GroupBy(m => Numero(m.IdOrigen)!.Value).ToDictionary(g => g.Key, g => departamentos[g.First().IdNuevo]);
        Departamento? sinDepartamento = null;
        Departamento DepartamentoDe(int? id)
        {
            if (id is { } i && deptoDe.TryGetValue(i, out var departamento))
                return departamento;
            if (sinDepartamento is null)
            {
                // El «- Sin Departamento -» de eleventa o el que se creó sin él; si no hay ninguno, se crea.
                sinDepartamento = departamentos.Values.FirstOrDefault(d => ReglasEleventa.EsSinDepartamento(d.Nombre));
                if (sinDepartamento is null)
                {
                    sinDepartamento = new Departamento { Nombre = "Sin departamento" };
                    lote.Departamentos.Add(sinDepartamento);
                }
            }
            return sinDepartamento;
        }

        var esComun = ImportadorEleventa.Comunes(e);
        var comun = mapeadas.FirstOrDefault(m => m.IdOrigen == ImportadorEleventa.IdArticuloComun)?.IdNuevo;
        var deComunes = e.Departamentos.FirstOrDefault(d => ReglasEleventa.EsDeComunes(d.Nombre)) is { } comunes
            && deptoDe.TryGetValue(comunes.Id, out var suyo) ? suyo : null;
        return l =>
        {
            if (esComun(l))
                return comun ??= ImportadorEleventa.CrearProducto(lote, ImportadorEleventa.ArticuloComun, deComunes ?? DepartamentoDe(l.DepartamentoId),
                    UnidadBase.PiezaAGranel, activo: false, codigo: null, ImportadorEleventa.IdArticuloComun).Id;
            var codigo = l.Codigo.Trim();
            if (duenoDelCodigo.TryGetValue(codigo, out var p))
            {
                if (porId.TryGetValue(p.Id, out var id))
                    return id;
                var unidad = ReglasEleventa.Unidad(p, conFracciones.Contains(codigo) || fraccionadas.Contains(p.Id), _ => { });
                var deEleventa = ImportadorEleventa.CrearProducto(lote, p.Descripcion.Trim(), DepartamentoDe(p.DepartamentoId), unidad, activo: false,
                    ocupados.Add(codigo) ? codigo : null, p.Id.ToString(CultureInfo.InvariantCulture));
                // Con su mínimo y su máximo, como al importar: si la caja lo vuelve a leer en eleventa, lo adopta así.
                ReglasEleventa.PonerMinimoYMaximo(deEleventa, llevaInventario: true, p, r.Avisar);
                porId[p.Id] = deEleventa.Id;
                r.Avisar(AvisoProductoSinLeer);
                return deEleventa.Id;
            }
            if (porCodigo.TryGetValue(codigo, out var anterior))
                return anterior;
            var inactiva = ImportadorEleventa.CrearProducto(lote, l.Nombre.Trim(), DepartamentoDe(l.DepartamentoId),
                conFracciones.Contains(codigo) ? UnidadBase.PiezaAGranel : UnidadBase.Pieza, activo: false, ocupados.Add(codigo) ? codigo : null,
                ImportadorEleventa.IdPorCodigo + codigo);
            porCodigo[codigo] = inactiva.Id;
            r.Avisar(ImportadorEleventa.AvisoCodigoInexistente);
            return inactiva.Id;
        };
    }

    /// <summary>
    /// Lo que hizo, dicho para quien la corre: cómo quedó el historial, qué no se trajo por ser de la caja 2 (y por qué), cómo
    /// cuadra con eleventa y lo que hay que revisar a mano.
    /// </summary>
    public static IEnumerable<string> Describir(ResultadoImportacionFinal r)
    {
        var p = r.DelPuente;
        var periodo = r.DesdeUtc is { } desde && r.HastaUtc is { } hasta ? $", del {Dia(desde)} al {Dia(hasta)}" : "";
        yield return "Historial de ventas de eleventa (para los reportes)";
        yield return $"  En el historial     {r.Tickets,9:N0} tickets{periodo}: {Pesos(r.TotalCentavos)} sin los cancelados";
        yield return $"  Nuevos              {r.Nuevos,9:N0}";
        yield return $"  Actualizados        {r.Actualizados,9:N0}  (cambiaron en eleventa desde la importación anterior: devoluciones o cancelaciones)";
        if (r.Corregidos > 0)
            yield return $"  Corregidos          {r.Corregidos,9:N0}  (eleventa no los cambió: la importación anterior leyó en $0 sus líneas viejas, sin "
                + "PRECIO_FINAL; ahora llevan su PRECIO_USADO)";
        yield return $"  Ya estaban          {r.Iguales,9:N0}  (sin cambios)";
        if (r.Quitados > 0)
            yield return $"  Quitados            {r.Quitados,9:N0}  (eran ventas de la caja 2)";
        if (r.YaNoEstan > 0)
            yield return $"  Ya no en eleventa   {r.YaNoEstan,9:N0}  (se dejaron como estaban)";

        yield return "";
        yield return r.MinimosYaTraidos
            ? "Mínimo y máximo de inventario de eleventa: ya se habían traído (con la base, con la lectura del piloto o en otra importación "
                + "final); no se vuelven a poner: los de aquí, también los que se quitaron en F3, se quedan"
            : r.MinimosYMaximos > 0
                ? $"Mínimo y máximo de inventario de eleventa, en los productos que aquí no tenían ninguno: {r.MinimosYMaximos:N0} "
                    + "(lo que ya tenían, de la lectura del piloto o de F3, no cambió)"
                : "Mínimo y máximo de inventario de eleventa: nada que poner (los productos de eleventa ya tienen los suyos, o eleventa no les puso)";

        yield return "";
        yield return "No se trajeron: son ventas de la caja 2, que ya están en esta base con sus devoluciones";
        if (p.Tickets.Count == 0 && p.NoLlegaron == 0)
            yield return "  Ninguna: desde esta base no se mandaron ventas a eleventa.";
        else
        {
            yield return $"  Tickets             {p.Tickets.Count,9:N0}  {Pesos(p.TotalCentavos)} sin los cancelados"
                + (p.YaReconocidos > 0 ? " (los reconoció la importación anterior: el puente ya no está)" : "");
            if (p.Cancelados > 0)
                yield return $"    cancelados        {p.Cancelados,9:N0}";
            if (p.ConDevoluciones > 0)
            {
                yield return $"    con devoluciones  {p.ConDevoluciones,9:N0}  (devoluciones y cancelaciones que eleventa ya les bajó)";
                yield return $"      de la caja 2    {p.DevolucionesDeAqui,9:N0}";
                yield return $"      de eleventa     {p.DevolucionesDeEleventa,9:N0}  (aquí ya están en su venta, sin efectivo)";
                if (p.DevolucionesSinPasar > 0)
                    yield return $"      sin pasar       {p.DevolucionesSinPasar,9:N0}  (hechas en eleventa que no se han pasado a su venta ni ligado: abajo)";
            }
            if (p.PorSuMarca > 0)
                yield return $"    solo por su marca {p.PorSuMarca,9:N0}  (aquí no se supo qué número les dio eleventa)";
            if (p.NoLlegaron > 0)
                yield return $"  No llegaron         {p.NoLlegaron,9:N0}  (siguen pendientes o quedaron por revisar en la bandeja: en eleventa no hay nada que quitar)";
        }
        if (p.Idas > 0)
            yield return $"  Devoluciones en la caja 2 de tickets de eleventa: {p.Idas:N0}. Ya están en esos tickets, que se importaron así.";

        yield return "";
        yield return $"En eleventa, en total: {Pesos(r.TotalCentavos + p.TotalCentavos)} sin los cancelados = historial {Pesos(r.TotalCentavos)} "
            + $"+ caja 2 {Pesos(p.TotalCentavos)}";

        if (p.PorRevisar.Count > 0)
        {
            yield return "";
            yield return $"Para revisar a mano ({p.PorRevisar.Count:N0})";
            foreach (var cosa in p.PorRevisar)
                yield return $"  - {cosa}";
        }
        if (r.Avisos.Count > 0)
        {
            yield return "";
            yield return "Avisos";
            foreach (var (aviso, veces) in r.Avisos)
                yield return $"  {veces,6:N0}  {aviso}";
        }
    }

    /// <summary>
    /// El ticket como lo leía la importación antes del 2026-10-24, si tiene líneas viejas (sin <c>PRECIO_FINAL</c>, con
    /// <c>PRECIO_USADO</c>) que entonces entraban en $0; vacío si no tiene: se lee igual que antes.
    /// </summary>
    private static VentaHistorica? SeLeiaMal(FilaTicket t, IEnumerable<FilaLinea> lineas, Guid sucursalId, Func<FilaLinea, bool> esComun)
    {
        var lista = lineas.ToList();
        return lista.Any(l => l.PrecioFinal is null && l.PrecioUsado is not null)
            ? ImportadorEleventa.Venta(t, lista.Select(l => l with { PrecioUsado = null }), sucursalId, _ => Guid.Empty, esComun, _ => { })
            : null;
    }

    /// <summary>Si el ticket guardado es como está en eleventa: su encabezado y sus líneas (de qué producto es cada una no cuenta).</summary>
    private static bool Igual(Guardada g, IEnumerable<(long, long, long, long, bool)> renglones, VentaHistorica como) =>
        g.FechaUtc == como.FechaUtc && g.FolioOrigen == como.FolioOrigen && g.TotalCentavos == como.TotalCentavos && g.Cancelada == como.Cancelada
        && renglones.Order().SequenceEqual(como.Lineas.Select(Renglon).Order());

    private static int? Numero(string texto) => int.TryParse(texto, NumberStyles.None, CultureInfo.InvariantCulture, out var n) ? n : null;

    private static DateTime Min(DateTime? a, DateTime b) => a is { } x && x < b ? x : b;

    private static DateTime Max(DateTime? a, DateTime b) => a is { } x && x > b ? x : b;

    private static string Fecha(DateTime utc) => HoraMexico.ALocal(utc).ToString("dd/MM/yyyy HH:mm", CultureInfo.InvariantCulture);

    private static string Dia(DateTime utc) => HoraMexico.ALocal(utc).ToString("dd/MM/yyyy", CultureInfo.InvariantCulture);

    private static string Pesos(long centavos) => (centavos / 100m).ToString("$#,##0.00", Mexico);

    private static readonly CultureInfo Mexico = CultureInfo.GetCultureInfo("es-MX");
}
