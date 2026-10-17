using System.Globalization;
using Microsoft.EntityFrameworkCore;
using PapeleriaPos.Nucleo;
using PapeleriaPos.Nucleo.Catalogo;
using PapeleriaPos.Nucleo.Contratos;
using PapeleriaPos.Nucleo.Control;
using PapeleriaPos.Nucleo.Inventario;
using PapeleriaPos.Nucleo.Ventas;

namespace PapeleriaPos.Datos;

/// <summary>
/// Empezar y terminar la temporada (entrega 13, parte 3; docs/propuesta-interfaz.md, «Lo que se señala», puntos 3 y 4), en
/// Productos (F3) › Temporadas:
/// <list type="bullet">
/// <item><b>Empezar,</b> una vez al año y antes de sacar la mercancía: la lista del año pasado (lo acomodado en la mesa de la
/// temporada, en su orden) con el precio que tuvo entonces, el de hoy y, para quien ve la utilidad, el costo de hoy, su margen
/// y un sugerido. Los precios se confirman una vez, todos juntos, con «Productos, precios y etiquetas» y las reglas de cambiar
/// precios de varios: el mayoreo nunca queda igual o más caro que el menudeo (aquí no se toca: si el precio nuevo lo deja así,
/// no se confirma nada y se dice cuál). También dice qué productos no tienen foto todavía.</item>
/// <item><b>La hoja de la temporada:</b> la foto, el nombre, el precio y el código de cada producto, en el orden de la mesa
/// (la arma y la imprime la caja, con <c>PapeleriaPos.Impresion.HojaDeTemporada</c>).</item>
/// <item><b>Terminar,</b> al final: lo que sobró se cuenta (con «Conteo completo de un departamento») y lo contado reemplaza
/// la existencia, como en el conteo completo; queda guardado el resumen de cada producto: lo que había, lo que se vendió y lo
/// que quedó, para comprar mejor el año siguiente.</item>
/// </list>
/// En el piloto con eleventa (<paramref name="lecturaDeEleventa"/>), los precios de lo de eleventa se cambian allá y llegan
/// con su lectura (aquí solo se confirman como están), y lo que sobró no se cuenta aquí: el inventario se lleva en eleventa.
/// El resumen sí: lo que bajó la existencia con la lectura de eleventa cuenta como vendido en la caja principal.
/// </summary>
/// <param name="reloj">La hora de la tienda para saber qué temporada sigue (la del servicio; las pruebas la fijan). Sin él, la de la base.</param>
public sealed class RegistroTemporadas(PosDbContext db, bool lecturaDeEleventa = false, TimeProvider? reloj = null)
{
    /// <summary>
    /// Como anota sus ajustes la lectura de eleventa en el kardex (SincronizacionEleventa.Documento, en el puente, que Datos no
    /// conoce; una prueba revisa que sean el mismo): en el piloto, lo que baja con ella es lo que vendió la caja principal.
    /// </summary>
    public const string LecturaDeEleventa = "lectura-eleventa";

    private const AccionControlada Permiso = AccionControlada.EditarCatalogo;
    private static readonly Cantidad Una = Cantidad.Unidades(1);
    private static readonly CultureInfo Invariante = CultureInfo.InvariantCulture;

    private TimeProvider Reloj => reloj ?? db.Reloj;
    private DateTime Ahora => Reloj.GetUtcNow().UtcDateTime;
    private DateOnly Hoy => Temporadas.Hoy(Reloj);

    // ── Las temporadas ──────────────────────────────────────────────────────

    /// <summary>
    /// Las temporadas activas con cómo van: primero las que corren, luego por la fecha en que empiezan; las que no tienen fechas
    /// (Primavera, mientras no se definen), al final.
    /// </summary>
    public async Task<TemporadasDto> ListarAsync(Guid sucursalId, CancellationToken ct = default)
    {
        var hoy = Hoy;
        var temporadas = await db.Exhibidores.AsNoTracking().Where(e => e.Activo && e.Tipo == TipoExhibidor.Temporada).ToListAsync(ct);
        var ids = temporadas.Select(t => t.Id).ToList();
        var puestos = await (from p in db.ProductosExhibidos.AsNoTracking()
                             join l in db.LugaresExhibidor.AsNoTracking() on p.LugarId equals l.Id
                             where ids.Contains(l.ExhibidorId)
                             select new { l.ExhibidorId, p.VarianteId }).ToListAsync(ct);
        var variantes = puestos.Select(p => p.VarianteId).Distinct().ToList();
        var conFoto = (await db.FotosProducto.AsNoTracking().Where(f => variantes.Contains(f.VarianteId)).Select(f => f.VarianteId).ToListAsync(ct)).ToHashSet();
        var corridas = await db.TemporadasDelAnio.AsNoTracking().Where(t => t.SucursalId == sucursalId && ids.Contains(t.ExhibidorId)).ToListAsync(ct);
        var usuarios = await NombresAsync(corridas.SelectMany(c => new[] { c.PreciosConfirmadosPorId, c.TerminadaPorId }), ct);

        var lista = new List<(TemporadaResumenDto Dto, int Orden)>();
        foreach (var t in temporadas)
        {
            var suyos = puestos.Where(p => p.ExhibidorId == t.Id).Select(p => p.VarianteId).Distinct().ToList();
            PeriodoDeTemporada? sigue = null, ultima = null;
            if (t.Desde is { } desde && t.Hasta is { } hasta)
                (sigue, ultima) = (PeriodoDeTemporada.QueSigue(desde, hasta, hoy), PeriodoDeTemporada.UltimaQueEmpezo(desde, hasta, hoy));
            var confirmada = sigue is { } s ? corridas.FirstOrDefault(c => c.ExhibidorId == t.Id && c.Inicio == s.Inicio && c.PreciosConfirmadosUtc is not null) : null;
            var terminada = ultima is { } u ? corridas.FirstOrDefault(c => c.ExhibidorId == t.Id && c.Inicio == u.Inicio && c.TerminadaUtc is not null) : null;
            lista.Add((new TemporadaResumenDto(t.Id, t.Nombre, Temporadas.Fechas(t.Desde, t.Hasta), t.CorreEl(hoy), sigue?.Inicio, sigue?.Fin, suyos.Count,
                suyos.Count(v => !conFoto.Contains(v)),
                confirmada is null ? null : QuienYCuando(usuarios, confirmada.PreciosConfirmadosPorId, confirmada.PreciosConfirmadosUtc, "confirmó los precios"),
                terminada is null ? null : QuienYCuando(usuarios, terminada.TerminadaPorId, terminada.TerminadaUtc, "la terminó")), t.Orden));
        }
        return new TemporadasDto(hoy, lista
            .OrderByDescending(x => x.Dto.Corre)
            .ThenBy(x => x.Dto.Inicio is null)
            .ThenBy(x => x.Dto.Inicio)
            .ThenBy(x => x.Orden).ThenBy(x => x.Dto.Nombre, StringComparer.CurrentCultureIgnoreCase)
            .Select(x => x.Dto).ToList());
    }

