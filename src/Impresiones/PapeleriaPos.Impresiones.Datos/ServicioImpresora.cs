using System.Globalization;
using Microsoft.EntityFrameworkCore;
using PapeleriaPos.Impresiones.Contratos;
using PapeleriaPos.Impresiones.Medicion;
using PapeleriaPos.Nucleo;

namespace PapeleriaPos.Impresiones.Datos;

/// <summary>
/// Las impresoras de la sucursal vistas desde la caja principal (docs/fase-1-impresion.md, parte 2; varias desde el 2026-11-03,
/// docs/impresion.md, «Varias impresoras»): cuáles hay, sus lecturas por la red, los avisos de consumibles, las hojas que de verdad
/// salieron, el contador contra lo cobrado, la calibración y lo propio de cada una en los costos (<see cref="CostosDeImpresora"/>).
/// Quien lee las impresoras (la tarea de fondo del servicio) le pasa aquí cada lectura. Lo que necesita del POS (la hora y los avisos)
/// lo pide por <paramref name="pos"/>; quién puede cambiar una impresora o aplicar la calibración lo revisa quien llama (la API).
/// <para>
/// La que había antes, cuando la sucursal tenía una sola, es la número 1: lo que no dice de qué impresora es (una caja de antes, la API
/// de siempre <c>/sucursales/{id}/impresora</c>) es de la primera.
/// </para>
/// </summary>
public sealed class ServicioImpresora(DbContext db, IPosParaImpresiones pos)
{
    /// <summary>Un salto hacia arriba de al menos esto (puntos) es un cartucho nuevo (o un tanque rellenado); en la caja de mantenimiento, 10.</summary>
    private const int CambioDeCartucho = 5;
    private const int CambioDeCaja = 10;

    /// <summary>Lo más largo del nombre de una impresora (sale en las tarjetas de F2).</summary>
    public const int NombreMaximo = 40;

    private DbSet<Impresora> Impresoras => db.Set<Impresora>();
    private DbSet<LecturaImpresora> Lecturas => db.Set<LecturaImpresora>();
    private DbSet<TrabajoImpresion> Trabajos => db.Set<TrabajoImpresion>();
    private DbSet<HistorialCostosImpresion> Historial => db.Set<HistorialCostosImpresion>();
    private DateTime Ahora => pos.Reloj.GetUtcNow().UtcDateTime;

    // ── Las impresoras de la sucursal ────────────────────────────────────────

    /// <summary>Las de la sucursal, por su número; sin las dadas de baja, salvo con <paramref name="tambienDeBaja"/>.</summary>
    public async Task<List<Impresora>> ImpresorasAsync(Guid sucursalId, bool tambienDeBaja = false, CancellationToken ct = default) =>
        await Impresoras.Where(i => i.SucursalId == sucursalId && (tambienDeBaja || i.Activa)).OrderBy(i => i.Numero).ToListAsync(ct);

    public async Task<Impresora?> ImpresoraAsync(Guid sucursalId, int numero, CancellationToken ct = default) =>
        await Impresoras.FirstOrDefaultAsync(i => i.SucursalId == sucursalId && i.Numero == numero, ct);

    public async Task<Impresora?> PorIdAsync(Guid id, CancellationToken ct = default) => await Impresoras.FindAsync([id], ct);

    /// <summary>
    /// La primera de la sucursal: la activa de número más bajo (la de antes es la 1; si todas están de baja, la de número más bajo).
    /// Con <paramref name="crear"/>, una sucursal sin ninguna (una tienda nueva) da de alta la 1, una WF-6590 como la de antes.
    /// </summary>
    public async Task<Impresora?> PrimeraAsync(Guid sucursalId, bool crear = false, CancellationToken ct = default)
    {
        var primera = await Impresoras.Where(i => i.SucursalId == sucursalId).OrderByDescending(i => i.Activa).ThenBy(i => i.Numero).FirstOrDefaultAsync(ct);
        if (primera is not null || !crear)
            return primera;
        primera = Nueva(sucursalId, 1, PerfilesImpresora.WF6590.NombreCorto, PerfilImpresora.WF6590, null);
        Impresoras.Add(primera);
        try
        {
            await db.SaveChangesAsync(ct);
            return primera;
        }
        catch (DbUpdateException)
        {
            // Otro trabajo de la misma sucursal la dio de alta al mismo tiempo (su número es único): se usa esa.
            db.Entry(primera).State = EntityState.Detached;
            return await Impresoras.Where(i => i.SucursalId == sucursalId).OrderBy(i => i.Numero).FirstAsync(ct);
        }
    }

    /// <summary>
    /// De qué impresora es lo que llega de una caja: la que dice por su número (caja.json, <c>ColasImpresion</c>) o, si no dice (una caja de
    /// antes, <c>ColaImpresion</c> sola), la primera. Si dice una que no existe o que se dio de baja, también la primera, con una nota que lo
    /// dice (como promete la Configuración al darla de baja): nada se pierde, y no se anota en una que ya no se lee.
    /// </summary>
    public async Task<(Impresora Impresora, string? Nota)> ParaElTrabajoAsync(Guid sucursalId, int? numero, CancellationToken ct = default)
    {
        var dicha = numero is { } n ? await ImpresoraAsync(sucursalId, n, ct) : null;
        if (dicha is { Activa: true })
            return (dicha, null);
        var primera = (await PrimeraAsync(sucursalId, crear: true, ct))!;
        return (primera, (numero, dicha) switch
        {
            (null, _) => null,
            (_, { } deBaja) => $"La caja dijo que salió de la impresora {deBaja.Numero} ({deBaja.Nombre}), que está dada de baja: se anotó en la "
                + $"{primera.Numero} ({primera.Nombre}).",
            ({ } otra, null) => $"La caja dijo que salió de la impresora {otra}, que no existe en la sucursal: se anotó en la {primera.Numero} ({primera.Nombre}).",
        });
    }

