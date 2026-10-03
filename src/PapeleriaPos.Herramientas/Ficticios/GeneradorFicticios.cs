using System.Globalization;
using PapeleriaPos.Nucleo;
using PapeleriaPos.Nucleo.Catalogo;
using PapeleriaPos.Nucleo.Historico;
using PapeleriaPos.Nucleo.Importacion;
using PapeleriaPos.Nucleo.Inventario;

namespace PapeleriaPos.Herramientas.Ficticios;

public sealed record OpcionesFicticios
{
    public int Semilla { get; init; } = 2026;

    /// <summary>Último día con ventas; las existencias se toman al cierre de ese día.</summary>
    public DateOnly UltimoDia { get; init; } = new(2026, 9, 30);

    public int Dias { get; init; } = 365;

    /// <summary>Tickets de un día normal entre semana, fuera de temporada.</summary>
    public double TicketsPorDia { get; init; } = 58;

    /// <summary>Valor del inventario a costo al cierre (el negocio real tiene unos 500 mil pesos).</summary>
    public long ValorInventarioCentavos { get; init; } = 500_000_00;
}

/// <summary>
/// Inventa una papelería con la forma que tendrá una importación de eleventa: catálogo, histórico de ventas y
/// existencias al cierre. La misma semilla da siempre los mismos datos, IDs incluidos.
/// <para>
/// Trae a propósito lo que el diagnóstico debe encontrar: mercancía sin movimiento, productos sin código, precios
/// debajo del costo y productos capturados dos veces.
/// </para>
/// </summary>
public sealed class GeneradorFicticios
{
    public const string Origen = "ficticios";

    private static readonly string[] NombresProveedores =
    [
        "Distribuidora Escolar del Centro", "Papelera del Bajío", "Comercializadora Lápiz de Oro", "Mayoreo Escolar Express",
        "Grupo Papelero Norte", "Abastecedora de Oficinas", "Importadora Arte y Color", "Surtidora La Escuelita",
        "Distribuciones Tecnológicas", "Envolturas y Regalos", "Proveedora de Servicios Digitales", "Cuadernos y Más",
    ];

    private readonly OpcionesFicticios _opciones;
    private readonly Random _r;
    private readonly DatosFicticios _datos;
    private readonly List<VarianteFicticia> _variantes = [];
    private readonly HashSet<string> _codigos = [];
    private int _consecutivoInterno;
    private int _consecutivoProducto;
    private int _consecutivoVariante;