    // ── Empezar la temporada ────────────────────────────────────────────────

    /// <summary>
    /// La que sigue (la que corre hoy o, si no, la siguiente) con la lista del año pasado: lo acomodado en su mesa, en su orden,
    /// cada producto con el precio que tuvo entonces y el de hoy. El costo, el margen y el sugerido solo van para quien tiene
    /// «Ver costos, utilidad y margen».
    /// </summary>
    public async Task<TemporadaParaEmpezarDto> ParaEmpezarAsync(Guid exhibidorId, Guid sucursalId, Guid usuarioId, CancellationToken ct = default)
    {
        var (temporada, puestos) = await TemporadaAsync(exhibidorId, ct);
        var (desde, hasta) = FechasDe(temporada);
        var periodo = PeriodoDeTemporada.QueSigue(desde, hasta, Hoy);
        var anterior = periodo.AnioAnterior(desde, hasta);
        var veCostos = await new ServicioControl(db).PuedeAsync(usuarioId, AccionControlada.VerUtilidad, ct);

        var ids = puestos.Select(p => p.VarianteId).ToList();
        var variantes = await new RegistroEtiquetas(db).VariantesAsync(ids, tracking: false, ct);
        var precios = (await db.Precios.AsNoTracking().Where(p => ids.Contains(p.VarianteId)).ToListAsync(ct)).ToLookup(p => p.VarianteId);
        var conFoto = (await db.FotosProducto.AsNoTracking().Where(f => ids.Contains(f.VarianteId)).Select(f => f.VarianteId).ToListAsync(ct)).ToHashSet();
        var deEleventa = lecturaDeEleventa ? await TrastiendaEnElPiloto.DeEleventaAsync(db, ids, ct) : [];
        var deEntonces = await PreciosDeEntoncesAsync(ids, anterior, sucursalId, ct);
        var costos = veCostos ? await CostosDeHoyAsync(ids, sucursalId, ct) : [];
        var general = AlertasTelegram.Configuracion(db, sucursalId).MargenMinimoCentesimas;
        var minimos = veCostos ? await db.Departamentos.AsNoTracking().ToDictionaryAsync(d => d.Id, d => d.MargenMinimoCentesimas, ct) : [];

        var productos = new List<ProductoDeTemporadaDto>();
        foreach (var puesto in puestos)
        {
            if (!variantes.TryGetValue(puesto.VarianteId, out var v))
                continue;
            var suyos = PreciosEnLaSucursal.De(precios[v.Id], v, sucursalId);
            var hoy = suyos.Menudeo?.PrecioCentavos;
            (long Precio, string DeDonde)? entonces = deEntonces.TryGetValue(v.Id, out var deEntoncesSuyo) ? deEntoncesSuyo : null;
            var producto = new ProductoDeTemporadaDto(v.Id, v.ProductoId, RegistroCompras.Nombre(v), RegistroEtiquetas.Codigo(v), puesto.Lugar,
                v.Activa && v.Producto!.Activo, conFoto.Contains(v.Id), entonces?.Precio, entonces?.DeDonde, hoy, suyos.Piso(), suyos.Texto(),
                deEleventa.Contains(v.Id));
            if (veCostos && costos.TryGetValue(v.Id, out var costo))
            {
                var minimo = minimos.GetValueOrDefault(v.Producto!.DepartamentoId) ?? general;
                producto = producto with
                {
                    CostoCentavos = (long)Math.Round(costo, MidpointRounding.AwayFromZero),
                    MargenCentesimas = hoy is > 0 ? Margen.Calcular(hoy.Value, costo) : null,
                    MargenMinimoCentesimas = minimo,
                    SugeridoCentavos = PreciosDeTemporada.Sugerido(hoy, entonces?.Precio, costo, minimo),
                };
            }
            productos.Add(producto);
        }

        var corrida = await db.TemporadasDelAnio.AsNoTracking()
            .SingleOrDefaultAsync(c => c.ExhibidorId == exhibidorId && c.SucursalId == sucursalId && c.Inicio == periodo.Inicio, ct);
        var confirmados = corrida?.PreciosConfirmadosUtc is null
            ? null
            : QuienYCuando(await NombresAsync([corrida.PreciosConfirmadosPorId], ct), corrida.PreciosConfirmadosPorId, corrida.PreciosConfirmadosUtc, "confirmó los precios");
        return new TemporadaParaEmpezarDto(temporada.Id, temporada.Nombre, Temporadas.Fechas(desde, hasta), periodo.Inicio, periodo.Fin, anterior.Inicio,
            anterior.Fin, periodo.Contiene(Hoy), confirmados, veCostos, deEleventa.Count > 0 ? TrastiendaEnElPiloto.ProductosEnEleventa : null, productos);
    }

