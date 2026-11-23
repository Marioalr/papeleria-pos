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

    public List<ConteoDelCelularRenglon> Renglones { get; init; } = [];

    public bool Pendiente => Estado == EstadoConteoDelCelular.Recibida;

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

    /// <summary>Lo que entró al kardex: se aceptó, no se quitó y no había algo más nuevo.</summary>
    public bool Aplicado => Aceptado is not null && !Superado;
}
