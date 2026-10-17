using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Microsoft.EntityFrameworkCore;
using PapeleriaPos.Nucleo;
using PapeleriaPos.Nucleo.Catalogo;
using PapeleriaPos.Nucleo.Contratos;
using PapeleriaPos.Nucleo.Control;

namespace PapeleriaPos.Datos;

/// <summary>
/// Productos (F3) › Cambiar precios: los de varios productos a la vez, como «Actualizar varios» de eleventa (cuando un
/// proveedor sube sus precios). Se eligen los productos activos de un departamento, de una marca, los que coinciden con un
/// texto o los marcados en la lista, y la regla (<see cref="CambioDePrecios"/>): un porcentaje o una cantidad más o menos,
/// o un precio fijo, sobre el menudeo; si se pide, el mayoreo (escalas y precios por paquete) en la misma proporción; y el
/// redondeo. Primero se ve cómo quedan; al aplicar se vuelve a calcular y, si algo cambió desde la vista, no se aplica.
/// <para>
/// Lo hace quien entró con «Productos, precios y etiquetas», sin volver a escribir su PIN; el margen de cada uno (con el
/// mínimo de su departamento o el general) solo lo ve quien tiene «Ver costos, utilidad y margen». Todo va en una sola
/// transacción, con un solo renglón en la bitácora que dice cuántos, cuáles y la regla; el historial de cada precio y la
/// versión del catálogo los escribe la base al guardar, como en la ficha, así que las cajas y el cobro con la versión de su
/// copia se enteran igual. Se cambian hasta <see cref="CambioDePrecios.Maximo"/> a la vez.
/// </para>
/// <para>
/// En el piloto con eleventa (<paramref name="lecturaDeEleventa"/>: su lectura prendida), de los productos de eleventa no
/// cambia lo que pone la lectura (<see cref="TrastiendaEnElPiloto"/>): el menudeo y las promociones de eleventa se cambian
/// allá. Su mayoreo de aquí (las escalas del F11 o de F3 y los precios por paquete) sí cambia, con la misma proporción que
/// tendría su menudeo, y los productos dados de alta aquí cambian completos.
/// </para>
/// </summary>
public sealed class RegistroPreciosDeVarios(PosDbContext db, bool lecturaDeEleventa = false)
{
    /// <summary>Al aplicar, si lo que se vio ya no es lo que quedaría (otro cambió un precio, entró o salió un producto).</summary>
    public const string CambioDesdeLaVista =
        "Algo cambió desde que se vio cómo quedan (un precio, o los productos que entran): revísalo otra vez antes de aplicar.";

    /// <summary>Lo que dice la bitácora al empezar: así se reconoce el renglón de un cambio de precios de varios.</summary>
    public const string EnLaBitacora = "Cambió el precio de ";

    private static readonly CultureInfo Mexico = CultureInfo.GetCultureInfo("es-MX");

    private DateTime Ahora => db.Reloj.GetUtcNow().UtcDateTime;

    /// <summary>Un precio que cambia, con el que queda.</summary>
    private sealed record Cambio(Precio Precio, long Despues);

    private sealed record Plan(VistaPreciosDeVariosDto Vista, List<Cambio> Cambios);

    /// <summary>Un producto elegido, con sus variantes activas (los precios son de cada variante).</summary>
    private sealed record Elegido(Guid Id, string Nombre, Guid DepartamentoId, List<(Guid Id, string Descripcion)> Variantes);

    /// <summary>Cómo quedan, sin cambiar nada.</summary>
    public async Task<VistaPreciosDeVariosDto> VistaAsync(SolicitudPreciosDeVarios s, CancellationToken ct = default)
    {
        await new ServicioControl(db).ExigirParaVerAsync(s.UsuarioId, s.SucursalId, AccionControlada.EditarCatalogo, ct);
        return (await PlanAsync(s, paraCambiar: false, ct)).Vista;
    }