    /// <summary>
    /// Los precios de la temporada, todos juntos y en una sola transacción: cambian los que cambian y los demás quedan
    /// confirmados como están. Nada se confirma si uno no se puede: su precio cambió mientras tanto, quedaría igual o más barato
    /// que su mayoreo, está desactivado o, en el piloto, es de eleventa (su precio se cambia allá). Queda un renglón en la
    /// bitácora con cuántos y cuáles, y el historial de cada precio; las cajas los toman con la versión del catálogo. Si la
    /// respuesta se pierde y se confirma otra vez, lo que ya quedó no se vuelve a cambiar.
    /// </summary>
    public async Task<PreciosConfirmadosDto> ConfirmarPreciosAsync(Guid exhibidorId, SolicitudConfirmarPrecios s, CancellationToken ct = default)
    {
        var control = new ServicioControl(db);
        await control.ExigirAsync(s.UsuarioId, s.SucursalId, Permiso, "Los precios de la temporada los confirma quien tiene «Productos, precios y etiquetas».", ct);
        var (temporada, puestos) = await TemporadaAsync(exhibidorId, ct);
        var (desde, hasta) = FechasDe(temporada);
        var periodo = PeriodoDeTemporada.QueSigue(desde, hasta, Hoy);
        if (s.Precios.Count == 0)
            throw new ArgumentException("No hay precios que confirmar: la temporada todavía no tiene productos en su mesa.");
        if (s.Precios.GroupBy(p => p.VarianteId).Any(g => g.Count() > 1))
            throw new ArgumentException("Un producto viene dos veces.");
        var deLaTemporada = puestos.Select(p => p.VarianteId).ToHashSet();
        if (s.Precios.Any(p => !deLaTemporada.Contains(p.VarianteId)))
            throw new ArgumentException("Uno de esos productos ya no está en la mesa de la temporada: vuelve a abrir la lista.");
        if (s.Precios.FirstOrDefault(p => p.PrecioCentavos <= 0) is not null)
            throw new ArgumentException("Cada precio va de más de cero: escribe el de cada producto.");
        if (s.Precios.Any(p => p.PrecioCentavos > PreciosDeTemporada.MaximoCentavos))
            throw new ArgumentException($"Revisa los precios: van hasta {AlertasTelegram.Dinero(PreciosDeTemporada.MaximoCentavos)}.");

        await using var transaccion = await db.Database.BeginTransactionAsync(ct);
        var ids = s.Precios.Select(p => p.VarianteId).ToList();
        var variantes = await new RegistroEtiquetas(db).VariantesAsync(ids, tracking: true, ct);
        var precios = (await db.Precios.Where(p => ids.Contains(p.VarianteId)).ToListAsync(ct)).ToLookup(p => p.VarianteId);
        var deEleventa = lecturaDeEleventa ? await TrastiendaEnElPiloto.DeEleventaAsync(db, ids, ct) : [];

        var cambios = new List<(Variante Variante, Precio? Menudeo, long Antes, long Nuevo)>();
        var problemas = new List<string>();
        foreach (var p in s.Precios)
        {
            if (!variantes.TryGetValue(p.VarianteId, out var v))
            {
                problemas.Add("uno de los productos ya no existe");
                continue;
            }
            var suyos = PreciosEnLaSucursal.De(precios[v.Id], v, s.SucursalId);
            var actual = suyos.Menudeo?.PrecioCentavos ?? 0;
            // Igual al de hoy: queda confirmado (también si ya se había confirmado y la respuesta se perdió).
            if (actual == p.PrecioCentavos)
                continue;
            var nombre = RegistroCompras.Nombre(v);
            if (actual != p.AntesCentavos)
                problemas.Add($"el precio de «{nombre}» cambió mientras tanto (ahora es {AlertasTelegram.Dinero(actual)}): vuelve a abrir la lista");
            else if (!v.Activa || !v.Producto!.Activo)
                problemas.Add($"«{nombre}» está desactivado");
            else if (deEleventa.Contains(v.Id))
                problemas.Add($"«{nombre}» es de eleventa: su precio se cambia allá y llega con la lectura");
            else if (p.PrecioCentavos < suyos.Piso())
                problemas.Add($"con {AlertasTelegram.Dinero(p.PrecioCentavos)}, «{nombre}» quedaría igual o más barato que su mayoreo ({suyos.Texto()}): cámbialo en su ficha");
            else
                cambios.Add((v, suyos.Menudeo, actual, p.PrecioCentavos));
        }
        if (problemas.Count > 0)
            throw new ArgumentException($"No se confirmó ningún precio: {string.Join("; ", problemas)}.");

        foreach (var (variante, menudeo, _, nuevo) in cambios)
        {
            if (menudeo is null)
                db.Precios.Add(new Precio { VarianteId = variante.Id, PrecioCentavos = nuevo });
            else
                menudeo.PrecioCentavos = nuevo;
        }
        var corrida = await CorridaAsync(exhibidorId, s.SucursalId, periodo, ct);
        (corrida.PreciosConfirmadosUtc, corrida.PreciosConfirmadosPorId) = (Ahora, s.UsuarioId);
        var detalle = cambios.Count == 0
            ? "ninguno cambió"
            : $"cambiaron {cambios.Count}: {string.Join(", ", cambios.Take(10).Select(c => $"«{RegistroCompras.Nombre(c.Variante)}» {AlertasTelegram.Dinero(c.Antes)} → {AlertasTelegram.Dinero(c.Nuevo)}"))}"
              + (cambios.Count > 10 ? $" y {cambios.Count - 10} más" : "");
        control.Registrar(s.SucursalId, null, s.UsuarioId, Permiso,
            $"Confirmó los precios de la temporada «{temporada.Nombre}» {periodo.Inicio.Year}: {s.Precios.Count} {(s.Precios.Count == 1 ? "producto" : "productos")}, {detalle}");
        await db.SaveChangesAsync(ct);
        await transaccion.CommitAsync(ct);

        var cambiados = cambios.Select(c => c.Variante.Id).ToList();
        var conTarjeta = await db.TarjetasDePrecio.AsNoTracking().CountAsync(t => t.SucursalId == s.SucursalId && cambiados.Contains(t.VarianteId), ct);
        return new PreciosConfirmadosDto(cambios.Count, conTarjeta, await ParaEmpezarAsync(exhibidorId, s.SucursalId, s.UsuarioId, ct));
    }

