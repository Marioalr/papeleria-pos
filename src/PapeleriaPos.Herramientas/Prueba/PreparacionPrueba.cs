using Microsoft.EntityFrameworkCore;
using PapeleriaPos.Datos;
using PapeleriaPos.Nucleo;
using PapeleriaPos.Nucleo.Clientes;
using PapeleriaPos.Nucleo.Contratos;
using PapeleriaPos.Nucleo.Impresiones;
using PapeleriaPos.Nucleo.Telegram;
using PapeleriaPos.Nucleo.Usuarios;

namespace PapeleriaPos.Herramientas.Prueba;

/// <summary>
/// La base de la prueba con una persona real (docs/plan.md): sobre la papelería ficticia, la caja y los usuarios de
/// prueba, un chat de Telegram simulado del dueño (sus avisos se ven en la caja), impresiones por cobrar como si
/// vinieran de la WF-6590, unos clientes registrados (dos con crédito, uno que ya debe y está vencido), y una factura y una
/// nota de un proveedor con productos de esta base.
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

        if (!await db.ClientesRegistrados.AnyAsync(ct))
        {
            db.ClientesRegistrados.AddRange(ClientesDePrueba.Select(c => new ClienteRegistrado
            {
                Nombre = c.Nombre, Telefono = ClienteRegistrado.SoloDigitos(c.Telefono), Nota = c.Nota, DescuentoCentesimas = c.Descuento, MayoreoSiempre = c.Mayoreo,
                PorRevisar = c.PorRevisar,
            }));
            await db.SaveChangesAsync(ct);
            hecho.Add($"{ClientesDePrueba.Length} clientes registrados de prueba");
        }

        if (!await db.ClientesRegistrados.AnyAsync(c => c.LimiteCreditoCentavos > 0, ct))
        {
            var dueno = await db.Usuarios.FirstAsync(u => u.Rol == Rol.Dueno, ct);
            var sucursal = (await db.Cajas.FirstAsync(ct)).SucursalId;
            foreach (var (nombre, _, _, _, _, _, credito) in ClientesDePrueba.Where(c => c.Credito > 0))
            {
                var cliente = await db.ClientesRegistrados.FirstAsync(c => c.Nombre == nombre, ct);
                cliente.LimiteCreditoCentavos = credito;
                if (nombre != ConDeuda)
                    continue;
                // Lo que ya debía en la libreta, de hace 40 días: con 30 de plazo, ya está vencido.
                var fecha = HoraMexico.AUtc(HoraMexico.ALocal(ahora).Date.AddDays(-DiasDeLaDeuda).AddHours(12));
                var ajuste = new MovimientoCredito
                {
                    Id = Ids.Nuevo(), ClienteId = cliente.Id, SucursalId = sucursal, UsuarioId = dueno.Id, Tipo = TipoMovimientoCredito.Ajuste,
                    MontoCentavos = DeudaCentavos, FechaUtc = fecha, Vence = CuentaCredito.Vence(fecha, cliente.PlazoCreditoDias),
                    SaldoCentavos = DeudaCentavos, Motivo = "Saldo inicial de la libreta",
                };
                db.MovimientosCredito.Add(ajuste);
                // Para practicar «Clientes → Por revisar»: así se ve lo que no cuadró al volver la conexión (aquí no pasó de verdad).
                db.RevisionesCredito.Add(new RevisionCredito
                {
                    SucursalId = sucursal, ClienteId = cliente.Id, MovimientoId = ajuste.Id, Motivo = MotivoRevisionCredito.PasoLimite,
                    MontoCentavos = DeudaCentavos + 90000 - credito,
                    // Con sus números: debía $350; la caja 2 le fió $600 sin saber de $300 que le fiaron en otra: $1,250 contra $1,000.
                    CreadaUtc = ahora, Detalle = $"Ejemplo de la prueba: fiado sin conexión en Caja 2 a «{ConDeuda}» por $600.00: la caja sabía que debía "
                        + $"{AlertasTelegram.Dinero(DeudaCentavos)}; con lo que llegó antes ahora debe {AlertasTelegram.Dinero(DeudaCentavos + 90000)} y pasó su "
                        + $"límite de {AlertasTelegram.Dinero(credito)} por {AlertasTelegram.Dinero(DeudaCentavos + 90000 - credito)} (se autorizaron $0.00).",
                });
            }
            await db.SaveChangesAsync(ct);
            hecho.Add($"Crédito para {ClientesDePrueba.Count(c => c.Credito > 0)} clientes; «{ConDeuda}» debe {AlertasTelegram.Dinero(DeudaCentavos)} (vencido) "
                + "y tiene un ejemplo «por revisar»");
        }

        hecho.AddRange(await EjemplosProveedor.EscribirAsync(db, carpetaEjemplos, ct));
        return hecho;
    }

    /// <summary>
    /// Uno con descuento y crédito, uno con mayoreo siempre, uno solo con crédito (que ya debe) y uno que venía de los
    /// clientes especiales. El crédito, en centavos.
    /// </summary>
    public static readonly (string Nombre, string Telefono, string? Nota, int Descuento, bool Mayoreo, bool PorRevisar, long Credito)[] ClientesDePrueba =
    [
        ("Escuela Primaria Benito Juárez", "5512345678", "Recoge la maestra Laura", 1000, false, false, 200000),
        ("Papelería La Esquina", "5587654321", "Revende: compra por paquete", 0, true, false, 0),
        ("Arq. Ramírez", "5522334455", "Paga en su siguiente compra", 0, false, false, 100000),
        ("Maestra Carmen (cliente especial)", "", null, 500, false, true, 0),
    ];

    /// <summary>El que ya debe desde la libreta, cuánto y de hace cuántos días.</summary>
    public const string ConDeuda = "Arq. Ramírez";

    public const long DeudaCentavos = 35000;
    public const int DiasDeLaDeuda = 40;

    private static TrabajoMedidoDto Trabajo(Guid cajaId, string documento, int paginas, bool color, DateTime impresoUtc) =>
        new(Ids.Nuevo(), cajaId, null, "PC-MOSTRADOR", "WF-6590 (prueba)", 100 + paginas, documento, "mostrador", impresoUtc, EstadoTrabajo.Impreso,
            new AjustesTrabajoDto("Standard", "Plain", "NorthAmericaLetter", 215.9, 279.4, color, false, 1, 1, true),
            Enumerable.Range(0, paginas).Select(_ => color
                ? new CoberturaPaginaDto(0.08, 0.07, 0.06, 0.04, 0.12, 93.5, true)
                : new CoberturaPaginaDto(0, 0, 0, 0.06, 0.06, 93.5, false)).ToList(),
            true, null, null, impresoUtc.AddSeconds(-20));
}
