using System.Globalization;
using PapeleriaPos.Nucleo.Catalogo;
using PapeleriaPos.Nucleo.Ventas;

namespace PapeleriaPos.Nucleo.Contratos;

/// <summary>El ticket de una venta cobrada. Lo arman igual el servicio y la caja sin conexión.</summary>
public static class ArmadoTicket
{
    /// <param name="presentacion">Nombre de una presentación y si es la base (las que no, salen entre paréntesis).</param>
    /// <param name="cliente">El cliente registrado del ticket, si hay.</param>
    public static TicketDto Desde(
        Venta v, string caja, string cajero, Func<Guid, (string Nombre, bool EsBase)?> presentacion, IReadOnlyList<string> encabezado,
        IReadOnlyList<string> pie, LogoTicketDto? logo, ClienteRegistradoDto? cliente = null)
    {
        var lineas = v.Lineas.Select(l =>
        {
            var descripcion = l.PresentacionId is { } pid && presentacion(pid) is { EsBase: false } p ? $"{l.Descripcion} ({p.Nombre})" : l.Descripcion;
            return new LineaTicketDto(CantidadTexto(l, esBase: descripcion == l.Descripcion), descripcion, l.PrecioCentavos, l.ImporteCentavos,
                l.DescuentoCentavos, l.DescuentoCentesimas is { } c ? Control.Permisos.TextoMonto(Control.AccionControlada.Descuento, c) : null,
                l.AhorroMayoreoCentavos, l.MayoreoDesde is not { } desde ? null
                    : l.MayoreoDeCliente ? "Mayoreo de cliente"
                    : $"Mayoreo desde {MayoreoTexto(desde, l.Unidad, esBase: descripcion == l.Descripcion)}");
        }).ToList();

        return new TicketDto(v.FolioTexto!, HoraMexico.ALocal(v.PagadaUtc!.Value), caja, cajero, lineas, v.NumeroDeArticulos, v.TotalCentavos,
            v.IvaCentavos,
            v.Cobros.OrderBy(c => c.Forma).Select(c => new CobroTicketDto(c.Forma, c.MontoCentavos, c.RecibidoCentavos, c.CambioCentavos, c.Referencia)).ToList(),
            encabezado, pie, logo, v.Id, v.RedondeoCentavos, cliente is null ? null : ClienteTexto(cliente.Nombre, PrivilegiosAplicados(v)));
    }

    /// <summary>"Escuela Benito Juárez (descuento 10 % · mayoreo siempre)", o solo el nombre si no tiene privilegios de venta.</summary>
    public static string ClienteTexto(ClienteRegistradoDto cliente) => ClienteTexto(cliente.Nombre, cliente.TextoPrivilegios());

    private static string ClienteTexto(string nombre, string privilegios) => privilegios.Length > 0 ? $"{nombre} ({privilegios})" : nombre;

    /// <summary>
    /// Los privilegios que llevó la venta, no los que el cliente tiene hoy: así la reimpresión dice lo mismo que el ticket
    /// que se entregó. El descuento es el de los renglones con su motivo; el mayoreo, el de los que lo tomaron sin llegar a
    /// la cantidad.
    /// </summary>
    private static string PrivilegiosAplicados(Venta v) => Clientes.ClienteRegistrado.Privilegios(
        v.Lineas.Where(l => l.MotivoDescuento?.StartsWith(Clientes.ClienteRegistrado.PrefijoMotivo, StringComparison.Ordinal) == true)
            .Select(l => l.DescuentoCentesimas ?? 0).DefaultIfEmpty(0).Max(),
        v.Lineas.Any(l => l.MayoreoDeCliente));

    /// <summary>"12" piezas (o "12 pzas" en un paquete, para no confundirlas con paquetes); "10 m", "5 kg", "2 l" a granel.</summary>
    public static string MayoreoTexto(Cantidad desde, UnidadBase unidad, bool esBase)
    {
        var numero = desde.EnUnidades.ToString("#,##0.###", CultureInfo.InvariantCulture);
        return unidad switch
        {
            UnidadBase.Metro => $"{numero} m",
            UnidadBase.Kilogramo => $"{numero} kg",
            UnidadBase.Litro => $"{numero} l",
            _ => esBase ? numero : $"{numero} pzas",
        };
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
