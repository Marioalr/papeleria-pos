using PapeleriaPos.Datos;
using PapeleriaPos.Nucleo.Contratos;

namespace PapeleriaPos.Servicio;

/// <summary>
/// Los clientes registrados (fase 3, entrega 2): la lista para elegirlos en la caja, la pantalla de clientes con su
/// evaluación, y el alta y la edición (dueño y encargado).
/// </summary>
public static class ApiClientes
{
    public static void MapearApiClientes(this IEndpointRouteBuilder app)
    {
        var api = app.MapGroup("/api").ConErrores().SoloSuCaja();

        // Para elegirlo en la caja: los activos. La ruta vieja es la de las cajas que todavía no se actualizan (CTRL+D).
        api.MapGet("/sucursales/{sucursalId:guid}/clientes-registrados", ActivosAsync);
        api.MapGet("/sucursales/{sucursalId:guid}/clientes-especiales", async (Guid sucursalId, PosDbContext db) =>
            ParaCajasDeAntes(await ActivosAsync(sucursalId, db)));

        api.MapGet("/sucursales/{sucursalId:guid}/clientes", (Guid sucursalId, bool? inactivos, PosDbContext db) =>
            new RegistroClientes(db).ListaAsync(sucursalId, inactivos ?? false));

        api.MapGet("/clientes/{clienteId:guid}", async (Guid clienteId, PosDbContext db) =>
            await new RegistroClientes(db).FichaAsync(clienteId) is { } ficha
                ? Results.Ok(ficha)
                : Results.NotFound(new ErrorDto("El cliente ya no existe.")));

        api.MapPost("/clientes", (SolicitudGuardarCliente s, PosDbContext db) => new RegistroClientes(db).GuardarAsync(s));
    }

    private static async Task<List<ClienteRegistradoDto>> ActivosAsync(Guid sucursalId, PosDbContext db) =>
        (await new RegistroClientes(db).DeLaSucursalAsync(sucursalId)).Where(c => c.Activo).Select(RegistroClientes.Dto).ToList();

    /// <summary>Los activos de la sucursal, para la copia de la caja sin conexión.</summary>
    internal static Task<List<ClienteRegistradoDto>> ParaLaCajaAsync(PosDbContext db, Guid sucursalId) => ActivosAsync(sucursalId, db);

    /// <summary>
    /// Lo que una caja de antes sabe usar: un cliente con descuento (CTRL+D aplica su porcentaje). Uno sin descuento le
    /// daría error, y el mayoreo siempre no lo conoce (manda el cobro sin él y el servicio calcula igual que ella).
    /// </summary>
    internal static List<ClienteRegistradoDto> ParaCajasDeAntes(List<ClienteRegistradoDto> clientes) =>
        clientes.Where(c => c.DescuentoCentesimas > 0).ToList();

    /// <summary>
    /// El mayoreo del cliente con que se calcula el cobro: solo si la caja lo usó (las de antes no lo mandan) y el cliente
    /// lo sigue teniendo; si se lo quitaron mientras el ticket estaba abierto, se avisa en vez de cobrar otro total.
    /// </summary>
    internal static async Task<bool> MayoreoDelCobroAsync(PosDbContext db, Guid? clienteId, bool? mayoreoDeLaCaja)
    {
        if (mayoreoDeLaCaja != true)
            return false;
        if (clienteId is not { } id || await db.ClientesRegistrados.FindAsync(id) is not { Activo: true, MayoreoSiempre: true })
            throw new InvalidOperationException("El cliente ya no tiene mayoreo siempre: vuelve a elegirlo con CTRL+L.");
        return true;
    }
}
