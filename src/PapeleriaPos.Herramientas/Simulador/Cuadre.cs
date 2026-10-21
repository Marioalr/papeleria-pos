using PapeleriaPos.Interfaz;
using PapeleriaPos.Nucleo;
using PapeleriaPos.Nucleo.Contratos;
using PapeleriaPos.Nucleo.Turnos;

namespace PapeleriaPos.Herramientas.Simulador;

/// <summary>Lo que lleva una caja simulada en su libro al final del día: contra esto se cuadra lo que dice la caja principal.</summary>
public sealed record LibroDeCaja(
    int Numero, string Nombre, Guid TurnoId, DateTime TurnoAbiertoLocal, long FondoCentavos, IReadOnlyList<VentaSimulada> Ventas,
    IReadOnlyList<DevolucionSimulada> Devoluciones, IReadOnlyList<MovimientoSimulado> Movimientos, CorteDto? Corte, long? Contado,
    int Pendientes, int ConError, string? UltimoError, int CobrosSinConfirmar)
{
    public static LibroDeCaja De(CajaSimulada c) => new(c.Numero, c.Nombre, c.Turno.Id, c.Turno.AbiertoLocal, c.Turno.FondoCentavos, c.Ventas.ToList(),
        c.Devoluciones.ToList(), c.Movimientos.ToList(), c.Corte, c.Contado, c.Contingencia.Pendientes, c.Contingencia.ConError, c.Contingencia.UltimoError,
        c.CobrosSinConfirmar);

    /// <summary>El fondo, lo que entró de cada venta, las entradas, menos las salidas y los reembolsos de esta caja.</summary>
    public long EfectivoEnCaja => FondoCentavos + Ventas.Sum(v => v.EfectivoCentavos)
        + Movimientos.Sum(m => m.Tipo == TipoMovimientoCaja.Entrada ? m.MontoCentavos : -m.MontoCentavos) - Devoluciones.Sum(d => d.ReembolsoEfectivoCentavos);
}

/// <summary>Lo que dice la caja principal al final del día (por la API, con la llave de cada caja) y lo que quedó en cada caja.</summary>
/// <param name="Ventas">Las ventas del día de cada caja (api/cajas/{id}/ventas-del-dia).</param>
/// <param name="Resumenes">La cuenta de cada turno (api/turnos/{id}/resumen).</param>
/// <param name="TurnosAbiertos">Si cada caja sigue con un turno abierto (no debe: ya se hizo el corte).</param>
/// <param name="Existencias">Lo que hay de cada producto que se movió, en la caja principal.</param>
/// <param name="Kardex">La suma de los movimientos del kardex de cada uno.</param>
/// <param name="Copias">La existencia de cada uno en la copia de cada caja, ya al día.</param>
/// <param name="PreciosEnLaCopia">El precio del producto que cambió el dueño, en la copia de cada caja.</param>
/// <param name="Bandejas">Lo que quedó en la bandeja de salida de cada caja.</param>
/// <param name="ExistenciasDistintasEnLaBase">Toda la base: las existencias que no son la suma de su kardex (vacío si no se revisó la base).</param>
/// <param name="FoliosRepetidosEnLaBase">Toda la base: los folios que están dos veces.</param>
public sealed record DelServicio(
    IReadOnlyDictionary<int, IReadOnlyList<VentaDelDiaDto>> Ventas, IReadOnlyDictionary<int, CorteDto?> Resumenes,
    IReadOnlyDictionary<int, bool> TurnosAbiertos, CorteDelDiaDto? CorteDelDia, IReadOnlyDictionary<Guid, decimal> Existencias,
    IReadOnlyDictionary<Guid, decimal> Kardex, IReadOnlyDictionary<int, IReadOnlyDictionary<Guid, decimal>> Copias,
    IReadOnlyDictionary<int, long?> PreciosEnLaCopia, IReadOnlyDictionary<int, IReadOnlyList<ElementoDeLaBandeja>> Bandejas,
    IReadOnlyList<string>? ExistenciasDistintasEnLaBase = null, IReadOnlyList<string>? FoliosRepetidosEnLaBase = null);

