using System.Globalization;
using PapeleriaPos.Nucleo;
using PapeleriaPos.Nucleo.Catalogo;
using PapeleriaPos.Nucleo.Configuracion;
using PapeleriaPos.Nucleo.Contratos;
using PapeleriaPos.Nucleo.Turnos;
using PapeleriaPos.Nucleo.Ventas;

namespace PapeleriaPos.Herramientas.Simulador;

/// <summary>Cómo llega un renglón al ticket: como lo mete quien atiende.</summary>
public enum FormaRenglon
{
    /// <summary>El lector: contra la copia de la caja.</summary>
    Escaneo,

    /// <summary>Se escribe el nombre (se busca en cuanto se teclea) y se elige de la lista.</summary>
    Busqueda,

    /// <summary>Un botón de la fila de rápidos (la copia, la CURP).</summary>
    Rapido,

    /// <summary>Un código que nadie ha dado de alta: no existe en la caja principal y quien atiende lo da de alta (alta rápida).</summary>
    ProductoNuevo,
}

/// <param name="Codigo">El que se escanea (el de la pieza o el del paquete); en una búsqueda, ninguno.</param>
/// <param name="Texto">Lo que se teclea para buscarlo.</param>
public sealed record RenglonDelGuion(FormaRenglon Forma, Guid? VarianteId, string? Codigo, string? Texto, Cantidad Cantidad, string Descripcion);

public enum FormaDePago
{
    Efectivo,
    Tarjeta,
    Transferencia,

    /// <summary>Parte con tarjeta y lo demás en efectivo.</summary>
    Mixto,
}

/// <summary>Una entrada (F7) o salida (F8) de efectivo antes del ticket.</summary>
public sealed record MovimientoDelGuion(TipoMovimientoCaja Tipo, long MontoCentavos, string Motivo);

/// <param name="Momento">De 0 a 1: cuándo llega el cliente en el día.</param>
/// <param name="Billete">Con qué paga en efectivo: 0, exacto; 1 a 6, el siguiente múltiplo de $10, $20, $50, $100, $200 o $500.</param>
/// <param name="Sobra">Algo que se escanea de más y se quita del ticket antes de cobrar (queda en la bitácora).</param>
/// <param name="Cancelar">Recién cobrado, el cliente se arrepiente: se cancela el ticket (lo autoriza el encargado).</param>
/// <param name="Devolver">Antes de este cliente, otro devuelve algo de un ticket de antes (de cualquier caja).</param>
/// <param name="CambiaPrecio">El dueño cambia el precio de su primer renglón después de escanearlo y antes de cobrar.</param>
/// <param name="UsaProductoNuevo">Su último renglón es el producto que la caja 1 dio de alta: espera a que exista.</param>
public sealed record TicketDelGuion(
    int Caja, int Numero, double Momento, IReadOnlyList<RenglonDelGuion> Renglones, FormaDePago Pago, int Billete, RenglonDelGuion? Sobra,
    bool Cancelar, bool Devolver, MovimientoDelGuion? Movimiento, bool CambiaPrecio, bool UsaProductoNuevo);

