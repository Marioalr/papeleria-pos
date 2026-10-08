using Microsoft.EntityFrameworkCore;
using PapeleriaPos.Nucleo;
using PapeleriaPos.Nucleo.Clientes;
using PapeleriaPos.Nucleo.Contratos;
using PapeleriaPos.Nucleo.Control;
using PapeleriaPos.Nucleo.Ventas;

namespace PapeleriaPos.Datos;

/// <summary>
/// Los clientes registrados (docs/fase-3-precios-clientes.md, entrega 2): los dan de alta y los editan el dueño y el
/// encargado, uno por uno; nunca se borran (sus ventas los siguen nombrando), se dejan inactivos. Su evaluación sale de
/// sus compras: cuánto compran por mes.
/// </summary>
public sealed class RegistroClientes(PosDbContext db)
{
    /// <summary>Los meses que muestra la ficha, contando el actual.</summary>
    public const int MesesEnFicha = 6;

    /// <summary>Los meses completos con que se saca el promedio mensual.</summary>
    public const int MesesDelPromedio = 3;

    private DateTime Ahora => db.Reloj.GetUtcNow().UtcDateTime;

    /// <summary>Los de la sucursal y los de todas, los activos primero.</summary>
    public async Task<List<ClienteRegistrado>> DeLaSucursalAsync(Guid sucursalId, CancellationToken ct = default) =>
        (await db.ClientesRegistrados.AsNoTracking().Where(c => c.SucursalId == sucursalId || c.SucursalId == null).ToListAsync(ct))
        .OrderByDescending(c => c.Activo).ThenBy(c => c.Nombre, StringComparer.CurrentCultureIgnoreCase).ToList();

    public static ClienteRegistradoDto Dto(ClienteRegistrado c) =>
        new(c.Id, c.Nombre, c.DescuentoCentesimas, c.Nota, c.Activo, c.Telefono, c.MayoreoSiempre, c.PedidoSinAnticipo, c.ApartarSinPagar, c.PorRevisar,
            c.LimiteCreditoCentavos, c.PlazoCreditoDias);

    /// <summary>La lista de la pantalla de clientes, con lo que compra cada uno.</summary>
    public async Task<List<ClienteResumenDto>> ListaAsync(Guid sucursalId, bool inactivos, CancellationToken ct = default)
    {
        var clientes = (await DeLaSucursalAsync(sucursalId, ct)).Where(c => inactivos || c.Activo).ToList();
        var ids = clientes.Select(c => c.Id).ToList();
        var compras = await ComprasAsync(ids, ct);
        var ultimas = await UltimasComprasAsync(ids, ct);
        return clientes.Select(c => new ClienteResumenDto(Dto(c), Promedio(Meses(compras, c.Id)), ultimas.GetValueOrDefault(c.Id))).ToList();
    }

    /// <summary>La ficha con su evaluación: lo que compró cada mes y el promedio.</summary>
    public async Task<FichaClienteDto?> FichaAsync(Guid clienteId, CancellationToken ct = default)
    {
        if (await db.ClientesRegistrados.AsNoTracking().SingleOrDefaultAsync(c => c.Id == clienteId, ct) is not { } cliente)
            return null;
        var meses = Meses(await ComprasAsync([cliente.Id], ct), cliente.Id);
        var ultima = (await UltimasComprasAsync([cliente.Id], ct)).GetValueOrDefault(cliente.Id);
        return new FichaClienteDto(Dto(cliente), meses, Promedio(meses), ultima);
    }

