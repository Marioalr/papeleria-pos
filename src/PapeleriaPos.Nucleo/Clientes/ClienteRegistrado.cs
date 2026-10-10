namespace PapeleriaPos.Nucleo.Clientes;

/// <summary>
/// Un cliente de confianza (docs/fase-3-precios-clientes.md, entrega 2): unos 25, dados de alta a mano por el dueño o el
/// encargado, sin RFC. Sus privilegios: descuento fijo, mayoreo siempre y, para la fase 4, pedido sin anticipo y apartar
/// sin pagar. En la caja se elige por nombre o teléfono y queda en el ticket aunque no lleve descuento. Antes eran los
/// «clientes especiales» (solo un descuento): pasaron con su mismo Id, marcados para revisar sus datos.
/// </summary>
public class ClienteRegistrado : IVersionado
{
    public Guid Id { get; set; } = Ids.Nuevo();

    /// <summary>Vacío en los nuevos: es cliente de todas las sucursales. Los que venían de clientes especiales conservan la suya.</summary>
    public Guid? SucursalId { get; set; }

    public required string Nombre { get; set; }

    /// <summary>Solo dígitos: para buscarlo en la caja y, después, mandarle su estado de cuenta.</summary>
    public string? Telefono { get; set; }

    public string? Nota { get; set; }

    /// <summary>Centésimas de porcentaje (1000 = 10 %); cero, sin descuento. No pide autorización: lo aprobó quien lo dio de alta.</summary>
    public int DescuentoCentesimas { get; set; }

    /// <summary>Paga desde la primera pieza el precio de la primera escala de mayoreo; las escalas más altas siguen al llegar a ellas.</summary>
    public bool MayoreoSiempre { get; set; }

    /// <summary>Cuánto se le puede fiar (fase 3, entrega 2); cero, sin crédito. Lo pone quien puede dar crédito (al empezar, el dueño).</summary>
    public long LimiteCreditoCentavos { get; set; }

    /// <summary>Días para pagar lo fiado, sin recargos. Cambiarlo no mueve lo que ya se fió: cada cargo guarda cuándo vence.</summary>
    public int PlazoCreditoDias { get; set; } = PlazoPorOmision;

    public bool TieneCredito => LimiteCreditoCentavos > 0;

    public const int PlazoPorOmision = 30;
    public const long LimiteMaximoCentavos = 10_000_000;
    public const int PlazoMaximoDias = 90;

    /// <summary>Fase 4 (pedidos): se le prepara un pedido sin anticipo.</summary>
    public bool PedidoSinAnticipo { get; set; }

    /// <summary>Fase 4 (pedidos): puede apartar sin pagar.</summary>
    public bool ApartarSinPagar { get; set; }

    public bool Activo { get; set; } = true;

    /// <summary>Venía de los clientes especiales: falta revisar sus datos (teléfono, privilegios). Se quita al guardarlo.</summary>
    public bool PorRevisar { get; set; }

    public long Version { get; set; }
    public DateTime ModificadoUtc { get; set; }

    /// <summary>Con esto empieza el motivo de su descuento en cada renglón: así se distingue de un descuento a mano.</summary>
    public const string PrefijoMotivo = "Cliente: ";

    public static string MotivoDescuento(string nombre) => PrefijoMotivo + nombre;

    /// <summary>El teléfono como se guarda y se busca: solo los dígitos; vacío si no trae ninguno.</summary>
    public static string? SoloDigitos(string? telefono)
    {
        var digitos = new string((telefono ?? "").Where(char.IsAsciiDigit).ToArray());
        return digitos.Length == 0 ? null : digitos;
    }

    /// <summary>"descuento 10 % · mayoreo siempre · crédito": lo que lleva el ticket junto al nombre (vacío si no tiene privilegios de venta).</summary>
    public static string Privilegios(int descuentoCentesimas, bool mayoreoSiempre, bool credito = false)
    {
        var partes = new List<string>();
        if (descuentoCentesimas > 0)
            partes.Add($"descuento {Control.Permisos.TextoMonto(Control.AccionControlada.Descuento, descuentoCentesimas)}");
        if (mayoreoSiempre)
            partes.Add("mayoreo siempre");
        if (credito)
            partes.Add("crédito");
        return string.Join(" · ", partes);
    }
}
