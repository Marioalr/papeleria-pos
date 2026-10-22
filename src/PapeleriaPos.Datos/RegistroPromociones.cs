using System.Globalization;
using Microsoft.EntityFrameworkCore;
using PapeleriaPos.Nucleo;
using PapeleriaPos.Nucleo.Catalogo;
using PapeleriaPos.Nucleo.Contratos;
using PapeleriaPos.Nucleo.Control;

namespace PapeleriaPos.Datos;

/// <summary>
/// Productos (F3) › Promociones (fase 3, docs/fase-3-precios-clientes.md, «Promociones y kits»): las de la tienda con sus
/// fechas, sus días y su sucursal; lleva N y paga M, el segundo a mitad de precio, un porcentaje (en productos, un departamento
/// o una marca) y el precio especial de unos días, que es lo que en el regreso a clases se hacía con F11: dejar al costo los
/// productos estratégicos. Se aplican solas en las cajas (con la copia, <see cref="CalculoPrecio"/>); aquí se dan de alta, se
/// cambian, se terminan y se ve qué corre hoy.
/// <para>
/// Lo hace quien entró a la caja con «Productos, precios y etiquetas», sin volver a escribir su PIN; cada cambio queda en la
/// bitácora. Nunca debajo del costo sin autorización, como Ctrl+D y Ctrl+E: si con la promoción algo queda debajo de lo que
/// cuesta (su precio más bajo, con el grupo completo de un 3×2, contra el costo con que saldría del inventario), lo hace quien
/// tiene «Vender debajo del costo» o con su autorización, y la bitácora lo dice. El costo y el margen solo los ve quien tiene
/// «Ver costos, utilidad y margen»; «Dejar al costo» también lo pide, porque pone los precios con el costo.
/// </para>
/// <para>
/// En el piloto con eleventa (<paramref name="lecturaDeEleventa"/>: su lectura prendida) las promociones no se aplican en la
/// caja (su catálogo en memoria no las lleva) ni se dan de alta aquí: los precios se ponen en eleventa (decisión del dueño,
/// 2026-10-10) y lo de temporada se sigue haciendo allá. Solo se consultan y se pueden terminar. Solo para convivir con
/// eleventa: se quita al dejarlo.
/// </para>
/// </summary>
/// <param name="reloj">El de los trabajos de fondo del servicio (las pruebas lo cambian); sin él, el de la base.</param>
public sealed class RegistroPromociones(PosDbContext db, bool lecturaDeEleventa = false, TimeProvider? reloj = null)
{
    /// <summary>Por qué en el piloto no se aplican ni se dan de alta: lo dice la página y, si se intenta, el servicio.</summary>
    public const string EnElPiloto =
        "Mientras dure el piloto con eleventa, las promociones no se aplican en esta caja ni se dan de alta aquí: los precios se ponen en eleventa, "
        + "y las de temporada (el F11 al costo) se siguen haciendo allá. Se prenden al dejar eleventa.";

    private static readonly CultureInfo Mexico = CultureInfo.GetCultureInfo("es-MX");

    private DateTime Ahora => (reloj ?? db.Reloj).GetUtcNow().UtcDateTime;

    /// <summary>El día de hoy en la tienda (hora de México).</summary>
    public DateOnly Hoy => DateOnly.FromDateTime(HoraMexico.ALocal(Ahora));

    private ServicioControl Control => new(db);

    /// <summary>
    /// Un producto (variante) que toca una promoción: lo que se vende, con su menudeo por unidad base y lo que necesita el cálculo
    /// de su precio más bajo con ella (sus precios en la sucursal, también los propios de sus presentaciones).
    /// </summary>
    private sealed record Tocado(
        Guid VarianteId, Guid ProductoId, string Nombre, UnidadBase Unidad, long MenudeoCentavos, IReadOnlyList<Precio> Precios,
        IReadOnlyList<Presentacion> Presentaciones);

    // ── Consultar ───────────────────────────────────────────────────────────

