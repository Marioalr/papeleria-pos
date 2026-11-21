namespace PapeleriaPos.Nucleo.Configuracion;

/// <summary>
/// La letra de la impresora de tickets (ESC M): la A, la de siempre, de 12×24 puntos (48 caracteres en 80 mm); la B, más chica,
/// de 9×17 (64 caracteres). Va por su número en la copia de cada caja y en el ticket: no se cambia el orden.
/// </summary>
public enum LetraTicket
{
    A,
    B,
}

/// <summary>El tamaño de la letra (GS !): normal o alta (doble alto, con los mismos caracteres por renglón).</summary>
public enum TamanoLetraTicket
{
    Normal,
    Alta,
}

/// <summary>El espacio entre renglones (ESC 2 y ESC 3): el de la impresora, más junto (gasta menos papel) o más separado.</summary>
public enum InterlineadoTicket
{
    Normal,
    Junto,
    Separado,
}

/// <summary>
/// Cómo sale el ticket de venta (Configuración › Ticket; dueño, 2026-11-21): la letra, su tamaño, el interlineado, los nombres de
/// los productos con mayúscula en cada palabra y qué renglones lleva. Por omisión, como salía antes, salvo los nombres, que salen
/// con mayúsculas. Se guarda en la sucursal (las dos cajas comparten la impresora) y llega a cada caja con su copia y con el
/// ticket de cada cobro. Uno que no trae algo (de una caja o una base de antes) lo toma de aquí.
/// </summary>
/// <param name="NombresConMayusculas">«Cuaderno Profesional 100 Hojas»: solo lo impreso; el nombre guardado no cambia.</param>
/// <param name="TotalResaltado">El total grande y en negritas, como siempre; apagado, como los demás renglones.</param>
/// <param name="MostrarCajero">«Atendió: …» junto a la caja.</param>
/// <param name="MostrarAhorro">
/// Bajo cada producto, lo que se ahorró (mayoreo, promoción, descuento) y «Usted ahorró» al final. Apagado, cada renglón dice lo
/// que se cobró y nada más.
/// </param>
public sealed record FormatoTicket(
    bool NombresConMayusculas = true, LetraTicket Letra = LetraTicket.A, TamanoLetraTicket Tamano = TamanoLetraTicket.Normal,
    InterlineadoTicket Interlineado = InterlineadoTicket.Normal, bool TotalResaltado = true, bool MostrarCajero = true, bool MostrarAhorro = true)
{
    /// <summary>Puntos de ancho del papel de 80 mm en la POS-8360.</summary>
    public const int Puntos = 576;

    public static FormatoTicket Predeterminado { get; } = new();

    /// <summary>Caracteres por renglón en papel de 80 mm: 48 con la letra A, 64 con la B.</summary>
    public int Columnas => Letra == LetraTicket.B ? 64 : 48;

    /// <summary>El mismo, si todo lo que trae existe; si no (un número que no es ninguna opción), lo dice.</summary>
    public FormatoTicket Validado() =>
        Enum.IsDefined(Letra) && Enum.IsDefined(Tamano) && Enum.IsDefined(Interlineado)
            ? this
            : throw new ArgumentException("La letra del ticket es A o B; su tamaño, normal o alta; y los renglones, normales, juntos o separados.");

    public static string Nombre(LetraTicket letra) => letra == LetraTicket.B ? "B (chica, 64 por renglón)" : "A (la de siempre, 48 por renglón)";

    public static string Nombre(TamanoLetraTicket tamano) => tamano == TamanoLetraTicket.Alta ? "Alta" : "Normal";

    public static string Nombre(InterlineadoTicket interlineado) => interlineado switch
    {
        InterlineadoTicket.Junto => "Juntos",
        InterlineadoTicket.Separado => "Separados",
        _ => "Normales",
    };

    /// <summary>Lo que cambió contra <paramref name="antes"/>, como se anota en la bitácora: «la letra: B», «quién atendió: no».</summary>
    public IReadOnlyList<string> Cambios(FormatoTicket antes)
    {
        var cambios = new List<string>();
        if (NombresConMayusculas != antes.NombresConMayusculas)
            cambios.Add($"los nombres con mayúsculas: {SiNo(NombresConMayusculas)}");
        if (Letra != antes.Letra)
            cambios.Add($"la letra: {Letra}");
        if (Tamano != antes.Tamano)
            cambios.Add($"el tamaño de la letra: {Nombre(Tamano).ToLowerInvariant()}");
        if (Interlineado != antes.Interlineado)
            cambios.Add($"los renglones: {Nombre(Interlineado).ToLowerInvariant()}");
        if (TotalResaltado != antes.TotalResaltado)
            cambios.Add($"el total grande y en negritas: {SiNo(TotalResaltado)}");
        if (MostrarCajero != antes.MostrarCajero)
            cambios.Add($"quién atendió: {SiNo(MostrarCajero)}");
        if (MostrarAhorro != antes.MostrarAhorro)
            cambios.Add($"lo que se ahorró: {SiNo(MostrarAhorro)}");
        return cambios;
    }

    private static string SiNo(bool si) => si ? "sí" : "no";
}