/// <summary>Una revisión del cuadre: qué se revisó, si cuadra y, si no, cada diferencia.</summary>
public sealed record Revision(string Que, bool Bien, IReadOnlyList<string> Detalles)
{
    public static Revision De(string que, List<string> diferencias, string? siCuadra = null) =>
        new(que, diferencias.Count == 0, diferencias.Count == 0 && siCuadra is not null ? [siCuadra] : diferencias);
}

/// <summary>
/// El cuadre del día (docs/prueba-sucursales-linux.md, «Cuadre»: ninguna venta perdida ni duplicada; tickets, cortes y
/// existencias cuadran): lo que cada caja cobró está una vez en la caja principal, con su folio y su total; los folios de cada
/// caja van seguidos; lo que vendió sin conexión subió una vez; la cuenta de cada turno es la del libro de la caja y su corte
/// cuadra al centavo; y cada producto que se movió bajó lo que se vendió menos lo que se devolvió, con su existencia igual a la
/// suma de su kardex, también en la copia de cada caja.
/// </summary>
public static class Cuadre
{
    public static List<Revision> Revisar(
        IReadOnlyList<LibroDeCaja> cajas, IReadOnlyDictionary<Guid, decimal> existenciasAntes, IReadOnlyDictionary<Guid, string> nombres,
        DelServicio servicio, long? precioNuevo)
    {
        var revisiones = new List<Revision>();
        foreach (var caja in cajas)
        {
            revisiones.Add(Ventas(caja, servicio.Ventas.GetValueOrDefault(caja.Numero) ?? []));
            revisiones.Add(Folios(caja));
            revisiones.Add(Bandeja(caja, servicio.Bandejas.GetValueOrDefault(caja.Numero) ?? []));
            revisiones.Add(Turno(caja, servicio.Resumenes.GetValueOrDefault(caja.Numero), servicio.TurnosAbiertos.GetValueOrDefault(caja.Numero)));
        }
        revisiones.Add(CorteDelDia(cajas, servicio.CorteDelDia, servicio.Resumenes));
        revisiones.Add(Existencias(cajas, existenciasAntes, nombres, servicio.Existencias));
        revisiones.Add(KardexCuadra(nombres, servicio.Existencias, servicio.Kardex));
        revisiones.Add(Copias(cajas, nombres, servicio.Existencias, servicio.Copias, servicio.PreciosEnLaCopia, precioNuevo));
        if (servicio.ExistenciasDistintasEnLaBase is { } distintas)
            revisiones.Add(Revision.De("Toda la base: cada existencia es la suma de su kardex", distintas.ToList(),
                "En toda la base de la caja principal, cada existencia es la suma de su kardex."));
        if (servicio.FoliosRepetidosEnLaBase is { } repetidos)
            revisiones.Add(Revision.De("Toda la base: ningún folio repetido", repetidos.ToList(), "En toda la base no hay un folio dos veces."));
        return revisiones;
    }

