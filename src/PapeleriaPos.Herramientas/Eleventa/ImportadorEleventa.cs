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

    public const string AvisoMayoreo = "Precio de mayoreo no importado: en eleventa se activa a mano en la venta, no por cantidad";
    public const string AvisoUnidadKilo = "Granel con medida KG en eleventa (la medida está desactivada): revisar la unidad";
    public const string AvisoFraccionEnPieza = "Por pieza en eleventa pero vendido o contado con fracciones: se importó a granel";
    public const string AvisoExistenciaNegativa = "Existencia negativa en eleventa: no se importó";
    public const string AvisoTicketAbierto = "Ticket sin terminar (sin fecha de venta): no se importó";
    public const string AvisoCodigoInexistente = "Vendido con un código que ya no existe en el catálogo: se creó como producto inactivo";
    public const string AvisoLineaSinCantidad = "Línea con cantidad cero o negativa: no se importó";
    public const string AvisoPrecioCero = "Producto sin precio";

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
        Departamento? sinDepartamento = null;
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
            porId[p.Id] = variante;
            porCodigo.TryAdd(codigo, (variante, p));

            var precio = Centavos(p.Precio);
            if (precio > 0)
                r.Precios.Add(new Precio { VarianteId = variante.Id, CantidadMinima = Cantidad.Unidades(1), PrecioCentavos = precio });
            else
                r.Avisar(AvisoPrecioCero);
            if (p.Mayoreo > 0 && Centavos(p.Mayoreo) < precio)
                r.Avisar(AvisoMayoreo);

            if (p.Costo > 0)
                r.ProveedoresVariantes.Add(new ProveedorVariante
                {
                    ProveedorId = proveedor.Id,
                    VarianteId = variante.Id,
                    UltimoCostoCentavos = Centavos(p.Costo),
                });
        }

        // Promociones por cantidad: son escalas de mayoreo de verdad.
        foreach (var promo in e.Promociones)
        {
            if (porCodigo.TryGetValue(promo.Codigo.Trim(), out var v) && promo.Desde > 1 && promo.PrecioConImpuestos > 0)
                r.Precios.Add(new Precio
                {
                    VarianteId = v.Variante.Id,
                    CantidadMinima = Cantidad.Unidades(Math.Round((decimal)promo.Desde, 3)),
                    PrecioCentavos = Centavos(promo.PrecioConImpuestos),
                });
        }

        // Ventas
        var lineasPorTicket = e.Lineas.ToLookup(l => l.TicketId);
        foreach (var t in e.Tickets.OrderBy(t => t.Id))
        {
            if (t.VendidoEn is not { } vendido)
            {
                r.Avisar(AvisoTicketAbierto);
                continue;
            }

            var venta = new VentaHistorica
            {
                SucursalId = matriz.Id,
                FechaUtc = HoraMexico.AUtc(vendido),
                FolioOrigen = (t.Folio ?? t.Id).ToString(),
                TotalCentavos = Centavos(t.Total),
                Cancelada = t.Cancelado,
            };

            foreach (var l in lineasPorTicket[t.Id])
            {
                var cantidad = Cantidad.Unidades(Math.Round((decimal)l.Cantidad, 3));
                if (!cantidad.EsPositiva)
                {
                    r.Avisar(AvisoLineaSinCantidad);
                    continue;
                }

                var codigo = l.Codigo.Trim();
                if (!porCodigo.TryGetValue(codigo, out var v))
                {
                    var unidad = conFracciones.Contains(codigo) ? UnidadBase.Kilogramo : UnidadBase.Pieza;
                    var nueva = CrearProducto(r, l.Nombre.Trim(), DepartamentoDe(l.DepartamentoId), unidad, activo: false, codigo, idOrigen: $"codigo:{codigo}");
                    v = (nueva, null);
                    porCodigo[codigo] = v;
                    r.Avisar(AvisoCodigoInexistente);
                }

                var importe = Centavos(l.TotalArticulo ?? l.PrecioFinal * l.Cantidad);
                venta.Lineas.Add(new VentaHistoricaLinea
                {
                    VentaHistoricaId = venta.Id,
                    VarianteId = v.Variante.Id,
                    Cantidad = cantidad,
                    PrecioCentavos = Centavos(l.PrecioFinal),
                    ImporteCentavos = importe,
                    CostoCentavos = importe - Centavos(l.Ganancia),
                });
            }

            r.Ventas.Add(venta);
            Mapear(r, nameof(VentaHistorica), t.Id.ToString(), venta.Id);
        }

        // Existencias al momento de la copia: después de la última venta.
        var corte = r.Ventas.Count > 0 ? r.Ventas.Max(v => v.FechaUtc).AddSeconds(1) : DateTime.UtcNow;
        foreach (var p in e.Productos)
        {
            if (!existencias.TryGetValue(p.Id, out var cantidad) || cantidad == 0)
                continue;
            if (cantidad < 0)
            {
                r.Avisar(AvisoExistenciaNegativa);
                continue;
            }

            var variante = porId[p.Id];
            var existencia = Cantidad.Unidades(Math.Round((decimal)cantidad, 3));
            var costo = p.Costo > 0 ? (long)Math.Round((decimal)p.Costo * 100 * existencia.EnUnidades) : 0;
            r.Movimientos.Add(Kardex.Entrada(matriz.Id, variante, TipoMovimiento.InventarioInicial, existencia, costo, corte,
                documentoTipo: Origen));
        }

        return r;
    }

    private static Variante CrearProducto(
        ResultadoImportacion r, string nombre, Departamento departamento, UnidadBase unidad, bool activo, string? codigo, string idOrigen)
    {
        var producto = new Producto { Nombre = nombre, DepartamentoId = departamento.Id, Activo = activo };
        var variante = new Variante { ProductoId = producto.Id, UnidadBase = unidad, Activa = activo };
        var presentacion = new Presentacion
        {
            VarianteId = variante.Id,
            Nombre = unidad.ToString(),
            Factor = Cantidad.Unidades(1),
            EsBase = true,
        };
        r.Productos.Add(producto);
        r.Variantes.Add(variante);
        r.Presentaciones.Add(presentacion);
        if (!string.IsNullOrEmpty(codigo))
            r.Codigos.Add(new CodigoBarras { Codigo = codigo, PresentacionId = presentacion.Id, Origen = OrigenDe(codigo) });
        Mapear(r, nameof(Producto), idOrigen, producto.Id);
        Mapear(r, nameof(Variante), idOrigen, variante.Id);
        return variante;
    }

    /// <summary>
    /// 'U' es por pieza; 'D' (granel) según la medida: metro, litro o kilo. Muchos productos a granel de eleventa
    /// tienen la medida KG aunque se venden por metro o por pieza suelta; si el nombre dice metro, se toma metro.
    /// </summary>
    private static UnidadBase Unidad(FilaProducto p, bool conFracciones, ResultadoImportacion r)
    {
        if (p.TipoVenta != 'D' && !conFracciones)
            return UnidadBase.Pieza;
        if (p.TipoVenta != 'D')
            r.Avisar(AvisoFraccionEnPieza);

        if (p.MedidaId == 3 || PareceMetro(p.Descripcion))
            return UnidadBase.Metro;
        if (p.MedidaId == 5)
            return UnidadBase.Litro;
        if (p.TipoVenta == 'D')
            r.Avisar(AvisoUnidadKilo);
        return UnidadBase.Kilogramo;
    }

    private static bool PareceMetro(string descripcion)
    {
        var palabras = Texto.Normalizar(descripcion).Replace(".", " ").Split(' ', StringSplitOptions.RemoveEmptyEntries);
        return palabras.Any(p => p is "METRO" or "METROS" or "MT" or "MTS" or "XM" or "XMT" or "XMETRO");
    }

    /// <summary>Numéricos de 8, 12, 13 o 14 dígitos son de fábrica (EAN, UPC); los demás, claves que puso la tienda.</summary>
    private static OrigenCodigo OrigenDe(string codigo) =>
        codigo.Length is 8 or 12 or 13 or 14 && codigo.All(char.IsAsciiDigit) ? OrigenCodigo.Fabrica : OrigenCodigo.Interno;

    private static bool EsEntero(double cantidad) => Math.Abs(cantidad - Math.Round(cantidad)) < 0.0005;

    /// <summary>Eleventa guarda los montos como FLOAT: se redondean al centavo.</summary>
    private static long Centavos(double pesos) => (long)Math.Round((decimal)pesos * 100, MidpointRounding.AwayFromZero);

    private static void Mapear(ResultadoImportacion r, string entidad, string idOrigen, Guid idNuevo) =>
        r.Mapeos.Add(new MapeoImportacion { Origen = Origen, Entidad = entidad, IdOrigen = idOrigen, IdNuevo = idNuevo });
}
