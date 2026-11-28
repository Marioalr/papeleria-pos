using System.Net.Http.Json;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.JSInterop;
using PapeleriaPos.Datos;
using PapeleriaPos.Impresiones.Contratos;
using PapeleriaPos.Interfaz;
using PapeleriaPos.Interfaz.Componentes;
using PapeleriaPos.Nucleo.Avisos;
using PapeleriaPos.Nucleo.Contratos;
using PapeleriaPos.Pruebas.Servicio;

namespace PapeleriaPos.Pruebas.Interfaz;

/// <summary>
/// El «!» de los avisos arriba de la caja (2026-11-28), solo con el teclado: ↓ en el «!» abre el menú con el cursor en el primero, ↑ ↓ Inicio
/// y Fin recorren los avisos (dando la vuelta), Enter hace la acción del aviso (el del papel abre «Anotar el papel» en su impresora), Enter
/// guarda y Esc regresa; Esc en el menú lo cierra y regresa el cursor a donde estaba. Atenuado y sin cuenta sin nada que atender; ámbar con
/// su cuenta con algo de atención. Ponerse encima también lo abre, y salir lo cierra.
/// </summary>
public sealed class AvisosArribaTests : IAsyncLifetime
{
    private readonly ServicioConImpresora _servicio = new();

    public Task InitializeAsync() => _servicio.InitializeAsync();

    public Task DisposeAsync() => _servicio.DisposeAsync();

    private static async Task<T> Leer<T>(HttpResponseMessage r)
    {
        Assert.True(r.IsSuccessStatusCode, await r.Content.ReadAsStringAsync());
        return (await r.Content.ReadFromJsonAsync<T>())!;
    }

    private static string? UltimoFoco(PaginaQueContesta pagina) =>
        pagina.Llamadas.LastOrDefault(l => l.Funcion is "enfocar" or "regresarDeLosAvisos") is var (funcion, args)
            ? funcion == "enfocar" ? args?[0]?.ToString() : funcion : null;