    // ── La hoja de la temporada ─────────────────────────────────────────────

    /// <summary>
    /// La hoja de la temporada lista para imprimir: cada lugar de la mesa con sus productos activos, en su orden, con su foto (la
    /// que se tomó; sin foto, la caja pone sus iniciales), su nombre, su precio de hoy y su código. Reemplaza la lista que hoy se
    /// guarda con ellos. A lo que no tiene código se le da uno interno (con «Productos, precios y etiquetas»), como al imprimir
    /// etiquetas: desde la hoja también se cobra escaneándolo.
    /// </summary>
    public async Task<HojaDeTemporadaDto> HojaAsync(Guid exhibidorId, SolicitudHojaDeTemporada s, CancellationToken ct = default)
    {
        var (temporada, puestos) = await TemporadaAsync(exhibidorId, ct);
        var etiquetas = new RegistroEtiquetas(db);
        var variantes = await etiquetas.VariantesAsync(puestos.Select(p => p.VarianteId), tracking: true, ct);
        var activas = variantes.Values.Where(v => v.Activa && v.Producto!.Activo).ToList();
        if (activas.Count == 0)
            throw new InvalidOperationException($"«{temporada.Nombre}» todavía no tiene productos en su mesa: acomódalos en Ctrl+M › Acomodar.");
        var nuevos = await etiquetas.DarCodigosAsync(activas, s.UsuarioId, s.SucursalId, $"para la hoja de la temporada «{temporada.Nombre}»", ct);
        var ids = activas.Select(v => v.Id).ToList();
        var precios = await etiquetas.MenudeoAsync(ids, s.SucursalId, ct);
        var fotos = await db.FotosProducto.AsNoTracking().Where(f => ids.Contains(f.VarianteId)).ToDictionaryAsync(f => f.VarianteId, f => f.Datos, ct);
        var sucursal = await db.Sucursales.AsNoTracking().Where(x => x.Id == s.SucursalId).Select(x => x.Nombre).SingleOrDefaultAsync(ct) ?? "";

        var lugares = puestos.Where(p => variantes.TryGetValue(p.VarianteId, out var v) && v.Activa && v.Producto!.Activo)
            .GroupBy(p => p.Lugar)
            .Select(g => new LugarEnHojaDto(g.Key, g.Select(p => variantes[p.VarianteId])
                .Select(v => new ProductoEnHojaDto(v.Id, RegistroCompras.Nombre(v), precios.TryGetValue(v.Id, out var precio) ? precio : null,
                    RegistroEtiquetas.Codigo(v)!, fotos.TryGetValue(v.Id, out var foto) ? Convert.ToBase64String(foto) : null))
                .ToList()))
            .ToList();
        return new HojaDeTemporadaDto(temporada.Nombre, Temporadas.Fechas(temporada.Desde, temporada.Hasta), sucursal, lugares, nuevos);
    }

    // ── Terminar la temporada ───────────────────────────────────────────────

    /// <summary>
    /// La última que empezó (corra todavía o ya haya terminado), con el resumen de cada producto: si ya se terminó, el que quedó
    /// guardado; si no, hasta hoy. En el piloto con eleventa no se ofrece contar.
    /// </summary>
    public async Task<TemporadaParaTerminarDto> ParaTerminarAsync(Guid exhibidorId, Guid sucursalId, CancellationToken ct = default)
    {
        var (temporada, puestos) = await TemporadaAsync(exhibidorId, ct);
        var (desde, hasta) = FechasDe(temporada);
        var periodo = PeriodoDeTemporada.UltimaQueEmpezo(desde, hasta, Hoy);
        var corrida = await db.TemporadasDelAnio.AsNoTracking().Include(c => c.Renglones)
            .SingleOrDefaultAsync(c => c.ExhibidorId == exhibidorId && c.SucursalId == sucursalId && c.Inicio == periodo.Inicio, ct);
        var enEleventa = lecturaDeEleventa
            ? $"{TrastiendaEnElPiloto.InventarioEnEleventa} Por eso aquí no se cuenta: lo que quedó es la existencia de eleventa, y lo que bajó con su lectura cuenta como vendido en la caja principal."
            : null;

        var desdeUtc = await DesdeDelResumenAsync(sucursalId, periodo, ct);
        var desdeLocal = desdeUtc is { } d ? DateOnly.FromDateTime(HoraMexico.ALocal(d)) : periodo.Inicio;
        if (corrida is { TerminadaUtc: { } terminadaUtc })
        {
            var variantes = await new RegistroEtiquetas(db).VariantesAsync(corrida.Renglones.Select(r => r.VarianteId), tracking: false, ct);
            var lugares = puestos.ToDictionary(p => p.VarianteId, p => p.Lugar);
            var guardado = corrida.Renglones.OrderBy(r => r.Posicion)
                .Where(r => variantes.ContainsKey(r.VarianteId))
                .Select(r =>
                {
                    var v = variantes[r.VarianteId];
                    return new ResumenDeProductoDto(v.Id, RegistroCompras.Nombre(v), RegistroEtiquetas.Codigo(v), lugares.GetValueOrDefault(v.Id, ""), v.UnidadBase,
                        v.Producto!.LlevaInventario, r.Inicial.EnUnidades, r.Vendido.EnUnidades, r.Quedo.EnUnidades);
                })
                .ToList();
            var quien = await NombresAsync([corrida.TerminadaPorId], ct);
            return new TemporadaParaTerminarDto(temporada.Id, temporada.Nombre, periodo.Inicio, periodo.Fin, desdeLocal,
                DateOnly.FromDateTime(HoraMexico.ALocal(terminadaUtc)), periodo.Contiene(Hoy),
                QuienYCuando(quien, corrida.TerminadaPorId, terminadaUtc, corrida.Contada ? "la terminó y contó lo que sobró" : "la terminó"),
                corrida.Contada, AntesDelPos: false, SeCuenta: false, enEleventa, guardado);
        }

        var resumen = desdeUtc is { } desdeDelResumen ? await ResumenAsync(puestos, sucursalId, desdeDelResumen, Ahora, ct) : [];
        return new TemporadaParaTerminarDto(temporada.Id, temporada.Nombre, periodo.Inicio, periodo.Fin, desdeLocal, Hoy, periodo.Contiene(Hoy), null, false,
            AntesDelPos: desdeUtc is null, SeCuenta: !lecturaDeEleventa && desdeUtc is not null, enEleventa, resumen);
    }

