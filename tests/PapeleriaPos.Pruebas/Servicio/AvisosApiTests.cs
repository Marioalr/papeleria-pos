using System.Net;
using System.Net.Http.Json;
using Microsoft.Extensions.DependencyInjection;
using PapeleriaPos.Datos;
using PapeleriaPos.Impresiones.Contratos;
using PapeleriaPos.Nucleo.Avisos;
using PapeleriaPos.Nucleo.Contratos;

namespace PapeleriaPos.Pruebas.Servicio;

/// <summary>
/// Los avisos de la caja (el «!» de arriba, 2026-11-28): el registro genérico donde cada módulo pone y quita los suyos, y el primero, el
/// papel de las impresoras, de punta a punta por la API: el aviso de anotar el llenado, el de «sin papel» cuando la impresora lo dice, y que
/// se quitan al anotar el llenado.
/// </summary>
public sealed class AvisosApiTests : IAsyncLifetime
{
    private readonly ServicioConImpresora _servicio = new();

    public Task InitializeAsync() => _servicio.InitializeAsync();

    public Task DisposeAsync() => _servicio.DisposeAsync();

    private static async Task<T> Leer<T>(HttpResponseMessage r)
    {
        Assert.True(r.IsSuccessStatusCode, await r.Content.ReadAsStringAsync());
        return (await r.Content.ReadFromJsonAsync<T>())!;
    }

    private async Task<(HttpClient Http, CajaDto Caja, SesionDto Cajero)> PrepararAsync()
    {
        var http = _servicio.Fabrica.CreateClient();
        var caja = (await http.GetFromJsonAsync<List<CajaDto>>("api/cajas"))!.Single();
        var cajero = await Leer<SesionDto>(await http.PostAsJsonAsync("api/sesion", new SolicitudEntrada("cajero", "1111")));
        var dueno = await Leer<SesionDto>(await http.PostAsJsonAsync("api/sesion", new SolicitudEntrada("dueno", "3333")));
        await Leer<EstadoImpresoraDto>(await http.PutAsJsonAsync($"api/sucursales/{caja.SucursalId}/impresora",
            new SolicitudConfiguracionImpresora(dueno.UsuarioId, new ConfiguracionImpresoraDto("impresora-de-prueba", 10, 5))));
        return (http, caja, cajero);
    }

    private static Task<List<AvisoDto>> AvisosAsync(HttpClient http, CajaDto caja) =>
        http.GetFromJsonAsync<List<AvisoDto>>($"api/sucursales/{caja.SucursalId}/avisos?cajaId={caja.Id}")!;

    [Fact]
    public async Task El_papel_avisa_en_la_caja_y_se_quita_al_anotar_el_llenado()
    {
        var (http, caja, cajero) = await PrepararAsync();

        // Leída la impresora, sin ningún llenado anotado: «anota el próximo», informativo, de Impresiones, con su acción.
        var aviso = Assert.Single(await AvisosAsync(http, caja));
        Assert.Equal(("impresiones", "Impresiones", "impresora", NivelAviso.Informativo), (aviso.Modulo, aviso.ModuloNombre, aviso.Icono, aviso.Nivel));
        Assert.Equal(("WF-6590 · Charola: anota el próximo llenado", "impresiones.papel", "1:1"), (aviso.Titulo, aviso.Accion, aviso.AccionDatos));

        // La impresora dice que se quedó sin papel (el error 0x06 de su ST2): el mismo aviso, urgente, sin moverle el «desde».
        _servicio.Impresora.Estado = _servicio.Impresora.Estado with { EpsonError = 0x06 };
        _servicio.Reloj.Avanzar(TimeSpan.FromMinutes(6));
        await http.PostAsync($"api/sucursales/{caja.SucursalId}/impresora/leer", null);
        var urgente = Assert.Single(await AvisosAsync(http, caja));
        Assert.Equal((aviso.Id, aviso.DesdeLocal, NivelAviso.Urgente, "WF-6590 · Charola: sin papel"), (urgente.Id, urgente.DesdeLocal, urgente.Nivel, urgente.Titulo));
        var papel = Assert.Single(await http.GetFromJsonAsync<List<PapelImpresoraDto>>($"api/sucursales/{caja.SucursalId}/impresoras/papel") ?? []);
        Assert.True(papel.Charolas[0].SinPapel);

        // Se llena: el aviso se quita al momento y la charola dice cuántas tiene.
        _servicio.Impresora.Estado = _servicio.Impresora.Estado with { EpsonError = null };
        var despues = await Leer<PapelImpresoraDto>(await http.PostAsJsonAsync($"api/sucursales/{caja.SucursalId}/impresoras/1/papel",
            new SolicitudLlenadoPapel(cajero.UsuarioId, 1, 0, Llena: true, "Carta")));
        Assert.Equal((500, false, (string?)null), (despues.Charolas[0].Quedan, despues.Charolas[0].SinPapel, despues.Charolas[0].Nivel));
        Assert.Empty(await AvisosAsync(http, caja));

        // Lo que no se puede anotar lo dice, para la cajera.
        var mal = await http.PostAsJsonAsync($"api/sucursales/{caja.SucursalId}/impresoras/1/papel", new SolicitudLlenadoPapel(cajero.UsuarioId, 3, 50, false, null));
        Assert.Equal(HttpStatusCode.BadRequest, mal.StatusCode);
        Assert.Equal("La WF-6590 no tiene la charola 3.", (await mal.Content.ReadFromJsonAsync<ErrorDto>())!.Mensaje);
    }

