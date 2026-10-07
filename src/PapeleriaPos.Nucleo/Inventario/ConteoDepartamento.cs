namespace PapeleriaPos.Nucleo.Inventario;

public enum EstadoConteoDepartamento
{
    Abierto,
    Cerrado,
    Cancelado,
}

/// <summary>
/// El conteo completo de un departamento (docs/fase-2-inventario.md, entrega 3, parte 3): el de arranque antes del piloto
/// y uno al año. Lo abre el encargado; varias personas cuentan a la vez desde el celular y cada captura se suma a lo
/// que ya contaron los demás (el anaquel, la bodega). Al cerrarlo, lo contado reemplaza la existencia: lo que nadie
/// contó queda en cero.
/// </summary>
public class ConteoDepartamento
{
    public Guid Id { get; init; } = Ids.Nuevo();
    public Guid SucursalId { get; init; }
    public Guid DepartamentoId { get; init; }
    public EstadoConteoDepartamento Estado { get; private set; }
    public Guid AbiertoPorId { get; init; }
    public DateTime AbiertoUtc { get; init; }

    /// <summary>Quien lo cerró o lo canceló.</summary>
    public Guid? TerminadoPorId { get; private set; }

    public DateTime? TerminadoUtc { get; private set; }
    public List<CapturaConteo> Capturas { get; init; } = [];

    /// <summary>El resultado al cerrar: cada producto con lo contado, lo que decía el sistema y lo que vale la diferencia.</summary>
    public List<ConteoDepartamentoRenglon> Renglones { get; init; } = [];

    /// <summary>Lo contado de cada producto: la suma de sus capturas vigentes (las de todos).</summary>
    public Dictionary<Guid, Cantidad> Contado() =>
        Capturas.Where(c => c.Vigente).GroupBy(c => c.VarianteId)
            .ToDictionary(g => g.Key, g => g.Aggregate(Cantidad.Cero, (suma, c) => suma + c.Cantidad));

    /// <summary>
    /// Contra qué se compara un producto contado: la existencia del sistema en su primera captura vigente. Lo que se venda
    /// o llegue después de contarlo sigue moviendo la existencia al cerrar.
    /// </summary>
    public Cantidad? EsperadoDe(Guid varianteId) =>
        Capturas.Where(c => c.Vigente && c.VarianteId == varianteId).OrderBy(c => c.CapturadoUtc).FirstOrDefault()?.Esperado;

    public CapturaConteo Capturar(Guid capturaId, Guid varianteId, Cantidad cantidad, Cantidad esperado, Guid usuarioId, DateTime ahoraUtc)
    {
        ExigirAbierto();
        if (cantidad.Milesimas < 0)
            throw new ArgumentException("Lo contado no puede ser negativo.");
        var captura = new CapturaConteo
        {
            Id = capturaId,
            ConteoId = Id,
            VarianteId = varianteId,
            Cantidad = cantidad,
            Esperado = esperado,
            UsuarioId = usuarioId,
            CapturadoUtc = ahoraUtc,
        };
        Capturas.Add(captura);
        return captura;
    }

    /// <summary>Quita una captura equivocada. Cada quien quita las suyas; el encargado, cualquiera.</summary>
    public void Quitar(CapturaConteo captura, Guid usuarioId, bool puedeQuitarDeOtros, DateTime ahoraUtc)
    {
        ExigirAbierto();
        if (captura.ConteoId != Id)
            throw new ArgumentException("Esa captura es de otro conteo.", nameof(captura));
        if (captura.UsuarioId != usuarioId && !puedeQuitarDeOtros)
            throw new InvalidOperationException("Solo puedes quitar lo que contaste tú; lo de otros lo quita el encargado.");
        captura.Quitar(usuarioId, ahoraUtc);
    }

    public void Cerrar(IEnumerable<ConteoDepartamentoRenglon> renglones, Guid usuarioId, DateTime ahoraUtc)
    {
        ExigirAbierto();
        Renglones.AddRange(renglones);
        (Estado, TerminadoPorId, TerminadoUtc) = (EstadoConteoDepartamento.Cerrado, usuarioId, ahoraUtc);
    }

    /// <summary>Se descarta lo contado y la existencia no cambia.</summary>
    public void Cancelar(Guid usuarioId, DateTime ahoraUtc)
    {
        ExigirAbierto();
        (Estado, TerminadoPorId, TerminadoUtc) = (EstadoConteoDepartamento.Cancelado, usuarioId, ahoraUtc);
    }

    private void ExigirAbierto()
    {
        if (Estado != EstadoConteoDepartamento.Abierto)
            throw new InvalidOperationException(Estado == EstadoConteoDepartamento.Cerrado
                ? "Ese conteo ya se cerró: lo contado ya reemplazó la existencia."
                : "Ese conteo se canceló.");
    }
}

/// <summary>Lo que contó una persona de un producto en un lugar (anaquel, mostrador, bodega). No se edita: se quita y se vuelve a capturar.</summary>
public class CapturaConteo
{
    public Guid Id { get; init; } = Ids.Nuevo();
    public Guid ConteoId { get; init; }
    public Guid VarianteId { get; init; }
    public Cantidad Cantidad { get; init; }

    /// <summary>La existencia del sistema al capturar (quien cuenta no la ve).</summary>
    public Cantidad Esperado { get; init; }

    public Guid UsuarioId { get; init; }
    public DateTime CapturadoUtc { get; init; }
    public Guid? QuitadaPorId { get; private set; }
    public DateTime? QuitadaUtc { get; private set; }

    public bool Vigente => QuitadaUtc is null;

    internal void Quitar(Guid usuarioId, DateTime ahoraUtc)
    {
        if (!Vigente)
            throw new InvalidOperationException("Esa captura ya se quitó.");
        (QuitadaPorId, QuitadaUtc) = (usuarioId, ahoraUtc);
    }
}

/// <summary>Un producto del departamento al cerrar el conteo.</summary>
public class ConteoDepartamentoRenglon
{
    public Guid Id { get; init; } = Ids.Nuevo();
    public Guid ConteoId { get; init; }
    public Guid VarianteId { get; init; }

    /// <summary>Lo que contaron entre todos; vacío si nadie lo contó (quedó en cero).</summary>
    public Cantidad? Contado { get; init; }

    /// <summary>Lo que decía el sistema: al contarlo por primera vez o, si nadie lo contó, al cerrar.</summary>
    public Cantidad Esperado { get; init; }

    /// <summary>Lo que vale la diferencia, con signo: + sobró, − faltó.</summary>
    public long CostoDiferenciaCentavos { get; init; }

    public Cantidad Diferencia => (Contado ?? Cantidad.Cero) - Esperado;
}
