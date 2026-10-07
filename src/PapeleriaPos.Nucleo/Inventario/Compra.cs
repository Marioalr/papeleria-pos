namespace PapeleriaPos.Nucleo.Inventario;

public enum EstadoCompra
{
    /// <summary>Quien recibe está contando lo que llegó, a ciegas.</summary>
    Recibiendo,

    /// <summary>El conteo se cerró: falta capturar la nota del proveedor (los costos).</summary>
    PorCostear,

    /// <summary>Entró al kardex. Ya no se cambia: un error se corrige con un ajuste.</summary>
    Cerrada,

    Cancelada,
}

/// <summary>
/// Una recepción de mercancía (docs/fase-2-inventario.md, entrega 2). Quien recibe cuenta lo que llegó sin ver la nota;
/// después, quien captura los costos pasa los renglones de la nota y el POS compara las dos cosas. Al cerrarla, lo
/// contado entra al kardex con el costo de la nota y las diferencias quedan para reclamarlas al proveedor.
/// </summary>
public class Compra
{
    public Guid Id { get; init; } = Ids.Nuevo();
    public Guid SucursalId { get; init; }
    public Guid ProveedorId { get; init; }

    /// <summary>Consecutivo de la base: C-000123.</summary>
    public int Folio { get; init; }

    /// <summary>El número de la nota o factura del proveedor, si lo trae.</summary>
    public string? Documento { get; set; }

    public EstadoCompra Estado { get; private set; } = EstadoCompra.Recibiendo;

    public Guid RecibioId { get; init; }
    public Guid? CosteoId { get; private set; }
    public DateTime AbiertaUtc { get; init; }
    public DateTime? ContadaUtc { get; private set; }
    public DateTime? CerradaUtc { get; private set; }

    /// <summary>Por qué se canceló.</summary>
    public string? Motivo { get; private set; }

    /// <summary>Lo que se contó, por presentación (la caja de 12 se cuenta por cajas).</summary>
    public List<CompraConteo> Conteo { get; init; } = [];

    /// <summary>Los renglones de la nota: lo que el proveedor dice que mandó y lo que cobra.</summary>
    public List<CompraRenglon> Renglones { get; init; } = [];

    public string FolioTexto => $"C-{Folio:D6}";

    public long TotalNotaCentavos => Renglones.Sum(r => r.ImporteCentavos);

    /// <summary>Pone la cantidad contada de una presentación (cero la quita). Solo mientras se recibe.</summary>
    public void Contar(Guid varianteId, Guid presentacionId, Cantidad cantidad, Cantidad factor)
    {
        RequerirEstado(EstadoCompra.Recibiendo, "El conteo ya se cerró.");
        if (cantidad.Milesimas < 0)
            throw new ArgumentOutOfRangeException(nameof(cantidad), "La cantidad no puede ser negativa.");
        var linea = Conteo.FirstOrDefault(c => c.PresentacionId == presentacionId);
        if (cantidad.Milesimas == 0)
        {
            if (linea is not null)
                Conteo.Remove(linea);
            return;
        }
        if (linea is null)
        {
            linea = new CompraConteo { CompraId = Id, VarianteId = varianteId, PresentacionId = presentacionId };
            Conteo.Add(linea);
        }
        linea.Cantidad = cantidad;
        linea.CantidadBase = cantidad.Por(factor);
    }

    public void TerminarConteo(DateTime ahoraUtc)
    {
        RequerirEstado(EstadoCompra.Recibiendo, "El conteo ya se cerró.");
        if (Conteo.Count == 0)
            throw new InvalidOperationException("Cuenta al menos un producto antes de terminar.");
        Estado = EstadoCompra.PorCostear;
        ContadaUtc = ahoraUtc;
    }

