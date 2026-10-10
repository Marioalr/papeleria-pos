using System.Globalization;
using PapeleriaPos.Nucleo.Catalogo;
using PapeleriaPos.Nucleo.Ventas;

namespace PapeleriaPos.Nucleo.Contratos;

/// <summary>El ticket de una venta cobrada. Lo arman igual el servicio y la caja sin conexión.</summary>
public static class ArmadoTicket
{
    /// <param name="presentacion">Nombre de una presentación y si es la base (las que no, salen entre paréntesis).</param>
    public static TicketDto Desde(
        Venta v, string caja, string cajero, Func<Guid, (string Nombre, bool EsBase)?> presentacion, IReadOnlyList<string> encabezado,
        IReadOnlyList<string> pie, LogoTicketDto? logo)
    {
        var lineas = v.Lineas.Select(l =>
        {
            var descripcion = l.PresentacionId is { } pid && presentacion(pid) is { EsBase: false } p ? $"{l.Descripcion} ({p.Nombre})" : l.Descripcion;
            return new LineaTicketDto(CantidadTexto(l, esBase: descripcion == l.Descripcion), descripcion, l.PrecioCentavos, l.ImporteCentavos,
                l.DescuentoCentavos, l.DescuentoCentesimas is { } c ? Control.Permisos.TextoMonto(Control.AccionControlada.Descuento, c) : null);
        }).ToList();

        return new TicketDto(v.FolioTexto!, HoraMexico.ALocal(v.PagadaUtc!.Value), caja, cajero, lineas, v.NumeroDeArticulos, v.TotalCentavos,
            v.IvaCentavos,
            v.Cobros.OrderBy(c => c.Forma).Select(c => new CobroTicketDto(c.Forma, c.MontoCentavos, c.RecibidoCentavos, c.CambioCentavos, c.Referencia)).ToList(),
            encabezado, pie, logo, v.Id, v.RedondeoCentavos);
    }

    /// <summary>"2" piezas; "1.5 m", "0.25 kg", "0.5 l" a granel.</summary>
    public static string CantidadTexto(VentaLinea l, bool esBase)
    {
        var numero = l.Cantidad.EnUnidades.ToString("0.###", CultureInfo.InvariantCulture);
        if (!esBase)
            return numero;
        return l.Unidad switch
        {
            UnidadBase.Metro => $"{numero} m",
            UnidadBase.Kilogramo => $"{numero} kg",
            UnidadBase.Litro => $"{numero} l",
            _ => numero,
        };
    }
}