    /// <summary>Lo que cobró la caja está en la caja principal una vez, con su folio, su total y si se canceló; y nada de más.</summary>
    public static Revision Ventas(LibroDeCaja caja, IReadOnlyList<VentaDelDiaDto> delServicio)
    {
        var diferencias = new List<string>();
        var alla = delServicio.GroupBy(v => v.Id).ToDictionary(g => g.Key, g => g.ToList());
        foreach (var dobles in caja.Ventas.GroupBy(v => v.VentaId).Where(g => g.Count() > 1))
            diferencias.Add($"La caja anotó dos veces la venta {dobles.First().Folio}.");
        foreach (var venta in caja.Ventas.DistinctBy(v => v.VentaId))
        {
            if (!alla.TryGetValue(venta.VentaId, out var suyas))
            {
                diferencias.Add($"Perdida: la venta {venta.Folio} de {Formato.Dinero(venta.TotalCentavos)} ({(venta.EnLinea ? "en línea" : "sin conexión")}) "
                    + "no está en la caja principal.");
                continue;
            }
            if (suyas.Count > 1)
                diferencias.Add($"Duplicada: la venta {venta.Folio} está {suyas.Count} veces en la caja principal.");
            var suya = suyas[0];
            if (suya.Folio != venta.Folio)
                diferencias.Add($"La venta {venta.Folio} quedó en la caja principal con el folio {suya.Folio}.");
            if (suya.TotalCentavos != venta.TotalCentavos)
                diferencias.Add($"La venta {venta.Folio}: la caja cobró {Formato.Dinero(venta.TotalCentavos)}, "
                    + $"la caja principal dice {Formato.Dinero(suya.TotalCentavos)}.");
            if (suya.Cancelada != venta.Cancelada)
                diferencias.Add($"La venta {venta.Folio} "
                    + (venta.Cancelada ? "se canceló, pero la caja principal no lo sabe." : "no se canceló, pero la caja principal dice que sí."));
        }
        var nuestras = caja.Ventas.Select(v => v.VentaId).ToHashSet();
        foreach (var deMas in delServicio.Where(v => v.FechaLocal >= caja.TurnoAbiertoLocal && !nuestras.Contains(v.Id)))
            diferencias.Add($"De más: la caja principal tiene la venta {deMas.Folio} de {Formato.Dinero(deMas.TotalCentavos)} que la caja no cobró.");
        foreach (var repetido in delServicio.GroupBy(v => v.Folio).Where(g => g.Count() > 1))
            diferencias.Add($"El folio {repetido.Key} está {repetido.Count()} veces en la caja principal.");
        var sinConexion = caja.Ventas.Count(v => !v.EnLinea);
        return Revision.De($"{caja.Nombre}: ninguna venta perdida ni duplicada", diferencias,
            $"{Cuantas(caja.Ventas.Count, "La venta que cobró", "Las {0} ventas que cobró")} ({sinConexion} sin conexión) "
            + $"{(caja.Ventas.Count == 1 ? "está" : "están")} "
            + $"una vez en la caja principal, con su folio y su total, y no tiene ninguna de más "
            + $"({Cuantas(caja.Ventas.Count(v => v.Cancelada), "ninguna cancelada", "una cancelada", "{0} canceladas")}, como allá).");
    }

    /// <summary>Los folios de la caja van seguidos, sin repetirse; los únicos huecos, los de cobros que no se supo si quedaron.</summary>
    public static Revision Folios(LibroDeCaja caja)
    {
        var diferencias = new List<string>();
        var numeros = caja.Ventas.Select(v => v.Numero).Order().ToList();
        foreach (var repetido in numeros.GroupBy(n => n).Where(g => g.Count() > 1))
            diferencias.Add($"El folio {repetido.Key} se usó {repetido.Count()} veces.");
        var huecos = new List<int>();
        for (var i = 1; i < numeros.Count; i++)
            for (var falta = numeros[i - 1] + 1; falta < numeros[i]; falta++)
                huecos.Add(falta);
        if (huecos.Count > caja.CobrosSinConfirmar)
            diferencias.Add($"Faltan los folios {string.Join(", ", huecos)}: "
                + (caja.CobrosSinConfirmar == 0 ? "ningún cobro quedó sin confirmar." : $"solo {caja.CobrosSinConfirmar} cobros quedaron sin confirmar."));
        var bien = numeros.Count == 0 ? "No cobró nada."
            : $"Del {numeros[0]} al {numeros[^1]}, seguidos"
              + (huecos.Count == 0 ? "." : $", salvo {string.Join(", ", huecos)}: los apartó un cobro que no se supo si había quedado.");
        return Revision.De($"{caja.Nombre}: los folios", diferencias, bien);
    }