    /// <summary>
    /// Una impresora nueva en la sucursal, con el número que sigue (nunca uno que ya se usó). La WF-6590 usa los cartuchos de la sucursal;
    /// la L5590 trae sus botellas, sin precios (<see cref="PerfilesImpresora.CostosIniciales"/>). La da de alta quien tiene «Configurar la
    /// impresora»: lo revisa quien llama. Sin <paramref name="guardar"/>, queda en el contexto para guardarse con lo demás (la bitácora).
    /// </summary>
    public async Task<Impresora> AgregarAsync(Guid sucursalId, string nombre, PerfilImpresora perfil, string? host, bool guardar = true,
        CancellationToken ct = default)
    {
        if (!Enum.IsDefined(perfil))
            throw new ArgumentException("Elige el modelo de la impresora: WF-6590 o L5590.");
        nombre = await NombreLibreAsync(sucursalId, nombre, null, ct);
        await RevisarDireccionAsync(sucursalId, Host(host), null, ct);
        var numero = (await Impresoras.Where(i => i.SucursalId == sucursalId).MaxAsync(i => (int?)i.Numero, ct) ?? 0) + 1;
        var impresora = Nueva(sucursalId, numero, nombre, perfil, Host(host));
        Impresoras.Add(impresora);
        if (guardar)
            await db.SaveChangesAsync(ct);
        return impresora;
    }

    /// <summary>
    /// Cambia cómo se lee una impresora, su nombre o si está activa (lo vacío se queda como está). La última activa no se da de baja: lo
    /// que llega sin decir de qué impresora es iría a una de baja. Quién puede lo revisa quien llama (la API). Sin <paramref name="guardar"/>,
    /// queda en el contexto para guardarse con lo demás (la bitácora).
    /// </summary>
    /// <param name="contador">Sus tarjetas «Copias o celular» (2026-11-08); vacío, como están.</param>
    public async Task<Impresora> CambiarAsync(Impresora impresora, ConfiguracionImpresoraDto? configuracion, string? nombre, bool? activa,
        bool guardar = true, CancellationToken ct = default, AjustesContadorDto? contador = null)
    {
        if (configuracion is { } c)
        {
            Revisar(c);
            await RevisarDireccionAsync(impresora.SucursalId, Host(c.Host), impresora.Id, ct);
            Leer(impresora, c);
        }
        if (contador is not null)
            new TarjetasDelContador(db, pos).Cambiar(impresora, contador);
        if (nombre is not null)
            impresora.Nombre = await NombreLibreAsync(impresora.SucursalId, nombre, impresora.Id, ct);
        if (activa is { } nueva && nueva != impresora.Activa)
        {
            if (!nueva && !await Impresoras.AnyAsync(i => i.SucursalId == impresora.SucursalId && i.Activa && i.Id != impresora.Id, ct))
                throw new InvalidOperationException($"La {impresora.Nombre} es la única impresora activa de la sucursal: agrega otra antes de darla de baja.");
            if (nueva)
            {
                await RevisarDireccionAsync(impresora.SucursalId, impresora.Host, impresora.Id, ct);
                // Lo que contó mientras estaba de baja no se vuelve tarjeta.
                AjustesDelContador.ContarDesde(impresora, Ahora);
            }
            impresora.Activa = nueva;
        }
        if (guardar)
            await db.SaveChangesAsync(ct);
        return impresora;
    }

    /// <summary>
    /// Cómo se lee la primera impresora de la sucursal (la de antes; la API de siempre, <c>PUT /sucursales/{id}/impresora</c>, y
    /// <c>importar-control-print</c>). Si la sucursal no tiene ninguna, la da de alta. La cambia quien tiene «Configurar la impresora»:
    /// quien llama ya lo revisó.
    /// </summary>
    public async Task<Impresora> GuardarConfiguracionAsync(Guid sucursalId, ConfiguracionImpresoraDto c, CancellationToken ct = default)
    {
        Revisar(c);
        var impresora = (await PrimeraAsync(sucursalId, crear: true, ct))!;
        await RevisarDireccionAsync(sucursalId, Host(c.Host), impresora.Id, ct);
        Leer(impresora, c);
        await db.SaveChangesAsync(ct);
        return impresora;
    }

    private static void Revisar(ConfiguracionImpresoraDto c)
    {
        if (c.AvisoTintaPorcentaje is < 0 or > 100)
            throw new ArgumentException("El aviso de tinta va de 0 a 100 %.");
        if (c.MinutosEntreLecturas is < 1 or > 120)
            throw new ArgumentException("Se lee cada 1 a 120 minutos.");
    }

    private void Leer(Impresora impresora, ConfiguracionImpresoraDto c)
    {
        Revisar(c);
        // En otra dirección puede contestar otra impresora: sus tarjetas «Copias o celular» cuentan desde ahora (2026-11-08).
        if (!string.Equals(impresora.Host, Host(c.Host), StringComparison.OrdinalIgnoreCase))
            AjustesDelContador.ContarDesde(impresora, Ahora);
        impresora.Host = Host(c.Host);
        impresora.AvisoTintaPorcentaje = c.AvisoTintaPorcentaje;
        impresora.MinutosEntreLecturas = c.MinutosEntreLecturas;
    }

    private static string? Host(string? host) => string.IsNullOrWhiteSpace(host) ? null : host.Trim();

    /// <summary>
    /// Dos impresoras activas no pueden estar en la misma dirección: las lecturas de una se contarían en las dos y se mezclarían sus contadores.
    /// Una dada de baja no estorba (la nueva puede tomar su IP).
    /// </summary>
    private async Task RevisarDireccionAsync(Guid sucursalId, string? host, Guid? suya, CancellationToken ct)
    {
        if (host is null)
            return;
        var otras = await Impresoras.AsNoTracking().Where(i => i.SucursalId == sucursalId && i.Activa && i.Id != suya && i.Host != null)
            .Select(i => new { i.Numero, i.Nombre, i.Host }).ToListAsync(ct);
        if (otras.FirstOrDefault(o => string.Equals(o.Host, host, StringComparison.OrdinalIgnoreCase)) is { } otra)
            throw new ArgumentException($"La dirección {host} ya es de la impresora {otra.Numero} («{otra.Nombre}»): cada impresora tiene la suya.");
    }