    /// <summary>
    /// Termina la última que empezó. Lo contado de cada producto reemplaza su existencia, con la diferencia en el kardex como la
    /// de un conteo (a su costo promedio), contra lo que dice el sistema ahora; lo que no se contó se queda como está. Contar
    /// pide «Conteo completo de un departamento» y no se hace en el piloto con eleventa. Queda guardado el resumen de cada
    /// producto (lo que había, lo que se vendió y lo que quedó) y un renglón en la bitácora. Se termina una vez por año.
    /// </summary>
    public async Task<TemporadaParaTerminarDto> TerminarAsync(Guid exhibidorId, SolicitudTerminarTemporada s, CancellationToken ct = default)
    {
        var control = new ServicioControl(db);
        await control.ExigirAsync(s.UsuarioId, s.SucursalId, Permiso, "La temporada la termina quien tiene «Productos, precios y etiquetas».", ct);
        var contado = s.Contado ?? [];
        if (contado.Count > 0)
        {
            await control.ExigirAsync(s.UsuarioId, s.SucursalId, AccionControlada.ConteoCompleto,
                "Lo que sobró lo cuenta quien tiene «Conteo completo de un departamento»: termínala sin contar, o que la termine quien lo tiene.", ct);
            TrastiendaEnElPiloto.RevisarInventario(lecturaDeEleventa);
        }
        var (temporada, puestos) = await TemporadaAsync(exhibidorId, ct);
        var (desde, hasta) = FechasDe(temporada);
        var periodo = PeriodoDeTemporada.UltimaQueEmpezo(desde, hasta, Hoy);
        if (contado.GroupBy(c => c.VarianteId).Any(g => g.Count() > 1))
            throw new ArgumentException("Un producto viene contado dos veces.");
        var deLaTemporada = puestos.Select(p => p.VarianteId).ToHashSet();
        if (contado.Any(c => !deLaTemporada.Contains(c.VarianteId)))
            throw new ArgumentException("Uno de los contados ya no está en la mesa de la temporada: vuelve a abrir la lista.");
        if (contado.Any(c => c.CantidadMilesimas < 0))
            throw new ArgumentException("Lo contado no puede ser negativo.");

        await using var transaccion = await db.Database.BeginTransactionAsync(ct);
        var corrida = await CorridaAsync(exhibidorId, s.SucursalId, periodo, ct);
        if (corrida.TerminadaUtc is not null)
            throw new InvalidOperationException($"«{temporada.Nombre}» de {periodo.Inicio.Year} ya se terminó: su resumen quedó guardado.");
        if (await DesdeDelResumenAsync(s.SucursalId, periodo, ct) is not { } desdeUtc)
            throw new InvalidOperationException($"«{temporada.Nombre}» de {periodo.Inicio.Year} fue antes de que la tienda usara este POS: no hay con qué armar su resumen.");

        var ahora = Ahora;
        var neto = 0L;
        var conDiferencia = 0;
        if (contado.Count > 0)
        {
            var variantes = await new RegistroEtiquetas(db).VariantesAsync(contado.Select(c => c.VarianteId), tracking: true, ct);
            var inventario = new RegistroInventario(db);
            foreach (var c in contado)
            {
                var variante = variantes[c.VarianteId];
                var nombre = RegistroCompras.Nombre(variante);
                if (!variante.Producto!.LlevaInventario)
                    throw new ArgumentException($"«{nombre}» no lleva inventario: no se cuenta.");
                var cantidad = new Cantidad(c.CantidadMilesimas);
                if (!variante.AdmiteCantidad(cantidad))
                    throw new ArgumentException($"«{nombre}» se cuenta en piezas enteras.");
                var diferencia = cantidad - (await inventario.ExistenciaAsync(s.SucursalId, variante.Id, ct)).Cantidad;
                if (diferencia.Milesimas != 0)
                    conDiferencia++;
                neto += await inventario.AgregarDiferenciaDeConteoAsync(s.SucursalId, variante, diferencia, ahora, nameof(TemporadaDelAnio), corrida.Id, ct);
            }
            // Lo contado ya queda en el kardex: el resumen lo lee de ahí.
            await db.SaveChangesAsync(ct);
        }

        var resumen = await ResumenAsync(puestos, s.SucursalId, desdeUtc, ahora.AddTicks(1), ct);
        // Con Add y no por la lista de la temporada: con su Id ya puesto, EF los tomaría por renglones que ya existen.
        db.TemporadasDelAnioRenglones.AddRange(resumen.Select((r, i) => new RenglonTemporadaDelAnio
        {
            TemporadaId = corrida.Id,
            VarianteId = r.VarianteId,
            Posicion = i + 1,
            Inicial = Cantidad.Unidades(r.Inicial),
            Vendido = Cantidad.Unidades(r.Vendido),
            Quedo = Cantidad.Unidades(r.Quedo),
        }));
        (corrida.TerminadaUtc, corrida.TerminadaPorId, corrida.Contada) = (ahora, s.UsuarioId, contado.Count > 0);
        var vendidos = resumen.Count(r => r.Vendido > 0);
        control.Registrar(s.SucursalId, null, s.UsuarioId, contado.Count > 0 ? AccionControlada.ConteoCompleto : Permiso,
            $"Terminó la temporada «{temporada.Nombre}» {periodo.Inicio.Year}: {resumen.Count} {(resumen.Count == 1 ? "producto" : "productos")}, "
            + $"{vendidos} {(vendidos == 1 ? "se vendió" : "se vendieron")}"
            + (contado.Count == 0 ? ", sin contar lo que sobró" : $"; contó {contado.Count}, {(conDiferencia == 0 ? "todo cuadra" : $"{conDiferencia} con diferencia")}"),
            contado.Count > 0 ? neto : null);
        await db.SaveChangesAsync(ct);
        await transaccion.CommitAsync(ct);
        return await ParaTerminarAsync(exhibidorId, s.SucursalId, ct);
    }