    /// <summary>Lo que hizo sin conexión subió una vez: la bandeja quedó vacía, sin errores, con cada venta sin conexión.</summary>
    public static Revision Bandeja(LibroDeCaja caja, IReadOnlyList<ElementoDeLaBandeja> bandeja)
    {
        var diferencias = new List<string>();
        foreach (var e in bandeja.Where(e => !e.Subido))
            diferencias.Add($"Quedó sin subir: {e.Ruta} ({e.Id}){(e.Error is { } error ? $": la caja principal no lo quiso: {error}" : "")}.");
        if (caja.Pendientes > 0 || caja.ConError > 0)
            diferencias.Add($"La caja dice que le quedan {caja.Pendientes} por subir y {caja.ConError} con error{(caja.UltimoError is { } u ? $" ({u})" : "")}.");
        var ventasEnLaBandeja = bandeja.Where(e => e.Ruta == "api/bandeja/ventas").Select(e => e.Id).ToList();
        foreach (var dos in ventasEnLaBandeja.GroupBy(id => id).Where(g => g.Count() > 1))
            diferencias.Add($"La venta {dos.Key} entró {dos.Count()} veces a la bandeja.");
        var enLaBandeja = ventasEnLaBandeja.ToHashSet();
        foreach (var venta in caja.Ventas.Where(v => !v.EnLinea && !enLaBandeja.Contains(v.VentaId)))
            diferencias.Add($"La venta {venta.Folio} se cobró sin conexión y no pasó por la bandeja.");
        var nuestras = caja.Ventas.Select(v => v.VentaId).ToHashSet();
        foreach (var ajena in enLaBandeja.Where(id => !nuestras.Contains(id)))
            diferencias.Add($"En la bandeja hay una venta ({ajena}) que la caja no anotó como cobrada.");
        var subidas = bandeja.Count(e => e.Subido);
        return Revision.De($"{caja.Nombre}: lo que hizo sin conexión subió una vez", diferencias,
            ventasEnLaBandeja.Count == 0
                ? $"No vendió sin conexión; su bandeja quedó vacía{(subidas == 0 ? "" : $" ({Cuantas(subidas, "", "subió una cosa", "subieron {0} cosas")})")}."
                : Cuantas(ventasEnLaBandeja.Count, "", "Su venta sin conexión subió una vez", "Sus {0} ventas sin conexión subieron una vez cada una") + ", y todo lo "
                  + $"demás de su bandeja también ({subidas} en total), sin errores.");
    }