    /// <summary>
    /// Alta (sin Id) o edición. El nombre y el teléfono no se repiten entre los activos. Lo que toca a sus privilegios
    /// queda en la bitácora como «Privilegios de…»: si no lo hizo el dueño, le llega por Telegram.
    /// </summary>
    public async Task<ClienteRegistradoDto> GuardarAsync(SolicitudGuardarCliente s, CancellationToken ct = default)
    {
        await new ServicioControl(db).ExigirAsync(s.UsuarioId, s.SucursalId, AccionControlada.AdministrarClientes,
            "Los clientes los dan de alta y los editan el dueño y el encargado.", ct);
        var f = s.Cliente;
        if (string.IsNullOrWhiteSpace(f.Nombre))
            throw new ArgumentException("El cliente lleva nombre.");
        var nombre = f.Nombre.Trim();
        if (nombre.Length > 80)
            throw new ArgumentException("El nombre va de hasta 80 letras.");
        var telefono = ClienteRegistrado.SoloDigitos(f.Telefono);
        if (telefono is { Length: < 7 or > 15 })
            throw new ArgumentException("El teléfono lleva de 7 a 15 dígitos (10 en México).");
        if (f.DescuentoCentesimas is < 0 or > 10000)
            throw new ArgumentException("El descuento va de 0 a 100 %.");
        var nota = string.IsNullOrWhiteSpace(f.Nota) ? null : f.Nota.Trim();
        if (nota is { Length: > 200 })
            throw new ArgumentException("La nota va de hasta 200 letras.");

        var cliente = f.Id == Guid.Empty ? null
            : await db.ClientesRegistrados.FindAsync([f.Id], ct) ?? throw new InvalidOperationException("El cliente ya no existe.");
        if (f.Activo)
        {
            var otros = await db.ClientesRegistrados.AsNoTracking().Where(c => c.Activo && c.Id != f.Id).ToListAsync(ct);
            if (otros.FirstOrDefault(o => Texto.Normalizar(o.Nombre) == Texto.Normalizar(nombre)) is { } mismoNombre)
                throw new InvalidOperationException($"Ya existe el cliente «{mismoNombre.Nombre}».");
            if (telefono is not null && otros.FirstOrDefault(o => o.Telefono == telefono) is { } mismoTelefono)
                throw new InvalidOperationException($"Ese teléfono ya es de «{mismoTelefono.Nombre}».");
        }

        // El crédito (límite y plazo) no se cambia aquí: tiene su propia ruta y su propio permiso. Así una caja que manda
        // la ficha completa (o una de antes, que no conoce el crédito) no se lo quita.
        var antes = cliente is null ? null : Dto(cliente);
        if (cliente is null)
        {
            cliente = new ClienteRegistrado { Nombre = nombre };
            db.ClientesRegistrados.Add(cliente);
        }
        (cliente.Nombre, cliente.Telefono, cliente.Nota, cliente.DescuentoCentesimas, cliente.MayoreoSiempre, cliente.PedidoSinAnticipo,
            cliente.ApartarSinPagar, cliente.Activo, cliente.PorRevisar) =
            (nombre, telefono, nota, f.DescuentoCentesimas, f.MayoreoSiempre, f.PedidoSinAnticipo, f.ApartarSinPagar, f.Activo, false);

        new ServicioControl(db).Registrar(s.SucursalId, null, s.UsuarioId, AccionControlada.AdministrarClientes, Detalle(antes, Dto(cliente)));
        await db.SaveChangesAsync(ct);
        return Dto(cliente);
    }

    /// <summary>
    /// Lo que queda en la bitácora, con lo de antes y lo de después. Con el prefijo «Privilegios de» se le avisa al dueño
    /// (<see cref="AlertasTelegram"/>): cuando se dan o cambian, y también cuando quien los tiene cambia de nombre o de
    /// teléfono o vuelve a estar activo (así no se le pasan los privilegios de uno a otra persona sin que lo sepa).
    /// </summary>
    public static string Detalle(ClienteRegistradoDto? antes, ClienteRegistradoDto despues)
    {
        var privilegios = new List<string>();
        void Comparar(string que, string? a, string d)
        {
            if (a != d)
                privilegios.Add(a is null ? $"{que} {d}" : $"{que} {a} → {d}");
        }
        string SiNo(bool b) => b ? "sí" : "no";
        Comparar("descuento", antes is null ? null : Permisos.TextoMonto(AccionControlada.Descuento, antes.DescuentoCentesimas),
            Permisos.TextoMonto(AccionControlada.Descuento, despues.DescuentoCentesimas));
        Comparar("mayoreo siempre", antes is null ? null : SiNo(antes.MayoreoSiempre), SiNo(despues.MayoreoSiempre));
        Comparar("pedido sin anticipo", antes is null ? null : SiNo(antes.PedidoSinAnticipo), SiNo(despues.PedidoSinAnticipo));
        Comparar("apartar sin pagar", antes is null ? null : SiNo(antes.ApartarSinPagar), SiNo(despues.ApartarSinPagar));

        var nombre = $"«{despues.Nombre}»";
        if (antes is null)
        {
            // En un alta solo cuenta lo que sí le da algo.
            privilegios.RemoveAll(c => c.EndsWith(" no", StringComparison.Ordinal) || c == "descuento 0 %");
            return privilegios.Count > 0 ? $"Privilegios de {nombre} (alta): {string.Join(", ", privilegios)}" : $"Alta del cliente {nombre}";
        }

        var quien = new List<string>();
        if (antes.Nombre != despues.Nombre)
            quien.Add($"nombre «{antes.Nombre}» → «{despues.Nombre}»");
        if (antes.Telefono != despues.Telefono)
            quien.Add($"teléfono {antes.Telefono ?? "—"} → {despues.Telefono ?? "—"}");
        if (antes.Activo != despues.Activo)
            quien.Add(despues.Activo ? "lo volvió a activar" : "lo dejó inactivo");
        var cambios = privilegios.Concat(quien).ToList();
        if (cambios.Count == 0)
            return $"Editó el cliente {nombre}";
        var tienePrivilegios = despues.DescuentoCentesimas > 0 || despues.MayoreoSiempre || despues.PedidoSinAnticipo || despues.ApartarSinPagar;
        var avisar = privilegios.Count > 0
            || (tienePrivilegios && despues.Activo && (antes.Nombre != despues.Nombre || antes.Telefono != despues.Telefono || !antes.Activo));
        return (avisar ? $"Privilegios de {nombre}: " : $"Cliente {nombre}: ") + string.Join(", ", cambios);
    }

