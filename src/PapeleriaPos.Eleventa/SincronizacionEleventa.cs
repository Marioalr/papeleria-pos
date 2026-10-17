using Microsoft.EntityFrameworkCore;
using PapeleriaPos.Datos;
using PapeleriaPos.Nucleo;
using PapeleriaPos.Nucleo.Catalogo;
using PapeleriaPos.Nucleo.Importacion;
using PapeleriaPos.Nucleo.Inventario;
using PapeleriaPos.Nucleo.Ventas;

namespace PapeleriaPos.Eleventa;

/// <summary>Lo que cambió en una lectura de eleventa, y lo que no se pudo tomar (para revisarlo).</summary>
public sealed class ResultadoSincronizacion
{
    /// <summary>Productos dados de alta en eleventa desde la lectura anterior.</summary>
    public int Nuevos { get; set; }

    /// <summary>Nombre, departamento, código o reactivados.</summary>
    public int Cambiados { get; set; }

    /// <summary>Borrados (o desaparecidos) en eleventa: aquí quedan inactivos.</summary>
    public int Desactivados { get; set; }

    /// <summary>Precios de menudeo y promociones por cantidad que cambiaron, se agregaron o se quitaron.</summary>
    public int Precios { get; set; }

    public int Costos { get; set; }

    /// <summary>Existencias ajustadas a lo que dice eleventa (con lo que esta caja movió y allá todavía no está).</summary>
    public int Existencias { get; set; }

    public int Departamentos { get; set; }

    /// <summary>Texto del aviso → cuántas veces pasó.</summary>
    public SortedDictionary<string, int> Avisos { get; } = new(StringComparer.Ordinal);

    public bool HuboCambios => Nuevos + Cambiados + Desactivados + Precios + Costos + Existencias + Departamentos > 0;

    internal void Avisar(string aviso) => Avisos[aviso] = Avisos.GetValueOrDefault(aviso) + 1;

    public override string ToString() =>
        HuboCambios
            ? string.Join(", ", new[]
              {
                  (Nuevos, "nuevos"), (Cambiados, "cambiados"), (Desactivados, "desactivados"), (Precios, "precios"), (Costos, "costos"),
                  (Existencias, "existencias"), (Departamentos, "departamentos"),
              }.Where(x => x.Item1 > 0).Select(x => $"{x.Item1} {x.Item2}"))
            : "sin cambios";
}

/// <summary>
/// Pone al día la base de la caja 2 con lo que dice eleventa (docs/plan.md, el piloto, paso 1). Durante el piloto
/// eleventa manda: las altas, los cambios de precio y las existencias se hacen allá y llegan aquí con cada lectura. La
/// base ya se armó con importar-eleventa; esto la sigue, con los mismos mapeos (el Id de eleventa de cada producto).
/// Lo que se dio de alta aquí y no viene de eleventa no se toca, ni lo que esta caja vendió o devolvió y todavía no está
/// en eleventa. No lee ni escribe nada en eleventa: recibe las filas.
/// </summary>
public static class SincronizacionEleventa
{
    public const string Origen = "eleventa";

    /// <summary>El documento de los ajustes de existencia en el kardex (lo importado al principio dice «eleventa»).</summary>
    public const string Documento = "lectura-eleventa";

    private const string EntidadPromocion = "Promocion";
    private const string SinDepartamento = "Sin departamento";
    private const string ProveedorGenerico = "Sin proveedor (importado de eleventa)";

    public const string AvisoCodigoOcupado = "Código de eleventa que aquí ya tiene otro producto: se dejó sin cambiar";
    public const string AvisoUnidadCambiada = "Cambió la unidad en eleventa (pieza, metro, kilo): revisar en F3, no se cambió sola";
    public const string AvisoFraccionEnPieza = "Existencia con fracciones en un producto por pieza: no se ajustó";
    public const string AvisoJuntada = "Producto juntado con otro en la depuración: no se toca";
    public const string AvisoAgrupado = "Producto con varias variantes aquí: su nombre no se cambia desde eleventa";
    public const string AvisoDesaparecido = "Producto que ya no está en eleventa: quedó inactivo";
    public const string AvisoPrecioCero = "Producto nuevo de eleventa sin precio";
    public const string AvisoMayoreoNuevo = "Producto nuevo de eleventa con mayoreo (F11): revisar en F3 desde cuántas piezas";
    public const string MotivoMayoreoNuevo = "Nuevo en eleventa: todavía no hay ventas con F11 para saber desde cuántas piezas";
    public const string AvisoEnCamino = "Existencia sin ajustar: una venta o devolución de esta caja va a eleventa y no se sabe si ya llegó (se ajusta en otra lectura)";