    /// <summary>La cuenta del turno en la caja principal es la del libro de la caja, el turno quedó cerrado y su corte cuadra al centavo.</summary>
    public static Revision Turno(LibroDeCaja caja, CorteDto? resumen, bool sigueAbierto)
    {
        var diferencias = new List<string>();
        if (resumen is null)
            return Revision.De($"{caja.Nombre}: su turno y su corte", [$"La caja principal no tiene el turno {caja.TurnoId}."]);
        var vigentes = caja.Ventas.Where(v => !v.Cancelada).ToList();
        void Igual(string que, long enLaCaja, long alla, bool dinero = true)
        {
            if (enLaCaja != alla)
                diferencias.Add($"{que}: según la caja {(dinero ? Formato.Dinero(enLaCaja) : enLaCaja)}, "
                    + $"según la caja principal {(dinero ? Formato.Dinero(alla) : alla)}.");
        }
        Igual("Ventas", vigentes.Count, resumen.Ventas, dinero: false);
        Igual("Cancelaciones", caja.Ventas.Count(v => v.Cancelada), resumen.Cancelaciones, dinero: false);
        Igual("Fondo", caja.FondoCentavos, resumen.FondoCentavos);
        Igual("Efectivo de las ventas", caja.Ventas.Sum(v => v.EfectivoCentavos), resumen.EfectivoVentasCentavos);
        Igual("Tarjeta", vigentes.Sum(v => v.TarjetaCentavos), resumen.TarjetaCentavos);
        Igual("Transferencias", vigentes.Sum(v => v.TransferenciaCentavos), resumen.TransferenciaCentavos);
        Igual("Entradas", caja.Movimientos.Where(m => m.Tipo == TipoMovimientoCaja.Entrada).Sum(m => m.MontoCentavos), resumen.EntradasCentavos);
        Igual("Salidas", caja.Movimientos.Where(m => m.Tipo == TipoMovimientoCaja.Salida).Sum(m => m.MontoCentavos), resumen.SalidasCentavos);
        Igual("Reembolsos (devoluciones y cancelaciones)", caja.Devoluciones.Sum(d => d.ReembolsoEfectivoCentavos), resumen.DevolucionesCentavos);
        Igual("Efectivo esperado", caja.EfectivoEnCaja, resumen.EsperadoCentavos);
        if (sigueAbierto)
            diferencias.Add("El turno sigue abierto en la caja principal: el corte no lo cerró.");
        if (caja.Corte is not { } corte)
            diferencias.Add("No se hizo el corte del turno.");
        else
        {
            if (corte.ContadoCentavos != caja.Contado)
                diferencias.Add($"El corte guardó {Formato.Dinero(corte.ContadoCentavos ?? 0)} contados; el encargado contó {Formato.Dinero(caja.Contado ?? 0)}.");
            if (corte.EsperadoCentavos != resumen.EsperadoCentavos)
                diferencias.Add($"El corte esperaba {Formato.Dinero(corte.EsperadoCentavos)}; "
                    + $"la cuenta del turno, ya con todo, {Formato.Dinero(resumen.EsperadoCentavos)}.");
            if (corte.DiferenciaCentavos is { } diferencia && diferencia != 0)
                diferencias.Add($"El corte salió con {(diferencia < 0 ? "faltante" : "sobrante")} de {Formato.Dinero(Math.Abs(diferencia))}.");
        }
        return Revision.De($"{caja.Nombre}: su turno y su corte", diferencias,
            $"{Cuantas(vigentes.Count, "Ninguna venta", "Una venta", "{0} ventas")}, "
            + $"{Cuantas(caja.Ventas.Count(v => v.Cancelada), "ninguna cancelada", "una cancelada", "{0} canceladas")}, "
            + $"{Formato.Dinero(vigentes.Sum(v => v.TarjetaCentavos))} con tarjeta y "
            + $"{Formato.Dinero(vigentes.Sum(v => v.TransferenciaCentavos))} por transferencia; el encargado contó {Formato.Dinero(caja.Contado ?? 0)}, lo que "
            + $"esperaba el corte{(caja.Corte?.EnContingencia == true ? " (hecho sin conexión)" : "")}: diferencia $0.00. El turno quedó cerrado.");
    }