    /// <summary>
    /// El resumen de cada producto de <paramref name="desdeUtc"/> (el primer día de la temporada, o cuando se empezó a usar este
    /// POS) a <paramref name="hastaUtc"/> (hoy, o el momento de terminarla), en unidad base y del kardex de la sucursal:
    /// <list type="bullet">
    /// <item><b>Inicial:</b> la existencia al empezar más lo que llegó en la temporada (compras e inventario inicial; en el piloto,
    /// también lo que subió con la lectura de eleventa).</item>
    /// <item><b>Vendido:</b> lo vendido menos lo devuelto (y lo vendido en eleventa antes de usar este POS, si cae en el periodo;
    /// en el piloto, también lo que bajó con la lectura de eleventa: lo vendido en la caja principal).</item>
    /// <item><b>Quedó:</b> la existencia de ahora (con lo contado, si se contó).</item>
    /// </list>
    /// Lo demás (mermas, ajustes, diferencias de un conteo) explica lo que no cuadre entre los tres.
    /// </summary>
    private async Task<List<ResumenDeProductoDto>> ResumenAsync(List<Puesto> puestos, Guid sucursalId, DateTime desdeUtc, DateTime hastaUtc,
        CancellationToken ct)
    {
        var ids = puestos.Select(p => p.VarianteId).ToList();
        var variantes = await new RegistroEtiquetas(db).VariantesAsync(ids, tracking: false, ct);
        var movimientos = (await db.MovimientosInventario.AsNoTracking()
                .Where(m => m.SucursalId == sucursalId && ids.Contains(m.VarianteId) && m.FechaUtc < hastaUtc)
                .Select(m => new { m.VarianteId, m.FechaUtc, m.Tipo, m.Cantidad, m.DocumentoTipo })
                .ToListAsync(ct))
            .ToLookup(m => m.VarianteId);
        var historicas = (await (from l in db.VentasHistoricasLineas.AsNoTracking()
                                 join v in db.VentasHistoricas.AsNoTracking() on l.VentaHistoricaId equals v.Id
                                 where v.SucursalId == sucursalId && !v.Cancelada && v.FechaUtc >= desdeUtc && v.FechaUtc < hastaUtc && ids.Contains(l.VarianteId)
                                 select new { l.VarianteId, l.Cantidad }).ToListAsync(ct))
            .GroupBy(x => x.VarianteId)
            .ToDictionary(g => g.Key, g => g.Aggregate(Cantidad.Cero, (suma, x) => suma + x.Cantidad));

        var resumen = new List<ResumenDeProductoDto>();
        foreach (var puesto in puestos)
        {
            if (!variantes.TryGetValue(puesto.VarianteId, out var v))
                continue;
            var antes = Cantidad.Cero;
            var llego = Cantidad.Cero;
            var vendido = historicas.GetValueOrDefault(v.Id);
            var quedo = Cantidad.Cero;
            foreach (var m in movimientos[v.Id])
            {
                quedo += m.Cantidad;
                if (m.FechaUtc < desdeUtc)
                {
                    antes += m.Cantidad;
                    continue;
                }
                var deLaLectura = lecturaDeEleventa && m.DocumentoTipo == LecturaDeEleventa;
                if (m.Tipo is TipoMovimiento.Venta or TipoMovimiento.Devolucion || (deLaLectura && m.Cantidad.Milesimas < 0))
                    vendido -= m.Cantidad;
                else if (m.Tipo is TipoMovimiento.Compra or TipoMovimiento.InventarioInicial || (deLaLectura && m.Cantidad.EsPositiva))
                    llego += m.Cantidad;
            }
            resumen.Add(new ResumenDeProductoDto(v.Id, RegistroCompras.Nombre(v), RegistroEtiquetas.Codigo(v), puesto.Lugar, v.UnidadBase, v.Producto!.LlevaInventario,
                (antes + llego).EnUnidades, vendido.EnUnidades, quedo.EnUnidades));
        }
        return resumen;
    }

    /// <summary>
    /// Desde cuándo va el resumen: el primer día de la temporada; si la tienda empezó a usar este POS después (el kardex de la
    /// sucursal empieza con la importación de eleventa), desde entonces, con lo que había ese día: lo de antes está en eleventa.
    /// Vacío si la temporada ya había terminado cuando se empezó a usar: no hay con qué armar su resumen.
    /// </summary>
    private async Task<DateTime?> DesdeDelResumenAsync(Guid sucursalId, PeriodoDeTemporada periodo, CancellationToken ct)
    {
        var (desdeUtc, hastaUtc) = periodo.EnUtc();
        var inicioDelPos = await db.MovimientosInventario.AsNoTracking().Where(m => m.SucursalId == sucursalId)
            .OrderBy(m => m.FechaUtc).Select(m => (DateTime?)m.FechaUtc).FirstOrDefaultAsync(ct);
        if (inicioDelPos is not { } inicio || inicio >= hastaUtc)
            return null;
        return inicio > desdeUtc ? inicio : desdeUtc;
    }

    // ── Lo de cada temporada ────────────────────────────────────────────────

    /// <summary>Un producto de la mesa de la temporada: el lugar donde está y en qué orden sale.</summary>
    internal sealed record Puesto(Guid VarianteId, string Lugar, int Posicion);