/// <summary>
/// El día pico de cada caja, armado con la copia del catálogo y la semilla (la misma semilla y la misma base dan el mismo
/// día): tickets de 1 a 8 renglones de lo más vendido (como la papelería ficticia: <see cref="Ficticios.GeneradorFicticios"/>),
/// con sus cantidades, algunos por nombre, los rápidos, pagos en efectivo con billetes, tarjeta y transferencia, entradas y
/// salidas de efectivo, cancelaciones y devoluciones; a media jornada el dueño cambia un precio (llega a las cajas con lo que
/// cambió) y la caja 1 da de alta un producto que la última vende poco después.
/// </summary>
/// <param name="PrecioQueCambia">El producto cuyo precio cambia el dueño; vacío si la copia no trae uno que sirva.</param>
/// <param name="CodigoNuevo">El código del producto que llega a media jornada (<see cref="NombreDelNuevo"/>): no está en la copia.</param>
public sealed record GuionDelDia(
    IReadOnlyList<IReadOnlyList<TicketDelGuion>> PorCaja, ProductoVentaDto? PrecioQueCambia, long PrecioNuevoCentavos, string CodigoNuevo)
{
    /// <summary>Lo que la pantalla de venta pide para juntar las familias (PantallaVenta.ParaFamilias).</summary>
    public const int ParaFamilias = 200;

    public static GuionDelDia Armar(PaqueteContingenciaDto copia, IReadOnlyList<Rapido> rapidos, OpcionesSimulador opciones)
    {
        var r = new Random(opciones.Semilla);
        var sucursal = copia.Caja.SucursalId;
        var vendidos = (copia.Vendidos ?? []).ToDictionary(v => v.VarianteId, v => v.Veces);
        var codigos = copia.Codigos.ToLookup(c => c.VarianteId);
        var deRapido = rapidos.GroupBy(x => x.VarianteId).ToDictionary(g => g.Key, g => g.First());

        // Lo que se puede vender, con su peso: las veces que se vendió en 90 días (lo que la copia trae para la búsqueda). Si la
        // copia no lo trae (una base sin ventas recientes), todo pesa igual.
        var candidatos = copia.Productos
            .Where(p => SeVende(p, sucursal))
            .Select(p => (Producto: p, Peso: vendidos.Count > 0 ? vendidos.GetValueOrDefault(p.VarianteId) : 1))
            .Where(x => x.Peso > 0)
            .ToList();
        if (candidatos.Count == 0)
            throw new InvalidOperationException("La copia del catálogo no trae nada que se pueda vender: ¿es la base de prueba?");
        var acumulados = new double[candidatos.Count];
        var total = 0.0;
        for (var i = 0; i < candidatos.Count; i++)
            acumulados[i] = total += candidatos[i].Peso;
        ProductoVentaDto Elegir()
        {
            var i = Array.BinarySearch(acumulados, r.NextDouble() * total);
            return candidatos[Math.Min(i >= 0 ? i : ~i, candidatos.Count - 1)].Producto;
        }

        // El precio que cambia: lo más vendido que se escanea, por pieza y con un solo precio (sin mayoreo).
        var precioQueCambia = candidatos
            .Where(x => x.Producto is { LlevaInventario: true, Unidad: UnidadBase.Pieza, Presentacion.EsBase: true, Codigo: { } codigo }
                        && x.Producto.Precios is [{ PresentacionId: null }] && codigos[x.Producto.VarianteId].Any(c => c.Codigo == codigo))
            .OrderByDescending(x => x.Peso).ThenBy(x => x.Producto.VarianteId)
            .Select(x => x.Producto)
            .FirstOrDefault();
        var precioNuevo = precioQueCambia is null ? 0 : Subir(precioQueCambia.Precios[0].PrecioCentavos);

        var conocidos = copia.Codigos.Select(c => c.Codigo).ToHashSet(StringComparer.OrdinalIgnoreCase);
        string codigoNuevo;
        do
            codigoNuevo = Ean13.Completar("750" + r.NextInt64(0, 1_000_000_000).ToString("000000000", CultureInfo.InvariantCulture));
        while (conocidos.Contains(codigoNuevo));

        RenglonDelGuion Renglon(ProductoVentaDto p)
        {
            var x = r.NextDouble();
            if (deRapido.TryGetValue(p.VarianteId, out var rapido))
            {
                // «20» y Alt+1: veinte copias. Un rápido que ya vende varias (la CURP, 10 impresiones), casi siempre una vez.
                var veces = rapido.Cantidad > 1 ? (x < 0.85 ? 1 : r.Next(2, 4)) : x < 0.5 ? r.Next(1, 4) : x < 0.9 ? r.Next(4, 16) : r.Next(16, 60);
                return new RenglonDelGuion(FormaRenglon.Rapido, p.VarianteId, null, null, Cantidad.Unidades(rapido.Cantidad * veces), p.Descripcion);
            }
            var (codigo, cantidad) = CodigoYCantidad(p, codigos[p.VarianteId].ToList(), r);
            // Lo que no tiene código se busca por nombre; lo que sí, a veces también (la etiqueta se cayó).
            if (codigo is null || r.NextDouble() < 0.06)
                return new RenglonDelGuion(FormaRenglon.Busqueda, p.VarianteId, null, TextoParaBuscar(p.Descripcion, r), cantidad, p.Descripcion);
            return new RenglonDelGuion(FormaRenglon.Escaneo, p.VarianteId, codigo, null, cantidad, p.Descripcion);
        }

        RenglonDelGuion Escaneo(ProductoVentaDto p) =>
            new(FormaRenglon.Escaneo, p.VarianteId, p.Codigo, null, Cantidad.Piezas(1), p.Descripcion);

        // Lo que se escanea de más y se quita: cualquier cosa con código.
        var conCodigo = candidatos.Select(c => c.Producto).Where(p => p.Codigo is { } c && codigos[p.VarianteId].Any(x => x.Codigo == c)).ToList();

        var porCaja = new List<IReadOnlyList<TicketDelGuion>>();
        for (var caja = 1; caja <= opciones.Cajas; caja++)
        {
            var momentos = Enumerable.Range(0, opciones.Tickets).Select(_ => Momento(r)).Order().ToList();
            var especiales = new HashSet<int>();
            int Primero(double desde, int despuesDe = -1)
            {
                var i = momentos.FindIndex(m => m >= desde);
                i = i < 0 ? momentos.Count - 1 : i;
                while (i <= despuesDe || especiales.Contains(i))
                    i++;
                return i < momentos.Count ? i : -1;
            }

            var cambiaPrecio = caja == 1 && precioQueCambia is not null ? Primero(opciones.CambioDePrecio) : -1;
            especiales.Add(cambiaPrecio);
            var creaProducto = caja == 1 ? Primero(opciones.ProductoNuevo) : -1;
            especiales.Add(creaProducto);
            // La última caja vende el producto nuevo poco después (con una sola caja, la misma, más adelante).
            var usaProducto = caja == opciones.Cajas ? Primero(opciones.ProductoNuevo + 0.02, opciones.Cajas == 1 ? creaProducto : -1) : -1;
            especiales.Add(usaProducto);

            var movimientos = new Dictionary<int, MovimientoDelGuion>();
            foreach (var (desde, movimiento) in new[]
                     {
                         (0.15, new MovimientoDelGuion(TipoMovimientoCaja.Entrada, 20_000, "Cambio en monedas para el fondo")),
                         (0.45, new MovimientoDelGuion(TipoMovimientoCaja.Salida, 4_500, "Garrafón de agua")),
                         (0.80, new MovimientoDelGuion(TipoMovimientoCaja.Salida, 3_000, "Pago al de la basura")),
                     })
            {
                var i = momentos.FindIndex(m => m >= desde);
                while (i >= 0 && movimientos.ContainsKey(i))
                    i = i + 1 < momentos.Count ? i + 1 : -1;
                if (i >= 0)
                    movimientos[i] = movimiento;
            }

            // Cancelaciones y devoluciones: unas cuantas (2 y 3 de cada 100); al menos una cancelación en la caja 1 y una devolución
            // en la última, para que siempre se prueben.
            var libres = Enumerable.Range(0, momentos.Count).Where(i => !especiales.Contains(i)).ToList();
            var cancelar = Tomar(libres, Math.Max(caja == 1 ? 1 : 0, (int)Math.Round(opciones.Tickets * 0.02)), r);
            var devolver = Tomar(libres.Where(i => i >= 2 && !cancelar.Contains(i)).ToList(),
                Math.Max(caja == opciones.Cajas ? 1 : 0, (int)Math.Round(opciones.Tickets * 0.03)), r);

            var tickets = new List<TicketDelGuion>();
            for (var i = 0; i < momentos.Count; i++)
            {
                // De 1 a 8 renglones: 45 de cada 100 llevan uno más (como la papelería ficticia).
                var cuantos = 1;
                while (cuantos < 8 && r.NextDouble() < 0.45)
                    cuantos++;
                var renglones = new List<RenglonDelGuion>();
                var usados = new HashSet<Guid>();
                for (var n = 0; n < cuantos; n++)
                {
                    var p = Elegir();
                    if (usados.Add(p.VarianteId))
                        renglones.Add(Renglon(p));
                }
                if (i == cambiaPrecio)
                {
                    renglones.RemoveAll(x => x.VarianteId == precioQueCambia!.VarianteId);
                    renglones.Insert(0, Escaneo(precioQueCambia!));
                }
                if (i == creaProducto)
                    renglones.Insert(0, new RenglonDelGuion(FormaRenglon.ProductoNuevo, null, codigoNuevo, null, Cantidad.Piezas(1), NombreDelNuevo));
                if (i == usaProducto)
                    renglones.Add(new RenglonDelGuion(FormaRenglon.Escaneo, null, codigoNuevo, null, Cantidad.Piezas(1), NombreDelNuevo));

                var x = r.NextDouble();
                var pago = x < 0.84 ? FormaDePago.Efectivo : x < 0.94 ? FormaDePago.Tarjeta : x < 0.98 ? FormaDePago.Transferencia : FormaDePago.Mixto;
                var billete = r.NextDouble() switch { < 0.35 => 0, < 0.50 => 1, < 0.60 => 2, < 0.75 => 3, < 0.87 => 4, < 0.96 => 5, _ => 6 };
                var sobra = r.NextDouble() < 0.03 && conCodigo.Count > 0 ? Escaneo(conCodigo[r.Next(conCodigo.Count)]) : null;
                tickets.Add(new TicketDelGuion(caja, i + 1, momentos[i], renglones, pago, billete, sobra, cancelar.Contains(i), devolver.Contains(i),
                    movimientos.GetValueOrDefault(i), i == cambiaPrecio, i == usaProducto));
            }
            porCaja.Add(tickets);
        }
        return new GuionDelDia(porCaja, precioQueCambia, precioNuevo, codigoNuevo);
    }

    /// <summary>El producto que se da de alta a media jornada: lo que llegó y nadie había dado de alta.</summary>
    public const string NombreDelNuevo = "Marcatextos pastel (llegó hoy)";

    public const long PrecioDelNuevo = 2_500;

    /// <summary>Si se puede agregar a un ticket como lo agrega la caja (tiene precio de menudeo y la venta lo acepta).</summary>
    private static bool SeVende(ProductoVentaDto p, Guid sucursal)
    {
        if (PrecioDeMenudeo.De(p.Precios, sucursal) <= 0)
            return false;
        try
        {
            var una = p.Unidad == UnidadBase.Pieza ? Cantidad.Piezas(1) : Cantidad.Unidades(1);
            new Venta { Serie = "S" }.AgregarProducto(p.ParaVenta(), p.PresentacionModelo(), una, p.PreciosModelo());
            return true;
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException)
        {
            return false;
        }
    }

    /// <summary>
    /// Qué se escanea y cuánto (como <c>GeneradorFicticios.CantidadVendida</c>): lo de granel, en metros, kilos o litros; las
    /// copias, de una a decenas; lo demás casi siempre una pieza, a veces varias, y a veces el paquete con su propio código.
    /// </summary>
    private static (string? Codigo, Cantidad Cantidad) CodigoYCantidad(ProductoVentaDto p, List<CodigoLocalDto> codigos, Random r)
    {
        var x = r.NextDouble();
        var dePieza = codigos.FirstOrDefault(c => c.Codigo == p.Codigo)?.Codigo ?? codigos.FirstOrDefault(c => c.PresentacionId == p.Presentacion.Id)?.Codigo;
        switch (p.Unidad)
        {
            case UnidadBase.Metro:
                return (dePieza, Cantidad.Unidades(0.5m * r.Next(1, 11)));
            case UnidadBase.Kilogramo:
                return (dePieza, Cantidad.Unidades(0.05m * r.Next(2, 21)));
            case UnidadBase.Litro:
                return (dePieza, Cantidad.Unidades(0.25m * r.Next(1, 9)));
        }
        if (p.Descripcion.StartsWith("Impresión", StringComparison.Ordinal) || p.Descripcion.StartsWith("Copia", StringComparison.Ordinal))
            return (dePieza, Cantidad.Piezas(x < 0.5 ? r.Next(1, 4) : x < 0.9 ? r.Next(4, 16) : r.Next(16, 60)));
        if (x < 0.06 && p.Presentaciones.FirstOrDefault(pr => !pr.EsBase) is { } paquete
            && codigos.FirstOrDefault(c => c.PresentacionId == paquete.Id) is { } delPaquete)
            return (delPaquete.Codigo, Cantidad.Piezas(1));
        var piezas = x < 0.70 ? 1 : x < 0.90 ? r.Next(2, 4) : r.Next(4, 13);
        return (dePieza, Cantidad.Piezas(piezas));
    }

    /// <summary>
    /// Lo que se teclea para buscarlo: la primera palabra de su nombre y el principio de la segunda («cuaderno prof»), como lo
    /// escribe quien atiende.
    /// </summary>
    private static string TextoParaBuscar(string descripcion, Random r)
    {
        var palabras = Texto.Normalizar(descripcion).Split(' ', StringSplitOptions.RemoveEmptyEntries).Where(p => p.Any(char.IsLetter)).ToList();
        if (palabras.Count == 0)
            return Texto.Normalizar(descripcion);
        if (palabras.Count == 1)
            return palabras[0];
        var segunda = palabras[1];
        return $"{palabras[0]} {segunda[..Math.Min(segunda.Length, r.Next(3, 6))]}";
    }

    /// <summary>
    /// Cuándo llega el cliente, de 0 a 1: el día de la tienda de 9:00 a 20:30, con más gente a media mañana y a la salida de la
    /// escuela (como <c>GeneradorFicticios.MinutoDeVenta</c>).
    /// </summary>
    private static double Momento(Random r)
    {
        var x = r.NextDouble();
        var centro = x < 0.45 ? 12 * 60 : x < 0.85 ? 17.5 * 60 : 10 * 60;
        var normal = Math.Sqrt(-2 * Math.Log(1 - r.NextDouble())) * Math.Cos(2 * Math.PI * r.NextDouble());
        var minuto = Math.Clamp(centro + normal * 75, 9 * 60, 20.5 * 60);
        return Math.Min((minuto - 9 * 60) / (11.5 * 60), 0.999);
    }

    /// <summary>Un precio 10 % más caro, al medio peso, y al menos un peso más.</summary>
    private static long Subir(long centavos) => Math.Max(centavos + 100, (long)Math.Round(centavos * 1.10m / 50, MidpointRounding.AwayFromZero) * 50);

    private static HashSet<int> Tomar(List<int> de, int cuantos, Random r)
    {
        var elegidos = new HashSet<int>();
        var lista = de.ToList();
        while (elegidos.Count < cuantos && lista.Count > 0)
        {
            var i = r.Next(lista.Count);
            elegidos.Add(lista[i]);
            lista.RemoveAt(i);
        }
        return elegidos;
    }
}