    /// <summary>El corte del día de la tienda tiene el turno de cada caja, cerrado, con sus tickets y su efectivo.</summary>
    public static Revision CorteDelDia(IReadOnlyList<LibroDeCaja> cajas, CorteDelDiaDto? corte, IReadOnlyDictionary<int, CorteDto?> resumenes)
    {
        var diferencias = new List<string>();
        if (corte is null)
            return Revision.De("El corte del día", ["No se pudo ver el corte del día (con el permiso del dueño)."]);
        foreach (var caja in cajas)
        {
            if (corte.Turnos.FirstOrDefault(t => t.Id == caja.TurnoId) is not { } turno)
            {
                diferencias.Add($"El corte del día no tiene el turno de la {caja.Nombre}.");
                continue;
            }
            var vigentes = caja.Ventas.Count(v => !v.Cancelada);
            if (turno.Tickets != vigentes)
                diferencias.Add($"{caja.Nombre}: el corte del día dice {turno.Tickets} tickets; la caja cobró {vigentes} (sin las canceladas).");
            if (turno.CerradoLocal is null)
                diferencias.Add($"{caja.Nombre}: en el corte del día su turno sigue abierto.");
            if (turno.ContadoCentavos != caja.Contado)
                diferencias.Add($"{caja.Nombre}: el corte del día dice {Formato.Dinero(turno.ContadoCentavos ?? 0)} contados; "
                    + $"se contaron {Formato.Dinero(caja.Contado ?? 0)}.");
            if (turno.DiferenciaCentavos is { } d && d != 0)
                diferencias.Add($"{caja.Nombre}: en el corte del día su turno tiene diferencia de {Formato.ConSigno(d)}.");
            if (resumenes.GetValueOrDefault(caja.Numero) is { } resumen && turno.EsperadoCentavos != resumen.EsperadoCentavos)
                diferencias.Add($"{caja.Nombre}: el corte del día espera {Formato.Dinero(turno.EsperadoCentavos ?? 0)}; "
                    + $"la cuenta del turno, {Formato.Dinero(resumen.EsperadoCentavos)}.");
        }
        return Revision.De("El corte del día", diferencias,
            $"El corte del día tiene el turno de cada caja simulada, cerrado, con sus tickets y lo contado (el de la tienda: {corte.Tickets} tickets, "
            + $"{Formato.Dinero(corte.VendidoCentavos)}).");
    }

    /// <summary>
    /// Cada producto que se movió bajó lo que se vendió, y subió lo que se devolvió y lo de las cancelaciones (lo que llegó
    /// dañado entra y sale como merma). Supone que mientras corre el simulador nadie más vende en esa base.
    /// </summary>
    public static Revision Existencias(
        IReadOnlyList<LibroDeCaja> cajas, IReadOnlyDictionary<Guid, decimal> antes, IReadOnlyDictionary<Guid, string> nombres,
        IReadOnlyDictionary<Guid, decimal> despues)
    {
        var cambio = CambioEsperado(cajas);
        var diferencias = new List<string>();
        foreach (var (varianteId, esperado) in cambio.OrderBy(x => nombres.GetValueOrDefault(x.Key)))
        {
            if (!despues.TryGetValue(varianteId, out var ahora))
            {
                diferencias.Add($"No se pudo leer la existencia de «{nombres.GetValueOrDefault(varianteId, varianteId.ToString())}» en la caja principal.");
                continue;
            }
            var real = Milesimas(ahora) - Milesimas(antes.GetValueOrDefault(varianteId));
            if (real != esperado)
                diferencias.Add($"«{nombres.GetValueOrDefault(varianteId, varianteId.ToString())}»: debía {(esperado <= 0 ? "bajar" : "subir")} "
                    + $"{new Cantidad(Math.Abs(esperado))} y {(real <= 0 ? "bajó" : "subió")} {new Cantidad(Math.Abs(real))} "
                    + $"(de {antes.GetValueOrDefault(varianteId):0.###} a {ahora:0.###}).");
        }
        return Revision.De("Existencias: lo vendido menos lo devuelto", diferencias,
            $"Los {cambio.Count} productos del inventario que se movieron bajaron exactamente lo que se vendió menos lo que se devolvió o se canceló.");
    }

    /// <summary>Lo que debe cambiar la existencia de cada producto, en milésimas de su unidad base.</summary>
    public static Dictionary<Guid, long> CambioEsperado(IReadOnlyList<LibroDeCaja> cajas)
    {
        var cambio = new Dictionary<Guid, long>();
        void Sumar(Guid varianteId, long milesimas) => cambio[varianteId] = cambio.GetValueOrDefault(varianteId) + milesimas;
        foreach (var renglon in cajas.SelectMany(c => c.Ventas).SelectMany(v => v.Renglones).Where(r => r is { LlevaInventario: true, VarianteId: not null }))
            Sumar(renglon.VarianteId!.Value, -renglon.CantidadBase);
        foreach (var (varianteId, cantidadBase, regresa) in cajas.SelectMany(c => c.Devoluciones).SelectMany(d => d.Renglones))
            Sumar(varianteId, regresa ? cantidadBase : 0);
        return cambio;
    }