    private Impresora Nueva(Guid sucursalId, int numero, string nombre, PerfilImpresora perfil, string? host) => new()
    {
        SucursalId = sucursalId, Numero = numero, Nombre = nombre, Perfil = perfil, Host = host, CreadaUtc = Ahora,
        CostosJson = CostosDeImpresora.Escribir(PerfilesImpresora.CostosIniciales(perfil)),
    };

    /// <summary>El nombre, sin espacios de más; ni vacío, ni largo, ni el de otra impresora de la sucursal (también las de baja).</summary>
    private async Task<string> NombreLibreAsync(Guid sucursalId, string? nombre, Guid? suya, CancellationToken ct)
    {
        var limpio = string.Join(' ', (nombre ?? "").Split(' ', StringSplitOptions.RemoveEmptyEntries));
        if (limpio.Length == 0)
            throw new ArgumentException("Escribe cómo se le dice a la impresora (por ejemplo, «WF-6590 de la entrada»).");
        if (limpio.Length > NombreMaximo)
            throw new ArgumentException($"El nombre de la impresora es de hasta {NombreMaximo} letras.");
        var otras = await Impresoras.AsNoTracking().Where(i => i.SucursalId == sucursalId && i.Id != suya).Select(i => i.Nombre).ToListAsync(ct);
        if (otras.Any(o => string.Equals(o, limpio, StringComparison.CurrentCultureIgnoreCase)))
            throw new ArgumentException($"Ya hay una impresora «{limpio}» en la sucursal: ponle otro nombre.");
        return limpio;
    }

    /// <summary>
    /// Cómo se llama cada impresora en lo que ve la caja (F2, el desglose del día): solo si la sucursal tiene más de una activa (como la
    /// Configuración); con una sola, siempre vacío, aunque haya otra de baja (no hace falta decirlo). Lo que no dice de qué impresora es, es
    /// de la primera.
    /// </summary>
    public async Task<Func<Guid?, string?>> NombresAsync(Guid sucursalId, CancellationToken ct = default)
    {
        var impresoras = await Impresoras.AsNoTracking().Where(i => i.SucursalId == sucursalId).OrderByDescending(i => i.Activa).ThenBy(i => i.Numero)
            .Select(i => new { i.Id, i.Nombre, i.Activa }).ToListAsync(ct);
        if (impresoras.Count(i => i.Activa) < 2)
            return _ => null;
        var nombres = impresoras.ToDictionary(i => i.Id, i => i.Nombre);
        var primera = impresoras[0].Nombre;
        return id => id is { } i ? nombres.GetValueOrDefault(i) : primera;
    }

    // ── Sus costos ───────────────────────────────────────────────────────────

    /// <summary>Lo propio de una impresora en los costos; vacío: todo es de la sucursal.</summary>
    public static CostosDeImpresora? Propios(Impresora impresora) => CostosDeImpresora.Leer(impresora.CostosJson);

    /// <summary>
    /// Los costos con que se cobra lo que sale de una impresora: los de la sucursal (o <paramref name="deLaSucursal"/>, como los de Control
    /// Print en la caja 2) con lo propio de la impresora encima.
    /// </summary>
    public async Task<CostSettings> CostosAsync(Impresora impresora, CostSettings? deLaSucursal = null, CancellationToken ct = default) =>
        CostosDeImpresora.Aplicar(deLaSucursal ?? await new RegistroImpresiones(db, pos).CostosAsync(impresora.SucursalId, ct), Propios(impresora));

    /// <summary>Las impresoras activas con sus propios consumibles (la L5590): sus precios, para la Configuración.</summary>
    public async Task<List<ConsumiblesImpresoraDto>> ConsumiblesPropiosAsync(Guid sucursalId, CancellationToken ct = default)
    {
        var lista = new List<ConsumiblesImpresoraDto>();
        foreach (var impresora in await ImpresorasAsync(sucursalId, ct: ct))
        {
            if (Propios(impresora)?.Consumibles is not { } c)
                continue;
            var perfil = PerfilesImpresora.De(impresora.Perfil);
            lista.Add(new ConsumiblesImpresoraDto(impresora.Numero, impresora.Nombre, impresora.Perfil,
                Canales.Select(t => new CartuchoCostoDto(Calibrator.Name(t), c.Tinta(t).Price, c.Tinta(t).IsoYield)).ToList(),
                c.Maintenance?.BoxPrice, PerfilesImpresora.Faltan(impresora.Nombre, perfil, c).ToList(), c.Maintenance?.PagesPerBox,
                Propios(impresora)?.Margenes is { } m ? CostosEnLaConfiguracion.Margenes(m) : null));
        }
        return lista;
    }

    /// <summary>
    /// Los márgenes propios de una impresora con sus propios consumibles (2026-11-08): con <paramref name="s"/> sin márgenes, vuelve a los de
    /// la sucursal. Se revisan como los de la sucursal; lo de antes se queda en el historial. Quién puede lo revisa quien llama. Sin
    /// <paramref name="guardar"/>, queda en el contexto para guardarse con lo demás (la bitácora).
    /// </summary>
    /// <returns>Qué cambió, sin los montos, para la bitácora (vacío si nada).</returns>
    public async Task<List<string>> GuardarMargenesAsync(Impresora impresora, SolicitudMargenesImpresora s, CostSettings deLaSucursal, bool guardar = true,
        CancellationToken ct = default)
    {
        var propios = Propios(impresora)?.Copia();
        if (propios?.Consumibles is null)
            throw new InvalidOperationException($"La {impresora.Nombre} usa los cartuchos y los márgenes de la sucursal: se cambian en los costos de arriba.");
        // Sus márgenes de hoy sobre los costos de la sucursal: se ven y se cambian aunque a sus consumibles les falte un precio.
        var antes = deLaSucursal.Clone();
        (propios.Margenes ?? MargenesDeImpresora.De(deLaSucursal)).Poner(antes);
        var cambios = new List<string>();
        if (s.Margenes is null)
        {
            if (propios.Margenes is not null)
                cambios.Add($"los márgenes de la {impresora.Nombre} (vuelve a los de la sucursal)");
            propios.Margenes = null;
        }
        else
        {
            var nuevos = CostosEnLaConfiguracion.AplicarMargenes(antes, s.Margenes);
            var despues = deLaSucursal.Clone();
            nuevos.Poner(despues);
            if (propios.Margenes is null)
                cambios.Add($"los márgenes de la {impresora.Nombre} (ahora los suyos)");
            cambios.AddRange(CostosEnLaConfiguracion.Cambios(antes, despues).Select(c => $"{c} de la {impresora.Nombre}"));
            propios.Margenes = nuevos;
        }
        if (cambios.Count > 0)
        {
            await GuardarPropiosAsync(impresora, propios, "Configuración", ct);
            if (guardar)
                await db.SaveChangesAsync(ct);
        }
        return cambios;
    }