    [Fact]
    public async Task Se_recorre_y_se_anota_el_papel_solo_con_el_teclado()
    {
        var http = _servicio.Fabrica.CreateClient();
        var caja = (await http.GetFromJsonAsync<List<CajaDto>>("api/cajas"))!.Single();
        var cajero = await Leer<SesionDto>(await http.PostAsJsonAsync("api/sesion", new SolicitudEntrada("cajero", "1111")));
        var dueno = await Leer<SesionDto>(await http.PostAsJsonAsync("api/sesion", new SolicitudEntrada("dueno", "3333")));
        // Con la impresora leída y sin llenado anotado: «anota el próximo llenado» (informativo). Y uno de otro módulo, de atención.
        await Leer<EstadoImpresoraDto>(await http.PutAsJsonAsync($"api/sucursales/{caja.SucursalId}/impresora",
            new SolicitudConfiguracionImpresora(dueno.UsuarioId, new ConfiguracionImpresoraDto("impresora-de-prueba", 10, 5))));
        using (var alcance = _servicio.Fabrica.Services.CreateScope())
        {
            var db = alcance.ServiceProvider.GetRequiredService<PosDbContext>();
            await new RegistroAvisos(db).PonerAsync(new AvisoNuevo(caja.SucursalId, "inventario", "sin-contar", NivelAviso.Atencion,
                "No se ha contado nada hoy", "Cuenta un departamento antes de cerrar."));
            await db.SaveChangesAsync();
        }

        var pagina = new PaginaQueContesta();
        var conexion = new ConexionCaja { Servicio = new Uri("http://localhost/"), CajaId = caja.Id };
        await using var servicios = new ServiceCollection().AddSingleton(conexion)
            .AddSingleton(new ClienteCaja(_servicio.Fabrica.CreateClient(), conexion)).AddSingleton<IJSRuntime>(pagina).AddScoped<ModuloCaja>()
            .BuildServiceProvider();
        await using var pantalla = new PantallaSinNavegador(servicios);
        await pantalla.AbrirAsync<AvisosArriba>(new() { [nameof(AvisosArriba.Caja)] = caja, [nameof(AvisosArriba.Sesion)] = cajero });

        // Ámbar, con la cuenta de lo que hay que atender (el informativo no cuenta).
        var boton = await EsperarAsync(pantalla, "Avisos: 1 por atender (Alt+A)");
        Assert.Contains("atencion", boton.Clase);
        Assert.Equal("! 1", PantallaSinNavegador.Limpio(boton.Texto));

        // ↓ en el «!»: el menú, con el cursor en el primero (el más urgente).
        await pantalla.TeclaAsync("boton-avisos", "ArrowDown");
        Assert.Equal("#aviso-0", UltimoFoco(pagina));
        var texto = PantallaSinNavegador.Limpio(await pantalla.TextoAsync());
        Assert.Contains("Inventario · desde las", texto);
        Assert.Contains("No se ha contado nada hoy", texto);
        Assert.Contains("Impresiones · desde las", texto);
        Assert.Contains("WF-6590 · Charola: anota el próximo llenado", texto);
        Assert.Contains("Enter Anotar el papel que pusiste", texto);
        Assert.Equal("true", (await pantalla.PorIdAsync("boton-avisos")).Atributos["aria-expanded"]?.ToString());

        // Las flechas recorren los avisos y dan la vuelta; Inicio y Fin.
        await pantalla.TeclaAsync("aviso-0", "ArrowDown");
        Assert.Equal("#aviso-1", UltimoFoco(pagina));
        await pantalla.TeclaAsync("aviso-0", "ArrowUp");
        Assert.Equal("#aviso-2", UltimoFoco(pagina));
        await pantalla.TeclaAsync("aviso-2", "ArrowDown");
        Assert.Equal("#aviso-0", UltimoFoco(pagina));
        await pantalla.TeclaAsync("aviso-0", "End");
        Assert.Equal("#aviso-2", UltimoFoco(pagina));
        await pantalla.TeclaAsync("aviso-2", "Home");
        Assert.Equal("#aviso-0", UltimoFoco(pagina));

        // Un aviso sin acción que esta caja conozca: Enter no hace nada.
        await pantalla.TeclaAsync("aviso-0", "Enter");
        Assert.DoesNotContain("Hojas que pusiste", await pantalla.TextoAsync());

        // Enter en el del papel: «Anotar el papel» en su impresora y charola, con el cursor en las hojas.
        await pantalla.TeclaAsync("aviso-1", "Enter");
        await pantalla.EsperarAsync(t => t.Contains("Hojas que pusiste", StringComparison.Ordinal), "«Anotar el papel»");
        Assert.Equal("#papel-hojas", UltimoFoco(pagina));
        Assert.Contains("Todavía no se ha anotado ningún llenado", await pantalla.TextoAsync());
        var grupo = await pantalla.ElementoAsync(e => e.Clase == "vn-papel", "«Anotar el papel»");

        // Esc regresa a los avisos, al mismo; Enter lo abre otra vez.
        await pantalla.DispararAsync(grupo, "onkeydown", new KeyboardEventArgs { Key = "Escape" });
        Assert.Equal("#aviso-1", UltimoFoco(pagina));
        await pantalla.TeclaAsync("aviso-1", "Enter");
        await pantalla.EsperarAsync(t => t.Contains("Hojas que pusiste", StringComparison.Ordinal), "«Anotar el papel» otra vez");

        // Enter sin hojas: lo dice y el cursor regresa a las hojas.
        grupo = await pantalla.ElementoAsync(e => e.Clase == "vn-papel", "«Anotar el papel»");
        await pantalla.DispararAsync(grupo, "onkeydown", new KeyboardEventArgs { Key = "Enter" });
        Assert.Contains("Escribe cuántas hojas pusiste, o marca «La llené».", await pantalla.TextoAsync());

        // 450 y Enter: se guarda, el aviso del papel se quita y el cursor regresa al menú.
        await pantalla.EscribirAsync("papel-hojas", "450");
        grupo = await pantalla.ElementoAsync(e => e.Clase == "vn-papel", "«Anotar el papel»");
        await pantalla.DispararAsync(grupo, "onkeydown", new KeyboardEventArgs { Key = "Enter" });
        await pantalla.EsperarAsync(t => !t.Contains("anota el próximo llenado", StringComparison.Ordinal) && t.Contains("Avisos", StringComparison.Ordinal),
            "los avisos sin el del papel");
        Assert.Equal("#aviso-1", UltimoFoco(pagina));
        var papel = Assert.Single(await http.GetFromJsonAsync<List<PapelImpresoraDto>>($"api/sucursales/{caja.SucursalId}/impresoras/papel") ?? []);
        Assert.Equal(450, papel.Charolas[0].Quedan);

        // Esc cierra el menú y el cursor regresa a donde estaba (caja.js se acuerda).
        await pantalla.TeclaAsync("aviso-0", "Escape");
        Assert.Equal("regresarDeLosAvisos", UltimoFoco(pagina));
        Assert.DoesNotContain("No se ha contado nada hoy", await pantalla.TextoAsync());

        // Ponerse encima lo abre (sin mover el cursor) y salir lo cierra.
        var contenedor = await pantalla.ElementoAsync(e => e.Clase == "vn-signo", "el «!»");
        var focosAntes = pagina.Llamadas.Count(l => l.Funcion == "enfocar");
        await pantalla.DispararAsync(contenedor, "onmouseenter", new MouseEventArgs());
        await pantalla.EsperarAsync(t => t.Contains("No se ha contado nada hoy", StringComparison.Ordinal), "el menú por encima");
        Assert.Equal(focosAntes, pagina.Llamadas.Count(l => l.Funcion == "enfocar"));
        contenedor = await pantalla.ElementoAsync(e => e.Clase == "vn-signo", "el «!»");
        await pantalla.DispararAsync(contenedor, "onmouseleave", new MouseEventArgs());
        Assert.DoesNotContain("No se ha contado nada hoy", await pantalla.TextoAsync());

        // Sin nada que atender: atenuado, sin cuenta; el menú dice «Sin avisos» y deja anotar el papel.
        using (var alcance = _servicio.Fabrica.Services.CreateScope())
        {
            var db = alcance.ServiceProvider.GetRequiredService<PosDbContext>();
            await new RegistroAvisos(db).QuitarAsync(caja.SucursalId, "inventario", "");
            await db.SaveChangesAsync();
        }
        await pantalla.TeclaAsync("boton-avisos", "Enter");
        boton = await EsperarAsync(pantalla, "Avisos: ninguno (Alt+A)");
        Assert.Contains("tranquilo", boton.Clase);
        Assert.Equal("!", PantallaSinNavegador.Limpio(boton.Texto));
        Assert.Contains("Sin avisos.", await pantalla.TextoAsync());
        Assert.Equal("#aviso-0", UltimoFoco(pagina));
        Assert.Contains("Anotar el papel de una impresora", (await pantalla.PorIdAsync("aviso-0")).Atributos["aria-label"]?.ToString());
        Assert.Empty(pantalla.Errores);
    }

    private static async Task<ElementoDibujado> EsperarAsync(PantallaSinNavegador pantalla, string etiqueta)
    {
        var limite = DateTime.UtcNow.AddSeconds(30);
        while (true)
        {
            var boton = await pantalla.PorIdAsync("boton-avisos");
            if (boton.Atributos.GetValueOrDefault("aria-label")?.ToString() == etiqueta)
                return boton;
            if (DateTime.UtcNow > limite)
                throw new Xunit.Sdk.XunitException($"El «!» no llegó a «{etiqueta}»: dice «{boton.Atributos.GetValueOrDefault("aria-label")}».");
            await Task.Delay(20);
        }
    }
}
