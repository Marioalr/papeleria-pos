using System.Globalization;
using PapeleriaPos.Nucleo.Catalogo;
using PapeleriaPos.Nucleo.Contratos;

namespace PapeleriaPos.Interfaz;

/// <summary>
/// Lo que se escribe y se lee en Inventario › «Recibir con factura» (2026-11-27): cuántos llegaron de cada renglón, en la unidad de la
/// factura, y cómo se dice cada estado. Como en los traspasos, pero aparte: los traspasos por archivo se quitan cuando llegue el
/// servidor, y esto se queda.
/// </summary>
public static class FacturasEnPantalla
{
    /// <summary>
    /// Lo escrito en «Llegó», en milésimas de la unidad de la factura: como «Cuántos llegaron» de «Agregar inventario», pero el cero
    /// vale (no llegó).
    /// </summary>
    public static long? Llego(string? texto, UnidadBase unidad, string producto, out string? problema)
    {
        if ((texto ?? "").Trim() is "0" or "0.0" or "0.00" or "0.000")
        {
            problema = null;
            return 0;
        }
        var milesimas = AgregarInventarioEnPantalla.Cantidad(texto, unidad, producto, out problema);
        if (problema is not null)
            problema = problema.Replace("más de cero y hasta", "de cero hasta", StringComparison.Ordinal) + " Si no llegó, escribe 0.";
        return milesimas;
    }

    /// <summary>
    /// En qué se cuenta lo que llegó de un renglón: en la unidad del producto si cada unidad de la factura es una de ellas (la pieza, el
    /// metro); en enteros si es un paquete (cajas). Sin emparejar, en enteros, salvo que la factura traiga fracciones.
    /// </summary>
    public static UnidadBase Unidad(FacturaRenglonDto r) =>
        r.PresentacionId is not null ? r.Factor == 1 ? r.UnidadBase : UnidadBase.Pieza
        : decimal.Truncate(r.Cantidad) == r.Cantidad ? UnidadBase.Pieza : UnidadBase.Metro;

    /// <summary>Una cantidad como se escribe en su campo: «12», «1.5».</summary>
    public static string Texto(decimal cantidad) => cantidad.ToString("0.###", CultureInfo.InvariantCulture);

    public static string Estado(EstadoFactura estado) => estado switch
    {
        EstadoFactura.PorRecibir => "Por recibir",
        EstadoFactura.EnRevision => "En revisión",
        EstadoFactura.Recibida => "Recibida",
        _ => "Descartada",
    };

    /// <summary>El color de su etiqueta: lo que espera a alguien, en azul o ámbar; lo terminado, sin color.</summary>
    public static string Color(EstadoFactura estado) => estado switch
    {
        EstadoFactura.PorRecibir => "azul",
        EstadoFactura.EnRevision => "ambar",
        EstadoFactura.Recibida => "",
        _ => "gris",
    };
}