    private static readonly InkChannel[] Canales = [InkChannel.Black, InkChannel.Cyan, InkChannel.Magenta, InkChannel.Yellow];

    private static readonly CultureInfo Mexico = CultureInfo.GetCultureInfo("es-MX");

    private static string Pesos(decimal valor) => valor.ToString("C2", Mexico);

    /// <summary>
    /// Cambia los consumibles propios de una impresora (la tinta de la L5590): cada botella (o cartucho) con su precio y lo que rinde, y su
    /// caja de mantenimiento. Las que usan los cartuchos de la sucursal se cambian en los costos de la sucursal. Lo de antes se queda en el
    /// historial, como los costos de la sucursal. Quién puede lo revisa quien llama. Sin <paramref name="guardar"/>, queda en el contexto
    /// para guardarse con lo demás (la bitácora).
    /// </summary>
    /// <returns>Qué cambió, sin los montos, para la bitácora (vacío si nada).</returns>
    public async Task<List<string>> GuardarConsumiblesAsync(Impresora impresora, SolicitudConsumiblesImpresora s, bool guardar = true,
        CancellationToken ct = default)
    {
        var propios = Propios(impresora)?.Copia();
        if (propios?.Consumibles is not { } consumibles)
            throw new InvalidOperationException($"La {impresora.Nombre} usa los cartuchos de la sucursal: sus precios se cambian en los costos de arriba.");
        var perfil = PerfilesImpresora.De(impresora.Perfil);
        var cambios = new List<string>();
        foreach (var canal in Canales)
        {
            var nombre = Calibrator.Name(canal);
            var que = PerfilesImpresora.Consumible(perfil, canal);
            var pedido = (s.Tintas ?? []).FirstOrDefault(t => string.Equals(t.Tinta, nombre, StringComparison.OrdinalIgnoreCase))
                ?? throw new ArgumentException($"Falta la {que} de la {impresora.Nombre}.");
            if (pedido.Precio is < 0 or > CostosEnLaConfiguracion.PrecioMaximo)
                throw new ArgumentException($"El precio de la {que} va de $0.00 a {Pesos(CostosEnLaConfiguracion.PrecioMaximo)}.");
            if (pedido.Rendimiento is < 1 or > CostosEnLaConfiguracion.RendimientoMaximo)
                throw new ArgumentException($"Lo que rinde la {que} va de 1 a {CostosEnLaConfiguracion.RendimientoMaximo.ToString("N0", Mexico)} páginas (lo dice su caja).");
            var tinta = consumibles.Tinta(canal);
            if (tinta.Price != pedido.Precio)
                cambios.Add($"el precio de la {que} de la {impresora.Nombre}");
            if (tinta.IsoYield != pedido.Rendimiento)
                cambios.Add($"lo que rinde la {que} de la {impresora.Nombre}");
            (tinta.Price, tinta.IsoYield) = (pedido.Precio, pedido.Rendimiento);
        }
        if (s.PaginasPorCaja is < 1 or > CostosEnLaConfiguracion.RendimientoMaximo)
            throw new ArgumentException($"Lo que dura la caja de mantenimiento va de 1 a {CostosEnLaConfiguracion.RendimientoMaximo.ToString("N0", Mexico)} caras.");
        if (s.CajaMantenimiento is { } precio)
        {
            if (precio is < 0 or > CostosEnLaConfiguracion.PrecioMaximo)
                throw new ArgumentException($"El precio de la caja de mantenimiento va de $0.00 a {Pesos(CostosEnLaConfiguracion.PrecioMaximo)}.");
            if (consumibles.Maintenance is { } caja)
            {
                if (caja.BoxPrice != precio)
                    cambios.Add($"el precio de la caja de mantenimiento de la {impresora.Nombre}");
                caja.BoxPrice = precio;
            }
            else if (s.PaginasPorCaja is { } paginas and > 0)
            {
                consumibles.Maintenance = new MaintenanceBox { Sku = "", BoxPrice = precio, PagesPerBox = paginas };
                cambios.Add($"la caja de mantenimiento de la {impresora.Nombre}");
            }
            else
            {
                throw new ArgumentException($"La {impresora.Nombre} no tiene caja de mantenimiento: para ponérsela, di también cuántas caras dura.");
            }
        }
        if (s.PaginasPorCaja is { } dura && consumibles.Maintenance is { } suCaja && suCaja.PagesPerBox != dura)
        {
            suCaja.PagesPerBox = dura;
            cambios.Add($"lo que dura la caja de mantenimiento de la {impresora.Nombre}");
        }
        else if (s.PaginasPorCaja is not null && s.CajaMantenimiento is null && consumibles.Maintenance is null)
        {
            throw new ArgumentException($"La {impresora.Nombre} no tiene caja de mantenimiento: para ponérsela, escribe también su precio.");
        }
        if (cambios.Count > 0)
        {
            await GuardarPropiosAsync(impresora, propios, "Configuración", ct);
            if (guardar)
                await db.SaveChangesAsync(ct);
        }
        return cambios;
    }

