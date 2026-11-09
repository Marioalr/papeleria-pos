using PapeleriaPos.Nucleo;

namespace PapeleriaPos.Impresiones;

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

    /// <summary>
    /// La copia de un trámite («T-0042 · CURP», <see cref="TrabajoDeTramite"/>; 2026-10-31): va incluida en él y se cobra con su
    /// producto en el ticket, así que no sale en F2 ni lleva precio; se queda su costo. Las copias de más de un trámite, no: esas se
    /// cobran en F2 como cualquier impresión.
    /// </summary>
    EnTramite,
}

public enum TipoTrabajoImpresion
{
    /// <summary>Salió desde una PC: lo midió el agente de esa caja.</summary>
    Impresion,

    /// <summary>Copias hechas en el panel de la impresora: se registran a mano al cobrarlas.</summary>
    Copia,

    /// <summary>
    /// Caras que contó la impresora y nadie registró (2026-11-08): copias del panel o lo que se imprimió desde un celular. La tarjeta
    /// «Copias o celular» la pone sola el servicio (<see cref="ConciliacionDelContador"/>), con el precio estimado; va en la línea
    /// «Impresiones». Sus <see cref="TrabajoImpresion.CarasContadas"/> son las que se le cuentan al contador; sus <see cref="TrabajoImpresion.Caras"/>,
    /// las que se cobran.
    /// </summary>
    Contador,
}

public enum MotivoNoVendida
{
    NoEraLoQueQueria,
    SalioMal,
    Atasco,
    Otro,
}

/// <summary>
/// Un trabajo que salió de una impresora de la sucursal desde una de sus PC: lo midió el agente de esa caja y el servicio le puso
/// costo y precio con los costos de esa impresora (el modelo de Control Print; <see cref="CostosDeImpresora"/>).
/// </summary>
public class TrabajoImpresion
{
    public Guid Id { get; set; } = Ids.Nuevo();
    public Guid SucursalId { get; set; }

    /// <summary>
    /// La impresora de donde salió (2026-11-03); en las copias, la del panel donde se hicieron. Vacío solo en lo de antes de varias
    /// impresoras que no se pudo ligar: cuenta como de la primera de la sucursal.
    /// </summary>
    public Guid? ImpresoraId { get; set; }

    public TipoTrabajoImpresion Tipo { get; set; }

    /// <summary>La caja de la PC desde la que se imprimió (en las copias, la que las registró).</summary>
    public Guid CajaId { get; set; }

    /// <summary>Quien atendía en esa caja al imprimir (vacío si nadie había entrado).</summary>
    public Guid? AtendioId { get; set; }

    public required string Pc { get; set; }
    public required string Cola { get; set; }
    public long SpoolId { get; set; }
    public required string Documento { get; set; }
    public string? UsuarioWindows { get; set; }
    public DateTime ImpresoUtc { get; set; }

    /// <summary>Cuando se mandó a la cola: desde ahí puede subir el contador de la impresora.</summary>
    public DateTime? EnviadoUtc { get; set; }

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

    /// <summary>Tinta que estimó el modelo por canal (pulgadas cuadradas a cobertura total): la base de la calibración.</summary>
    public double TintaC { get; set; }
    public double TintaM { get; set; }
    public double TintaY { get; set; }
    public double TintaK { get; set; }

    /// <summary>
    /// Caras que contó la impresora para este trabajo (vacío mientras no se verifica). Menos que <see cref="Caras"/>:
    /// un atasco o una cancelación en el panel, que Windows no ve.
    /// </summary>
    public int? CarasContadas { get; set; }

    public bool FaltaronCaras => CarasContadas is { } c && c < Caras;

    // Costo y precio (solo el dueño ve el costo).
    public long CostoTintaCentavos { get; set; }
    public long CostoPapelCentavos { get; set; }

    /// <summary>Caja de mantenimiento, luz, depreciación y extras.</summary>
    public long CostoOtrosCentavos { get; set; }

    public long CostoCentavos { get; set; }
    public decimal MargenPorcentaje { get; set; }
    public long PrecioCentavos { get; set; }

    /// <summary>
    /// El precio del trabajo completo cuando se cobra solo lo que salió (<see cref="CobrarSoloLoQueSalio"/>); en las
    /// copias, el precio sugerido si se cobró otro.
    /// </summary>
    public long? PrecioCompletoCentavos { get; set; }

