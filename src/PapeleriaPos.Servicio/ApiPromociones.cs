using PapeleriaPos.Datos;
using PapeleriaPos.Nucleo.Contratos;

namespace PapeleriaPos.Servicio;

/// <summary>
/// Productos (F3) › Promociones (fase 3): las promociones con fechas que se aplican solas en las cajas. Las ve, las da de alta,
/// las cambia y las termina quien entró con «Productos, precios y etiquetas»; si una deja algo debajo del costo, además
/// «Vender debajo del costo» o la autorización de quien lo tiene. El costo y el margen, solo para quien ve la utilidad. En el
/// piloto con eleventa (su lectura prendida) no se aplican ni se dan de alta: solo se consultan y se terminan.
/// </summary>
public static class ApiPromociones
{
    public static void MapearApiPromociones(this IEndpointRouteBuilder app)
    {
        var api = app.MapGroup("/api/promociones").ConErrores().SoloSuCaja();

        // El día es el de los trabajos de fondo del servicio (las pruebas lo cambian), como el de la copia y el del cobro.
        api.MapGet("", (Guid sucursalId, Guid usuarioId, PosDbContext db, OpcionesEleventa eleventa, TimeProvider reloj) =>
            new RegistroPromociones(db, eleventa.Activo, reloj).ListarAsync(sucursalId, usuarioId));

        api.MapGet("/{id:guid}", (Guid id, Guid sucursalId, Guid usuarioId, PosDbContext db, OpcionesEleventa eleventa, TimeProvider reloj) =>
            new RegistroPromociones(db, eleventa.Activo, reloj).FichaAsync(id, sucursalId, usuarioId));

        // Cómo quedaría, sin guardar nada: lo que toca, lo que queda debajo del costo y, para quien ve la utilidad, el margen.
        api.MapPost("/vista", (SolicitudPromocion s, PosDbContext db, OpcionesEleventa eleventa, TimeProvider reloj) =>
            new RegistroPromociones(db, eleventa.Activo, reloj).VistaAsync(s));

        // Los productos marcados en la lista, con sus variantes: para agregarlos, o «Dejar al costo» (pide ver la utilidad).
        api.MapPost("/productos", (SolicitudProductosPromocion s, PosDbContext db, OpcionesEleventa eleventa, TimeProvider reloj) =>
            new RegistroPromociones(db, eleventa.Activo, reloj).ProductosAsync(s));

        // Darla de alta o cambiarla (con su Id): las cajas se enteran con su copia.
        api.MapPost("", (SolicitudPromocion s, PosDbContext db, OpcionesEleventa eleventa, TimeProvider reloj) =>
            new RegistroPromociones(db, eleventa.Activo, reloj).GuardarAsync(s));

        api.MapPost("/{id:guid}/terminar", (Guid id, SolicitudTerminarPromocion s, PosDbContext db, OpcionesEleventa eleventa, TimeProvider reloj) =>
            new RegistroPromociones(db, eleventa.Activo, reloj).TerminarAsync(id, s));
    }
}