    /// <param name="mandados">
    /// Lo que la bandeja mandó a eleventa mientras se leía (o se estaba mandando), o se anotó a mano en ese rato: pudo
    /// haber entrado allá sin que aquí se sepa todavía, y la existencia de sus productos se ajusta en otra lectura. También
    /// dice qué rechazó eleventa en su último intento. Sin ella, de lo pendiente que ya se intentó no se sabe si entró.
    /// </param>
    public static async Task<ResultadoSincronizacion> AplicarAsync(PosDbContext db, DatosEleventa e, DateTime ahoraUtc,
        EnviosEnCurso.Vigilancia? mandados = null, CancellationToken ct = default)
    {
        var r = new ResultadoSincronizacion();
        var sucursal = await db.Sucursales.OrderBy(s => s.Clave != "M").ThenBy(s => s.Nombre).FirstAsync(ct);
        var mapeos = await db.MapeosImportacion.Where(m => m.Origen == Origen).ToListAsync(ct);
        MapeoImportacion Mapear(string entidad, string idOrigen, Guid idNuevo)
        {
            var mapeo = new MapeoImportacion { Origen = Origen, Entidad = entidad, IdOrigen = idOrigen, IdNuevo = idNuevo };
            db.MapeosImportacion.Add(mapeo);
            mapeos.Add(mapeo);
            return mapeo;
        }

        // Departamentos
        var departamentos = await db.Departamentos.ToDictionaryAsync(d => d.Id, ct);
        var deptoDe = mapeos.Where(m => m.Entidad == nameof(Departamento)).GroupBy(m => m.IdOrigen).ToDictionary(g => g.Key, g => g.First().IdNuevo);
        foreach (var fila in e.Departamentos)
        {
            var nombre = fila.Nombre.Trim();
            if (deptoDe.TryGetValue(fila.Id.ToString(), out var id) && departamentos.TryGetValue(id, out var departamento))
            {
                if (departamento.Nombre == nombre && departamento.Activo == fila.Activo)
                    continue;
                (departamento.Nombre, departamento.Activo) = (nombre, fila.Activo);
            }
            else
            {
                departamento = new Departamento { Nombre = nombre, Activo = fila.Activo };
                db.Departamentos.Add(departamento);
                departamentos[departamento.Id] = departamento;
                deptoDe[fila.Id.ToString()] = departamento.Id;
                Mapear(nameof(Departamento), fila.Id.ToString(), departamento.Id);
            }
            r.Departamentos++;
        }
        Guid DepartamentoDe(int? idEleventa)
        {
            if (idEleventa is { } i && deptoDe.TryGetValue(i.ToString(), out var id) && departamentos.ContainsKey(id))
                return id;
            if (departamentos.Values.FirstOrDefault(d => d.Nombre == SinDepartamento) is not { } sin)
            {
                sin = new Departamento { Nombre = SinDepartamento };
                db.Departamentos.Add(sin);
                departamentos[sin.Id] = sin;
            }
            return sin.Id;
        }

        // El catálogo, completo y con seguimiento: se compara todo y se guarda lo que cambió.
        var productos = await db.Productos.ToDictionaryAsync(p => p.Id, ct);
        var variantes = await db.Variantes.ToDictionaryAsync(v => v.Id, ct);
        var porProducto = variantes.Values.GroupBy(v => v.ProductoId).ToDictionary(g => g.Key, g => g.Count());
        var bases = (await db.Presentaciones.Where(p => p.EsBase).ToListAsync(ct)).GroupBy(p => p.VarianteId).ToDictionary(g => g.Key, g => g.First());
        var codigos = await db.CodigosBarras.ToListAsync(ct);
        var porCodigo = codigos.GroupBy(c => c.Codigo, StringComparer.OrdinalIgnoreCase).ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);
        var codigosDe = codigos.ToLookup(c => c.PresentacionId);
        var precios = await db.Precios.Where(p => p.PresentacionId == null && p.SucursalId == null).ToListAsync(ct);
        var precioDe = precios.ToLookup(p => p.VarianteId);
        var proveedor = await db.Proveedores.FirstOrDefaultAsync(p => p.Nombre == ProveedorGenerico, ct)
            ?? db.Proveedores.Add(new Proveedor { Nombre = ProveedorGenerico, Notas = "Eleventa no tenía proveedores capturados; guarda el último costo." }).Entity;
        var costos = await db.ProveedoresVariantes.Where(pv => pv.ProveedorId == proveedor.Id).ToDictionaryAsync(pv => pv.VarianteId, ct);
        var varianteDe = mapeos.Where(m => m.Entidad == nameof(Variante) && int.TryParse(m.IdOrigen, out _))
            .GroupBy(m => int.Parse(m.IdOrigen)).ToDictionary(g => g.Key, g => g.First().IdNuevo);