    /// <summary>Cobertura por página y ajustes, para volver a calcular si cambian los costos.</summary>
    public required string DetalleJson { get; set; }

    // Cobro.
    public EstadoCobroImpresion Cobro { get; set; }
    public Guid? VentaId { get; set; }
    public DateTime? CobroUtc { get; set; }
    public MotivoNoVendida? MotivoNoVendida { get; set; }
    public string? NotaNoVendida { get; set; }
    public Guid? MarcoId { get; set; }

    // Lo cotizado (2026-11-08): si es la impresión de un PDF que se cotizó antes (<see cref="CotizacionImpresion"/>, <see cref="LoCotizado"/>).

    /// <summary>La cotización de la que es la impresión.</summary>
    public Guid? CotizacionId { get; set; }

    /// <summary>El precio que se cotizó.</summary>
    public long? PrecioCotizadoCentavos { get; set; }

    /// <summary>El precio que calculó la impresión (el de siempre), para cobrarlo en lugar de lo cotizado.</summary>
    public long? PrecioImpresoCentavos { get; set; }

    /// <summary>Se cobra lo cotizado (<see cref="PrecioCentavos"/> es lo cotizado); si no, lo impreso.</summary>
    public bool CobraLoCotizado { get; set; }

    /// <summary>
    /// Es la impresión de una cotización: se guarda lo cotizado y lo impreso, y se cobra lo cotizado si <paramref name="cobrarLoCotizado"/>
    /// (salió igual y su precio está cerca: <see cref="LoCotizado.SeCobraSinPreguntar"/>).
    /// </summary>
    public void LigarCotizacion(CotizacionImpresion cotizacion, bool cobrarLoCotizado)
    {
        (CotizacionId, PrecioCotizadoCentavos, PrecioImpresoCentavos) = (cotizacion.Id, cotizacion.PrecioCentavos, PrecioCentavos);
        ElegirLoCotizado(cobrarLoCotizado);
    }

    /// <summary>El cajero elige qué se cobra: lo cotizado o lo que calculó la impresión.</summary>
    public void CobrarLoCotizado(bool loCotizado)
    {
        if (Cobro != EstadoCobroImpresion.PorCobrar)
            throw new InvalidOperationException($"«{Documento}» ya no está por cobrar.");
        if (CotizacionId is null)
            throw new InvalidOperationException($"«{Documento}» no se cotizó.");
        if (PrecioCompletoCentavos is not null)
            throw new InvalidOperationException("Ya se cobra solo lo que salió.");
        ElegirLoCotizado(loCotizado);
    }

    private void ElegirLoCotizado(bool loCotizado)
    {
        CobraLoCotizado = loCotizado;
        PrecioCentavos = loCotizado ? PrecioCotizadoCentavos!.Value : PrecioImpresoCentavos!.Value;
    }

    public void Cobrar(Guid ventaId, DateTime ahoraUtc)
    {
        if (Cobro != EstadoCobroImpresion.PorCobrar)
            throw new InvalidOperationException($"«{Documento}» ya no está por cobrar.");
        (Cobro, VentaId, CobroUtc) = (EstadoCobroImpresion.Cobrada, ventaId, ahoraUtc);
    }

    /// <summary>
    /// Lo que contó la impresora. Si salieron menos caras, el costo (tinta, papel y lo demás) baja en proporción: es lo
    /// que de verdad se gastó. El precio se queda como el del trabajo completo hasta que se decida cobrar solo lo que salió.
    /// </summary>
    public void AplicarCarasContadas(int contadas)
    {
        contadas = Math.Clamp(contadas, 0, Math.Max(0, Caras));
        CarasContadas = contadas;
        if (Caras <= 0 || contadas >= Caras)
            return;
        var f = (double)contadas / Caras;
        (TintaC, TintaM, TintaY, TintaK) = (TintaC * f, TintaM * f, TintaY * f, TintaK * f);
        long Escalar(long centavos) => (long)Math.Round(centavos * f, MidpointRounding.AwayFromZero);
        (CostoTintaCentavos, CostoPapelCentavos, CostoOtrosCentavos) = (Escalar(CostoTintaCentavos), Escalar(CostoPapelCentavos), Escalar(CostoOtrosCentavos));
        CostoCentavos = CostoTintaCentavos + CostoPapelCentavos + CostoOtrosCentavos;
        CarasColor = Math.Min(CarasColor, contadas);
    }