    /// <summary>Agrega o cambia un renglón de la nota. Solo con el conteo cerrado: así quien cuenta no ve la nota.</summary>
    public CompraRenglon Renglon(
        Guid? renglonId, Guid varianteId, Guid presentacionId, Cantidad cantidad, Cantidad factor, long importeCentavos, string descripcion,
        string? codigoProveedor)
    {
        RequerirEstado(EstadoCompra.PorCostear, "La nota se captura cuando termina el conteo.");
        if (!cantidad.EsPositiva)
            throw new ArgumentOutOfRangeException(nameof(cantidad), "La cantidad de la nota va mayor a cero.");
        if (importeCentavos < 0)
            throw new ArgumentOutOfRangeException(nameof(importeCentavos), "El importe no puede ser negativo.");
        var renglon = renglonId is { } id
            ? Renglones.FirstOrDefault(r => r.Id == id) ?? throw new InvalidOperationException("El renglón ya no existe.")
            : null;
        if (renglon is null)
        {
            renglon = new CompraRenglon { CompraId = Id, Orden = Renglones.Count == 0 ? 1 : Renglones.Max(r => r.Orden) + 1 };
            Renglones.Add(renglon);
        }
        renglon.VarianteId = varianteId;
        renglon.PresentacionId = presentacionId;
        renglon.Cantidad = cantidad;
        renglon.CantidadBase = cantidad.Por(factor);
        renglon.ImporteCentavos = importeCentavos;
        renglon.Descripcion = descripcion;
        renglon.CodigoProveedor = string.IsNullOrWhiteSpace(codigoProveedor) ? null : codigoProveedor.Trim();
        return renglon;
    }

    public void QuitarRenglon(Guid renglonId)
    {
        RequerirEstado(EstadoCompra.PorCostear, "La nota ya no se puede cambiar.");
        Renglones.Remove(Renglones.FirstOrDefault(r => r.Id == renglonId) ?? throw new InvalidOperationException("El renglón ya no existe."));
    }

    public void Cerrar(Guid costeoId, DateTime ahoraUtc)
    {
        RequerirEstado(EstadoCompra.PorCostear, Estado == EstadoCompra.Recibiendo ? "Primero termina el conteo." : "La compra ya se cerró.");
        if (Renglones.Count == 0)
            throw new InvalidOperationException("Captura la nota del proveedor antes de cerrar.");
        Estado = EstadoCompra.Cerrada;
        CosteoId = costeoId;
        CerradaUtc = ahoraUtc;
    }

    public void Cancelar(string motivo, DateTime ahoraUtc)
    {
        if (Estado is EstadoCompra.Cerrada or EstadoCompra.Cancelada)
            throw new InvalidOperationException("Una compra cerrada no se cancela: se corrige con un ajuste.");
        if (string.IsNullOrWhiteSpace(motivo))
            throw new ArgumentException("Escribe por qué se cancela.");
        Estado = EstadoCompra.Cancelada;
        Motivo = motivo.Trim();
        CerradaUtc = ahoraUtc;
    }

    private void RequerirEstado(EstadoCompra estado, string mensaje)
    {
        if (Estado != estado)
            throw new InvalidOperationException(Estado == EstadoCompra.Cancelada ? "La compra se canceló." : mensaje);
    }
}

/// <summary>Lo que contó quien recibió, de una presentación.</summary>
public class CompraConteo
{
    public Guid Id { get; init; } = Ids.Nuevo();
    public Guid CompraId { get; init; }
    public Guid VarianteId { get; init; }
    public Guid PresentacionId { get; init; }

    /// <summary>En presentaciones: 3 cajas.</summary>
    public Cantidad Cantidad { get; set; }

    /// <summary>En unidad base, con el factor de cuando se contó: 36 piezas.</summary>
    public Cantidad CantidadBase { get; set; }
}

/// <summary>Un renglón de la nota del proveedor: qué producto, cuánto dice que mandó y cuánto cobra por todo el renglón.</summary>
public class CompraRenglon
{
    public Guid Id { get; init; } = Ids.Nuevo();
    public Guid CompraId { get; init; }
    public int Orden { get; init; }

    /// <summary>La clave con la que el proveedor conoce el producto, si viene en la nota.</summary>
    public string? CodigoProveedor { get; set; }

    /// <summary>Como viene en la nota (o el nombre del producto si se capturó a mano).</summary>
    public string Descripcion { get; set; } = "";

    public Guid VarianteId { get; set; }
    public Guid PresentacionId { get; set; }

    /// <summary>En la presentación de la nota (cajas, paquetes, piezas).</summary>
    public Cantidad Cantidad { get; set; }

    public Cantidad CantidadBase { get; set; }

    /// <summary>Lo que se paga por el renglón, con IVA y descuentos: el costo es lo que sale de la caja.</summary>
    public long ImporteCentavos { get; set; }
}