    /// <summary>La existencia de cada producto que se movió es la suma de su kardex.</summary>
    public static Revision KardexCuadra(
        IReadOnlyDictionary<Guid, string> nombres, IReadOnlyDictionary<Guid, decimal> existencias, IReadOnlyDictionary<Guid, decimal> kardex)
    {
        var diferencias = existencias
            .Where(e => Milesimas(kardex.GetValueOrDefault(e.Key)) != Milesimas(e.Value))
            .Select(e => $"«{nombres.GetValueOrDefault(e.Key, e.Key.ToString())}»: existencia {e.Value:0.###}, "
                         + $"suma del kardex {kardex.GetValueOrDefault(e.Key):0.###}.")
            .ToList();
        return Revision.De("Existencias = suma del kardex", diferencias,
            $"La existencia de los {existencias.Count} productos que se movieron es la suma de su kardex.");
    }

    /// <summary>La copia de cada caja quedó al día: sus existencias son las de la caja principal y trae el precio que cambió.</summary>
    public static Revision Copias(
        IReadOnlyList<LibroDeCaja> cajas, IReadOnlyDictionary<Guid, string> nombres, IReadOnlyDictionary<Guid, decimal> existencias,
        IReadOnlyDictionary<int, IReadOnlyDictionary<Guid, decimal>> copias, IReadOnlyDictionary<int, long?> precios, long? precioNuevo)
    {
        var diferencias = new List<string>();
        foreach (var caja in cajas)
        {
            var copia = copias.GetValueOrDefault(caja.Numero) ?? new Dictionary<Guid, decimal>();
            var distintas = existencias.Where(e => copia.TryGetValue(e.Key, out var enLaCopia) && Milesimas(enLaCopia) != Milesimas(e.Value)).ToList();
            foreach (var (varianteId, alla) in distintas.Take(5))
                diferencias.Add($"{caja.Nombre}: «{nombres.GetValueOrDefault(varianteId, varianteId.ToString())}» en su copia {copia[varianteId]:0.###}, "
                    + $"en la caja principal {alla:0.###}.");
            if (distintas.Count > 5)
                diferencias.Add($"{caja.Nombre}: y {distintas.Count - 5} productos más con otra existencia en su copia.");
            if (precioNuevo is { } nuevo && precios.GetValueOrDefault(caja.Numero) is var enLaCopiaPrecio && enLaCopiaPrecio != nuevo)
                diferencias.Add($"{caja.Nombre}: su copia no trae el precio que cambió el dueño ({Formato.Dinero(nuevo)}): "
                    + (enLaCopiaPrecio is { } p ? $"{Formato.Dinero(p)}." : "no lo tiene."));
        }
        return Revision.De("La copia de cada caja quedó al día", diferencias,
            $"La copia de cada caja tiene las existencias de la caja principal{(precioNuevo is null ? "" : " y el precio que cambió el dueño")}.");
    }

    /// <summary>«ninguna», «una», «3 canceladas»: el texto según cuántas (con {0} para el número).</summary>
    private static string Cuantas(int cuantas, string una, string varias) => Cuantas(cuantas, varias, una, varias);

    private static string Cuantas(int cuantas, string ninguna, string una, string varias) =>
        string.Format(Mexico, cuantas switch { 0 => ninguna, 1 => una, _ => varias }, cuantas.ToString("N0", Mexico));

    private static readonly System.Globalization.CultureInfo Mexico = System.Globalization.CultureInfo.GetCultureInfo("es-MX");

    public static long Milesimas(decimal unidades) => (long)Math.Round(unidades * Cantidad.Escala, MidpointRounding.AwayFromZero);
}
