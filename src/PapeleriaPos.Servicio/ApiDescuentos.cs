using Microsoft.EntityFrameworkCore;
using PapeleriaPos.Datos;
using PapeleriaPos.Nucleo.Contratos;
using PapeleriaPos.Nucleo.Control;
using PapeleriaPos.Nucleo.Ventas;

namespace PapeleriaPos.Servicio;

/// <summary>Descuentos (entrega 5, parte 3): los clientes especiales que administra el dueño y la revisión del costo.</summary>
public static class ApiDescuentos
{
    public static void MapearApiDescuentos(this IEndpointRouteBuilder app)
    {
        var api = app.MapGroup("/api").ConErrores().SoloSuCaja();

        api.MapGet("/sucursales/{sucursalId:guid}/clientes-especiales", async (Guid sucursalId, PosDbContext db) =>
            (await ClientesAsync(db, sucursalId)).Select(Dto).ToList());

        // La lista completa: los que ya no vienen quedan inactivos (sus ventas los siguen nombrando).
        api.MapPut("/sucursales/{sucursalId:guid}/clientes-especiales", async (Guid sucursalId, SolicitudClientesEspeciales s, PosDbContext db) =>
        {
            var control = new ServicioControl(db);
            var dueno = await control.IdentificarAsync(s.Usuario, s.Pin, sucursalId, AccionControlada.AdministrarClientes);
            foreach (var c in s.Clientes)
            {
                if (string.IsNullOrWhiteSpace(c.Nombre))
                    throw new ArgumentException("Cada cliente especial lleva nombre.");
                if (c.DescuentoCentesimas is <= 0 or > 10000)
                    throw new ArgumentException($"El descuento de {c.Nombre.Trim()} va de más de 0 a 100 %.");
            }
            var repetido = s.Clientes.Where(c => c.Activo).GroupBy(c => c.Nombre.Trim(), StringComparer.CurrentCultureIgnoreCase).FirstOrDefault(g => g.Count() > 1);
            if (repetido is not null)
                throw new ArgumentException($"«{repetido.Key}» está dos veces.");

            var guardados = await db.ClientesEspeciales.Where(c => c.SucursalId == sucursalId || c.SucursalId == null).ToListAsync();
            foreach (var c in s.Clientes)
            {
                var cliente = guardados.FirstOrDefault(g => g.Id == c.Id);
                if (cliente is null)
                {
                    cliente = new ClienteEspecial { Id = c.Id == Guid.Empty ? PapeleriaPos.Nucleo.Ids.Nuevo() : c.Id, SucursalId = sucursalId, Nombre = "" };
                    db.ClientesEspeciales.Add(cliente);
                }
                (cliente.Nombre, cliente.DescuentoCentesimas, cliente.Nota, cliente.Activo) =
                    (c.Nombre.Trim(), c.DescuentoCentesimas, string.IsNullOrWhiteSpace(c.Nota) ? null : c.Nota.Trim(), c.Activo);
            }
            foreach (var quitado in guardados.Where(g => g.Activo && s.Clientes.All(c => c.Id != g.Id)))
                quitado.Activo = false;
            control.Registrar(sucursalId, null, dueno.Id, AccionControlada.AdministrarClientes, "Cambió los clientes especiales");
            await db.SaveChangesAsync();
            return (await ClientesAsync(db, sucursalId)).Select(Dto).ToList();
        });

        // Antes de cobrar: ¿algún descuento deja una línea debajo del costo? Solo dice cuáles y cuánto falta, no el costo.
        api.MapPost("/descuentos/bajo-costo", async (SolicitudBajoCosto s, PosDbContext db, CatalogoEnMemoria catalogo) =>
        {
            var caja = await db.Cajas.FindAsync(s.CajaId) ?? throw new InvalidOperationException("La caja no existe.");
            var venta = await ApiCaja.ArmarVentaAsync(db, catalogo, caja, Guid.Empty, Guid.Empty, Guid.NewGuid(), s.Lineas);
            var bajoCosto = await new RegistroCaja(db).BajoCostoAsync(venta);
            return new BajoCostoDto(bajoCosto.Sum(b => b.Costo - b.Linea.ImporteCentavos), bajoCosto.Select(b => b.Linea.Descripcion).ToList());
        });
    }

    internal static async Task<List<ClienteEspecial>> ClientesAsync(PosDbContext db, Guid sucursalId) =>
        (await db.ClientesEspeciales.AsNoTracking().Where(c => c.SucursalId == sucursalId || c.SucursalId == null).ToListAsync())
        .OrderByDescending(c => c.Activo).ThenBy(c => c.Nombre, StringComparer.CurrentCultureIgnoreCase).ToList();

    internal static ClienteEspecialDto Dto(ClienteEspecial c) => new(c.Id, c.Nombre, c.DescuentoCentesimas, c.Nota, c.Activo);
}
