namespace PapeleriaPos.Nucleo.Impresiones;

public enum EstadoTrabajo
{
    Impreso,
    /// <summary>La impresora lo detuvo (atasco, cancelación en el panel): las hojas reales se verifican con el contador.</summary>
    TerminoConError,
}

public enum EstadoCobroImpresion
{
    PorCobrar,
    Cobrada,
    /// <summary>Se imprimió y no se cobró (con motivo): su costo es pérdida.</summary>
    NoVendida,
}

public enum MotivoNoVendida
{
    NoEraLoQueQueria,
    SalioMal,
    Atasco,
    Otro,
}

/// <summary>
/// Un trabajo que salió de la WF-6590 desde una PC de la sucursal: lo midió el agente de esa caja y el servicio le puso
/// costo y precio con la configuración de la sucursal (el modelo de Control Print).
/// </summary>
public class TrabajoImpresion
{
    public Guid Id { get; set; } = Ids.Nuevo();
    public Guid SucursalId { get; set; }

    /// <summary>La caja de la PC desde la que se imprimió.</summary>
    public Guid CajaId { get; set; }

    /// <summary>Quien atendía en esa caja al imprimir (vacío si nadie había entrado).</summary>
    public Guid? AtendioId { get; set; }

    public required string Pc { get; set; }
    public required string Cola { get; set; }
    public long SpoolId { get; set; }
    public required string Documento { get; set; }
    public string? UsuarioWindows { get; set; }
    public DateTime ImpresoUtc { get; set; }
    public EstadoTrabajo Estado { get; set; }

    // Lo que se imprimió.
    public int Paginas { get; set; }
    public int Copias { get; set; }
    public int Caras { get; set; }
    public int CarasColor { get; set; }
    public int Hojas { get; set; }
    public bool Color { get; set; }
    public bool Duplex { get; set; }
    public required string Tamano { get; set; }
    public required string TipoPapel { get; set; }
    public required string Calidad { get; set; }

    /// <summary>Si la tinta se midió página por página (o se estimó por número de páginas).</summary>
    public bool Medido { get; set; }

    public string? Nota { get; set; }
    public byte[]? Miniatura { get; set; }

    // Costo y precio (solo el dueño ve el costo).
    public long CostoTintaCentavos { get; set; }
    public long CostoPapelCentavos { get; set; }

    /// <summary>Caja de mantenimiento, luz, depreciación y extras.</summary>
    public long CostoOtrosCentavos { get; set; }

    public long CostoCentavos { get; set; }
    public decimal MargenPorcentaje { get; set; }
    public long PrecioCentavos { get; set; }

    /// <summary>Cobertura por página y ajustes, para volver a calcular si cambian los costos.</summary>
    public required string DetalleJson { get; set; }

    // Cobro.
    public EstadoCobroImpresion Cobro { get; set; }
    public Guid? VentaId { get; set; }
    public DateTime? CobroUtc { get; set; }
    public MotivoNoVendida? MotivoNoVendida { get; set; }
    public string? NotaNoVendida { get; set; }
    public Guid? MarcoId { get; set; }

    public void Cobrar(Guid ventaId, DateTime ahoraUtc)
    {
        if (Cobro != EstadoCobroImpresion.PorCobrar)
            throw new InvalidOperationException($"«{Documento}» ya no está por cobrar.");
        (Cobro, VentaId, CobroUtc) = (EstadoCobroImpresion.Cobrada, ventaId, ahoraUtc);
    }

    /// <summary>Si se cancela o se devuelve la venta, vuelve a estar por cobrar.</summary>
    public void Liberar() => (Cobro, VentaId, CobroUtc) = (EstadoCobroImpresion.PorCobrar, null, null);

    public void MarcarNoVendida(MotivoNoVendida motivo, string? nota, Guid usuarioId, DateTime ahoraUtc)
    {
        if (Cobro != EstadoCobroImpresion.PorCobrar)
            throw new InvalidOperationException($"«{Documento}» ya no está por cobrar.");
        if (motivo == Impresiones.MotivoNoVendida.Otro && string.IsNullOrWhiteSpace(nota))
            throw new ArgumentException("Con «otro», escribe qué pasó.");
        (Cobro, MotivoNoVendida, NotaNoVendida, MarcoId, CobroUtc) = (EstadoCobroImpresion.NoVendida, motivo, nota?.Trim(), usuarioId, ahoraUtc);
    }
}

/// <summary>Los costos y márgenes de impresión de una sucursal: la configuración de Control Print, guardada tal cual (JSON).</summary>
public class ConfiguracionImpresion
{
    public Guid SucursalId { get; set; }
    public required string CostosJson { get; set; }
    public DateTime ModificadoUtc { get; set; }
}
