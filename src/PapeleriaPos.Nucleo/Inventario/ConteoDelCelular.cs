namespace PapeleriaPos.Nucleo.Inventario;

/// <summary>
/// Cómo va una lista contada con el celular (docs/celular-como-caja.md, «Mandar un conteo a una caja»). Se guardan por su nombre:
/// nunca se renombran, y los nuevos van al final.
/// </summary>
public enum EstadoConteoDelCelular
{
    /// <summary>Llegó a la caja y espera a que la revisen.</summary>
    Recibida,

    /// <summary>Se aplicó tal como llegó.</summary>
    Aceptada,

    /// <summary>Se aplicó después de cambiar una cantidad o quitar un producto en la caja.</summary>
    AceptadaConCambios,

    /// <summary>No se aplicó nada: la existencia no cambió.</summary>
    Rechazada,
}

/// <summary>
/// Cómo va un producto de una lista aceptada en el piloto con eleventa (docs/celular-como-caja.md, «En el piloto con eleventa: la lista
/// por capturar», 2026-11-27): ahí aceptar no cambia existencias, deja la diferencia para capturarla en eleventa. Se guardan por su
/// nombre: nunca se renombran, y los nuevos van al final (los de mandarla al puente: en camino, ya quedó, por revisar).
/// </summary>
public enum ConteoEnEleventa
{
    /// <summary>Tiene diferencia y espera a que alguien la capture a mano en eleventa (Inventario › Ajustes).</summary>
    PorCapturar,

    /// <summary>Quien puede dijo «Ya lo capturé en eleventa».</summary>
    Capturado,

    /// <summary>El producto se dio de alta aquí y no existe en eleventa: no hay dónde capturarlo.</summary>
    NoEsta,
}

/// <summary>
/// Una lista contada con el celular y mandada a una caja (dueño, 2026-11-22): cada producto con lo que hay en total (el anaquel, el
/// mostrador y la bodega). El celular nunca cambia existencias: en la caja, quien tiene «Conteo completo de un departamento» la
/// acepta (tal cual o cambiando cantidades y quitando productos) o la rechaza. Al aceptarla, cada producto queda como en el conteo
/// completo: lo contado reemplaza la existencia, y lo que se vendió (o entró) después de contar sigue contando. Lo que no viene en la
/// lista no se toca.
/// </summary>
public class ConteoDelCelular
{
    /// <summary>Lo pone el celular, uno por lista: mandarla otra vez con el mismo no la duplica.</summary>
    public Guid Id { get; init; }

    public Guid SucursalId { get; init; }

    /// <summary>El celular que la mandó (una caja más, que nunca abre turno).</summary>
    public Guid CelularId { get; init; }

    /// <summary>La caja a la que llegó: solo ahí se acepta o se rechaza.</summary>
    public Guid CajaId { get; init; }

    /// <summary>Quien contó, si el celular entró con su usuario; si no, solo se sabe el celular.</summary>
    public Guid? ContoId { get; init; }

    /// <summary>Cuándo se contó (la hora del celular, sin pasar de cuando llegó): desde ahí cuenta lo que se vendió.</summary>
    public DateTime ContadoUtc { get; init; }

    public DateTime RecibidoUtc { get; init; }

    public EstadoConteoDelCelular Estado { get; private set; }

    /// <summary>Quien la aceptó o la rechazó en la caja.</summary>
    public Guid? ResolvioId { get; private set; }

    public DateTime? ResueltoUtc { get; private set; }

    /// <summary>Por qué se rechazó (si lo escribieron).</summary>
    public string? Motivo { get; private set; }

    /// <summary>En el piloto con eleventa: quién dijo «Ya lo capturé en eleventa» y cuándo. Vacío mientras no, y fuera del piloto.</summary>
    public Guid? CapturoId { get; private set; }

    public DateTime? CapturadoUtc { get; private set; }

    public List<ConteoDelCelularRenglon> Renglones { get; init; } = [];

    public bool Pendiente => Estado == EstadoConteoDelCelular.Recibida;

    /// <summary>Aceptada en el piloto con eleventa y con diferencias que nadie ha dicho que ya capturó allá.</summary>
    public bool PorCapturar => Renglones.Any(r => r.Eleventa == ConteoEnEleventa.PorCapturar);

