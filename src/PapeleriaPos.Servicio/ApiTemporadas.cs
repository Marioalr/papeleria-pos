using PapeleriaPos.Datos;
using PapeleriaPos.Nucleo.Contratos;

namespace PapeleriaPos.Servicio;

/// <summary>
/// Empezar y terminar la temporada, su hoja, las tarjetas de precio y lo más preguntado (entrega 13, parte 3): Productos (F3)
/// los pide a la caja principal; nada de esto viaja en la copia de las cajas. La venta solo manda cada consulta del
/// verificador (F9). En el piloto con eleventa (su lectura prendida), los precios de lo de eleventa se cambian allá y lo que
/// sobró de la temporada no se cuenta aquí.
/// </summary>
public static class ApiTemporadas
{
    public static void MapearApiTemporadas(this IEndpointRouteBuilder app)
    {
        var api = app.MapGroup("/api").ConErrores().SoloSuCaja();

        // ── Las temporadas: empezar (los precios del año pasado), la hoja y terminar (el resumen) ──

        api.MapGet("/temporadas", (Guid sucursalId, PosDbContext db, OpcionesEleventa eleventa, TimeProvider reloj) =>
            new RegistroTemporadas(db, eleventa.Activo, reloj).ListarAsync(sucursalId));

        // El costo, el margen y el sugerido solo van para quien ve la utilidad (el servicio lo revisa con quien pide).
        api.MapGet("/temporadas/{id:guid}/empezar", (Guid id, Guid sucursalId, Guid usuarioId, PosDbContext db, OpcionesEleventa eleventa, TimeProvider reloj) =>
            new RegistroTemporadas(db, eleventa.Activo, reloj).ParaEmpezarAsync(id, sucursalId, usuarioId));

        api.MapPost("/temporadas/{id:guid}/precios", (Guid id, SolicitudConfirmarPrecios s, PosDbContext db, OpcionesEleventa eleventa, TimeProvider reloj) =>
            new RegistroTemporadas(db, eleventa.Activo, reloj).ConfirmarPreciosAsync(id, s));

        // La hoja la arma y la imprime la caja; aquí se le da código a lo que no tiene, por eso es un POST.
        api.MapPost("/temporadas/{id:guid}/hoja", (Guid id, SolicitudHojaDeTemporada s, PosDbContext db, OpcionesEleventa eleventa, TimeProvider reloj) =>
            new RegistroTemporadas(db, eleventa.Activo, reloj).HojaAsync(id, s));

        api.MapGet("/temporadas/{id:guid}/terminar", (Guid id, Guid sucursalId, PosDbContext db, OpcionesEleventa eleventa, TimeProvider reloj) =>
            new RegistroTemporadas(db, eleventa.Activo, reloj).ParaTerminarAsync(id, sucursalId));

        // Lo contado de un producto, al escribirlo: se guarda con lo que dice el sistema en ese momento (como el conteo completo).
        api.MapPut("/temporadas/{id:guid}/contado", (Guid id, SolicitudContado s, PosDbContext db, OpcionesEleventa eleventa, TimeProvider reloj) =>
            new RegistroTemporadas(db, eleventa.Activo, reloj).ContarAsync(id, s));

        api.MapPost("/temporadas/{id:guid}/terminar", (Guid id, SolicitudTerminarTemporada s, PosDbContext db, OpcionesEleventa eleventa, TimeProvider reloj) =>
            new RegistroTemporadas(db, eleventa.Activo, reloj).TerminarAsync(id, s));

        // ── Las tarjetas de precio: por reimprimir, las de un exhibidor, lo más preguntado sin tarjeta ──

        api.MapGet("/tarjetas", (Guid sucursalId, PosDbContext db, TimeProvider reloj) => new RegistroTarjetas(db, reloj).LeerAsync(sucursalId));

        api.MapGet("/tarjetas/exhibidor/{id:guid}", (Guid id, Guid sucursalId, PosDbContext db, TimeProvider reloj) =>
            new RegistroTarjetas(db, reloj).DeExhibidorAsync(id, sucursalId));

        api.MapGet("/tarjetas/mas-preguntados", (Guid sucursalId, int? maximo, PosDbContext db, TimeProvider reloj) =>
            new RegistroTarjetas(db, reloj).MasPreguntadosAsync(sucursalId, maximo ?? RegistroTarjetas.MasPreguntados));

        // Las tarjetas listas para la hoja (con código para lo que no tiene) y, al imprimirlas, con qué precio y código salieron.
        api.MapPost("/tarjetas/preparar", (SolicitudPrepararTarjetas s, PosDbContext db, TimeProvider reloj) => new RegistroTarjetas(db, reloj).PrepararAsync(s));
        api.MapPost("/tarjetas/impresas", (SolicitudTarjetasImpresas s, PosDbContext db, TimeProvider reloj) => new RegistroTarjetas(db, reloj).ImpresasAsync(s));
        api.MapPost("/tarjetas/{varianteId:guid}/quitar", (Guid varianteId, SolicitudQuitarTarjeta s, PosDbContext db, TimeProvider reloj) =>
            new RegistroTarjetas(db, reloj).QuitarAsync(varianteId, s));

        // Cada consulta del verificador: cuenta para lo más preguntado. Si falla, la caja no se entera ni espera.
        api.MapPost("/consultas", async (SolicitudConsulta s, PosDbContext db, TimeProvider reloj) =>
        {
            await new RegistroTarjetas(db, reloj).ContarConsultaAsync(s);
            return Results.NoContent();
        });
    }
}