    /// <summary>
    /// La temporada (activa) y sus productos en el orden de la mesa: sus lugares de arriba abajo y en cada uno de izquierda a
    /// derecha. Un producto que está en dos lugares sale una vez, en el primero.
    /// </summary>
    internal async Task<(Exhibidor Temporada, List<Puesto> Puestos)> TemporadaAsync(Guid exhibidorId, CancellationToken ct, bool soloTemporada = true)
    {
        var temporada = await db.Exhibidores.AsNoTracking().SingleOrDefaultAsync(e => e.Id == exhibidorId && e.Activo, ct)
            ?? throw new ArgumentException("Esa temporada ya no está: vuelve a abrir la lista.");
        if (soloTemporada && temporada.Tipo != TipoExhibidor.Temporada)
            throw new ArgumentException($"«{temporada.Nombre}» es un exhibidor, no una temporada.");
        var lugares = await db.LugaresExhibidor.AsNoTracking().Where(l => l.ExhibidorId == exhibidorId).ToListAsync(ct);
        var ids = lugares.Select(l => l.Id).ToList();
        var productos = await db.ProductosExhibidos.AsNoTracking().Where(p => ids.Contains(p.LugarId)).ToListAsync(ct);
        var puestos = new List<Puesto>();
        var vistos = new HashSet<Guid>();
        foreach (var lugar in lugares.OrderBy(l => l.Orden).ThenBy(l => l.Id))
        {
            foreach (var p in productos.Where(p => p.LugarId == lugar.Id).OrderBy(p => p.Posicion).ThenBy(p => p.Id))
            {
                if (vistos.Add(p.VarianteId))
                    puestos.Add(new Puesto(p.VarianteId, lugar.Nombre, puestos.Count + 1));
            }
        }
        return (temporada, puestos);
    }

    private static (DiaDelAnio Desde, DiaDelAnio Hasta) FechasDe(Exhibidor temporada) =>
        temporada.Desde is { } desde && temporada.Hasta is { } hasta
            ? (desde, hasta)
            : throw new ArgumentException($"«{temporada.Nombre}» todavía no tiene fechas: pónselas en Configuración › Mostrador y temporadas.");

    /// <summary>La de ese año (rastreada), nueva si no existía.</summary>
    private async Task<TemporadaDelAnio> CorridaAsync(Guid exhibidorId, Guid sucursalId, PeriodoDeTemporada periodo, CancellationToken ct)
    {
        var corrida = await db.TemporadasDelAnio.Include(c => c.Renglones)
            .SingleOrDefaultAsync(c => c.ExhibidorId == exhibidorId && c.SucursalId == sucursalId && c.Inicio == periodo.Inicio, ct);
        if (corrida is null)
        {
            corrida = new TemporadaDelAnio { ExhibidorId = exhibidorId, SucursalId = sucursalId, Inicio = periodo.Inicio, Fin = periodo.Fin };
            db.TemporadasDelAnio.Add(corrida);
        }
        return corrida;
    }

    /// <summary>
    /// El precio que tuvo cada producto en la temporada del año pasado:
    /// <list type="number">
    /// <item>El de su historial: el menudeo que tenía al terminar el primer día de esa temporada (con lo que se confirmó al
    /// empezarla; si después se rebajó para sacar lo que sobraba, no cuenta).</item>
    /// <item>Si su historial no llega tan atrás (el primer año, con lo que vino de eleventa): el precio al que más veces se
    /// cobró en esa temporada, en este POS o en eleventa.</item>
    /// </list>
    /// Sin ninguno de los dos, no se sabe (no estaba, o no se vendió).
    /// </summary>
    private async Task<Dictionary<Guid, (long Precio, string DeDonde)>> PreciosDeEntoncesAsync(
        List<Guid> ids, PeriodoDeTemporada anterior, Guid sucursalId, CancellationToken ct)
    {
        var resultado = new Dictionary<Guid, (long, string)>();
        var alTerminarElPrimerDia = HoraMexico.AUtc(anterior.Inicio.AddDays(1).ToDateTime(TimeOnly.MinValue));
        var historial = (await db.PreciosHistorial.AsNoTracking()
                .Where(h => ids.Contains(h.VarianteId) && h.PresentacionId == null && h.SucursalId == null)
                .ToListAsync(ct))
            .Where(h => h.CantidadMinima == Una)
            .ToLookup(h => h.VarianteId);
        foreach (var id in ids)
        {
            var cambios = historial[id].OrderBy(h => h.FechaUtc).ToList();
            var ultimoAntes = cambios.LastOrDefault(h => h.FechaUtc < alTerminarElPrimerDia);
            var precio = ultimoAntes is not null
                ? ultimoAntes.PrecioNuevoCentavos
                : cambios.FirstOrDefault()?.PrecioAnteriorCentavos;
            if (precio is > 0)
                resultado[id] = (precio.Value, "historial");
        }

        var faltan = ids.Where(id => !resultado.ContainsKey(id)).ToList();
        if (faltan.Count == 0)
            return resultado;
        var (desdeUtc, hastaUtc) = anterior.EnUtc();
        var delPos = await (from l in db.VentasLineas.AsNoTracking()
                            join v in db.Ventas.AsNoTracking() on l.VentaId equals v.Id
                            where v.SucursalId == sucursalId && v.Estado == EstadoVenta.Pagada && v.PagadaUtc >= desdeUtc && v.PagadaUtc < hastaUtc
                                  && l.VarianteId != null && faltan.Contains(l.VarianteId.Value) && !l.EsImpresiones && l.PrecioOriginalCentavos == null
                            select new { Variante = l.VarianteId!.Value, Fecha = v.PagadaUtc!.Value, l.PrecioCentavos, l.Cantidad, l.CantidadBase })
            .ToListAsync(ct);
        var deEleventa = await (from l in db.VentasHistoricasLineas.AsNoTracking()
                                join v in db.VentasHistoricas.AsNoTracking() on l.VentaHistoricaId equals v.Id
                                where v.SucursalId == sucursalId && !v.Cancelada && v.FechaUtc >= desdeUtc && v.FechaUtc < hastaUtc && faltan.Contains(l.VarianteId)
                                select new { Variante = l.VarianteId, Fecha = v.FechaUtc, l.PrecioCentavos })
            .ToListAsync(ct);
        // Por pieza: lo vendido en su presentación base (el precio de un paquete es de otra cosa).
        var cobrados = delPos.Where(l => l.Cantidad == l.CantidadBase).Select(l => (l.Variante, l.Fecha, l.PrecioCentavos))
            .Concat(deEleventa.Select(l => (l.Variante, l.Fecha, l.PrecioCentavos)))
            .Where(l => l.PrecioCentavos > 0);
        foreach (var grupo in cobrados.GroupBy(l => l.Variante))
        {
            // El que más veces se cobró (cada ticket cuenta una vez, aunque lleve muchas piezas); si empatan, el último.
            var masVeces = grupo.GroupBy(l => l.PrecioCentavos)
                .OrderByDescending(g => g.Count()).ThenByDescending(g => g.Max(l => l.Fecha))
                .First().Key;
            resultado[grupo.Key] = (masVeces, "ventas");
        }
        return resultado;
    }

