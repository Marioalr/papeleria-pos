using PapeleriaPos.Eleventa;
using PapeleriaPos.Herramientas.Importacion;
using PapeleriaPos.Nucleo;
using PapeleriaPos.Nucleo.Catalogo;
using PapeleriaPos.Nucleo.Historico;
using PapeleriaPos.Nucleo.Importacion;
using PapeleriaPos.Nucleo.Inventario;

namespace PapeleriaPos.Herramientas.Eleventa;

/// <summary>Lo importado y lo que hubo que interpretar o dejar fuera, para revisarlo.</summary>
public sealed class ResultadoImportacion : LoteDeDatos
{
    /// <summary>Texto del aviso → cuántas veces pasó.</summary>
    public SortedDictionary<string, int> Avisos { get; } = new(StringComparer.Ordinal);

    internal void Avisar(string aviso, int veces = 1) => Avisos[aviso] = Avisos.GetValueOrDefault(aviso) + veces;
}

/// <summary>
/// Pasa los datos de eleventa al modelo del POS. Cada producto de eleventa queda como un producto con una sola
/// variante (agruparlas es un paso posterior). No lee la base: recibe las filas, así se prueba sin Firebird.
/// </summary>
public static class ImportadorEleventa
{
    public const string Origen = "eleventa";

    /// <summary>El Id de origen de lo vendido con un código que ya no está en el catálogo de eleventa: «codigo:» y el código.</summary>
    internal const string IdPorCodigo = "codigo:";

    /// <summary>
    /// El Id de origen del producto donde va todo lo vendido en eleventa como artículo común (2026-10-24): uno solo, inactivo. Cada
    /// común de eleventa tiene su propio código (COMUN-…), y antes cada uno se volvía un producto.
    /// </summary>
    internal const string IdArticuloComun = "comun";

    /// <summary>El nombre del producto de los artículos comunes de eleventa (<see cref="IdArticuloComun"/>).</summary>
    public const string ArticuloComun = "Artículo común de eleventa";

    public const string AvisoMayoreoDeducido = "Mayoreo de eleventa (F11) pasado a escala por cantidad: el paquete salió de sus ventas con F11";
    public const string AvisoMayoreoPorRevisar = "Mayoreo de eleventa (F11) por revisar en F3: no se pudo saber desde cuántas piezas";
    public const string AvisoMayoreoYaEstaba = "Mayoreo de eleventa (F11) igual a una promoción por cantidad: se quedó la promoción";
    public const string AvisoPromocionRepetida = "Promoción por cantidad repetida en eleventa: se importó una vez";

    public const string MotivoAlCosto = "El mayoreo está al costo o debajo: parece el precio del regreso a clases";
    public const string MotivoGranel = "Se vende a granel: captura la escala en metros o kilos";
    public const string MotivoChoca = "Choca con una promoción por cantidad de eleventa (el precio subiría con la cantidad)";
    public const string MotivoCodigoRepetido = "Su código está repetido en eleventa: sus ventas con F11 no se pueden separar";
    public const string AvisoUnidadKilo = ReglasEleventa.AvisoUnidadKilo;
    public const string AvisoFraccionEnPieza = ReglasEleventa.AvisoFraccionEnPieza;
    public const string AvisoExistenciaNegativa = "Existencia negativa en eleventa: no se importó";
    public const string AvisoTicketAbierto = "Ticket sin terminar (sin fecha de venta): no se importó";
    public const string AvisoCodigoInexistente = "Vendido con un código que ya no existe en el catálogo: se creó como producto inactivo";
    public const string AvisoLineaSinCantidad = "Línea con cantidad cero o negativa: no se importó";
    public const string AvisoPrecioCero = "Producto sin precio";

    /// <summary>
    /// Un ticket cobrado cuyo total no es la suma de sus artículos (una versión vieja de eleventa, o una línea sin cantidad que
    /// no se importó): la venta neta de los reportes lleva su total, y la diferencia sale aparte, «Sin desglose», en las ventas
    /// por departamento (revisión del PR #131, 2026-10-22).
    /// </summary>
    public const string AvisoTotalSinDesglose =
        "Ticket cuyo total no es la suma de sus artículos: en los reportes la diferencia sale como «Sin desglose»";