    /// <summary>Con un atasco o una cancelación, se cobran solo las caras que salieron (el precio lo calcula quien llama).</summary>
    public void CobrarSoloLoQueSalio(long precioCentavos)
    {
        if (Cobro != EstadoCobroImpresion.PorCobrar)
            throw new InvalidOperationException($"«{Documento}» ya no está por cobrar.");
        if (!FaltaronCaras || CarasContadas == 0)
            throw new InvalidOperationException(CarasContadas == 0
                ? "No salió ninguna cara: márcala como no vendida (atasco)."
                : "La impresora contó todas las caras de este trabajo.");
        if (PrecioCompletoCentavos is not null)
            throw new InvalidOperationException("Ya se cobra solo lo que salió.");
        (PrecioCompletoCentavos, PrecioCentavos) = (PrecioCentavos, Math.Min(precioCentavos, PrecioCentavos));
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

/// <summary>
/// Los costos y márgenes de impresión de una sucursal (la configuración de Control Print, guardada tal cual en JSON). Cómo se lee cada
/// impresora (su dirección, el aviso de tinta y cada cuánto) es de cada una desde el 2026-11-03 (<see cref="Impresora"/>): antes vivía
/// aquí, y la migración «VariasImpresoras» lo pasó a la impresora 1.
/// </summary>
public class ConfiguracionImpresion
{
    public Guid SucursalId { get; set; }
    public required string CostosJson { get; set; }
    public DateTime ModificadoUtc { get; set; }
}

/// <summary>
/// Unos costos de la sucursal que se reemplazaron (2026-10-29): cada vez que cambian (los de Control Print, o en Configuración), los de
/// antes se quedan aquí, con sus fechas. Así nada se pierde, ni la calibración de la tinta, que va en los costos (un Control Print
/// reinstalado traería los de fábrica). Desde el 2026-11-03 también lo propio de una impresora (<see cref="ImpresoraId"/>).
/// </summary>
public class HistorialCostosImpresion
{
    public Guid Id { get; set; } = Ids.Nuevo();
    public Guid SucursalId { get; set; }

    /// <summary>
    /// Vacío: los costos de la sucursal (<see cref="CostosJson"/> es un <c>CostSettings</c>). Con una impresora: lo propio de ella que se
    /// reemplazó (<see cref="CostosJson"/> es un <see cref="CostosDeImpresora"/>; vacío si no tenía nada propio).
    /// </summary>
    public Guid? ImpresoraId { get; set; }

    /// <summary>Desde cuándo eran los de la sucursal.</summary>
    public DateTime DesdeUtc { get; set; }

    /// <summary>Cuándo se reemplazaron.</summary>
    public DateTime HastaUtc { get; set; }

    /// <summary>De dónde vinieron los que los reemplazaron («Control Print», «Configuración»); vacío si no se sabe.</summary>
    public string? PorLosDe { get; set; }

    public required string CostosJson { get; set; }
}

/// <summary>
/// Una lectura de la impresora por la red (SNMP y su página de estado): niveles en porcentaje restante y contadores.
/// Con ellas se verifican las hojas que salieron, se compara el contador con lo cobrado y se calibra la tinta.
/// </summary>
public class LecturaImpresora
{
    public Guid Id { get; set; } = Ids.Nuevo();
    public Guid SucursalId { get; set; }

    /// <summary>De qué impresora (2026-11-03). Vacío solo en las de antes que no se pudieron ligar: cuentan como de la primera.</summary>
    public Guid? ImpresoraId { get; set; }

    public DateTime TomadaUtc { get; set; }
    public int? TintaK { get; set; }
    public int? TintaC { get; set; }
    public int? TintaM { get; set; }
    public int? TintaY { get; set; }

    /// <summary>Vida restante de la caja de mantenimiento.</summary>
    public int? Mantenimiento { get; set; }

    /// <summary>Caras que lleva la impresora en total (todas las fuentes: PC, copias, celular).</summary>
    public long? Contador { get; set; }

    public long? ContadorColor { get; set; }
    public long? ContadorBN { get; set; }
    public string? Serie { get; set; }
}