    /// <summary>
    /// Aplica lo que se vio (<see cref="SolicitudPreciosDeVarios.Huella"/>), en una sola transacción. Si ya se aplicó con ese
    /// <see cref="SolicitudPreciosDeVarios.Id"/> (la respuesta se perdió y se vuelve a pedir), no lo hace otra vez.
    /// </summary>
    public async Task<PreciosDeVariosAplicadosDto> AplicarAsync(SolicitudPreciosDeVarios s, CancellationToken ct = default)
    {
        await new ServicioControl(db).ExigirDeLaSesionAsync(s.UsuarioId, s.SucursalId, null, AccionControlada.EditarCatalogo, ct);
        await using var transaccion = await db.Database.BeginTransactionAsync(ct);
        if (s.Id is { } id && await db.Bitacora.AsNoTracking().SingleOrDefaultAsync(b => b.Id == id, ct) is { } hecho)
        {
            if (hecho.Accion != AccionControlada.EditarCatalogo || !hecho.Detalle.StartsWith(EnLaBitacora, StringComparison.Ordinal))
                throw new ArgumentException("Ese cambio no es de precios: vuelve a ver cómo quedan.");
            return new PreciosDeVariosAplicadosDto(0, 0, hecho.Detalle, YaSeHabiaAplicado: true);
        }
        var plan = await PlanAsync(s, paraCambiar: true, ct);
        if (plan.Cambios.Count == 0)
            throw new InvalidOperationException("Con esa regla no cambia ningún precio.");
        if (string.IsNullOrEmpty(s.Huella))
            throw new InvalidOperationException("Primero ve cómo quedan.");
        if (s.Huella != plan.Vista.Huella)
            throw new InvalidOperationException(CambioDesdeLaVista);

        foreach (var cambio in plan.Cambios)
            cambio.Precio.PrecioCentavos = cambio.Despues;
        var productos = plan.Vista.Cambian.Count;
        var detalle = $"{EnLaBitacora}{(productos == 1 ? "1 producto" : $"{productos.ToString("#,##0", Mexico)} productos")} ({plan.Vista.Cuales}): "
            + plan.Vista.Regla;
        db.Bitacora.Add(new Bitacora
        {
            Id = s.Id ?? Ids.Nuevo(),
            SucursalId = s.SucursalId,
            UsuarioId = s.UsuarioId,
            Accion = AccionControlada.EditarCatalogo,
            Detalle = detalle,
            Resultado = ResultadoBitacora.Hecho,
            FechaUtc = Ahora,
        });
        await db.SaveChangesAsync(ct);
        await transaccion.CommitAsync(ct);
        return new PreciosDeVariosAplicadosDto(productos, plan.Cambios.Count, detalle);
    }