    /// <summary>Lo propio de una impresora, con lo de antes en el historial (como los costos de la sucursal).</summary>
    private async Task GuardarPropiosAsync(Impresora impresora, CostosDeImpresora? nuevos, string origen, CancellationToken ct)
    {
        var json = CostosDeImpresora.Escribir(nuevos);
        if (json == impresora.CostosJson)
            return;
        var desde = await Historial.Where(h => h.ImpresoraId == impresora.Id).MaxAsync(h => (DateTime?)h.HastaUtc, ct) ?? impresora.CreadaUtc;
        Historial.Add(new HistorialCostosImpresion
        {
            SucursalId = impresora.SucursalId, ImpresoraId = impresora.Id, DesdeUtc = desde, HastaUtc = Ahora, PorLosDe = origen,
            CostosJson = impresora.CostosJson ?? "{}",
        });
        impresora.CostosJson = json;
    }

    // ── Sus lecturas ─────────────────────────────────────────────────────────

    /// <summary>Las impresoras que se leen por la red: activas y con dirección.</summary>
    public async Task<List<Impresora>> ConImpresoraAsync(CancellationToken ct = default) =>
        await Impresoras.AsNoTracking().Where(i => i.Activa && i.Host != null && i.Host != "").OrderBy(i => i.Numero).ToListAsync(ct);

    /// <summary>
    /// Las lecturas de una impresora. La migración VariasImpresoras ligó todas las de antes a la 1 y cada lectura nueva lleva la suya: sin el
    /// «o sin impresora» que hacía recorrer la tabla entera (con meses de lecturas, cada vez que se abre la Configuración), va por su índice.
    /// </summary>
    private IQueryable<LecturaImpresora> LecturasDe(Impresora i) => Lecturas.AsNoTracking().Where(l => l.ImpresoraId == i.Id);

    /// <summary>Lo que salió de una impresora (lo de antes de varias impresoras que no se ligó es de la 1).</summary>
    internal IQueryable<TrabajoImpresion> TrabajosDe(Impresora i) => i.Numero == 1
        ? Trabajos.AsNoTracking().Where(t => t.ImpresoraId == i.Id || (t.ImpresoraId == null && t.SucursalId == i.SucursalId))
        : Trabajos.AsNoTracking().Where(t => t.ImpresoraId == i.Id);

    public async Task<LecturaImpresora?> UltimaAsync(Impresora impresora, CancellationToken ct = default) =>
        await LecturasDe(impresora).OrderByDescending(l => l.TomadaUtc).FirstOrDefaultAsync(ct);

    /// <summary>La última lectura con contador tomada a más tardar en ese momento (la base para verificar las hojas).</summary>
    public async Task<LecturaImpresora?> AntesDeAsync(Impresora impresora, DateTime utc, CancellationToken ct = default) =>
        await LecturasDe(impresora).Where(l => l.Contador != null && l.TomadaUtc <= utc).OrderByDescending(l => l.TomadaUtc).FirstOrDefaultAsync(ct);

    /// <summary>
    /// Guarda una lectura y avisa lo que cambió desde la anterior: una tinta o la caja de mantenimiento que bajó al umbral, o un consumible
    /// nuevo (en la L5590, un tanque rellenado). Con más de una impresora, el aviso dice cuál.
    /// <para>
    /// Si en su dirección contesta otra serie (el módem les cambió la IP a dos iguales), la lectura no se guarda: su contador es de otra
    /// impresora y cerraría los lotes de esta con hojas que no salieron de ella (PageAuditor: se puede no ver un faltante, pero nunca
    /// inventarlo). Se avisa una vez por serie y se devuelve qué pasa en <c>OtraSerie</c>, para la pantalla; hasta corregir la IP, nada de
    /// esa dirección se guarda. Una impresora que se cambió por otra se da de baja y la nueva se agrega (su contador empieza de nuevo).
    /// </para>
    /// </summary>
    public async Task<(LecturaImpresora? Lectura, string? OtraSerie)> GuardarLecturaAsync(Impresora impresora, PrinterStatus s, CancellationToken ct = default)
    {
        // Contra la última que dijo su serie: la de antes pudo no decirla.
        var serieDeAntes = s.Serial is null ? null
            : await LecturasDe(impresora).Where(l => l.Serie != null).OrderByDescending(l => l.TomadaUtc).Select(l => l.Serie).FirstOrDefaultAsync(ct);
        var cual = await VariasAsync(impresora.SucursalId, ct) ? $"Impresora {impresora.Nombre}" : "Impresora";
        if (serieDeAntes is not null && !string.Equals(serieDeAntes, s.Serial, StringComparison.OrdinalIgnoreCase))
        {
            if (pos.Avisar(impresora.SucursalId, AvisoImpresiones.Impresora,
                    $"{cual}: contesta otra impresora\nEn {impresora.Host} antes contestaba la serie {serieDeAntes} y ahora la {s.Serial}: ¿le cambiaron la IP, o se "
                    + "cambió la impresora? Con la IP equivocada se mezclan sus contadores: mientras tanto no se lee. Revisa la IP en la Configuración.",
                    $"impresora:otra-serie:{impresora.Id:N}:{s.Serial}"))
                await db.SaveChangesAsync(ct);
            return (null, OtraSerie(impresora, serieDeAntes, s.Serial!));
        }
        // Cada consumible contra la última lectura que sí lo trajo: una lectura a medias (un paquete UDP perdido) no repite el aviso de
        // «baja» con la siguiente completa (auditoría, 2026-11-04).
        var anterior = new LecturaImpresora
        {
            SucursalId = impresora.SucursalId,
            TintaK = await UltimoNivelAsync(impresora, l => l.TintaK, ct), TintaC = await UltimoNivelAsync(impresora, l => l.TintaC, ct),
            TintaM = await UltimoNivelAsync(impresora, l => l.TintaM, ct), TintaY = await UltimoNivelAsync(impresora, l => l.TintaY, ct),
            Mantenimiento = await UltimoNivelAsync(impresora, l => l.Mantenimiento, ct),
        };
        var lectura = new LecturaImpresora
        {
            SucursalId = impresora.SucursalId, ImpresoraId = impresora.Id, TomadaUtc = s.TakenUtc, TintaK = s.InkK, TintaC = s.InkC, TintaM = s.InkM,
            TintaY = s.InkY, Mantenimiento = s.Maintenance, Contador = s.LifeCount, ContadorColor = s.ColorCount, ContadorBN = s.MonoCount, Serie = s.Serial,
        };
        Lecturas.Add(lectura);

        var umbral = impresora.AvisoTintaPorcentaje;
        foreach (var (nombre, femenino, antes, ahora, salto) in Consumibles(PerfilesImpresora.De(impresora.Perfil), anterior, lectura))
        {
            if (ahora is not { } nivel)
                continue;
            if (antes is { } previo && nivel - previo >= salto)
                pos.Avisar(impresora.SucursalId, AvisoImpresiones.Impresora,
                    $"{cual}: {nombre} {(femenino ? "nueva" : "nuevo")}\nPasó de {previo} % a {nivel} %.", $"impresora:cambio:{nombre}:{lectura.Id}");
            else if (nivel <= umbral && (antes is null || antes > umbral))
                pos.Avisar(impresora.SucursalId, AvisoImpresiones.Impresora,
                    $"{cual}: {nombre} {(nivel == 0 ? femenino ? "agotada" : "agotado" : femenino ? "baja" : "bajo")}\n"
                    + $"Queda {nivel} % (el aviso es desde {umbral} %).",
                    $"impresora:bajo:{nombre}:{lectura.Id}");
        }
        await db.SaveChangesAsync(ct);
        return (lectura, null);
    }