    /// <summary>Cómo empieza el código con que eleventa guarda un artículo común: «COMUN-», un número al azar y un contador.</summary>
    public const string PrefijoComun = "COMUN-";

    private const string ProveedorGenerico = "Sin proveedor (importado de eleventa)";

    public static ResultadoImportacion Importar(DatosEleventa e)
    {
        var r = new ResultadoImportacion();
        var matriz = r.Matriz = new Sucursal { Clave = "M", Nombre = "Matriz" };
        r.Sucursales.Add(matriz);

        // Departamentos
        var departamentos = new Dictionary<int, Departamento>();
        foreach (var d in e.Departamentos)
        {
            var departamento = new Departamento { Nombre = d.Nombre.Trim(), Activo = d.Activo };
            departamentos[d.Id] = departamento;
            r.Departamentos.Add(departamento);
            Mapear(r, nameof(Departamento), d.Id.ToString(), departamento.Id);
        }
        // Lo que no tiene departamento va al «- Sin Departamento -» de eleventa, si lo tiene; si no, a uno que se crea.
        var sinDepartamento = e.Departamentos.FirstOrDefault(d => ReglasEleventa.EsSinDepartamento(d.Nombre)) is { } suyo ? departamentos[suyo.Id] : null;
        Departamento DepartamentoDe(int? id)
        {
            if (id is { } i && departamentos.TryGetValue(i, out var d))
                return d;
            if (sinDepartamento is null)
            {
                sinDepartamento = new Departamento { Nombre = "Sin departamento" };
                r.Departamentos.Add(sinDepartamento);
            }
            return sinDepartamento;
        }

        var proveedor = new Proveedor { Nombre = ProveedorGenerico, Notas = "Eleventa no tenía proveedores capturados; guarda el último costo." };
        r.Proveedores.Add(proveedor);

        // Lo vendido o contado con fracciones decide si un producto "por pieza" en realidad es a granel.
        var conFracciones = e.Lineas.Where(l => !EsEntero(l.Cantidad)).Select(l => l.Codigo.Trim()).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var existencias = e.Existencias.GroupBy(x => x.ProductoId).ToDictionary(g => g.Key, g => g.Sum(x => x.Cantidad));
        foreach (var p in e.Productos)
        {
            if (existencias.TryGetValue(p.Id, out var cantidad) && !EsEntero(cantidad))
                conFracciones.Add(p.Codigo.Trim());
        }

        // Productos
        var porCodigo = new Dictionary<string, (Variante Variante, FilaProducto? Fila)>(StringComparer.OrdinalIgnoreCase);
        var porId = new Dictionary<int, Variante>();
        foreach (var p in e.Productos.OrderBy(p => p.Eliminado).ThenBy(p => p.Id))
        {
            var codigo = p.Codigo.Trim();
            var unidad = Unidad(p, conFracciones.Contains(codigo), r);
            var variante = CrearProducto(r, p.Descripcion.Trim(), DepartamentoDe(p.DepartamentoId), unidad, activo: !p.Eliminado,
                codigo: porCodigo.ContainsKey(codigo) ? null : codigo, idOrigen: p.Id.ToString());
            // Su mínimo y su máximo de «Productos bajos en inventario», en su unidad (la tienda los usa, base del 2026-10-24).
            ReglasEleventa.PonerMinimoYMaximo(variante, llevaInventario: true, p, aviso => r.Avisar(aviso));
            porId[p.Id] = variante;
            porCodigo.TryAdd(codigo, (variante, p));

            var precio = Centavos(p.Precio);
            if (precio > 0)
                r.Precios.Add(new Precio { VarianteId = variante.Id, CantidadMinima = Cantidad.Unidades(1), PrecioCentavos = precio });
            else
                r.Avisar(AvisoPrecioCero);

            if (p.Costo > 0)
                r.ProveedoresVariantes.Add(new ProveedorVariante
                {
                    ProveedorId = proveedor.Id,
                    VarianteId = variante.Id,
                    UltimoCostoCentavos = Centavos(p.Costo),
                });
        }

        // Promociones por cantidad: son escalas de mayoreo de verdad. Una escala repetida tumbaría el guardado.
        var escalas = new HashSet<(Guid Variante, long Desde)>();
        foreach (var promo in e.Promociones)
        {
            // Un DESDE de 1.0004 se redondea a 1: sería el menudeo otra vez.
            var desde = CantidadDe(promo.Desde);
            if (!porCodigo.TryGetValue(promo.Codigo.Trim(), out var v) || desde <= Cantidad.Unidades(1) || promo.PrecioConImpuestos <= 0)
                continue;
            if (!escalas.Add((v.Variante.Id, desde.Milesimas)))
            {
                r.Avisar(AvisoPromocionRepetida);
                continue;
            }
            r.Precios.Add(new Precio { VarianteId = v.Variante.Id, CantidadMinima = desde, PrecioCentavos = Centavos(promo.PrecioConImpuestos) });
        }

        ImportarMayoreo(e, r, porId, escalas);

        // Ventas
        var lineasPorTicket = e.Lineas.ToLookup(l => l.TicketId);
        var esComun = Comunes(e);
        // Los artículos comunes no son productos: todos van a uno solo, inactivo, en el departamento de los comunes de eleventa.
        var deComunes = e.Departamentos.FirstOrDefault(d => ReglasEleventa.EsDeComunes(d.Nombre)) is { } comunes ? departamentos[comunes.Id] : null;
        Variante? comun = null;
        Guid VarianteDe(FilaLinea l)
        {
            if (esComun(l))
                return (comun ??= CrearProducto(r, ArticuloComun, deComunes ?? DepartamentoDe(l.DepartamentoId), UnidadBase.PiezaAGranel, activo: false,
                    codigo: null, idOrigen: IdArticuloComun)).Id;
            var codigo = l.Codigo.Trim();
            if (!porCodigo.TryGetValue(codigo, out var v))
            {
                // Sin su producto no se sabe la medida: con fracciones, la pieza a granel (sin «kg»).
                var unidad = conFracciones.Contains(codigo) ? UnidadBase.PiezaAGranel : UnidadBase.Pieza;
                var nueva = CrearProducto(r, l.Nombre.Trim(), DepartamentoDe(l.DepartamentoId), unidad, activo: false, codigo, idOrigen: $"{IdPorCodigo}{codigo}");
                v = (nueva, null);
                porCodigo[codigo] = v;
                r.Avisar(AvisoCodigoInexistente);
            }
            return v.Variante.Id;
        }
        foreach (var t in e.Tickets.OrderBy(t => t.Id))
        {
            if (t.VendidoEn is null)
            {
                r.Avisar(AvisoTicketAbierto);
                continue;
            }

            var venta = Venta(t, lineasPorTicket[t.Id], matriz.Id, VarianteDe, esComun, aviso => r.Avisar(aviso));
            r.Ventas.Add(venta);
            Mapear(r, nameof(VentaHistorica), t.Id.ToString(), venta.Id);
        }

        // Existencias al momento de la copia: después de la última venta.
        var corte = r.Ventas.Count > 0 ? r.Ventas.Max(v => v.FechaUtc).AddSeconds(1) : DateTime.UtcNow;
        foreach (var p in e.Productos)
        {
            // Redondeada a la milésima antes de verla, como en la lectura: −0.0000012 de un FLOAT a granel es cero, no negativa.
            if (!existencias.TryGetValue(p.Id, out var cantidad) || CantidadDe(cantidad) is not { Milesimas: not 0 } existencia)
                continue;
            if (existencia.Milesimas < 0)
            {
                r.Avisar(AvisoExistenciaNegativa);
                continue;
            }

            var variante = porId[p.Id];
            var costo = p.Costo > 0 ? (long)Math.Round((decimal)p.Costo * 100 * existencia.EnUnidades) : 0;
            r.Movimientos.Add(Kardex.Entrada(matriz.Id, variante, TipoMovimiento.InventarioInicial, existencia, costo, corte,
                documentoTipo: Origen));
        }

        return r;
    }