    /// <summary>El costo de hoy por unidad base: el de su última compra; sin compras, el promedio de su existencia. Sin ninguno, no está.</summary>
    private async Task<Dictionary<Guid, decimal>> CostosDeHoyAsync(List<Guid> ids, Guid sucursalId, CancellationToken ct)
    {
        var ultimos = (await db.ProveedoresVariantes.AsNoTracking()
                .Where(p => ids.Contains(p.VarianteId) && p.UltimoCostoCentavos != null)
                .Select(p => new { p.VarianteId, p.UltimoCostoCentavos, p.UltimaCompraUtc }).ToListAsync(ct))
            .GroupBy(p => p.VarianteId)
            .ToDictionary(g => g.Key, g => (decimal)g.OrderByDescending(p => p.UltimaCompraUtc ?? DateTime.MinValue).First().UltimoCostoCentavos!.Value);
        var promedios = await db.Existencias.AsNoTracking().Where(e => e.SucursalId == sucursalId && ids.Contains(e.VarianteId)).ToListAsync(ct);
        foreach (var e in promedios)
        {
            if (!ultimos.ContainsKey(e.VarianteId) && e.CostoPromedio is { } promedio && promedio > 0)
                ultimos[e.VarianteId] = promedio;
        }
        return ultimos.Where(c => c.Value > 0).ToDictionary(c => c.Key, c => c.Value);
    }

    private async Task<Dictionary<Guid, string>> NombresAsync(IEnumerable<Guid?> usuarios, CancellationToken ct)
    {
        var ids = usuarios.OfType<Guid>().Distinct().ToList();
        return ids.Count == 0 ? [] : await db.Usuarios.AsNoTracking().Where(u => ids.Contains(u.Id)).ToDictionaryAsync(u => u.Id, u => u.Nombre, ct);
    }

    /// <summary>«Luis confirmó los precios el 10/10/2026».</summary>
    private static string QuienYCuando(IReadOnlyDictionary<Guid, string> nombres, Guid? usuarioId, DateTime? cuandoUtc, string que) =>
        $"{(usuarioId is { } id && nombres.TryGetValue(id, out var nombre) ? nombre : "Alguien")} {que} el "
        + HoraMexico.ALocal(cuandoUtc ?? DateTime.UtcNow).ToString("d/M/yyyy", Invariante);
}

/// <summary>
/// Los precios de una variante en una sucursal, como los cobra la venta (<see cref="CalculoPrecio"/>): los de la sucursal si
/// tiene, si no los generales; por unidad base el menudeo (el de la cantidad más chica) y sus escalas, y aparte el de cada
/// paquete con precio propio.
/// </summary>
internal sealed record PreciosEnLaSucursal(Precio? Menudeo, IReadOnlyList<Precio> Escalas, IReadOnlyList<(Precio Precio, string Paquete, long FactorMilesimas)> Paquetes)
{
    private static readonly CultureInfo Mexico = CultureInfo.GetCultureInfo("es-MX");

    public static PreciosEnLaSucursal De(IEnumerable<Precio> precios, Variante variante, Guid sucursalId)
    {
        var lista = precios.ToList();
        List<Precio> DeSucursalOBase(IEnumerable<Precio> grupo)
        {
            var suyos = grupo.ToList();
            var deSucursal = suyos.Where(p => p.SucursalId == sucursalId).ToList();
            return deSucursal.Count > 0 ? deSucursal : suyos.Where(p => p.SucursalId is null).ToList();
        }
        var porUnidad = DeSucursalOBase(lista.Where(p => p.PresentacionId is null)).OrderBy(p => p.CantidadMinima).ToList();
        var paquetes = new List<(Precio, string, long)>();
        // La pieza con precio propio sería otra cosa (rarísimo); los paquetes, sí.
        foreach (var presentacion in variante.Presentaciones.Where(p => !p.EsBase))
        {
            if (DeSucursalOBase(lista.Where(p => p.PresentacionId == presentacion.Id)).MinBy(p => p.CantidadMinima) is { } propio)
                paquetes.Add((propio, presentacion.Nombre, presentacion.Factor.Milesimas));
        }
        return new PreciosEnLaSucursal(porUnidad.FirstOrDefault(), porUnidad.Skip(1).ToList(), paquetes);
    }

    /// <summary>Lo más bajo que puede quedar el menudeo con su mayoreo (<see cref="PreciosDeTemporada.Piso"/>).</summary>
    public long Piso() =>
        PreciosDeTemporada.Piso(Menudeo?.PrecioCentavos ?? 0, Escalas.Select(e => e.PrecioCentavos), Paquetes.Select(p => (p.FactorMilesimas, p.Precio.PrecioCentavos)));

    /// <summary>«desde 12: $5.50 · Caja 12: $50.00», como en Productos; vacío sin mayoreo.</summary>
    public string? Texto()
    {
        var partes = Escalas.Select(e => $"desde {e.CantidadMinima.EnUnidades.ToString("#,##0.###", Mexico)}: {AlertasTelegram.Dinero(e.PrecioCentavos)}")
            .Concat(Paquetes.Select(p => $"{p.Paquete}: {AlertasTelegram.Dinero(p.Precio.PrecioCentavos)}"))
            .ToList();
        return partes.Count == 0 ? null : string.Join(" · ", partes);
    }
}