    private GeneradorFicticios(OpcionesFicticios opciones)
    {
        _opciones = opciones;
        _r = new Random(opciones.Semilla);
        var inicio = opciones.UltimoDia.AddDays(1 - opciones.Dias).ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc);
        _datos = new DatosFicticios(new IdsDeterministas(opciones.Semilla, inicio));
    }

    public static DatosFicticios Generar(OpcionesFicticios? opciones = null)
    {
        var generador = new GeneradorFicticios(opciones ?? new OpcionesFicticios());
        using (Ids.Usar(generador._datos.Ids.Nuevo))
        {
            generador.CrearCatalogo();
            generador.CrearDuplicados();
            generador.CrearVentas();
            generador.CrearExistencias();
        }
        return generador._datos;
    }

    private sealed class VarianteFicticia
    {
        public required Familia Familia { get; init; }
        public required Producto Producto { get; init; }
        public required Variante Variante { get; init; }
        public required Presentacion Base { get; init; }
        public Presentacion? Paquete { get; set; }
        public List<Precio> Precios { get; } = [];

        /// <summary>Centavos por unidad base, sin redondear.</summary>
        public decimal CostoUnitario { get; set; }

        /// <summary>Qué tanto se vende; 0 = mercancía sin movimiento.</summary>
        public double Peso { get; set; }

        public Cantidad VendidoUltimos90Dias { get; set; }

        public bool LlevaInventario => Producto.LlevaInventario;
    }

    // ── Catálogo ────────────────────────────────────────────────────────────────

    private void CrearCatalogo()
    {
        var matriz = _datos.Matriz = new Sucursal { Clave = "M", Nombre = "Matriz" };
        _datos.Sucursales.Add(matriz);

        var departamentos = Familias.Todas.Select(f => f.Departamento).Distinct()
            .ToDictionary(n => n, n => new Departamento { Nombre = n });
        _datos.Departamentos.AddRange(departamentos.Values);

        var marcas = Familias.Todas.SelectMany(f => f.Marcas).Distinct()
            .ToDictionary(n => n, n => new Marca { Nombre = n });
        _datos.Marcas.AddRange(marcas.Values);

        _datos.Proveedores.AddRange(NombresProveedores.Select(n => new Proveedor
        {
            Nombre = n,
            Telefono = $"55{_r.Next(1000_0000, 9999_9999)}",
        }));

        foreach (var familia in Familias.Todas)
        {
            var nombresMarca = familia.Marcas.Length > 0 ? familia.Marcas.Select(m => (string?)m) : [null];
            var rango = 0;
            foreach (var nombreMarca in nombresMarca)
            {
                var marca = nombreMarca is null ? null : marcas[nombreMarca];
                CrearProducto(familia, departamentos[familia.Departamento], marca, rango++);
            }
        }
    }

    private void CrearProducto(Familia familia, Departamento departamento, Marca? marca, int rangoMarca)
    {
        var producto = new Producto
        {
            Nombre = familia.Nombre,
            MarcaId = marca?.Id,
            DepartamentoId = departamento.Id,
            Tipo = familia.Tipo,
            Impuesto = familia.Tipo == TipoProducto.CobroTerceros ? Impuesto.Exento : Impuesto.Iva16,
            ClaveSat = familia.Tipo == TipoProducto.Mercancia ? "44121700" : null,
            ClaveUnidadSat = familia.Unidad switch
            {
                UnidadBase.Metro => "MTR",
                UnidadBase.Kilogramo => "KGM",
                UnidadBase.Litro => "LTR",
                _ => "H87",
            },
        };
        _datos.Productos.Add(producto);
        Mapear(nameof(Producto), $"P{++_consecutivoProducto:00000}", producto.Id);

        // La marca líder sale más cara y se vende más; las siguientes, un poco menos.
        var precioProducto = 100 * Uniforme(familia.Precio.Min, familia.Precio.Max) * (decimal)(1.15 - 0.08 * Math.Min(rangoMarca, 4));
        // Con mayoreo fuerte (hojas, folders) el margen de menudeo es mayor, para que el mayoreo no quede bajo costo.
        var margenMinimo = Math.Max(1.40m, 1.15m / (1 - (familia.DescuentoMayoreo ?? 0)));
        var margen = Uniforme(margenMinimo, margenMinimo + 0.60m);
        var pesoMarca = 1.0 / (1 + 0.35 * rangoMarca);
        var proveedor = _datos.Proveedores[(_datos.Departamentos.IndexOf(departamento) + rangoMarca) % _datos.Proveedores.Count];

        foreach (var combinacion in Combinar(familia.Dimensiones))
        {
            var descripcion = string.Join(" · ", combinacion.Where(c => c.Length > 0));
            var precio = familia.Tipo == TipoProducto.CobroTerceros
                ? decimal.Parse(descripcion.TrimStart('$'), CultureInfo.InvariantCulture) * 100
                : RedondearPrecio(precioProducto * (_r.NextDouble() < 0.2 ? Uniforme(0.9m, 1.1m) : 1));

            var vf = CrearVariante(familia, producto, descripcion, (long)precio);
            vf.CostoUnitario = familia.Tipo switch
            {
                TipoProducto.Servicio => precio * Uniforme(0.20m, 0.40m),
                TipoProducto.CobroTerceros => precio * 0.97m,
                _ => precio / margen * Uniforme(0.97m, 1.03m),
            };
            vf.Peso = familia.Popularidad * familia.Popularidad * pesoMarca * Math.Exp(Normal() * 0.6);

            if (vf.LlevaInventario)
            {
                // Anomalías que el diagnóstico debe encontrar.
                if (_r.NextDouble() < 0.08)
                {
                    vf.Peso = 0;
                    _datos.Resumen.SinMovimiento.Add(vf.Variante.Id);
                }
                if (_r.NextDouble() < 0.01)
                {
                    vf.CostoUnitario = precio * Uniforme(1.05m, 1.30m);
                    _datos.Resumen.PrecioBajoCosto.Add(vf.Variante.Id);
                }

                _datos.ProveedoresVariantes.Add(new ProveedorVariante
                {
                    ProveedorId = proveedor.Id,
                    VarianteId = vf.Variante.Id,
                    CodigoProveedor = $"{proveedor.Nombre[..3].ToUpperInvariant()}-{_r.Next(10000, 99999)}",
                    UltimoCostoCentavos = (long)Math.Round(vf.CostoUnitario),
                    UltimaCompraUtc = HoraMexico.AUtc(_opciones.UltimoDia.AddDays(-_r.Next(1, 120)).ToDateTime(new TimeOnly(10, 0))),
                });
            }
        }
    }

    private VarianteFicticia CrearVariante(Familia familia, Producto producto, string descripcion, long precioCentavos)
    {
        var variante = new Variante { ProductoId = producto.Id, Descripcion = descripcion, UnidadBase = familia.Unidad };
        var basePresentacion = new Presentacion
        {
            VarianteId = variante.Id,
            Nombre = familia.Tipo == TipoProducto.Mercancia ? familia.Unidad.ToString() : "Servicio",
            Factor = Cantidad.Unidades(1),
            EsBase = true,
        };
        _datos.Variantes.Add(variante);
        _datos.Presentaciones.Add(basePresentacion);
        Mapear(nameof(Variante), $"V{++_consecutivoVariante:00000}", variante.Id);

        var vf = new VarianteFicticia { Familia = familia, Producto = producto, Variante = variante, Base = basePresentacion };
        _variantes.Add(vf);

        AgregarPrecio(vf, null, Cantidad.Unidades(1), precioCentavos);

        var conMarca = producto.MarcaId is not null;
        var conCodigo = false;
        if (familia.Tipo == TipoProducto.Mercancia)
        {
            conCodigo = conMarca ? _r.NextDouble() < 0.94 : _r.NextDouble() < 0.55;
            if (conCodigo)
                AgregarCodigo(basePresentacion, conMarca);
            else
                _datos.Resumen.SinCodigo.Add(variante.Id);
        }

        if (familia.Paquete is { } paquete)
        {
            vf.Paquete = new Presentacion { VarianteId = variante.Id, Nombre = paquete.Nombre, Factor = Cantidad.Piezas(paquete.Piezas) };
            _datos.Presentaciones.Add(vf.Paquete);
            // Si la pieza trae código de fábrica, a veces la caja también.
            if (conMarca && conCodigo && _r.NextDouble() < 0.4)
                AgregarCodigo(vf.Paquete, deFabrica: true);

            if (familia.DescuentoMayoreo is { } descuento)
                AgregarPrecio(vf, null, vf.Paquete.Factor, RedondearPrecio(precioCentavos * (1 - descuento)));
            else if (_r.NextDouble() < 0.5)
                AgregarPrecio(vf, null, vf.Paquete.Factor, RedondearPrecio(precioCentavos * (1 - Uniforme(0.08m, 0.15m))));

            // Precio propio del paquete: es raro.
            if (_r.NextDouble() < 0.03)
                AgregarPrecio(vf, vf.Paquete.Id, Cantidad.Piezas(1), RedondearPrecio(precioCentavos * paquete.Piezas * Uniforme(0.85m, 0.95m)));
        }

        return vf;
    }

    private void AgregarPrecio(VarianteFicticia vf, Guid? presentacionId, Cantidad desde, long centavos)
    {
        var precio = new Precio { VarianteId = vf.Variante.Id, PresentacionId = presentacionId, CantidadMinima = desde, PrecioCentavos = centavos };
        vf.Precios.Add(precio);
        _datos.Precios.Add(precio);
    }

    private void AgregarCodigo(Presentacion presentacion, bool deFabrica)
    {
        string codigo;
        do
        {
            codigo = deFabrica
                ? Ean13.Completar("750" + _r.NextInt64(0, 1_000_000_000).ToString("000000000", CultureInfo.InvariantCulture))
                : $"P{++_consecutivoInterno:000000}";
        }
        while (!_codigos.Add(codigo));

        _datos.Codigos.Add(new CodigoBarras
        {
            Codigo = codigo,
            PresentacionId = presentacion.Id,
            Origen = deFabrica ? OrigenCodigo.Fabrica : OrigenCodigo.Interno,
        });
    }

    /// <summary>Productos capturados dos veces con otro nombre, como pasa en un catálogo de años.</summary>
    private void CrearDuplicados()
    {
        var candidatas = _variantes.Where(v => v.LlevaInventario && v.Peso > 0 && v.Producto.MarcaId is not null).ToList();
        var marcas = _datos.Marcas.ToDictionary(m => m.Id);

        for (var i = 0; i < 15 && candidatas.Count > 0; i++)
        {
            var original = candidatas[_r.Next(candidatas.Count)];
            candidatas.Remove(original);

            var nombre = $"{original.Producto.Nombre} {marcas[original.Producto.MarcaId!.Value].Nombre} {original.Variante.Descripcion}";
            var duplicado = new Producto
            {
                Nombre = _r.NextDouble() < 0.5 ? nombre.ToUpperInvariant() : nombre.Replace("punto", "p.").Replace("Cuaderno", "Cuad."),
                DepartamentoId = original.Producto.DepartamentoId,
                Tipo = original.Producto.Tipo,
                Impuesto = original.Producto.Impuesto,
            };
            _datos.Productos.Add(duplicado);
            Mapear(nameof(Producto), $"P{++_consecutivoProducto:00000}", duplicado.Id);

            var precioBase = original.Precios.First(p => p.PresentacionId is null && p.CantidadMinima == Cantidad.Unidades(1)).PrecioCentavos;
            var copia = CrearVariante(original.Familia with { Paquete = null }, duplicado, "", precioBase + (_r.NextDouble() < 0.5 ? 50 : 0));
            copia.CostoUnitario = original.CostoUnitario;
            copia.Peso = original.Peso * 0.4;
            original.Peso *= 0.6;
            _datos.Resumen.Duplicados.Add((original.Variante.Id, copia.Variante.Id));
        }
    }

    // ── Ventas ──────────────────────────────────────────────────────────────────

    private void CrearVentas()
    {
        var primerDia = _opciones.UltimoDia.AddDays(1 - _opciones.Dias);
        var inicio90 = _opciones.UltimoDia.AddDays(-89);
        var acumulados = new double[_variantes.Count];
        var folio = 0;

        for (var dia = primerDia; dia <= _opciones.UltimoDia; dia = dia.AddDays(1))
        {
            var tickets = (int)Math.Round(_opciones.TicketsPorDia * FactorDelDia(dia) * Uniforme(0.85, 1.15));
            if (tickets == 0)
                continue;

            var escolar = IntensidadEscolar(dia);
            var navidad = dia.Month == 12 && dia.Day <= 24 ? 1.0 : 0.0;
            var total = 0.0;
            for (var i = 0; i < _variantes.Count; i++)
            {
                var vf = _variantes[i];
                var temporada = vf.Familia.Temporada switch
                {
                    Temporada.Escolar => 1 + 3 * escolar,
                    Temporada.Navidad => 1 + 4 * navidad,
                    _ => 1,
                };
                total += vf.Peso * temporada;
                acumulados[i] = total;
            }

            var inflacion = 1 - 0.06m * (_opciones.UltimoDia.DayNumber - dia.DayNumber) / 365m;
            var minutos = Enumerable.Range(0, tickets).Select(_ => MinutoDeVenta()).Order().ToList();
            foreach (var minuto in minutos)
            {
                var fecha = HoraMexico.AUtc(dia.ToDateTime(TimeOnly.FromTimeSpan(TimeSpan.FromMinutes(minuto))));
                var venta = CrearVenta(++folio, fecha, acumulados, total, inflacion, contarPara90: dia >= inicio90);
                _datos.Ventas.Add(venta);
                Mapear(nameof(VentaHistorica), venta.FolioOrigen, venta.Id);
            }
        }
    }

    private VentaHistorica CrearVenta(int folio, DateTime fechaUtc, double[] acumulados, double total, decimal inflacion, bool contarPara90)
    {
        var venta = new VentaHistorica
        {
            SucursalId = _datos.Matriz.Id,
            FechaUtc = fechaUtc,
            FolioOrigen = folio.ToString(CultureInfo.InvariantCulture),
            Cancelada = _r.NextDouble() < 0.01,
        };

        var renglones = 1;
        while (renglones < 8 && _r.NextDouble() < 0.45)
            renglones++;

        var usadas = new HashSet<int>();
        for (var i = 0; i < renglones; i++)
        {
            var indice = Elegir(acumulados, total);
            if (!usadas.Add(indice))
                continue;

            var vf = _variantes[indice];
            var (presentacion, cantidad) = CantidadVendida(vf);
            var aplicado = CalculoPrecio.Calcular(vf.Precios, _datos.Matriz.Id, presentacion, cantidad)!;
            var unidades = cantidad.Por(presentacion.Factor);
            var precioPorUnidad = aplicado.Precio.PresentacionId is null
                ? aplicado.Precio.PrecioCentavos
                : Redondeo.Dividir(aplicado.ImporteCentavos * Cantidad.Escala, unidades.Milesimas);

            venta.Lineas.Add(new VentaHistoricaLinea
            {
                VentaHistoricaId = venta.Id,
                VarianteId = vf.Variante.Id,
                Cantidad = unidades,
                PrecioCentavos = precioPorUnidad,
                ImporteCentavos = aplicado.ImporteCentavos,
                CostoCentavos = (long)Math.Round(vf.CostoUnitario * inflacion * unidades.Milesimas / Cantidad.Escala),
            });

            if (contarPara90 && !venta.Cancelada)
                vf.VendidoUltimos90Dias += unidades;
        }

        venta.TotalCentavos = venta.Lineas.Sum(l => l.ImporteCentavos);
        return venta;
    }

    private (Presentacion, Cantidad) CantidadVendida(VarianteFicticia vf)
    {
        var x = _r.NextDouble();
        switch (vf.Variante.UnidadBase)
        {
            case UnidadBase.Metro:
                return (vf.Base, Cantidad.Unidades(0.5m * _r.Next(1, 11)));
            case UnidadBase.Kilogramo:
                return (vf.Base, Cantidad.Unidades(0.05m * _r.Next(2, 21)));
            case UnidadBase.Litro:
                return (vf.Base, Cantidad.Unidades(0.25m * _r.Next(1, 9)));
        }

        if (vf.Producto.Tipo == TipoProducto.CobroTerceros)
            return (vf.Base, Cantidad.Piezas(1));

        if (vf.Familia.Nombre.StartsWith("Impresión", StringComparison.Ordinal) || vf.Familia.Nombre.StartsWith("Copia", StringComparison.Ordinal))
            return (vf.Base, Cantidad.Piezas(x < 0.5 ? _r.Next(1, 4) : x < 0.9 ? _r.Next(4, 16) : _r.Next(16, 60)));

        if (vf.Paquete is not null && x < 0.06)
            return (vf.Paquete, Cantidad.Piezas(1));

        var piezas = x < 0.70 ? 1 : x < 0.90 ? _r.Next(2, 4) : _r.Next(4, 13);
        // Hojas, clips, sobres: se venden por varias piezas.
        if (vf.Paquete is { } paquete && paquete.Factor >= Cantidad.Piezas(50))
            piezas *= _r.Next(2, 11);
        return (vf.Base, Cantidad.Piezas(piezas));
    }

    /// <summary>Lunes a sábado normal, domingo medio día; cerrado en días festivos; el regreso a clases casi duplica.</summary>
    private static double FactorDelDia(DateOnly dia)
    {
        if ((dia.Month, dia.Day) is (1, 1) or (5, 1) or (9, 16) or (12, 25))
            return 0;

        var semana = dia.DayOfWeek switch
        {
            DayOfWeek.Sunday => 0.4,
            DayOfWeek.Saturday => 0.9,
            _ => 1.0,
        };
        var vacaciones = (dia.Month == 7 && dia.Day < 15) || (dia.Month == 12 && dia.Day >= 20) || (dia.Month == 1 && dia.Day <= 5) ? 0.75 : 1.0;
        var enero = dia.Month == 1 && dia.Day is > 5 and <= 15 ? 1.3 : 1.0;
        var diciembre = dia.Month == 12 && dia.Day < 20 ? 1.15 : 1.0;
        return semana * vacaciones * enero * diciembre * (1 + 1.2 * IntensidadEscolar(dia));
    }

    /// <summary>De 0 a 1: sube desde mediados de julio, llega al máximo a fines de agosto y baja a mediados de septiembre.</summary>
    private static double IntensidadEscolar(DateOnly dia)
    {
        var pico = new DateOnly(dia.Year, 8, 25).DayNumber;
        var distancia = dia.DayNumber - pico;
        return distancia switch
        {
            < -40 or > 20 => 0,
            < 0 => 1 + distancia / 40.0,
            _ => 1 - distancia / 20.0,
        };
    }

    /// <summary>Minuto del día entre 9:00 y 20:30, con más gente a media mañana y al salir de la escuela.</summary>
    private double MinutoDeVenta()
    {
        var x = _r.NextDouble();
        var centro = x < 0.45 ? 12 * 60 : x < 0.85 ? 17.5 * 60 : 10 * 60;
        return Math.Clamp(centro + Normal() * 75, 9 * 60, 20.5 * 60);
    }

    // ── Existencias ─────────────────────────────────────────────────────────────

    /// <summary>
    /// Existencias al cierre del último día, como las daría eleventa. Lo que se vende tiene de 1 a 8 semanas de
    /// venta; lo que no se mueve tiene un poco de todo. Al final se ajusta al valor de inventario pedido.
    /// </summary>
    private void CrearExistencias()
    {
        var conInventario = _variantes.Where(v => v.LlevaInventario).ToList();
        var cantidades = conInventario.Select(vf =>
        {
            if (vf.Peso == 0)
                return vf.Variante.UnidadBase == UnidadBase.Pieza ? _r.Next(3, 41) : _r.Next(5, 51);
            if (_r.NextDouble() < 0.03)
                return 0; // agotado
            var porSemana = (double)vf.VendidoUltimos90Dias.EnUnidades / 13;
            return porSemana * Uniforme(1.0, 8.0) + _r.Next(0, 6);
        }).ToList();

        var valor = conInventario.Select((vf, i) => (double)vf.CostoUnitario * cantidades[i]).Sum();
        var escala = _opciones.ValorInventarioCentavos / valor;

        var cierre = HoraMexico.AUtc(_opciones.UltimoDia.ToDateTime(new TimeOnly(21, 0)));
        for (var i = 0; i < conInventario.Count; i++)
        {
            var vf = conInventario[i];
            var cantidad = vf.Variante.UnidadBase == UnidadBase.Pieza
                ? Cantidad.Piezas((long)Math.Round(cantidades[i] * escala))
                : Cantidad.Unidades(Math.Round((decimal)(cantidades[i] * escala), 1));
            if (!cantidad.EsPositiva)
                continue;

            var costo = (long)Math.Round(vf.CostoUnitario * cantidad.Milesimas / Cantidad.Escala);
            _datos.Movimientos.Add(Kardex.Entrada(_datos.Matriz.Id, vf.Variante, TipoMovimiento.InventarioInicial, cantidad, costo, cierre,
                documentoTipo: Origen));
        }
    }

    // ── Utilidades ──────────────────────────────────────────────────────────────

    private void Mapear(string entidad, string idOrigen, Guid idNuevo) =>
        _datos.Mapeos.Add(new MapeoImportacion { Origen = Origen, Entidad = entidad, IdOrigen = idOrigen, IdNuevo = idNuevo });

    private static IEnumerable<string[]> Combinar(string[][] dimensiones) =>
        dimensiones.Aggregate(
            (IEnumerable<string[]>)[[]],
            (combinaciones, valores) => combinaciones.SelectMany(c => valores.Select(v => (string[])[.. c, v])));

    /// <summary>Al peso con 10 centavos hasta $5; arriba, al medio peso. Las hojas sueltas, a 5 centavos.</summary>
    private static long RedondearPrecio(decimal centavos)
    {
        var paso = centavos switch { < 100 => 5m, < 500 => 10m, _ => 50m };
        return (long)Math.Max(paso, Math.Round(centavos / paso, MidpointRounding.AwayFromZero) * paso);
    }

    private int Elegir(double[] acumulados, double total)
    {
        var objetivo = _r.NextDouble() * total;
        var i = Array.BinarySearch(acumulados, objetivo);
        return Math.Min(i >= 0 ? i : ~i, acumulados.Length - 1);
    }

    private decimal Uniforme(decimal min, decimal max) => min + (max - min) * (decimal)_r.NextDouble();

    private double Uniforme(double min, double max) => min + (max - min) * _r.NextDouble();

    /// <summary>Normal estándar (Box-Muller).</summary>
    private double Normal() => Math.Sqrt(-2 * Math.Log(1 - _r.NextDouble())) * Math.Cos(2 * Math.PI * _r.NextDouble());
}