    /// <summary>
    /// Un ticket cobrado de eleventa como venta del historial: su total (eleventa ya le bajó lo devuelto) y, de cada línea, la
    /// cantidad, el precio, el importe, el costo y si es un artículo común. Lo mismo para una base nueva que en la importación
    /// final (<see cref="ImportacionFinal"/>). Si el total de un ticket que no se canceló no es la suma de sus líneas, lo avisa
    /// (<see cref="AvisoTotalSinDesglose"/>).
    /// </summary>
    /// <param name="variante">La variante de cada línea (por su código); no se pide para las líneas sin cantidad.</param>
    /// <param name="esComun">Si una línea es un artículo común de eleventa (<see cref="Comunes"/>).</param>
    /// <param name="id">El de la venta que reemplaza, si ya estaba en el historial; si no, uno nuevo.</param>
    internal static VentaHistorica Venta(
        FilaTicket t, IEnumerable<FilaLinea> lineas, Guid sucursalId, Func<FilaLinea, Guid> variante, Func<FilaLinea, bool> esComun, Action<string> avisar,
        Guid? id = null)
    {
        var venta = new VentaHistorica
        {
            SucursalId = sucursalId,
            FechaUtc = HoraMexico.AUtc(t.VendidoEn ?? throw new ArgumentException("El ticket no se ha cobrado.", nameof(t))),
            FolioOrigen = (t.Folio ?? t.Id).ToString(),
            TotalCentavos = Centavos(t.Total),
            Cancelada = t.Cancelado,
        };
        if (id is { } propio)
            venta.Id = propio;

        foreach (var l in lineas)
        {
            var cantidad = CantidadDe(l.Cantidad);
            if (!cantidad.EsPositiva)
            {
                avisar(AvisoLineaSinCantidad);
                continue;
            }

            // En las líneas viejas, el precio de PRECIO_USADO (FilaLinea.Precio).
            var importe = Centavos(l.Importe);
            venta.Lineas.Add(new VentaHistoricaLinea
            {
                VentaHistoricaId = venta.Id,
                VarianteId = variante(l),
                Cantidad = cantidad,
                PrecioCentavos = Centavos(l.Precio),
                ImporteCentavos = importe,
                CostoCentavos = importe - Centavos(l.Ganancia * l.Cantidad),
                EsArticuloComun = esComun(l),
            });
        }
        if (!venta.Cancelada && venta.TotalCentavos != venta.Lineas.Sum(l => l.ImporteCentavos))
            avisar(AvisoTotalSinDesglose);
        return venta;
    }