    /// <summary>
    /// Lo que quedaría: cada producto elegido con su menudeo nuevo y lo que cambia de su mayoreo, lo que no cambia y por qué,
    /// y la huella de los cambios. Con <paramref name="paraCambiar"/>, los precios quedan cargados para cambiarlos.
    /// </summary>
    private async Task<Plan> PlanAsync(SolicitudPreciosDeVarios s, bool paraCambiar, CancellationToken ct)
    {
        var regla = new ReglaDePrecios(s.Como, s.Valor, s.ConMayoreo, s.Redondeo);
        CambioDePrecios.Validar(regla);
        var (elegidos, cuales) = await ElegidosAsync(s, ct);
        var variantes = elegidos
            .SelectMany(p => p.Variantes.Select(v => (Producto: p, v.Id, Nombre: string.IsNullOrWhiteSpace(v.Descripcion) ? p.Nombre : $"{p.Nombre} {v.Descripcion}")))
            .OrderBy(v => v.Nombre, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
        if (variantes.Count > CambioDePrecios.Maximo)
            throw new ArgumentException(MuchosMensaje(variantes.Count));

        var ids = variantes.Select(v => v.Id).ToList();
        var consulta = db.Precios.Where(p => ids.Contains(p.VarianteId) && p.SucursalId == null);
        var precios = (paraCambiar ? await consulta.ToListAsync(ct) : await consulta.AsNoTracking().ToListAsync(ct)).ToLookup(p => p.VarianteId);
        var paquetes = await db.Presentaciones.AsNoTracking().Where(p => ids.Contains(p.VarianteId) && !p.EsBase).ToDictionaryAsync(p => p.Id, p => p.Nombre, ct);

        // El piloto con eleventa: el menudeo y las promociones de un producto de eleventa los pone su lectura.
        var deEleventa = lecturaDeEleventa ? await TrastiendaEnElPiloto.DeEleventaAsync(db, ids, ct) : [];
        var promociones = await TrastiendaEnElPiloto.PromocionesAsync(db, deEleventa.SelectMany(v => precios[v]).Select(p => p.Id), ct);
        bool PoneLaLectura(Precio p) => deEleventa.Contains(p.VarianteId) && (TrastiendaEnElPiloto.EsMenudeo(p) || promociones.Contains(p.Id));

        // El margen, solo para quien ve la utilidad: el menudeo contra el costo promedio, como en la lista de Productos.
        var conMargen = await new ServicioControl(db).PuedeAsync(s.UsuarioId, AccionControlada.VerUtilidad, ct);
        var costos = conMargen
            ? (await db.Existencias.AsNoTracking().Where(e => e.SucursalId == s.SucursalId && ids.Contains(e.VarianteId)).ToListAsync(ct))
                .Where(e => e.CostoPromedio is not null).ToDictionary(e => e.VarianteId, e => e.CostoPromedio!.Value)
            : [];
        var general = conMargen ? AlertasTelegram.Configuracion(db, s.SucursalId).MargenMinimoCentesimas : 0;
        var minimos = conMargen ? await db.Departamentos.AsNoTracking().ToDictionaryAsync(d => d.Id, d => d.MargenMinimoCentesimas, ct) : [];

        var cambian = new List<PrecioQueCambiaDto>();
        var noCambian = new List<PrecioQueNoCambiaDto>();
        var cambios = new List<Cambio>();
        foreach (var (producto, varianteId, nombre) in variantes)
        {
            PrecioQueNoCambiaDto NoCambia(PorQueNoCambia porQue) => new(producto.Id, varianteId, nombre, porQue);
            var suyos = precios[varianteId].ToList();
            var menudeo = suyos.Where(p => p.PresentacionId is null).MinBy(p => p.CantidadMinima);
            if (menudeo is not { PrecioCentavos: > 0 })
            {
                noCambian.Add(NoCambia(PorQueNoCambia.SinPrecio));
                continue;
            }
            // Su mayoreo cambia en la proporción de su menudeo, aunque en el piloto el menudeo de eleventa se cambie allá.
            var enEleventa = PoneLaLectura(menudeo);
            var proporcion = CambioDePrecios.ProporcionDe(regla, menudeo.PrecioCentavos);
            var nuevoMenudeo = enEleventa ? menudeo.PrecioCentavos : CambioDePrecios.Menudeo(regla, menudeo.PrecioCentavos);
            var mayoreo = regla.ConMayoreo
                ? suyos.Where(p => p != menudeo && p.PrecioCentavos > 0 && !PoneLaLectura(p))
                    .OrderBy(p => p.PresentacionId is not null).ThenBy(p => p.CantidadMinima)
                    .Select(p => (Precio: p, Despues: CambioDePrecios.EnProporcion(p.PrecioCentavos, proporcion, regla.Redondeo)))
                    .ToList()
                : [];
            if (nuevoMenudeo is not { } despues || mayoreo.Any(m => m.Despues is null))
            {
                noCambian.Add(NoCambia(PorQueNoCambia.QuedariaEnCero));
                continue;
            }
            var deEste = new List<Cambio>();
            if (despues != menudeo.PrecioCentavos)
                deEste.Add(new Cambio(menudeo, despues));
            deEste.AddRange(mayoreo.Where(m => m.Despues != m.Precio.PrecioCentavos).Select(m => new Cambio(m.Precio, m.Despues!.Value)));
            if (deEste.Count == 0)
            {
                noCambian.Add(NoCambia(enEleventa ? PorQueNoCambia.EnEleventa : PorQueNoCambia.QuedaIgual));
                continue;
            }
            cambios.AddRange(deEste);

            int? margenAntes = null, margenDespues = null, minimo = null;
            if (costos.TryGetValue(varianteId, out var costo))
                (margenAntes, margenDespues, minimo) = (Margen.Calcular(menudeo.PrecioCentavos, costo), Margen.Calcular(despues, costo),
                    minimos.GetValueOrDefault(producto.DepartamentoId) ?? general);
            cambian.Add(new PrecioQueCambiaDto(producto.Id, varianteId, nombre, menudeo.PrecioCentavos, despues,
                deEste.Where(c => c.Precio != menudeo).Select(c => new MayoreoQueCambiaDto(Cual(c.Precio, paquetes), c.Precio.PrecioCentavos, c.Despues)).ToList(),
                enEleventa, margenAntes, margenDespues, minimo));
        }

        var algunoDeEleventa = cambian.Any(c => c.MenudeoEnEleventa) || noCambian.Any(n => n.PorQue == PorQueNoCambia.EnEleventa);
        return new Plan(
            new VistaPreciosDeVariosDto(cuales, CambioDePrecios.Texto(regla), cambian, noCambian,
                algunoDeEleventa ? TrastiendaEnElPiloto.ProductosEnEleventa : null, conMargen, Huella(cambios)),
            cambios);
    }

    /// <summary>
    /// Los productos activos que se eligieron (sin los kits ni los cobros de terceros, como eleventa deja fuera sus kits), con
    /// sus variantes activas, y cómo se dice cuáles son.
    /// </summary>
    private async Task<(List<Elegido> Productos, string Cuales)> ElegidosAsync(SolicitudPreciosDeVarios s, CancellationToken ct)
    {
        var consulta = db.Productos.AsNoTracking().Where(p => p.Activo && (p.Tipo == TipoProducto.Mercancia || p.Tipo == TipoProducto.Servicio));
        string cuales;
        switch (s.Cuales)
        {
            case CualesProductos.Departamento:
                if (s.DepartamentoId is not { } departamentoId)
                    throw new ArgumentException("Elige el departamento.");
                var departamento = await db.Departamentos.AsNoTracking().SingleOrDefaultAsync(d => d.Id == departamentoId, ct)
                    ?? throw new ArgumentException("Ese departamento ya no existe.");
                consulta = consulta.Where(p => p.DepartamentoId == departamentoId);
                cuales = $"el departamento «{departamento.Nombre}»";
                break;
            case CualesProductos.Marca:
                if (string.IsNullOrWhiteSpace(s.Marca))
                    throw new ArgumentException("Elige la marca.");
                var normalizada = Texto.Normalizar(s.Marca);
                var marca = await db.Marcas.AsNoTracking().FirstOrDefaultAsync(m => m.NombreNormalizado == normalizada, ct)
                    ?? throw new ArgumentException($"No hay una marca «{s.Marca.Trim()}».");
                consulta = consulta.Where(p => p.MarcaId == marca.Id);
                cuales = $"la marca «{marca.Nombre}»";
                break;
            case CualesProductos.Busqueda:
                var texto = Texto.Normalizar(s.Texto ?? "");
                if (texto.Length == 0)
                    throw new ArgumentException("Escribe qué buscar: parte del nombre, la marca, el departamento o un código.");
                var coinciden = await CoincidenAsync(consulta, texto.Split(' ', StringSplitOptions.RemoveEmptyEntries), ct);
                consulta = consulta.Where(p => coinciden.Contains(p.Id));
                cuales = $"lo que coincide con «{s.Texto!.Trim()}»";
                break;
            case CualesProductos.Marcados:
                var marcados = (s.Productos ?? []).Distinct().ToList();
                if (marcados.Count == 0)
                    throw new ArgumentException("Marca en la lista los productos que vas a cambiar.");
                if (marcados.Count > CambioDePrecios.Maximo)
                    throw new ArgumentException(MuchosMensaje(marcados.Count));
                consulta = consulta.Where(p => marcados.Contains(p.Id));
                cuales = marcados.Count == 1 ? "el marcado en la lista" : "los marcados en la lista";
                break;
            default:
                throw new ArgumentException("Elige cuáles productos cambian.");
        }

        var productos = await consulta
            .Select(p => new { p.Id, p.Nombre, p.DepartamentoId, Variantes = p.Variantes.Where(v => v.Activa).Select(v => new { v.Id, v.Descripcion }).ToList() })
            .ToListAsync(ct);
        return (productos.Select(p => new Elegido(p.Id, p.Nombre, p.DepartamentoId, p.Variantes.Select(v => (v.Id, v.Descripcion)).ToList())).ToList(), cuales);
    }

    /// <summary>
    /// Los que coinciden con lo escrito como en la lista de Productos (<see cref="RegistroCatalogo.ListaAsync"/>): cada palabra,
    /// sin acentos ni mayúsculas, está en el nombre, la marca, el departamento o un código.
    /// </summary>
    private async Task<HashSet<Guid>> CoincidenAsync(IQueryable<Producto> consulta, string[] palabras, CancellationToken ct)
    {
        var productos = await consulta
            .Select(p => new
            {
                p.Id, p.Nombre, Marca = p.Marca == null ? null : p.Marca.Nombre, Departamento = p.Departamento!.Nombre,
                Variantes = p.Variantes.Select(v => v.Id).ToList(),
            })
            .ToListAsync(ct);
        var codigos = (await db.CodigosBarras.AsNoTracking().Select(c => new { c.Codigo, c.Presentacion!.VarianteId }).ToListAsync(ct))
            .ToLookup(c => c.VarianteId, c => c.Codigo);
        return productos
            .Where(p => Texto.Normalizar($"{p.Nombre} {p.Marca} {p.Departamento} {string.Join(" ", p.Variantes.SelectMany(v => codigos[v]))}") is var todo
                && palabras.All(todo.Contains))
            .Select(p => p.Id)
            .ToHashSet();
    }

    /// <summary>«desde 12», o el paquete con su precio propio: «Caja 12» (y desde cuántos, si es más de uno).</summary>
    private static string Cual(Precio precio, IReadOnlyDictionary<Guid, string> paquetes)
    {
        var desde = precio.CantidadMinima > Cantidad.Unidades(1) ? $"desde {precio.CantidadMinima}" : null;
        if (precio.PresentacionId is not { } paquete)
            return desde ?? "desde 1";
        var nombre = paquetes.GetValueOrDefault(paquete) ?? "Paquete";
        return desde is null ? nombre : $"{nombre} {desde}";
    }

    private static string MuchosMensaje(int cuantos) =>
        $"Son {cuantos.ToString("#,##0", Mexico)} productos: se cambian hasta {CambioDePrecios.Maximo.ToString("#,##0", Mexico)} a la vez. "
        + "Elige un departamento, una marca o una búsqueda más chica.";

    /// <summary>Lo que se vio: cada precio que cambia, con el de antes y el que queda. Al aplicar tiene que ser igual.</summary>
    private static string Huella(IEnumerable<Cambio> cambios)
    {
        var texto = string.Join("\n", cambios.Select(c => $"{c.Precio.Id:N}:{c.Precio.PrecioCentavos}:{c.Despues}").Order(StringComparer.Ordinal));
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(texto)))[..32];
    }
}