    [Fact]
    public async Task Cada_modulo_pone_y_quita_los_suyos_y_cada_caja_ve_los_de_todas_y_los_suyos()
    {
        var (http, caja, _) = await PrepararAsync();
        using (var alcance = _servicio.Fabrica.Services.CreateScope())
        {
            var db = alcance.ServiceProvider.GetRequiredService<PosDbContext>();
            // Quitar los del papel, para ver solo los de esta prueba.
            await new RegistroAvisos(db).QuitarAsync(caja.SucursalId, "impresiones", "");
            var avisos = new RegistroAvisos(db);
            Assert.True(await avisos.PonerAsync(new AvisoNuevo(caja.SucursalId, "inventario", "sin-contar", NivelAviso.Atencion, "No se ha contado nada hoy")));
            // Poner otra vez antes de guardar no lo duplica.
            Assert.False(await avisos.PonerAsync(new AvisoNuevo(caja.SucursalId, "inventario", "sin-contar", NivelAviso.Atencion, "No se ha contado nada hoy")));
            await avisos.PonerAsync(new AvisoNuevo(caja.SucursalId, "eleventa", "detenido", NivelAviso.Urgente, "El puente se detuvo"));
            await avisos.PonerAsync(new AvisoNuevo(caja.SucursalId, "otro-modulo", "x", NivelAviso.Informativo, "Solo de esta caja", CajaId: caja.Id));
            await db.SaveChangesAsync();
        }

        var vistos = await AvisosAsync(http, caja);
        // Del más urgente al más tranquilo; un módulo que la caja no conoce, con su clave y el icono de alerta.
        Assert.Equal(["Piloto con eleventa · El puente se detuvo", "Inventario · No se ha contado nada hoy", "otro-modulo · Solo de esta caja"],
            vistos.Select(a => $"{a.ModuloNombre} · {a.Titulo}"));
        Assert.Equal("alerta", vistos[2].Icono);
        var desde = vistos[1].DesdeLocal;
        // Desde otra caja, el de esta no se ve.
        var deOtra = await http.GetFromJsonAsync<List<AvisoDto>>($"api/sucursales/{caja.SucursalId}/avisos?cajaId={Guid.NewGuid()}");
        Assert.Equal(2, deOtra!.Count);

        _servicio.Reloj.Avanzar(TimeSpan.FromMinutes(10));
        using (var alcance = _servicio.Fabrica.Services.CreateScope())
        {
            var db = alcance.ServiceProvider.GetRequiredService<PosDbContext>();
            var avisos = new RegistroAvisos(db);
            // Otra vez, con otro texto: el mismo aviso, con su «desde».
            Assert.False(await avisos.PonerAsync(new AvisoNuevo(caja.SucursalId, "inventario", "sin-contar", NivelAviso.Urgente, "Ya son las 6 y no se ha contado")));
            // Quitar por prefijo, menos los que siguen: solo quita los suyos (el mismo prefijo en otro módulo no).
            Assert.Equal(0, await avisos.QuitarAsync(caja.SucursalId, "inventario", "sin-", ["sin-contar"]));
            Assert.Equal(1, await avisos.QuitarAsync(caja.SucursalId, "eleventa", ""));
            Assert.Equal(1, await avisos.QuitarAsync(caja.SucursalId, "otro-modulo", "x"));
            await db.SaveChangesAsync();
        }
        var ahora = Assert.Single(await AvisosAsync(http, caja));
        Assert.Equal((NivelAviso.Urgente, "Ya son las 6 y no se ha contado", desde), (ahora.Nivel, ahora.Titulo, ahora.DesdeLocal));

        // Quitado, se queda como historia; ponerlo otra vez es un aviso nuevo.
        using (var alcance = _servicio.Fabrica.Services.CreateScope())
        {
            var db = alcance.ServiceProvider.GetRequiredService<PosDbContext>();
            Assert.Equal(1, await new RegistroAvisos(db).QuitarAsync(caja.SucursalId, "inventario", "sin-contar"));
            await db.SaveChangesAsync();
            Assert.True(await new RegistroAvisos(db).PonerAsync(new AvisoNuevo(caja.SucursalId, "inventario", "sin-contar", NivelAviso.Atencion, "Otra vez")));
            await db.SaveChangesAsync();
            Assert.Equal(2, db.Avisos.Count(a => a.Clave == "sin-contar"));
        }
    }
}