    /// <summary>«Ya lo capturé en eleventa»: cada diferencia que esperaba queda como capturada.</summary>
    public void Capturar(Guid usuarioId, DateTime ahoraUtc)
    {
        if (!PorCapturar)
            throw new InvalidOperationException("Esa lista ya no tiene nada por capturar en eleventa.");
        foreach (var r in Renglones.Where(r => r.Eleventa == ConteoEnEleventa.PorCapturar))
            r.Eleventa = ConteoEnEleventa.Capturado;
        (CapturoId, CapturadoUtc) = (usuarioId, ahoraUtc);
    }

    public void Aceptar(bool conCambios, Guid usuarioId, DateTime ahoraUtc)
    {
        ExigirPendiente();
        (Estado, ResolvioId, ResueltoUtc) = (conCambios ? EstadoConteoDelCelular.AceptadaConCambios : EstadoConteoDelCelular.Aceptada, usuarioId, ahoraUtc);
    }

    public void Rechazar(string? motivo, Guid usuarioId, DateTime ahoraUtc)
    {
        ExigirPendiente();
        (Estado, ResolvioId, ResueltoUtc, Motivo) = (EstadoConteoDelCelular.Rechazada, usuarioId, ahoraUtc, string.IsNullOrWhiteSpace(motivo) ? null : motivo.Trim());
    }

    private void ExigirPendiente()
    {
        if (!Pendiente)
            throw new InvalidOperationException(Estado == EstadoConteoDelCelular.Rechazada ? "Esa lista ya se rechazó." : "Esa lista ya se aplicó.");
    }
}

/// <summary>
/// Un producto de la lista: lo que se contó de él en su unidad base (las cajas de 12 ya convertidas y sumadas con las piezas
/// sueltas) y, al resolverla, qué se aplicó.
/// </summary>
public class ConteoDelCelularRenglon
{
    public Guid Id { get; init; } = Ids.Nuevo();
    public Guid ConteoId { get; init; }
    public int Orden { get; init; }
    public Guid VarianteId { get; init; }

    /// <summary>Su nombre cuando llegó (por si después cambia).</summary>
    public string Descripcion { get; init; } = "";

    /// <summary>Cómo lo mandó el celular, si no fue solo en piezas: «2 Caja 12 + 5 Pieza».</summary>
    public string? Detalle { get; init; }

    /// <summary>Lo que contó el celular, en unidad base.</summary>
    public Cantidad Contado { get; init; }

    /// <summary>
    /// Cuándo se contó este producto (su último escaneo), si el celular lo manda: lo vendido después se cuenta desde aquí. Vacío: vale
    /// la hora de la lista.
    /// </summary>
    public DateTime? ContadoUtc { get; init; }

    /// <summary>Lo que quedó al aceptarla (cambiado o no en la caja); vacío si se quitó, o si se rechazó la lista.</summary>
    public Cantidad? Aceptado { get; set; }

    /// <summary>Lo que se movió desde que se contó hasta que se aplicó (lo vendido, con signo −; lo que entró, +).</summary>
    public Cantidad? Desde { get; set; }

    /// <summary>Lo que decía el sistema justo antes de aplicarlo.</summary>
    public Cantidad? Sistema { get; set; }

    /// <summary>Lo que vale la diferencia que entró al kardex, con signo.</summary>
    public long? CostoDiferenciaCentavos { get; set; }

    /// <summary>
    /// No se aplicó porque ya se contó después (otra lista contada más tarde, el conteo del día, un ajuste o el conteo completo): vale
    /// lo más nuevo.
    /// </summary>
    public bool Superado { get; set; }

    /// <summary>
    /// En el piloto con eleventa, cómo va su diferencia allá; vacío fuera del piloto, si cuadró (nada que capturar), si se quitó o si
    /// ya se había contado después.
    /// </summary>
    public ConteoEnEleventa? Eleventa { get; set; }

    /// <summary>La diferencia que salió al aceptarlo, con signo: lo aceptado más lo movido desde que se contó, menos lo que decía el sistema.</summary>
    public Cantidad? Diferencia => Aceptado is { } aceptado && Desde is { } desde && Sistema is { } sistema ? aceptado + desde - sistema : null;

    /// <summary>
    /// Ya quedó en el inventario: se aceptó, no se quitó, no había algo más nuevo y, en el piloto con eleventa, no espera a que lo
    /// capturen allá (ni le falta existir allá).
    /// </summary>
    public bool Aplicado => Aceptado is not null && !Superado && Eleventa is not (ConteoEnEleventa.PorCapturar or ConteoEnEleventa.NoEsta);
}