    /// <summary>
    /// Las de esa sucursal (y las de todas): primero las que corren hoy, luego las que hoy no tocan, las que vienen y al final
    /// las que ya terminaron, de la más reciente a la más vieja.
    /// </summary>
    public async Task<ListaPromocionesDto> ListarAsync(Guid sucursalId, Guid usuarioId, CancellationToken ct = default)
    {
        await Control.ExigirParaVerAsync(usuarioId, sucursalId, AccionControlada.EditarCatalogo, ct);
        var hoy = Hoy;
        var promociones = await db.Promociones.AsNoTracking().Include(p => p.Alcance)
            .Where(p => p.SucursalId == null || p.SucursalId == sucursalId).ToListAsync(ct);
        var nombres = await NombresAsync(promociones.SelectMany(p => p.Alcance), ct);
        var sucursales = await db.Sucursales.AsNoTracking().ToDictionaryAsync(s => s.Id, s => s.Nombre, ct);
        var lista = promociones
            .Select(p => new PromocionResumenDto(p.Id, p.Nombre, p.Tipo, p.TipoTexto(), AQue(p.Alcance, nombres), Promocion.FechasTexto(p.Desde, p.Hasta, p.Dias),
                p.EstadoEl(hoy), p.Desde, p.Hasta, p.SucursalId is { } s ? sucursales.GetValueOrDefault(s) : null))
            .OrderBy(r => r.Estado switch
            {
                EstadoPromocion.Hoy => 0,
                EstadoPromocion.HoyNoToca => 1,
                EstadoPromocion.PorVenir => 2,
                _ => 3,
            })
            .ThenBy(r => r.Estado is EstadoPromocion.Termino or EstadoPromocion.Terminada ? DateOnly.MaxValue.DayNumber - (r.Hasta ?? r.Desde).DayNumber : r.Desde.DayNumber)
            .ThenBy(r => r.Nombre, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
        return new ListaPromocionesDto(hoy, lista, lecturaDeEleventa ? EnElPiloto : null, await Control.PuedeAsync(usuarioId, AccionControlada.VerUtilidad, ct));
    }

    /// <summary>Una promoción completa: sus productos (con su precio especial), sus departamentos y sus marcas, quién la hizo y cuántos toca hoy.</summary>
    public async Task<PromocionFichaDto> FichaAsync(Guid id, Guid sucursalId, Guid usuarioId, CancellationToken ct = default)
    {
        await Control.ExigirParaVerAsync(usuarioId, sucursalId, AccionControlada.EditarCatalogo, ct);
        var promocion = await db.Promociones.AsNoTracking().Include(p => p.Alcance).SingleOrDefaultAsync(p => p.Id == id, ct)
            ?? throw new InvalidOperationException("Esa promoción ya no existe.");
        return await FichaAsync(promocion, sucursalId, usuarioId, ct);
    }

    private async Task<PromocionFichaDto> FichaAsync(Promocion p, Guid sucursalId, Guid usuarioId, CancellationToken ct)
    {
        var veCostos = await Control.PuedeAsync(usuarioId, AccionControlada.VerUtilidad, ct);
        var tocados = await TocaAsync(p.Alcance, sucursalId, ct);
        var deLaLista = p.Alcance.Where(a => a.VarianteId is not null).ToDictionary(a => a.VarianteId!.Value, a => a.PrecioCentavos);
        var costos = await CostosPorUnidadAsync(db, sucursalId, tocados.Select(t => t.VarianteId), ct);
        var productos = tocados.Where(t => deLaLista.ContainsKey(t.VarianteId))
            .Select(t => Renglon(p, t, deLaLista[t.VarianteId], costos, veCostos, sucursalId)).ToList();
        // Lo que se queda fuera por el costo (sin autorización): las cajas no se la aplican (CatalogoEnMemoria, con este cálculo).
        var fuera = p.BajoCostoAutorizado ? []
            : tocados.Where(t => DebajoDelCosto(MasBajo(p, t, deLaLista.GetValueOrDefault(t.VarianteId), sucursalId), costos.GetValueOrDefault(t.VarianteId)))
                .Select(t => t.Nombre).ToList();
        var nombres = await NombresAsync(p.Alcance, ct);
        var usuarios = await db.Usuarios.AsNoTracking().Where(u => u.Id == p.CreadaPorId || u.Id == p.TerminadaPorId)
            .ToDictionaryAsync(u => u.Id, u => u.Nombre, ct);
        string Quien(Guid? id, DateTime cuando) =>
            $"{(id is { } u ? usuarios.GetValueOrDefault(u, "—") : "—")}, el {HoraMexico.ALocal(cuando).ToString("d/MM/yyyy 'a las' HH:mm", Mexico)}";
        return new PromocionFichaDto(p.Id, p.Nombre, p.Tipo, p.Lleva, p.Paga, p.Centesimas, p.Desde, p.Hasta, p.Dias, p.SucursalId, p.EstadoEl(Hoy),
            p.TipoTexto(), Promocion.FechasTexto(p.Desde, p.Hasta, p.Dias), productos,
            p.Alcance.Where(a => a.DepartamentoId is not null).Select(a => new OpcionCatalogoDto(a.DepartamentoId!.Value, nombres.GetValueOrDefault(a.DepartamentoId.Value, "—")))
                .OrderBy(d => d.Nombre, StringComparer.CurrentCultureIgnoreCase).ToList(),
            p.Alcance.Where(a => a.MarcaId is not null).Select(a => nombres.GetValueOrDefault(a.MarcaId!.Value, "—")).Order(StringComparer.CurrentCultureIgnoreCase).ToList(),
            tocados.Count - fuera.Count, Quien(p.CreadaPorId, p.CreadaUtc), p.TerminadaUtc is { } terminada ? Quien(p.TerminadaPorId, terminada) : null, veCostos,
            fuera);
    }

    /// <summary>
    /// Cómo quedaría antes de guardarla: los productos que toca (con los de sus departamentos y marcas), su precio con la
    /// promoción y cuáles quedan debajo del costo. Cuánto falta, el costo y el margen, solo para quien ve la utilidad: con un
    /// solo producto, lo que falta dice su costo.
    /// </summary>
    public async Task<VistaPromocionDto> VistaAsync(SolicitudPromocion s, CancellationToken ct = default)
    {
        await Control.ExigirParaVerAsync(s.UsuarioId, s.SucursalId, AccionControlada.EditarCatalogo, ct);
        var promocion = await ArmarAsync(s, ct);
        var veCostos = await Control.PuedeAsync(s.UsuarioId, AccionControlada.VerUtilidad, ct);
        var tocados = await TocaAsync(promocion.Alcance, s.SucursalId, ct);
        var costos = await CostosPorUnidadAsync(db, s.SucursalId, tocados.Select(t => t.VarianteId), ct);
        var especiales = PreciosEspeciales(promocion);
        var renglones = tocados.Select(t => Renglon(promocion, t, especiales.GetValueOrDefault(t.VarianteId), costos, veCostos, s.SucursalId)).ToList();
        var bajoCosto = BajoCosto(promocion, tocados, costos, s.SucursalId);
        return new VistaPromocionDto(renglones.Take(VistaPromocionDto.MaximoEnLaVista).ToList(), tocados.Count,
            veCostos ? bajoCosto : bajoCosto with { FaltanteCentavos = 0 }, veCostos);
    }

    /// <summary>
    /// Los productos que se agregan a una promoción (los marcados en la lista), con cada variante activa y su menudeo. Con
    /// <see cref="SolicitudProductosPromocion.AlCosto"/> («Dejar al costo», que pide ver la utilidad), cada uno con el precio de
    /// su costo, redondeado hacia arriba al centavo para no quedar debajo; los que no tienen costo todavía no se agregan.
    /// </summary>
    public async Task<ProductosPromocionDto> ProductosAsync(SolicitudProductosPromocion s, CancellationToken ct = default)
    {
        await Control.ExigirParaVerAsync(s.UsuarioId, s.SucursalId, AccionControlada.EditarCatalogo, ct);
        if (s.AlCosto)
            await Control.ExigirParaVerAsync(s.UsuarioId, s.SucursalId, AccionControlada.VerUtilidad, ct);
        if (s.Productos.Count > Promocion.MaximoAlcance)
            throw new ArgumentException($"Una promoción toca hasta {Promocion.MaximoAlcance:N0} productos: pártela en dos.");
        var ids = s.Productos.Distinct().ToList();
        var variantes = await db.Variantes.AsNoTracking().Where(v => ids.Contains(v.ProductoId) && v.Activa && v.Producto!.Activo)
            .Select(v => v.Id).ToListAsync(ct);
        var tocados = await TocaAsync(variantes.Select(v => new AlcancePromocion { VarianteId = v }).ToList(), s.SucursalId, ct);
        var veCostos = await Control.PuedeAsync(s.UsuarioId, AccionControlada.VerUtilidad, ct);
        var costos = await CostosPorUnidadAsync(db, s.SucursalId, tocados.Select(t => t.VarianteId), ct);
        var sinCosto = new List<string>();
        var renglones = new List<ProductoEnPromocionDto>();
        var precioEspecial = new Promocion { Nombre = "", Tipo = TipoPromocion.PrecioEspecial };
        foreach (var t in tocados)
        {
            long? precio = null;
            if (s.AlCosto)
            {
                var costo = costos.GetValueOrDefault(t.VarianteId);
                if (costo <= 0)
                {
                    sinCosto.Add(t.Nombre);
                    continue;
                }
                precio = (long)Math.Ceiling(costo);
            }
            renglones.Add(Renglon(precioEspecial, t, precio, costos, veCostos, s.SucursalId));
        }
        return new ProductosPromocionDto(renglones, sinCosto);
    }

    // ── Cambiar ─────────────────────────────────────────────────────────────

    /// <summary>
    /// Da de alta una promoción, o cambia la que ya está con ese Id (y la vuelve a activar si se había terminado). Si deja algo
    /// debajo del costo, pide «Vender debajo del costo» (o la autorización de quien lo tiene). Todo en una transacción, con su
    /// renglón en la bitácora; la versión del catálogo la sube la base al guardar, así que las cajas se enteran.
    /// </summary>
    public async Task<PromocionFichaDto> GuardarAsync(SolicitudPromocion s, CancellationToken ct = default)
    {
        var control = Control;
        await control.ExigirDeLaSesionAsync(s.UsuarioId, s.SucursalId, s.CajaId, AccionControlada.EditarCatalogo, ct);
        if (lecturaDeEleventa)
            throw new InvalidOperationException(EnElPiloto);
        var borrador = await ArmarAsync(s, ct);
        if (borrador.Hasta is { } hasta && hasta < Hoy)
            throw new ArgumentException("Su último día ya pasó: pon uno de hoy en adelante.");
        await using var transaccion = await db.Database.BeginTransactionAsync(ct);
        var existente = await db.Promociones.Include(p => p.Alcance).SingleOrDefaultAsync(p => p.Id == borrador.Id, ct);
        // Lo que queda debajo del costo con ella en esta sucursal, con el grupo de precios que más la baja (también una
        // presentación con precio propio). Si algo queda, guardarla así es autorizarlo: sus productos la llevan aunque queden
        // debajo; si no, el que después quede debajo se queda fuera (CatalogoEnMemoria).
        var tocados = await TocaAsync(borrador.Alcance, s.SucursalId, ct);
        var bajoCosto = BajoCosto(borrador, tocados, await CostosPorUnidadAsync(db, s.SucursalId, tocados.Select(t => t.VarianteId), ct), s.SucursalId);
        borrador.BajoCostoAutorizado = bajoCosto.Lineas.Count > 0;
        // Ya está así (se perdió la respuesta y se mandó otra vez, o se guardó sin cambiar nada): no se hace otra vez, ni se gasta
        // otra autorización. Si ahora algo quedó fuera por el costo y no estaba autorizado, guardarla otra vez es autorizarlo.
        if (existente is { Activa: true } && Igual(existente, borrador) && (existente.BajoCostoAutorizado || !borrador.BajoCostoAutorizado))
            return await FichaAsync(existente, s.SucursalId, s.UsuarioId, ct);

        // Nunca debajo del costo sin autorización (como Ctrl+D y Ctrl+E): la bitácora lo dice, con quién lo autorizó y cuánto falta.
        if (borrador.BajoCostoAutorizado)
            await control.RequerirAsync(s.UsuarioId, s.SucursalId, s.CajaId, AccionControlada.VenderBajoCosto, bajoCosto.FaltanteCentavos,
                $"La promoción «{borrador.Nombre}» deja debajo del costo: {Lista(bajoCosto.Lineas.Take(10).ToList(), bajoCosto.Lineas.Count)}",
                s.AutorizacionBajoCostoId, ct: ct);

        Promocion promocion;
        string que;
        if (existente is null)
        {
            promocion = borrador;
            promocion.CreadaPorId = s.UsuarioId;
            promocion.CreadaUtc = Ahora;
            db.Promociones.Add(promocion);
            que = "nueva";
        }
        else
        {
            promocion = existente;
            que = existente.Activa ? "cambiada" : "activa otra vez";
            (promocion.Nombre, promocion.Tipo, promocion.Lleva, promocion.Paga, promocion.Centesimas) =
                (borrador.Nombre, borrador.Tipo, borrador.Lleva, borrador.Paga, borrador.Centesimas);
            (promocion.Desde, promocion.Hasta, promocion.Dias, promocion.SucursalId) = (borrador.Desde, borrador.Hasta, borrador.Dias, borrador.SucursalId);
            (promocion.Activa, promocion.TerminadaUtc, promocion.TerminadaPorId) = (true, null, null);
            promocion.BajoCostoAutorizado = borrador.BajoCostoAutorizado;
            CambiarAlcance(db, promocion, borrador.Alcance);
        }
        control.Registrar(s.SucursalId, s.CajaId, s.UsuarioId, AccionControlada.EditarCatalogo,
            $"Promoción «{promocion.Nombre}» ({promocion.TipoTexto()}; {AQue(promocion.Alcance, await NombresAsync(promocion.Alcance, ct))}; "
            + $"{Promocion.FechasTexto(promocion.Desde, promocion.Hasta, promocion.Dias)}): {que}");
        await db.SaveChangesAsync(ct);
        await transaccion.CommitAsync(ct);
        return await FichaAsync(promocion, s.SucursalId, s.UsuarioId, ct);
    }

    /// <summary>
    /// «Terminar ahora»: deja de aplicarse desde ya (las cajas se enteran con su copia; una sin conexión, al volver). Se queda en
    /// la lista como terminada, con quién y cuándo. También en el piloto: solo quita.
    /// </summary>
    public async Task<PromocionFichaDto> TerminarAsync(Guid id, SolicitudTerminarPromocion s, CancellationToken ct = default)
    {
        var control = Control;
        await control.ExigirDeLaSesionAsync(s.UsuarioId, s.SucursalId, s.CajaId, AccionControlada.EditarCatalogo, ct);
        var promocion = await db.Promociones.Include(p => p.Alcance).SingleOrDefaultAsync(p => p.Id == id, ct)
            ?? throw new InvalidOperationException("Esa promoción ya no existe.");
        if (promocion.Activa)
        {
            (promocion.Activa, promocion.TerminadaUtc, promocion.TerminadaPorId) = (false, Ahora, s.UsuarioId);
            control.Registrar(s.SucursalId, s.CajaId, s.UsuarioId, AccionControlada.EditarCatalogo,
                $"Promoción «{promocion.Nombre}» ({promocion.TipoTexto()}): terminada");
            await db.SaveChangesAsync(ct);
        }
        return await FichaAsync(promocion, s.SucursalId, s.UsuarioId, ct);
    }

    /// <summary>La misma promoción: lo de su tipo, sus fechas, sus días, su sucursal y a qué toca (con sus precios).</summary>
    private static bool Igual(Promocion a, Promocion b)
    {
        static string Alcance(Promocion p) => string.Join(";", p.Alcance
            .Select(x => $"{x.VarianteId}|{x.DepartamentoId}|{x.MarcaId}|{x.PrecioCentavos}").Order(StringComparer.Ordinal));
        return (a.Nombre, a.Tipo, a.Lleva, a.Paga, a.Centesimas, a.Desde, a.Hasta, a.Dias, a.SucursalId)
               == (b.Nombre, b.Tipo, b.Lleva, b.Paga, b.Centesimas, b.Desde, b.Hasta, b.Dias, b.SucursalId)
               && Alcance(a) == Alcance(b);
    }

    /// <summary>Lo que toca, sin rehacer lo que no cambió: así solo sube la versión de lo que de verdad cambió.</summary>
    private static void CambiarAlcance(PosDbContext db, Promocion promocion, List<AlcancePromocion> nuevo)
    {
        static (Guid?, Guid?, Guid?) Llave(AlcancePromocion a) => (a.VarianteId, a.DepartamentoId, a.MarcaId);
        var queda = nuevo.ToDictionary(Llave);
        foreach (var a in promocion.Alcance.ToList())
        {
            if (!queda.Remove(Llave(a), out var igual))
            {
                promocion.Alcance.Remove(a);
                db.AlcancesPromocion.Remove(a);
            }
            else if (a.PrecioCentavos != igual.PrecioCentavos)
                a.PrecioCentavos = igual.PrecioCentavos;
        }
        foreach (var a in queda.Values)
        {
            a.PromocionId = promocion.Id;
            promocion.Alcance.Add(a);
        }
    }

    // ── Lo que se arma ──────────────────────────────────────────────────────

    /// <summary>La promoción que pide la pantalla, revisada: sus marcas por su nombre, lo que toca que exista y lo de su tipo.</summary>
    private async Task<Promocion> ArmarAsync(SolicitudPromocion s, CancellationToken ct)
    {
        var promocion = new Promocion
        {
            Id = s.Id ?? Ids.Nuevo(),
            Nombre = s.Nombre ?? "",
            Tipo = s.Tipo,
            Lleva = s.Lleva,
            Paga = s.Paga,
            Centesimas = s.Centesimas,
            Desde = s.Desde,
            Hasta = s.Hasta,
            Dias = s.Dias,
            SucursalId = s.SoloEstaSucursal ? s.SucursalId : null,
        };
        foreach (var a in s.Alcance ?? [])
        {
            Guid? marcaId = null;
            if (!string.IsNullOrWhiteSpace(a.Marca))
            {
                var normalizada = Texto.Normalizar(a.Marca);
                marcaId = await db.Marcas.AsNoTracking().Where(m => m.NombreNormalizado == normalizada).Select(m => (Guid?)m.Id).FirstOrDefaultAsync(ct)
                    ?? throw new ArgumentException($"No existe la marca «{a.Marca.Trim()}».");
            }
            promocion.Alcance.Add(new AlcancePromocion
            {
                PromocionId = promocion.Id, VarianteId = a.VarianteId, DepartamentoId = a.DepartamentoId, MarcaId = marcaId, PrecioCentavos = a.PrecioCentavos,
            });
        }
        promocion.Validar();
        var variantes = promocion.Alcance.Where(a => a.VarianteId is not null).Select(a => a.VarianteId!.Value).ToList();
        if (variantes.Count > 0 && await db.Variantes.CountAsync(v => variantes.Contains(v.Id), ct) != variantes.Count)
            throw new ArgumentException("Uno de los productos ya no está en el catálogo: quítalo de la promoción.");
        var departamentos = promocion.Alcance.Where(a => a.DepartamentoId is not null).Select(a => a.DepartamentoId!.Value).ToList();
        if (departamentos.Count > 0 && await db.Departamentos.CountAsync(d => departamentos.Contains(d.Id) && d.Activo, ct) != departamentos.Count)
            throw new ArgumentException("Ese departamento ya no está activo.");
        return promocion;
    }

    /// <summary>
    /// Lo que se vende y la promoción toca: sus productos (las variantes activas) y los de sus departamentos y marcas, cada uno
    /// con su nombre como sale en la venta, su menudeo por unidad base en la sucursal y sus precios y presentaciones (para su
    /// precio más bajo con la promoción), por nombre.
    /// </summary>
    private async Task<List<Tocado>> TocaAsync(IReadOnlyCollection<AlcancePromocion> alcance, Guid sucursalId, CancellationToken ct)
    {
        var variantes = alcance.Where(a => a.VarianteId is not null).Select(a => a.VarianteId!.Value).ToList();
        var departamentos = alcance.Where(a => a.DepartamentoId is not null).Select(a => a.DepartamentoId!.Value).ToList();
        var marcas = alcance.Where(a => a.MarcaId is not null).Select(a => a.MarcaId!.Value).ToList();
        if (variantes.Count + departamentos.Count + marcas.Count == 0)
            return [];
        var encontradas = await db.Variantes.AsNoTracking()
            .Where(v => v.Activa && v.Producto!.Activo
                        && (variantes.Contains(v.Id) || departamentos.Contains(v.Producto.DepartamentoId)
                            || v.Producto.MarcaId != null && marcas.Contains(v.Producto.MarcaId.Value)))
            .Select(v => new
            {
                v.Id, v.ProductoId, v.Descripcion, v.UnidadBase, Producto = v.Producto!.Nombre,
                Marca = v.Producto.Marca == null ? null : v.Producto.Marca.Nombre,
            })
            .ToListAsync(ct);
        var ids = encontradas.Select(v => v.Id).ToList();
        var precios = (await db.Precios.AsNoTracking()
                .Where(p => ids.Contains(p.VarianteId) && (p.SucursalId == null || p.SucursalId == sucursalId)).ToListAsync(ct))
            .ToLookup(p => p.VarianteId);
        var presentaciones = (await db.Presentaciones.AsNoTracking().Where(p => ids.Contains(p.VarianteId)).ToListAsync(ct)).ToLookup(p => p.VarianteId);
        return encontradas
            .Select(v =>
            {
                var suyos = precios[v.Id].ToList();
                var porUnidad = suyos.Where(p => p.PresentacionId is null).ToList();
                var deSucursal = porUnidad.Where(p => p.SucursalId == sucursalId).ToList();
                var menudeo = (deSucursal.Count > 0 ? deSucursal : porUnidad.Where(p => p.SucursalId is null)).MinBy(p => p.CantidadMinima)?.PrecioCentavos ?? 0;
                var nombre = string.Join(" ", new[] { v.Producto, v.Marca, v.Descripcion }.Where(x => !string.IsNullOrWhiteSpace(x)));
                return new Tocado(v.Id, v.ProductoId, nombre, v.UnidadBase, menudeo, suyos, presentaciones[v.Id].ToList());
            })
            .Where(t => t.MenudeoCentavos > 0)
            .OrderBy(t => t.Nombre, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
    }

    /// <summary>
    /// Lo que cuesta una unidad base de cada uno, como saldría del inventario al venderla (Kardex.Salida): el costo promedio de
    /// la sucursal o, sin existencia, el último costo de su proveedor. Sin ninguno, no está. Con esto se revisa el costo al
    /// guardar una promoción y al llevarla a las cajas (CatalogoEnMemoria): con lo mismo, para que lo que se autorizó y lo que
    /// queda fuera cuadren.
    /// </summary>
    public static async Task<Dictionary<Guid, decimal>> CostosPorUnidadAsync(PosDbContext db, Guid sucursalId, IEnumerable<Guid> variantes, CancellationToken ct = default)
    {
        var ids = variantes.Distinct().ToList();
        if (ids.Count == 0)
            return [];
        var costos = (await db.Existencias.AsNoTracking().Where(e => e.SucursalId == sucursalId && ids.Contains(e.VarianteId)).ToListAsync(ct))
            .Where(e => e.CostoPromedio is > 0).ToDictionary(e => e.VarianteId, e => e.CostoPromedio!.Value);
        var faltan = ids.Where(id => !costos.ContainsKey(id)).ToList();
        if (faltan.Count > 0)
        {
            var ultimos = await db.ProveedoresVariantes.AsNoTracking()
                .Where(pv => faltan.Contains(pv.VarianteId) && pv.UltimoCostoCentavos != null)
                .Select(pv => new { pv.VarianteId, pv.UltimaCompraUtc, Costo = pv.UltimoCostoCentavos!.Value }).ToListAsync(ct);
            foreach (var g in ultimos.GroupBy(u => u.VarianteId))
            {
                var ultimo = g.OrderByDescending(u => u.UltimaCompraUtc).First().Costo;
                if (ultimo > 0)
                    costos[g.Key] = ultimo;
            }
        }
        return costos;
    }

    /// <summary>Si con la promoción queda debajo de lo que cuesta: su precio más bajo con ella (vacío si no lo baja) contra su costo (sin costo, no).</summary>
    public static bool DebajoDelCosto(decimal? precioMasBajo, decimal costo) => precioMasBajo is { } precio && costo > 0 && precio < costo;

    /// <summary>Su precio por unidad base más bajo con la promoción, con cada grupo de precios con que lo vende la venta; vacío si no lo baja.</summary>
    private static decimal? MasBajo(Promocion promocion, Tocado t, long? precioEspecial, Guid sucursalId) =>
        CalculoPrecio.PrecioMasBajoCon(promocion.Regla(precioEspecial), t.Precios, t.Presentaciones, sucursalId);

    /// <summary>Un renglón de la promoción: su menudeo, su precio especial, y si queda debajo del costo (el costo y el margen, solo para quien los ve).</summary>
    private static ProductoEnPromocionDto Renglon(
        Promocion promocion, Tocado t, long? precioEspecial, Dictionary<Guid, decimal> costos, bool veCostos, Guid sucursalId)
    {
        var masBajo = MasBajo(promocion, t, precioEspecial, sucursalId);
        decimal? costo = costos.TryGetValue(t.VarianteId, out var c) ? c : null;
        var debajo = costo is { } suyo && DebajoDelCosto(masBajo, suyo);
        return new ProductoEnPromocionDto(t.VarianteId, t.ProductoId, t.Nombre, t.MenudeoCentavos, precioEspecial, UnidadTexto(t.Unidad),
            veCostos ? costo : null, veCostos && costo is { } conCosto ? Margen.Calcular((long)Math.Floor(masBajo ?? t.MenudeoCentavos), conCosto) : null, debajo);
    }

    /// <summary>
    /// Lo que queda debajo del costo con la promoción: cuáles y cuánto falta en total, por una unidad base de cada uno (lo que
    /// falta solo lo ve quien ve la utilidad: con un producto, dice su costo).
    /// </summary>
    private static BajoCostoDto BajoCosto(Promocion promocion, IReadOnlyList<Tocado> tocados, Dictionary<Guid, decimal> costos, Guid sucursalId)
    {
        var especiales = PreciosEspeciales(promocion);
        var debajo = tocados
            .Select(t => (t.Nombre, Precio: MasBajo(promocion, t, especiales.GetValueOrDefault(t.VarianteId), sucursalId), Costo: costos.GetValueOrDefault(t.VarianteId)))
            .Where(x => DebajoDelCosto(x.Precio, x.Costo))
            .ToList();
        return new BajoCostoDto((long)Math.Ceiling(debajo.Sum(x => x.Costo - x.Precio!.Value)), debajo.Select(x => x.Nombre).ToList());
    }

    private static Dictionary<Guid, long?> PreciosEspeciales(Promocion promocion) =>
        promocion.Alcance.Where(a => a.VarianteId is not null).GroupBy(a => a.VarianteId!.Value).ToDictionary(g => g.Key, g => g.First().PrecioCentavos);

    /// <summary>Los nombres de los departamentos y las marcas que toca, por su Id.</summary>
    private async Task<Dictionary<Guid, string>> NombresAsync(IEnumerable<AlcancePromocion> alcance, CancellationToken ct)
    {
        var lista = alcance.ToList();
        var departamentos = lista.Where(a => a.DepartamentoId is not null).Select(a => a.DepartamentoId!.Value).Distinct().ToList();
        var marcas = lista.Where(a => a.MarcaId is not null).Select(a => a.MarcaId!.Value).Distinct().ToList();
        var nombres = departamentos.Count == 0 ? []
            : await db.Departamentos.AsNoTracking().Where(d => departamentos.Contains(d.Id)).ToDictionaryAsync(d => d.Id, d => d.Nombre, ct);
        if (marcas.Count > 0)
        {
            foreach (var m in await db.Marcas.AsNoTracking().Where(m => marcas.Contains(m.Id)).Select(m => new { m.Id, m.Nombre }).ToListAsync(ct))
                nombres[m.Id] = m.Nombre;
        }
        return nombres;
    }

    /// <summary>«12 productos», «el departamento «Cuadernos»», «2 marcas», juntos con «y».</summary>
    private static string AQue(IEnumerable<AlcancePromocion> alcance, Dictionary<Guid, string> nombres)
    {
        var lista = alcance.ToList();
        var partes = new List<string>();
        var productos = lista.Count(a => a.VarianteId is not null);
        if (productos > 0)
            partes.Add(productos == 1 ? "1 producto" : $"{productos.ToString("#,##0", Mexico)} productos");
        var departamentos = lista.Where(a => a.DepartamentoId is not null).Select(a => nombres.GetValueOrDefault(a.DepartamentoId!.Value, "—")).ToList();
        if (departamentos.Count > 0)
            partes.Add(departamentos.Count == 1 ? $"el departamento «{departamentos[0]}»" : $"{departamentos.Count} departamentos");
        var marcas = lista.Where(a => a.MarcaId is not null).Select(a => nombres.GetValueOrDefault(a.MarcaId!.Value, "—")).ToList();
        if (marcas.Count > 0)
            partes.Add(marcas.Count == 1 ? $"la marca «{marcas[0]}»" : $"{marcas.Count} marcas");
        return Lista(partes, partes.Count);
    }

    /// <summary>«a, b y c», o «a, b y 3 más» si son más de los que se ponen.</summary>
    private static string Lista(IReadOnlyList<string> partes, int total)
    {
        if (partes.Count == 0)
            return "nada";
        var mas = total - partes.Count;
        if (mas > 0)
            return $"{string.Join(", ", partes)} y {mas} más";
        return partes.Count == 1 ? partes[0] : $"{string.Join(", ", partes.Take(partes.Count - 1))} y {partes[^1]}";
    }

    public static string UnidadTexto(UnidadBase unidad) => unidad switch
    {
        UnidadBase.Metro => "metro",
        UnidadBase.Kilogramo => "kilo",
        UnidadBase.Litro => "litro",
        _ => "pieza",
    };
}