    /// <summary>
    /// Si una línea de eleventa es un artículo común: su código empieza con <see cref="PrefijoComun"/> o, con un código que no
    /// es de ningún producto de eleventa, va en el departamento de los comunes («Productos Comunes»,
    /// <see cref="EnvioEleventa.DepartamentoComunes"/>). Un producto del catálogo en ese departamento sigue siendo producto.
    /// </summary>
    internal static Func<FilaLinea, bool> Comunes(DatosEleventa e)
    {
        var deComunes = e.Departamentos.Where(d => ReglasEleventa.EsDeComunes(d.Nombre)).Select(d => d.Id).ToHashSet();
        var delCatalogo = e.Productos.Select(p => p.Codigo.Trim()).ToHashSet(StringComparer.OrdinalIgnoreCase);
        return l => l.Codigo.Trim().StartsWith(PrefijoComun, StringComparison.OrdinalIgnoreCase)
            || (l.DepartamentoId is { } departamento && deComunes.Contains(departamento) && !delCatalogo.Contains(l.Codigo.Trim()));
    }

    /// <summary>
    /// El mayoreo de F11 (docs/fase-3-precios-clientes.md, entrega 1): en eleventa se escribían las piezas del paquete y
    /// se oprimía F11. De esas ventas sale el paquete y queda una escala desde ahí, al precio de mayoreo; lo que no se
    /// pueda deducir queda en <see cref="LoteDeDatos.MayoreoPorRevisar"/> para que el encargado lo capture en F3. Lo
    /// deducido también se anota (ya resuelto), para que una decisión de la depuración lo cambie al repetirse.
    /// </summary>
    private static void ImportarMayoreo(DatosEleventa e, ResultadoImportacion r, Dictionary<int, Variante> porId, HashSet<(Guid Variante, long Desde)> escalas)
    {
        var vendidos = e.Tickets.Where(t => t.VendidoEn is not null && !t.Cancelado).ToDictionary(t => t.Id, t => t.VendidoEn!.Value);
        var conF11 = e.Lineas.Where(l => l.UsaMayoreo && vendidos.ContainsKey(l.TicketId))
            .ToLookup(l => l.Codigo.Trim(), l => new VentaConMayoreo(l.Cantidad, vendidos[l.TicketId], l.Ganancia), StringComparer.OrdinalIgnoreCase);
        var activos = e.Productos.Where(p => !p.Eliminado).ToList();
        // Las ventas solo dicen el código: si dos productos lo comparten, no se sabe de cuál fue cada una.
        var repetidos = activos.GroupBy(p => p.Codigo.Trim(), StringComparer.OrdinalIgnoreCase).Where(g => g.Count() > 1)
            .Select(g => g.Key).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var fila in activos)
        {
            var variante = porId[fila.Id];
            var codigo = fila.Codigo.Trim();
            var menudeo = Centavos(fila.Precio);
            var mayoreo = Centavos(fila.Mayoreo);
            if (mayoreo <= 0 || mayoreo >= menudeo)
                continue;
            var costo = Centavos(fila.Costo);
            var repetido = repetidos.Contains(codigo);
            var deduccion = DeduccionMayoreo.Deducir(conF11[codigo]);
            var sugerida = repetido ? null : deduccion.Sugerida;

            string? motivo = costo > 0 && mayoreo <= costo ? MotivoAlCosto
                : variante.UnidadBase != UnidadBase.Pieza ? MotivoGranel
                : repetido ? MotivoCodigoRepetido
                : deduccion.Motivo;
            if (motivo is null && deduccion.Paquete is { } paquete)
            {
                if (escalas.Contains((variante.Id, paquete.Milesimas)))
                {
                    // Anotado, ya resuelto: la lectura del piloto sabe qué mayoreo tenía (2026-10-24) y no lo toma por nuevo.
                    var igual = Revisar(variante, menudeo, mayoreo, costo, deduccion, DeduccionMayoreo.Deducido, sugerida);
                    igual.Resolver($"Igual a la promoción por cantidad de eleventa desde {paquete}", DateTime.UtcNow);
                    r.MayoreoPorRevisar.Add(igual);
                    r.Avisar(AvisoMayoreoYaEstaba);
                    continue;
                }
                // Con las promociones, el precio por pieza no puede subir al subir la cantidad.
                if (Choca(r, variante, paquete, mayoreo))
                    (motivo, sugerida) = (MotivoChoca, null);
                else
                {
                    escalas.Add((variante.Id, paquete.Milesimas));
                    r.Precios.Add(new Precio { VarianteId = variante.Id, CantidadMinima = paquete, PrecioCentavos = mayoreo });
                    var deducido = Revisar(variante, menudeo, mayoreo, costo, deduccion, DeduccionMayoreo.Deducido, sugerida);
                    deducido.Resolver($"Desde {paquete} a {mayoreo / 100m:N2}, por sus ventas con F11", DateTime.UtcNow, paquete);
                    r.MayoreoPorRevisar.Add(deducido);
                    r.Avisar(AvisoMayoreoDeducido);
                    continue;
                }
            }

            // Lo sugerido no sirve si ahí ya hay una promoción (con otro precio) o si haría subir el precio con la cantidad.
            if (sugerida is { } s && (escalas.Contains((variante.Id, s.Milesimas)) || Choca(r, variante, s, mayoreo)))
                sugerida = null;
            r.MayoreoPorRevisar.Add(Revisar(variante, menudeo, mayoreo, costo, deduccion, motivo ?? DeduccionMayoreo.SinVentas, sugerida));
            r.Avisar(AvisoMayoreoPorRevisar);
        }
    }

    /// <summary>Si con una escala desde <paramref name="desde"/> el precio por pieza subiría al subir la cantidad.</summary>
    private static bool Choca(ResultadoImportacion r, Variante variante, Cantidad desde, long precioCentavos)
    {
        var todas = r.Precios.Where(p => p.VarianteId == variante.Id && p.PresentacionId == null && p.SucursalId == null)
            .Select(p => (p.CantidadMinima, p.PrecioCentavos)).Append((desde, precioCentavos)).OrderBy(x => x.Item1).ToList();
        return todas.Zip(todas.Skip(1)).Any(par => par.Second.Item2 > par.First.Item2);
    }

    private static MayoreoPorRevisar Revisar(
        Variante variante, long menudeo, long mayoreo, long costo, ResultadoMayoreo deduccion, string motivo, Cantidad? sugerida) =>
        new()
        {
            VarianteId = variante.Id,
            PrecioMayoreoCentavos = mayoreo,
            MenudeoCentavos = menudeo,
            CostoCentavos = costo,
            VentasConMayoreo = deduccion.Ventas,
            Cantidades = deduccion.Cantidades,
            Motivo = motivo,
            Sugerida = sugerida,
        };

    internal static Variante CrearProducto(
        ResultadoImportacion r, string nombre, Departamento departamento, UnidadBase unidad, bool activo, string? codigo, string idOrigen)
    {
        var producto = new Producto { Nombre = nombre, DepartamentoId = departamento.Id, Activo = activo };
        var variante = new Variante { ProductoId = producto.Id, UnidadBase = unidad, Activa = activo };
        var presentacion = new Presentacion
        {
            VarianteId = variante.Id,
            Nombre = ReglasEleventa.NombreDeLaPresentacion(unidad),
            Factor = Cantidad.Unidades(1),
            EsBase = true,
        };
        r.Productos.Add(producto);
        r.Variantes.Add(variante);
        r.Presentaciones.Add(presentacion);
        if (!string.IsNullOrEmpty(codigo))
            r.Codigos.Add(new CodigoBarras { Codigo = codigo, PresentacionId = presentacion.Id, Origen = CodigoBarras.OrigenDe(codigo) });
        Mapear(r, nameof(Producto), idOrigen, producto.Id);
        Mapear(r, nameof(Variante), idOrigen, variante.Id);
        return variante;
    }

    // Las mismas reglas que la lectura de cada pocos minutos (SincronizacionEleventa).
    private static UnidadBase Unidad(FilaProducto p, bool conFracciones, ResultadoImportacion r) => ReglasEleventa.Unidad(p, conFracciones, aviso => r.Avisar(aviso));

    private static bool EsEntero(double cantidad) => ReglasEleventa.EsEntero(cantidad);

    private static long Centavos(double pesos) => ReglasEleventa.Centavos(pesos);

    /// <summary>Toda cantidad de eleventa (la existencia, lo vendido, las promociones, el mínimo) con la misma conversión.</summary>
    private static Cantidad CantidadDe(double cantidad) => ReglasEleventa.CantidadDe(cantidad);

    internal static void Mapear(ResultadoImportacion r, string entidad, string idOrigen, Guid idNuevo) =>
        r.Mapeos.Add(new MapeoImportacion { Origen = Origen, Entidad = entidad, IdOrigen = idOrigen, IdNuevo = idNuevo });
}
