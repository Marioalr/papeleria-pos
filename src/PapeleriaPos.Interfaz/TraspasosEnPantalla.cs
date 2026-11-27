using System.Globalization;
using PapeleriaPos.Nucleo.Catalogo;
using PapeleriaPos.Nucleo.Inventario;

namespace PapeleriaPos.Interfaz;

/// <summary>
/// Lo que se escribe y se lee en Inventario › «Traspasos» (2026-11-26, provisional mientras no exista el servidor): cuántos se
/// mandan y cuántos llegaron, con la regla de los números del POS, y cómo se dice cada estado.
/// </summary>
public static class TraspasosEnPantalla
{
    /// <summary>
    /// Lo escrito en «Llegó», en milésimas de la unidad base: como «Cuántos llegaron» de «Agregar inventario»
    /// (<see cref="AgregarInventarioEnPantalla.Cantidad"/>), pero aquí el cero vale: no llegó.
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

    /// <summary>Lo escrito en «Cuántos van»: más de cero.</summary>
    public static long? Van(string? texto, UnidadBase unidad, string producto, out string? problema)
    {
        var milesimas = AgregarInventarioEnPantalla.Cantidad(texto, unidad, producto, out problema);
        problema = problema?.Replace("cuántos llegaron", "cuántos van", StringComparison.Ordinal);
        return milesimas;
    }

    /// <summary>Una cantidad como se escribe en su campo: «12», «1.5».</summary>
    public static string Texto(decimal cantidad) => cantidad.ToString("0.###", CultureInfo.InvariantCulture);

    public static string Estado(EstadoTraspaso estado) => estado switch
    {
        EstadoTraspaso.Borrador => "Borrador",
        EstadoTraspaso.Enviado => "Exportado",
        EstadoTraspaso.PorRecibir => "Por recibir",
        EstadoTraspaso.EnRevision => "En revisión",
        EstadoTraspaso.Recibido => "Recibido",
        _ => "Descartado",
    };

    /// <summary>El color de su etiqueta: lo que espera a alguien, en azul o ámbar; lo terminado, en verde.</summary>
    public static string Color(EstadoTraspaso estado) => estado switch
    {
        EstadoTraspaso.Borrador or EstadoTraspaso.PorRecibir => "azul",
        EstadoTraspaso.EnRevision => "ambar",
        EstadoTraspaso.Enviado or EstadoTraspaso.Recibido => "",
        _ => "gris",
    };

    /// <summary>«Sale a Sucursal 2» o «Llega de Matriz».</summary>
    public static string Sentido(SentidoTraspaso sentido, string otra) =>
        sentido == SentidoTraspaso.Salida ? $"Sale a {(otra.Length == 0 ? "(sin sucursal)" : otra)}" : $"Llega de {otra}";
}
