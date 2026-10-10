using Microsoft.EntityFrameworkCore;
using PapeleriaPos.Datos;
using PapeleriaPos.Nucleo;
using PapeleriaPos.Nucleo.Contratos;
using PapeleriaPos.Nucleo.Impresiones;
using PapeleriaPos.Nucleo.Telegram;
using PapeleriaPos.Nucleo.Usuarios;

namespace PapeleriaPos.Herramientas.Prueba;

/// <summary>
/// La base de la prueba con una persona real (docs/plan.md): sobre la papelería ficticia, la caja y los usuarios de
/// prueba, un chat de Telegram simulado del dueño (sus avisos se ven en la caja), impresiones por cobrar como si
/// vinieran de la WF-6590, y una factura y una nota de un proveedor con productos de esta base.
/// </summary>
public static class PreparacionPrueba
{
    public static async Task<List<string>> PrepararAsync(PosDbContext db, string carpetaEjemplos, CancellationToken ct = default)
    {
        var hecho = await PreparacionCaja.PrepararAsync(db, ct);
        var ahora = db.Reloj.GetUtcNow().UtcDateTime;

        if (!await db.ChatsTelegram.AnyAsync(c => c.ChatId == ChatTelegram.Simulado, ct))
        {
            var dueno = await db.Usuarios.FirstAsync(u => u.Rol == Rol.Dueno, ct);
            db.ChatsTelegram.Add(new ChatTelegram { UsuarioId = dueno.Id, ChatId = ChatTelegram.Simulado, Nombre = "Prueba (sin Telegram)", VinculadoUtc = ahora });
            await db.SaveChangesAsync(ct);
            hecho.Add("Chat de Telegram simulado del dueño");
        }

        if (!await db.TrabajosImpresion.AnyAsync(ct))
        {
            var caja = await db.Cajas.FirstAsync(ct);
            var impresiones = new RegistroImpresiones(db);
            var minuto = 0;
            foreach (var (documento, paginas, color) in new[]
                     {
                         ("Tarea de ciencias.docx", 3, false), ("Invitación de cumpleaños.pdf", 1, true), ("CURP.pdf", 1, false),
                         ("Fotos del viaje.jpg", 2, true),
                     })
                await impresiones.RegistrarAsync(Trabajo(caja.Id, documento, paginas, color, ahora.AddMinutes(-30 + 5 * minuto++)), ct);
            hecho.Add("4 impresiones por cobrar (como si vinieran de la WF-6590)");
        }

        hecho.AddRange(await EjemplosProveedor.EscribirAsync(db, carpetaEjemplos, ct));
        return hecho;
    }

    private static TrabajoMedidoDto Trabajo(Guid cajaId, string documento, int paginas, bool color, DateTime impresoUtc) =>
        new(Ids.Nuevo(), cajaId, null, "PC-MOSTRADOR", "WF-6590 (prueba)", 100 + paginas, documento, "mostrador", impresoUtc, EstadoTrabajo.Impreso,
            new AjustesTrabajoDto("Standard", "Plain", "NorthAmericaLetter", 215.9, 279.4, color, false, 1, 1, true),
            Enumerable.Range(0, paginas).Select(_ => color
                ? new CoberturaPaginaDto(0.08, 0.07, 0.06, 0.04, 0.12, 93.5, true)
                : new CoberturaPaginaDto(0, 0, 0, 0.06, 0.06, 93.5, false)).ToList(),
            true, null, null, impresoUtc.AddSeconds(-20));
}