        var existenciaDe = e.Existencias.GroupBy(x => x.ProductoId).ToDictionary(g => g.Key, g => g.Sum(x => x.Cantidad));
        // Un código repetido en eleventa es del primero (los activos antes, luego el más viejo), como al importar.
        var filas = e.Productos.OrderBy(p => p.Eliminado).ThenBy(p => p.Id).ToList();
        var duenoDelCodigo = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var fila in filas)
            duenoDelCodigo.TryAdd(fila.Codigo.Trim(), fila.Id);
        var activas = new Dictionary<int, Variante>();
        // Las dadas de alta en esta lectura, con su costo: su existencia entra como inventario inicial.
        var nuevas = new Dictionary<Guid, long>();

        foreach (var fila in filas)
        {
            var codigo = fila.Codigo.Trim();
            var esDueno = codigo.Length > 0 && duenoDelCodigo[codigo] == fila.Id;
            var unidad = ReglasEleventa.Unidad(fila, existenciaDe.TryGetValue(fila.Id, out var cantidad) && !ReglasEleventa.EsEntero(cantidad), _ => { });
            var activo = !fila.Eliminado;

            if (!varianteDe.TryGetValue(fila.Id, out var varianteId) || !variantes.TryGetValue(varianteId, out var variante)
                || !productos.TryGetValue(variante.ProductoId, out var producto))
            {
                // Lo borrado antes de que existiera aquí no se da de alta.
                if (fila.Eliminado)
                    continue;
                variante = Alta(fila, unidad, DepartamentoDe(fila.DepartamentoId), esDueno ? codigo : null);
                activas[fila.Id] = variante;
                r.Nuevos++;
                continue;
            }
            if (variante.JuntadaEnId is not null)
            {
                r.Avisar(AvisoJuntada);
                continue;
            }
            activas[fila.Id] = variante;

            // Si la depuración ya juntó varias variantes en un producto, el producto no es de una sola fila de eleventa.
            var unica = porProducto.GetValueOrDefault(producto.Id) == 1;
            var cambio = false;
            var nombre = fila.Descripcion.Trim();
            if (producto.Nombre != nombre)
            {
                if (unica)
                    (producto.Nombre, cambio) = (nombre, true);
                else
                    r.Avisar(AvisoAgrupado);
            }
            var departamentoId = DepartamentoDe(fila.DepartamentoId);
            if (unica && producto.DepartamentoId != departamentoId)
                (producto.DepartamentoId, cambio) = (departamentoId, true);
            if (variante.Activa != activo || (unica && producto.Activo != activo))
            {
                variante.Activa = activo;
                if (unica)
                    producto.Activo = activo;
                if (activo)
                    cambio = true;
                else
                    r.Desactivados++;
            }
            if (esDueno && bases.TryGetValue(variante.Id, out var presentacion) && CambiarCodigo(presentacion, codigo))
                cambio = true;
            // Lo importado a granel por sus ventas con fracciones dice «por pieza» en eleventa: eso no es un cambio.
            if (fila.TipoVenta == 'D' && variante.UnidadBase == UnidadBase.Pieza)
                r.Avisar(AvisoUnidadCambiada);
            if (cambio)
                r.Cambiados++;

            var menudeo = ReglasEleventa.Centavos(fila.Precio);
            if (menudeo > 0)
            {
                var actual = precioDe[variante.Id].FirstOrDefault(p => p.CantidadMinima == Cantidad.Unidades(1));
                if (actual is null)
                {
                    db.Precios.Add(new Precio { VarianteId = variante.Id, CantidadMinima = Cantidad.Unidades(1), PrecioCentavos = menudeo });
                    r.Precios++;
                }
                else if (actual.PrecioCentavos != menudeo)
                {
                    actual.PrecioCentavos = menudeo;
                    r.Precios++;
                }
            }
            var costo = ReglasEleventa.Centavos(fila.Costo);
            if (costo > 0)
            {
                if (costos.TryGetValue(variante.Id, out var pv))
                {
                    if (pv.UltimoCostoCentavos != costo)
                        (pv.UltimoCostoCentavos, r.Costos) = (costo, r.Costos + 1);
                }
                else
                {
                    pv = new ProveedorVariante { ProveedorId = proveedor.Id, VarianteId = variante.Id, UltimoCostoCentavos = costo };
                    db.ProveedoresVariantes.Add(pv);
                    costos[variante.Id] = pv;
                    r.Costos++;
                }
            }
        }

        // Lo que ya no viene de eleventa (borrado de la base, no solo marcado): inactivo, sin borrar sus ventas.
        var enEleventa = e.Productos.Select(p => p.Id).ToHashSet();
        foreach (var (idEleventa, varianteId) in varianteDe)
        {
            if (enEleventa.Contains(idEleventa) || !variantes.TryGetValue(varianteId, out var variante) || !variante.Activa || variante.JuntadaEnId is not null)
                continue;
            variante.Activa = false;
            if (productos.TryGetValue(variante.ProductoId, out var producto) && porProducto.GetValueOrDefault(producto.Id) == 1)
                producto.Activo = false;
            r.Desactivados++;
            r.Avisar(AvisoDesaparecido);
        }

        Promociones(db, e, r, activas, duenoDelCodigo, precioDe, mapeos, Mapear);
        await db.SaveChangesAsync(ct);

        // Las existencias, con la base apartada para escribir desde antes de leerlas (SQLite: BEGIN IMMEDIATE): una venta que
        // se cobre en ese momento espera unos milisegundos y no se pierde ni el ajuste ni la venta. La bandeja se lee en el
        // mismo momento que las existencias, y lo que se mandó mientras se leía eleventa, hasta aquí.
        await using (var propia = db.Database.CurrentTransaction is null ? await db.Database.BeginTransactionAsync(ct) : null)
        {
            // Las devoluciones de aquí cuya anotación a mano quizá ya está en eleventa (la vuelta la retuvo): sin ajustar.
            var conAnotacion = e.Piloto?.Devoluciones is { Count: > 0 } devoluciones
                ? await DevolucionesConEleventa.ConAnotacionRetenidaAsync(db, devoluciones, ct)
                : [];
            var bandeja = await BandejaAsync(db, sucursal.Id, mandados?.Mandados() ?? new HashSet<Guid>(),
                mandados?.Rechazados() ?? new HashSet<Guid>(), conAnotacion, ct);
            await ExistenciasAsync(db, r, sucursal.Id, activas, nuevas, existenciaDe, productos, bandeja, ahoraUtc, ct);
            await db.SaveChangesAsync(ct);
            if (propia is not null)
                await propia.CommitAsync(ct);
        }
        return r;

        Variante Alta(FilaProducto fila, UnidadBase unidad, Guid departamentoId, string? codigo)
        {
            var producto = new Producto { Nombre = fila.Descripcion.Trim(), DepartamentoId = departamentoId };
            var variante = new Variante { ProductoId = producto.Id, UnidadBase = unidad };
            var presentacion = new Presentacion { VarianteId = variante.Id, Nombre = unidad.ToString(), Factor = Cantidad.Unidades(1), EsBase = true };
            db.Productos.Add(producto);
            db.Variantes.Add(variante);
            db.Presentaciones.Add(presentacion);
            productos[producto.Id] = producto;
            variantes[variante.Id] = variante;
            porProducto[producto.Id] = 1;
            bases[variante.Id] = presentacion;
            if (codigo is not null)
            {
                if (porCodigo.ContainsKey(codigo))
                    r.Avisar(AvisoCodigoOcupado);
                else
                {
                    var nuevo = new CodigoBarras { Codigo = codigo, PresentacionId = presentacion.Id, Origen = ReglasEleventa.OrigenDe(codigo) };
                    db.CodigosBarras.Add(nuevo);
                    porCodigo[codigo] = nuevo;
                }
            }
            var menudeo = ReglasEleventa.Centavos(fila.Precio);
            if (menudeo > 0)
                db.Precios.Add(new Precio { VarianteId = variante.Id, CantidadMinima = Cantidad.Unidades(1), PrecioCentavos = menudeo });
            else
                r.Avisar(AvisoPrecioCero);
            var costo = ReglasEleventa.Centavos(fila.Costo);
            nuevas[variante.Id] = costo;
            if (costo > 0)
            {
                var pv = new ProveedorVariante { ProveedorId = proveedor.Id, VarianteId = variante.Id, UltimoCostoCentavos = costo };
                db.ProveedoresVariantes.Add(pv);
                costos[variante.Id] = pv;
            }
            var mayoreo = ReglasEleventa.Centavos(fila.Mayoreo);
            if (mayoreo > 0 && mayoreo < menudeo)
            {
                db.MayoreoPorRevisar.Add(new MayoreoPorRevisar
                {
                    VarianteId = variante.Id, PrecioMayoreoCentavos = mayoreo, MenudeoCentavos = menudeo, CostoCentavos = costo, Motivo = MotivoMayoreoNuevo,
                });
                r.Avisar(AvisoMayoreoNuevo);
            }
            Mapear(nameof(Producto), fila.Id.ToString(), producto.Id);
            Mapear(nameof(Variante), fila.Id.ToString(), variante.Id);
            varianteDe[fila.Id] = variante.Id;
            return variante;
        }

        // El código de eleventa es el de su presentación base: si cambió allá, cambia aquí (si no lo tiene otro producto).
        bool CambiarCodigo(Presentacion presentacion, string codigo)
        {
            var propios = codigosDe[presentacion.Id].ToList();
            if (propios.Any(c => string.Equals(c.Codigo, codigo, StringComparison.OrdinalIgnoreCase)))
                return false;
            if (porCodigo.ContainsKey(codigo))
            {
                r.Avisar(AvisoCodigoOcupado);
                return false;
            }
            if (propios.Count == 1)
            {
                porCodigo.Remove(propios[0].Codigo);
                (propios[0].Codigo, propios[0].Origen) = (codigo, ReglasEleventa.OrigenDe(codigo));
                porCodigo[codigo] = propios[0];
                return true;
            }
            var nuevo = new CodigoBarras { Codigo = codigo, PresentacionId = presentacion.Id, Origen = ReglasEleventa.OrigenDe(codigo) };
            db.CodigosBarras.Add(nuevo);
            porCodigo[codigo] = nuevo;
            return true;
        }
    }

    /// <summary>
    /// Las promociones por cantidad de eleventa son escalas de mayoreo. Cada una queda anotada (su producto y desde
    /// cuánto) para saber cuál quitar si la quitan allá; las que ya estaban de la importación se adoptan la primera vez.
    /// Las demás escalas (las del F11 deducidas, las capturadas en F3) no se tocan.
    /// </summary>
    private static void Promociones(PosDbContext db, DatosEleventa e, ResultadoSincronizacion r, Dictionary<int, Variante> activas,
        Dictionary<string, int> duenoDelCodigo, ILookup<Guid, Precio> precioDe, List<MapeoImportacion> mapeos,
        Func<string, string, Guid, MapeoImportacion> mapear)
    {
        var anotadas = mapeos.Where(m => m.Entidad == EntidadPromocion).ToDictionary(m => m.IdOrigen);
        var vigentes = new HashSet<string>();
        foreach (var promo in e.Promociones)
        {
            var desde = ReglasEleventa.CantidadDe(promo.Desde);
            var precio = ReglasEleventa.Centavos(promo.PrecioConImpuestos);
            if (!duenoDelCodigo.TryGetValue(promo.Codigo.Trim(), out var idEleventa) || !activas.TryGetValue(idEleventa, out var variante)
                || desde <= Cantidad.Unidades(1) || precio <= 0)
                continue;
            var clave = $"{idEleventa}@{desde.Milesimas}";
            if (!vigentes.Add(clave))
                continue;
            var escala = anotadas.TryGetValue(clave, out var mapeo)
                ? precioDe[variante.Id].FirstOrDefault(p => p.Id == mapeo.IdNuevo)
                : precioDe[variante.Id].FirstOrDefault(p => p.CantidadMinima == desde);
            if (escala is null)
            {
                escala = new Precio { VarianteId = variante.Id, CantidadMinima = desde, PrecioCentavos = precio };
                db.Precios.Add(escala);
                r.Precios++;
            }
            else if (escala.PrecioCentavos != precio)
            {
                escala.PrecioCentavos = precio;
                r.Precios++;
            }
            if (mapeo is null)
                mapear(EntidadPromocion, clave, escala.Id);
            else if (mapeo.IdNuevo != escala.Id)
                mapeo.IdNuevo = escala.Id;
        }
        foreach (var (clave, mapeo) in anotadas)
        {
            if (vigentes.Contains(clave))
                continue;
            if (precioDe.SelectMany(g => g).FirstOrDefault(p => p.Id == mapeo.IdNuevo) is { } quitada)
            {
                db.Precios.Remove(quitada);
                r.Precios++;
            }
            db.MapeosImportacion.Remove(mapeo);
        }
    }

    /// <summary>
    /// La existencia de cada producto queda en lo que dice eleventa, más lo que esta caja movió y eleventa todavía no tiene
    /// (<see cref="BandejaAsync"/>), con un ajuste en el kardex (al costo promedio, como la diferencia de un conteo) a
    /// nombre de «eleventa». Lo que eleventa no lleva en inventario cuenta como cero. Un producto nuevo entra como
    /// inventario inicial, al costo de eleventa (como al importar).
    /// </summary>
    private static async Task ExistenciasAsync(PosDbContext db, ResultadoSincronizacion r, Guid sucursalId, Dictionary<int, Variante> activas,
        Dictionary<Guid, long> nuevas, Dictionary<int, double> existenciaDe, Dictionary<Guid, Producto> productos, Bandeja bandeja, DateTime ahoraUtc,
        CancellationToken ct)
    {
        var registro = new RegistroInventario(db);
        var actuales = await db.Existencias.Where(x => x.SucursalId == sucursalId).ToDictionaryAsync(x => x.VarianteId, ct);
        var corrida = Ids.Nuevo();
        foreach (var (idEleventa, variante) in activas)
        {
            if (!productos.TryGetValue(variante.ProductoId, out var producto) || !producto.LlevaInventario)
                continue;
            if (bandeja.EnCamino.Contains(variante.Id))
            {
                r.Avisar(AvisoEnCamino);
                continue;
            }
            var deseada = ReglasEleventa.CantidadDe(existenciaDe.GetValueOrDefault(idEleventa)) + bandeja.SinLlegar.GetValueOrDefault(variante.Id);
            var diferencia = deseada - (actuales.TryGetValue(variante.Id, out var actual) ? actual.Cantidad : Cantidad.Cero);
            if (diferencia.Milesimas == 0)
                continue;
            if (!variante.AdmiteCantidad(diferencia.Abs()))
            {
                r.Avisar(AvisoFraccionEnPieza);
                continue;
            }
            if (nuevas.TryGetValue(variante.Id, out var costo) && diferencia.EsPositiva)
                await registro.AgregarAsync(Kardex.Entrada(sucursalId, variante, TipoMovimiento.InventarioInicial, diferencia, diferencia.Importe(costo),
                    ahoraUtc, documentoTipo: Documento, documentoId: corrida), ct);
            else
                await registro.AgregarDiferenciaDeConteoAsync(sucursalId, variante, diferencia, ahoraUtc, Documento, corrida, ct);
            r.Existencias++;
        }
    }

    /// <param name="SinLlegar">Lo que esta caja movió de cada variante y eleventa no tiene, con su signo (una venta resta).</param>
    /// <param name="EnCamino">Las variantes de algo que pudo haber entrado a eleventa sin que aquí se sepa todavía.</param>
    private sealed record Bandeja(Dictionary<Guid, Cantidad> SinLlegar, HashSet<Guid> EnCamino);

    /// <summary>
    /// Lo que eleventa no tiene de esta caja, para que la lectura no lo deshaga: las ventas, cancelaciones y devoluciones
    /// de la bandeja que siguen pendientes (todavía no llegan), las que quedaron por revisar (no van solas: hasta que
    /// alguien las anote a mano allá) y las descartadas (una venta que se canceló antes de llegar, y su cancelación: allá
    /// nunca existieron). Lo que movieron es lo de su kardex: lo vendido y lo devuelto, también lo que volvió dañado.
    /// Lo enviado y lo anotado a mano ya está en eleventa.
    /// <para>
    /// No se sabe si ya está allá lo que se mandó o se anotó a mano mientras se leía eleventa (<paramref name="mandados"/>),
    /// ni lo pendiente cuyo último intento falló por la red (se pudo cortar justo después de que eleventa lo guardó; el
    /// siguiente intento lo encuentra por su marca), o del que no se sabe cómo falló (al arrancar el servicio, hasta que
    /// se vuelve a intentar): sus variantes se ajustan en otra lectura. Lo que eleventa rechazó (<paramref name="rechazados"/>)
    /// no está allá, aunque la bandeja siga detenida en eso. Tampoco se sabe de una devolución de aquí que espera su anotación
    /// a mano y ya tiene en eleventa una de su ticket que puede serlo (<paramref name="conAnotacion"/>): sumarla sería
    /// contarla dos veces si lo es (revisión del 2026-10-17); se ajusta cuando se elige.
    /// </para>
    /// </summary>
    private static async Task<Bandeja> BandejaAsync(PosDbContext db, Guid sucursalId, IReadOnlySet<Guid> mandados, IReadOnlySet<Guid> rechazados,
        IReadOnlySet<Guid> conAnotacion, CancellationToken ct)
    {
        var vistos = mandados.ToList();
        var envios = await db.EnviosAEleventa.AsNoTracking()
            .Where(x => x.Tipo == TipoEnvioEleventa.Venta || x.Tipo == TipoEnvioEleventa.Cancelacion || x.Tipo == TipoEnvioEleventa.Devolucion
                || x.Tipo == TipoEnvioEleventa.DevolucionDeEleventa)
            .Where(x => x.Estado == EstadoEnvioEleventa.Pendiente || x.Estado == EstadoEnvioEleventa.PorRevisar || x.Estado == EstadoEnvioEleventa.Descartada
                || vistos.Contains(x.Id))
            .Select(x => new
            {
                x.Id, x.Estado, x.Intentos,
                // La devolución de un ticket de eleventa (la ida) movió el kardex con su propio documento.
                Documento = x.Tipo == TipoEnvioEleventa.Venta ? x.VentaId : x.Tipo == TipoEnvioEleventa.DevolucionDeEleventa ? x.DevolucionDeEleventaId : x.DevolucionId,
            })
            .ToListAsync(ct);
        var bandeja = new Bandeja([], []);
        if (envios.Count == 0)
            return bandeja;

        var enCamino = envios.Where(x => mandados.Contains(x.Id)
                || x.Estado == EstadoEnvioEleventa.Pendiente && x.Intentos > 0 && !rechazados.Contains(x.Id)
                || x.Documento is { } documento && conAnotacion.Contains(documento))
            .Select(x => x.Documento).ToHashSet();
        var documentos = envios.Where(x => x.Documento is not null).Select(x => x.Documento!.Value).Distinct().ToList();
        var movimientos = await db.MovimientosInventario.AsNoTracking()
            .Where(m => m.SucursalId == sucursalId
                && (m.DocumentoTipo == nameof(Venta) || m.DocumentoTipo == nameof(Devolucion) || m.DocumentoTipo == nameof(DevolucionDeEleventa))
                && m.DocumentoId != null && documentos.Contains(m.DocumentoId.Value))
            .Select(m => new { m.DocumentoId, m.VarianteId, m.Cantidad })
            .ToListAsync(ct);
        foreach (var m in movimientos)
        {
            if (enCamino.Contains(m.DocumentoId))
                bandeja.EnCamino.Add(m.VarianteId);
            else
                bandeja.SinLlegar[m.VarianteId] = bandeja.SinLlegar.GetValueOrDefault(m.VarianteId) + m.Cantidad;
        }
        return bandeja;
    }
}