    /// <summary>El último nivel que dio la impresora de un consumible (vacío si nunca lo dio).</summary>
    private async Task<int?> UltimoNivelAsync(Impresora impresora, System.Linq.Expressions.Expression<Func<LecturaImpresora, int?>> nivel, CancellationToken ct) =>
        await LecturasDe(impresora).OrderByDescending(l => l.TomadaUtc).Select(nivel).Where(n => n != null).FirstOrDefaultAsync(ct);

    /// <summary>Lo que dice la Configuración de una impresora en cuya dirección contesta otra serie.</summary>
    public static string OtraSerie(Impresora impresora, string suya, string otra) =>
        $"En {impresora.Host} contesta otra impresora (la serie {otra}; esta es la {suya}): ¿le cambió la IP el módem? Mientras tanto no se lee ni se "
        + "verifican sus hojas. Corrige su IP aquí abajo; si se cambió la impresora por otra, dala de baja y agrega la nueva.";

    /// <summary>Una lectura de la primera impresora de la sucursal (la de antes; si no tiene ninguna, la da de alta).</summary>
    public async Task<(LecturaImpresora? Lectura, string? OtraSerie)> GuardarLecturaAsync(Guid sucursalId, PrinterStatus s, CancellationToken ct = default) =>
        await GuardarLecturaAsync((await PrimeraAsync(sucursalId, crear: true, ct))!, s, ct);

    /// <summary>Si la sucursal tiene más de una impresora activa: entonces los avisos dicen cuál.</summary>
    private async Task<bool> VariasAsync(Guid sucursalId, CancellationToken ct) =>
        await Impresoras.CountAsync(i => i.SucursalId == sucursalId && i.Activa, ct) > 1;

    /// <summary>Cada consumible con su nombre («cartucho negro», «tinta negra» en la L5590) y si se dice en femenino («baja»).</summary>
    private static IEnumerable<(string Nombre, bool Femenino, int? Antes, int? Ahora, int Salto)> Consumibles(PerfilDeImpresora perfil,
        LecturaImpresora? a, LecturaImpresora b)
    {
        var femenino = perfil.ConBotellas;
        yield return (PerfilesImpresora.NivelDe(perfil, InkChannel.Black), femenino, a?.TintaK, b.TintaK, CambioDeCartucho);
        yield return (PerfilesImpresora.NivelDe(perfil, InkChannel.Cyan), femenino, a?.TintaC, b.TintaC, CambioDeCartucho);
        yield return (PerfilesImpresora.NivelDe(perfil, InkChannel.Magenta), femenino, a?.TintaM, b.TintaM, CambioDeCartucho);
        yield return (PerfilesImpresora.NivelDe(perfil, InkChannel.Yellow), femenino, a?.TintaY, b.TintaY, CambioDeCartucho);
        yield return ("caja de mantenimiento", true, a?.Mantenimiento, b.Mantenimiento, CambioDeCaja);
    }

    /// <summary>
    /// Lo que contó una impresora para un lote de sus trabajos (<see cref="PageAuditor"/>). Si faltaron caras, el costo baja a lo que
    /// salió y se avisa: puede ser un atasco o una cancelación en el panel de la impresora.
    /// </summary>
    public async Task AplicarVerificacionAsync(IReadOnlyList<PageAuditor.JobCount> conteo, Impresora? impresora = null, CancellationToken ct = default)
    {
        var cual = impresora is not null && await VariasAsync(impresora.SucursalId, ct) ? $"la {impresora.Nombre}" : "la impresora";
        foreach (var c in conteo)
        {
            if (await Trabajos.FindAsync([c.JobId], ct) is not { } trabajo || trabajo.CarasContadas is not null)
                continue;
            trabajo.AplicarCarasContadas(c.Counted);
            if (trabajo.FaltaronCaras)
                pos.Avisar(trabajo.SucursalId, AvisoImpresiones.HojasFaltantes,
                    $"Impresión incompleta · {trabajo.Pc}\n«{trabajo.Documento}»: {cual} contó {trabajo.CarasContadas} de {trabajo.Caras} caras. "
                    + "Puede ser un atasco o una cancelación en el panel.",
                    $"hojas:{trabajo.Id}");
        }
        await db.SaveChangesAsync(ct);
    }

    /// <summary>Lo que necesita la verificación de cada trabajo del lote: caras esperadas y si salió (todos ya terminaron en Windows).</summary>
    public async Task<Dictionary<Guid, PageAuditor.JobInfo>> InfoAsync(IReadOnlyList<Guid> ids, CancellationToken ct = default) =>
        await Trabajos.AsNoTracking().Where(t => ids.Contains(t.Id))
            .ToDictionaryAsync(t => t.Id, t => new PageAuditor.JobInfo(t.Caras, t.Estado == EstadoTrabajo.Impreso, Finished: true), ct);