    // ── Evaluación ───────────────────────────────────────────────────────────

    private sealed record Compra(Guid ClienteId, DateTime FechaLocal, long Centavos);

    /// <summary>Primer día (local) del mes, contando desde el actual: 0 es este mes, −1 el anterior.</summary>
    private DateTime InicioDeMes(int meses)
    {
        var hoy = HoraMexico.ALocal(Ahora);
        return new DateTime(hoy.Year, hoy.Month, 1).AddMonths(meses);
    }

    /// <summary>Lo comprado en los meses de la ficha: las ventas cobradas (sin las canceladas), menos lo que devolvió de cada una.</summary>
    private async Task<List<Compra>> ComprasAsync(List<Guid> clientes, CancellationToken ct)
    {
        var desde = HoraMexico.AUtc(InicioDeMes(-(MesesEnFicha - 1)));
        var ventas = await db.Ventas.AsNoTracking()
            .Where(v => v.ClienteId != null && clientes.Contains(v.ClienteId.Value) && v.Estado == EstadoVenta.Pagada && v.PagadaUtc >= desde)
            .Select(v => new { v.Id, v.ClienteId, v.PagadaUtc, v.TotalCentavos })
            .ToListAsync(ct);
        var ids = ventas.Select(v => v.Id).ToList();
        var devuelto = (await db.Devoluciones.AsNoTracking().Where(d => ids.Contains(d.VentaId) && !d.EsCancelacion)
                .Select(d => new { d.VentaId, d.TotalCentavos }).ToListAsync(ct))
            .GroupBy(d => d.VentaId).ToDictionary(g => g.Key, g => g.Sum(d => d.TotalCentavos));
        return ventas.Select(v => new Compra(v.ClienteId!.Value, HoraMexico.ALocal(v.PagadaUtc!.Value), v.TotalCentavos - devuelto.GetValueOrDefault(v.Id)))
            .ToList();
    }

    /// <summary>La última vez que compró (cualquier fecha), en hora local.</summary>
    private async Task<Dictionary<Guid, DateTime?>> UltimasComprasAsync(List<Guid> clientes, CancellationToken ct) =>
        (await db.Ventas.AsNoTracking()
            .Where(v => v.ClienteId != null && clientes.Contains(v.ClienteId.Value) && v.Estado == EstadoVenta.Pagada)
            .Select(v => new { v.ClienteId, v.PagadaUtc })
            .ToListAsync(ct))
        .GroupBy(v => v.ClienteId!.Value)
        .ToDictionary(g => g.Key, g => g.Max(v => v.PagadaUtc) is { } utc ? HoraMexico.ALocal(utc) : (DateTime?)null);

    /// <summary>Los meses de la ficha, del más reciente al más viejo (también los meses sin compras).</summary>
    private List<CompraMesDto> Meses(List<Compra> compras, Guid clienteId) =>
        Enumerable.Range(0, MesesEnFicha).Select(i => InicioDeMes(-i)).Select(inicio =>
        {
            var delMes = compras.Where(c => c.ClienteId == clienteId && c.FechaLocal >= inicio && c.FechaLocal < inicio.AddMonths(1)).ToList();
            return new CompraMesDto(inicio.Year, inicio.Month, delMes.Count, delMes.Sum(c => c.Centavos));
        }).ToList();

    /// <summary>El promedio de los últimos meses completos (sin el actual, que va a la mitad).</summary>
    private static long Promedio(List<CompraMesDto> meses) =>
        Redondeo.Dividir(meses.Skip(1).Take(MesesDelPromedio).Sum(m => m.TotalCentavos), MesesDelPromedio);
}