    // ── Contador contra lo cobrado ───────────────────────────────────────────

    /// <summary>Desde que abrió la tienda hoy (o el día que se pida): lo que contó una impresora contra lo registrado en ella.</summary>
    public async Task<CounterCheck?> ComparacionAsync(Impresora impresora, DateOnly fecha, CancellationToken ct = default)
    {
        var desde = HoraMexico.AUtc(fecha.ToDateTime(TimeOnly.MinValue));
        var hasta = HoraMexico.AUtc(fecha.AddDays(1).ToDateTime(TimeOnly.MinValue));
        var historial = await HistorialAsync(impresora, desde.AddDays(-1), hasta, ct);
        var registro = new RegistroImpresiones(db, pos);
        return CounterCheck.Compute(historial, desde, hasta, (a, b) => registro.CarasRegistradas(TrabajosDe(impresora), a, b));
    }

    /// <summary>
    /// Lo de todas las impresoras de la sucursal en un día, sumado (el resumen del día): cada una contra lo registrado en ella. Vacío si
    /// ninguna tiene dos lecturas del contador ese día; si alguna no da el contador de color, la suma tampoco.
    /// </summary>
    public async Task<CounterCheck?> ComparacionDeLaSucursalAsync(Guid sucursalId, DateOnly fecha, CancellationToken ct = default)
    {
        var de = new List<CounterCheck>();
        foreach (var impresora in await ImpresorasAsync(sucursalId, tambienDeBaja: true, ct))
            if (await ComparacionAsync(impresora, fecha, ct) is { } c)
                de.Add(c);
        if (de.Count == 0)
            return null;
        if (de.Count == 1)
            return de[0];
        return new CounterCheck(de.Min(c => c.FromUtc), de.Max(c => c.ToUtc), de.Sum(c => c.Counted),
            de.All(c => c.CountedColor is not null) ? de.Sum(c => c.CountedColor!.Value) : null,
            new RegisteredSides(de.Sum(c => c.Registered.PrintSides), de.Sum(c => c.Registered.PrintColorSides), de.Sum(c => c.Registered.CopySides),
                de.Sum(c => c.Registered.CopyColorSides)));
    }

    public async Task<EstadoImpresoraDto> EstadoAsync(Impresora impresora, string? error, string? otraSerie = null, CancellationToken ct = default)
    {
        var ultima = await UltimaAsync(impresora, ct);
        var hoy = await ComparacionAsync(impresora, DateOnly.FromDateTime(HoraMexico.ALocal(Ahora)), ct);
        return new EstadoImpresoraDto(
            new ConfiguracionImpresoraDto(impresora.Host, impresora.AvisoTintaPorcentaje, impresora.MinutosEntreLecturas),
            ultima is null ? null : Dto(ultima), error, hoy is null ? null : Dto(hoy), otraSerie);
    }

    /// <summary>La primera impresora de la sucursal (la API de siempre, <c>GET /sucursales/{id}/impresora</c>); sin ninguna, como antes: sin IP.</summary>
    public async Task<EstadoImpresoraDto> EstadoAsync(Guid sucursalId, string? error, string? otraSerie = null, CancellationToken ct = default) =>
        await PrimeraAsync(sucursalId, ct: ct) is { } impresora
            ? await EstadoAsync(impresora, error, otraSerie, ct)
            : new EstadoImpresoraDto(new ConfiguracionImpresoraDto(null, Impresora.AvisoTintaPorOmision, Impresora.MinutosEntreLecturasPorOmision), null,
                error, null);

    public async Task<ImpresoraDto> DtoAsync(Impresora impresora, string? error, string? otraSerie = null, CancellationToken ct = default)
    {
        var hoy = DateOnly.FromDateTime(HoraMexico.ALocal(Ahora));
        return new(impresora.Numero, impresora.Nombre, impresora.Perfil, impresora.Activa, await EstadoAsync(impresora, error, otraSerie, ct),
            Propios(impresora)?.Consumibles is not null,
            await new TarjetasDelContador(db, pos).DeLaImpresoraAsync(impresora, await ComparacionAsync(impresora, hoy, ct), hoy, ct));
    }

    public static LecturaImpresoraDto Dto(LecturaImpresora l) =>
        new(HoraMexico.ALocal(l.TomadaUtc), l.TintaK, l.TintaC, l.TintaM, l.TintaY, l.Mantenimiento, l.Contador, l.ContadorColor, l.ContadorBN, l.Serie);

    public static ComparacionContadorDto Dto(CounterCheck c) =>
        new(HoraMexico.ALocal(c.FromUtc), HoraMexico.ALocal(c.ToUtc), c.Counted, c.CountedColor, c.Registered.PrintSides, c.Registered.CopySides,
            c.Unregistered, c.UnregisteredColor);

    // ── Calibración ──────────────────────────────────────────────────────────

    /// <summary>
    /// La tinta real (niveles de la impresora) contra la que estimó el modelo, desde la última calibración aplicada: de ahí salen los
    /// factores que corrigen el costo de cada tinta y las páginas reales por caja de mantenimiento. Solo con sus lecturas y lo que salió
    /// de ella.
    /// </summary>
    public async Task<(CalibrationResult Resultado, CostSettings Costos)> CalcularCalibracionAsync(Impresora impresora, CancellationToken ct = default)
    {
        var costos = await CostosAsync(impresora, ct: ct);
        var desde = costos.Calibration.SinceUtc ?? DateTime.MinValue;
        var historial = await HistorialAsync(impresora, desde, DateTime.MaxValue, ct);
        var trabajos = await TrabajosDe(impresora)
            .Where(t => t.ImpresoUtc >= desde && (t.Estado == EstadoTrabajo.Impreso || t.CarasContadas > 0))
            .Select(t => new { t.ImpresoUtc, t.EnviadoUtc, t.Caras, t.CarasContadas, t.CarasColor, t.TintaC, t.TintaM, t.TintaY, t.TintaK })
            .ToListAsync(ct);
        var paraCalibrar = trabajos.Select(t => new CalibrationJob(
            // Si el agente lo mandó mucho después de imprimirse (la caja estaba cerrada), cuenta cuando se envió.
            t.EnviadoUtc is { } enviado && t.ImpresoUtc - enviado > TimeSpan.FromMinutes(10) ? enviado.AddMinutes(1) : t.ImpresoUtc,
            t.CarasContadas ?? t.Caras, t.CarasColor, t.TintaC, t.TintaM, t.TintaY, t.TintaK)).ToList();
        return (Calibrator.Calibrate(historial, paraCalibrar, costos), costos);
    }

    public async Task<CalibracionDto> CalibracionAsync(Impresora impresora, CancellationToken ct = default)
    {
        var (r, costos) = await CalcularCalibracionAsync(impresora, ct);
        var actual = costos.Calibration;
        double Actual(InkChannel canal) => canal switch
        {
            InkChannel.Cyan => actual.C,
            InkChannel.Magenta => actual.M,
            InkChannel.Yellow => actual.Y,
            _ => actual.K,
        };
        return new CalibracionDto(
            r.FromUtc is { } desde ? HoraMexico.ALocal(desde) : null, r.ToUtc is { } hasta ? HoraMexico.ALocal(hasta) : null,
            r.Channels.Select(c => new CanalCalibradoDto(Calibrator.Name(c.Channel), c.ObservedPoints, c.EstimatedPoints, c.Ratio, c.Factor,
                Actual(c.Channel), Calidad(c.Quality))).ToList(),
            r.PrinterPages, r.OurSides, r.MaintenancePagesPerBox, costos.Maintenance.PagesPerBox,
            r.Changes.Select(c => $"{HoraMexico.ALocal(c.Utc):dd/MM HH:mm} · {c.Supply}: de {c.From} % a {c.To} %").ToList(),
            actual.UpdatedUtc is { } aplicada ? HoraMexico.ALocal(aplicada) : null);
    }

    /// <summary>
    /// Para quien tiene «Configurar la impresora» (quien llama ya lo revisó: la API, con su usuario y su PIN): guarda los factores
    /// calculados; las impresiones nuevas de esa impresora ya salen con ellos. La 1 (la de antes, con los cartuchos de la sucursal) los
    /// guarda en los costos de la sucursal, como siempre; las demás, en lo suyo (<see cref="CostosDeImpresora.Calibracion"/>), con lo de antes
    /// en el historial. Como en Control Print, el factor es la tinta real entre la del modelo (no se acumula) y la caja de mantenimiento
    /// se corrige con datos preliminares. No es automática: mientras el agente vigila la cola de laboratorio, lo que registra el POS no
    /// sale de la impresora y la calibración no sirve.
    /// </summary>
    public async Task<CalibracionDto> AplicarCalibracionAsync(Impresora impresora, CancellationToken ct = default)
    {
        // Con sus consumibles a medias se cobra como una WF-6590: lo que se midiera sería contra otra tinta.
        if (Propios(impresora)?.Consumibles is { } suyos && !CostosDeImpresora.Completos(suyos))
            throw new InvalidOperationException($"A la {impresora.Nombre} le faltan los precios de su tinta: escríbelos en los costos antes de calibrarla.");
        var (r, costos) = await CalcularCalibracionAsync(impresora, ct);
        var canales = r.Channels.Where(c => c.Quality >= CalibrationQuality.Preliminary).ToList();
        var caja = r.MaintenanceQuality >= CalibrationQuality.Preliminary ? r.MaintenancePagesPerBox : null;
        if (canales.Count == 0 && caja is null)
            throw new InvalidOperationException("Todavía no hay datos suficientes: los niveles de tinta tienen que bajar al menos 3 puntos.");
        var cal = costos.Calibration;
        foreach (var c in canales)
        {
            switch (c.Channel)
            {
                case InkChannel.Cyan: cal.C = c.Factor; break;
                case InkChannel.Magenta: cal.M = c.Factor; break;
                case InkChannel.Yellow: cal.Y = c.Factor; break;
                default: cal.K = c.Factor; break;
            }
        }
        cal.Enabled = true;
        cal.UpdatedUtc = Ahora;
        var propios = Propios(impresora);
        if (impresora.CalibraEnLaSucursal(propios))
        {
            var registro = new RegistroImpresiones(db, pos);
            var deLaSucursal = await registro.CostosAsync(impresora.SucursalId, ct);
            deLaSucursal.Calibration = cal;
            if (caja is { } paginas)
                deLaSucursal.Maintenance.PagesPerBox = paginas;
            await registro.GuardarCostosAsync(impresora.SucursalId, deLaSucursal, ct);
        }
        else
        {
            var nuevos = propios?.Copia() ?? new CostosDeImpresora();
            nuevos.Calibracion = new CalibracionDeImpresora { Calibration = cal, PaginasPorCaja = caja ?? nuevos.Calibracion?.PaginasPorCaja };
            await GuardarPropiosAsync(impresora, nuevos, "Calibración", ct);
            await db.SaveChangesAsync(ct);
        }
        return await CalibracionAsync(impresora, ct);
    }

    private static string Calidad(CalibrationQuality q) => q switch
    {
        CalibrationQuality.NoData => "Sin datos",
        CalibrationQuality.Collecting => "Juntando datos",
        CalibrationQuality.Preliminary => "Preliminar",
        _ => "Confiable",
    };

    private async Task<List<PrinterStatus>> HistorialAsync(Impresora impresora, DateTime desdeUtc, DateTime hastaUtc, CancellationToken ct) =>
        (await LecturasDe(impresora).Where(l => l.TomadaUtc >= desdeUtc && l.TomadaUtc <= hastaUtc).OrderBy(l => l.TomadaUtc).ToListAsync(ct))
        .Select(l => new PrinterStatus
        {
            TakenUtc = l.TomadaUtc, InkK = l.TintaK, InkC = l.TintaC, InkM = l.TintaM, InkY = l.TintaY, Maintenance = l.Mantenimiento,
            LifeCount = l.Contador, ColorCount = l.ContadorColor, MonoCount = l.ContadorBN, Serial = l.Serie,
        }).ToList();
}
