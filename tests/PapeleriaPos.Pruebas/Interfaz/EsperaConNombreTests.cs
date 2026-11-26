using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using PapeleriaPos.Interfaz;
using PapeleriaPos.Nucleo.Contratos;
using PapeleriaPos.Nucleo.Control;
using PapeleriaPos.Pruebas.Servicio;

namespace PapeleriaPos.Pruebas.Interfaz;

/// <summary>
/// Los tickets en espera con nombre y el menú de F5 (2026-11-22, el dueño), solo con el teclado: F6 pone el cursor en la ficha del ticket
/// que se apartó para escribirle un nombre (hasta 15), Enter lo deja, Esc lo deja sin nombre; el nombre se guarda con la espera y vuelve
/// después de reiniciar la caja. F5 con varios abre un menú con todos (el nombre completo, la hora, cuántos productos distintos y el total):
/// ↑ ↓ eligen, Enter retoma, Esc cierra.
/// </summary>
public sealed class EsperaConNombreTests : IDisposable
{
    private readonly string _baseLocal = Path.Combine(Path.GetTempPath(), $"caja-nombre-{Guid.NewGuid():N}.db");

    public void Dispose() => ArchivosDePrueba.BorrarBase(_baseLocal);

    private static Task Tecla(VentaConCopia v, string tecla, string? texto = "") => v.Pantalla.Dispatcher.InvokeAsync(() => v.Venta.Tecla(tecla, texto));

    private static async Task<string> Texto(VentaConCopia v) => PantallaSinNavegador.Limpio(await v.Pantalla.TextoAsync());

    /// <summary>Las fichas de la espera (no el campo del nombre), con su texto.</summary>
    private static async Task<List<ElementoDibujado>> Fichas(VentaConCopia v) =>
        (await v.Pantalla.LeerAsync()).Elementos.Where(e => e.Etiqueta == "button" && e.Clase?.Split(' ').Contains("vn-ficha") == true).ToList();

    [Fact]
    public async Task F6_escribe_el_nombre_en_su_ficha_Enter_lo_deja_y_vuelve_despues_de_reiniciar()
    {
        await using var servicio = await ServicioDePrueba.CrearAsync();
        string total;
        await using (var v = await VentaConCopia.AbrirAsync(servicio, _baseLocal))
        {
            var (a, b) = v.DosDeLaCopia();
            await v.EscanearAsync(a.Codigo!);
            await v.EscanearAsync(b.Codigo!);
            await v.EscanearAsync(a.Codigo!);

            // F6: el ticket se aparta y el cursor va al campo del nombre, en su ficha.
            await Tecla(v, "F6");
            var campo = await v.Pantalla.PorIdAsync("nombre-espera");
            Assert.Equal("15", campo.Atributos["maxlength"]?.ToString());
            Assert.True(campo.Atributos.ContainsKey("data-nombre-espera"));
            Assert.Contains("Escribe su nombre (hasta 15 letras) y Enter, o Esc para dejarlo sin nombre", await Texto(v));
            Assert.Empty(await VentaConCopia.RenglonesAsync(v.Pantalla));

            // Lo escrito de más se recorta a 15 (el navegador ni lo deja escribir), y Enter lo deja.
            await v.Pantalla.EscribirAsync("nombre-espera", "Señora del uniforme");
            await v.Pantalla.TeclaAsync("nombre-espera", "Enter");
            Assert.DoesNotContain((await v.Pantalla.LeerAsync()).Elementos, e => e.Id == "nombre-espera");
            Assert.Contains("Ticket en espera: «Señora del unif». F5 lo retoma.", await Texto(v));
            var ficha = Assert.Single(await Fichas(v));
            var enLaFicha = System.Text.RegularExpressions.Regex.Match(PantallaSinNavegador.Limpio(ficha.Texto), @"^Señora del unif · (\$[\d,.]+)$");
            Assert.True(enLaFicha.Success, ficha.Texto);
            total = enLaFicha.Groups[1].Value;
            // El nombre completo en su ventanita (title), para el mouse.
            Assert.StartsWith("«Señora del unif», en espera desde las", ficha.Atributos["title"]?.ToString());
            await VentaConCopia.EsperarAsync(() => v.Contingencia.TicketsEnEspera() is [{ Nombre: "Señora del unif" }], "el nombre guardado");
        }

        // Se reinicia la caja: el ticket vuelve con su nombre, y F5 (uno solo) lo retoma directo.
        await using (var otra = await VentaConCopia.AbrirAsync(servicio, _baseLocal))
        {
            Assert.Equal($"Señora del unif · {total}", PantallaSinNavegador.Limpio(Assert.Single(await Fichas(otra)).Texto));
            await Tecla(otra, "F5");
            Assert.Equal(2, (await VentaConCopia.RenglonesAsync(otra.Pantalla)).Count);
            Assert.Contains("Retomaste el ticket «Señora del unif».", await Texto(otra));
            Assert.Empty(otra.Pantalla.Errores);
        }
    }

    [Fact]
    public async Task Esc_lo_deja_sin_nombre_y_otra_tecla_o_un_escaneo_dejan_lo_escrito()
    {
        await using var servicio = await ServicioDePrueba.CrearAsync();
        await using var v = await VentaConCopia.AbrirAsync(servicio, _baseLocal);
        var lista = v.DeLaCopia();

        // Esc: sin nombre, como antes (la ficha dice la hora); el cursor regresa al cuadro de captura.
        await v.EscanearAsync(lista[0].Codigo!);
        await Tecla(v, "F6");
        await v.Pantalla.EscribirAsync("nombre-espera", "Luis");
        await Tecla(v, "Escape");
        Assert.DoesNotContain((await v.Pantalla.LeerAsync()).Elementos, e => e.Id == "nombre-espera");
        Assert.Contains("Ticket en espera sin nombre. F5 lo retoma.", await Texto(v));
        Assert.DoesNotContain("Luis", PantallaSinNavegador.Limpio(Assert.Single(await Fichas(v)).Texto));

        // Enter con el campo vacío: también sin nombre.
        await v.EscanearAsync(lista[1].Codigo!);
        await Tecla(v, "F6");
        await v.Pantalla.TeclaAsync("nombre-espera", "Enter");
        Assert.Contains("Ticket en espera sin nombre. F5 lo retoma.", await Texto(v));

        // Escribir y luego escanear (el siguiente cliente; caja.js lleva el código al cuadro): el nombre queda y el producto entra al
        // ticket nuevo. Supr con el nombre a medias no borra nada del ticket.
        await v.EscanearAsync(lista[2].Codigo!);
        await Tecla(v, "F6");
        await v.Pantalla.EscribirAsync("nombre-espera", "Ana");
        await Tecla(v, "Delete");
        Assert.NotNull((await v.Pantalla.LeerAsync()).Elementos.FirstOrDefault(e => e.Id == "nombre-espera"));
        await v.EscanearAsync(lista[3].Codigo!);
        var renglon = Assert.Single(await VentaConCopia.RenglonesAsync(v.Pantalla));
        Assert.Contains(lista[3].Descripcion, renglon);
        Assert.Contains(await Fichas(v), f => PantallaSinNavegador.Limpio(f.Texto).StartsWith("Ana · ", StringComparison.Ordinal));

        // Otra tecla (F6 otra vez, con el ticket nuevo) también deja lo escrito.
        await Tecla(v, "F6");
        await v.Pantalla.EscribirAsync("nombre-espera", "Don Pepe");
        await Tecla(v, "Alt+1");
        // Con cuatro en espera y tres fichas, ya nombrado se va a «Más» (mientras se nombraba, era ficha).
        Assert.DoesNotContain((await v.Pantalla.LeerAsync()).Elementos, e => e.Id == "nombre-espera");
        await VentaConCopia.EsperarAsync(() => v.Contingencia.TicketsEnEspera().Count(t => t.Nombre is "Ana" or "Don Pepe") == 2, "los nombres guardados");
        Assert.Empty(v.Pantalla.Errores);
    }

    [Fact]
    public async Task F5_con_varios_abre_el_menu_con_nombre_hora_productos_distintos_y_total_y_se_maneja_con_flechas()
    {
        await using var servicio = await ServicioDePrueba.CrearAsync();
        await using var v = await VentaConCopia.AbrirAsync(servicio, _baseLocal);
        var (a, b) = v.DosDeLaCopia();

        // El primero: el mismo producto tres veces (1 producto), con nombre.
        await v.EscanearAsync("3*" + a.Codigo);
        await Tecla(v, "F6");
        await v.Pantalla.EscribirAsync("nombre-espera", "Señora azul");
        await v.Pantalla.TeclaAsync("nombre-espera", "Enter");
        // El segundo: dos productos distintos, sin nombre.
        await v.EscanearAsync(a.Codigo!);
        await v.EscanearAsync(b.Codigo!);
        await Tecla(v, "F6");
        await Tecla(v, "Escape");
        // El que se atiende: b.
        await v.EscanearAsync(b.Codigo!);

        // F5: el menú con los dos, el que lleva más tiempo ya elegido; el ticket que se atiende no cambia.
        await Tecla(v, "F5");
        var uno = await v.Pantalla.PorIdAsync("elegir-espera-0");
        var dos = await v.Pantalla.PorIdAsync("elegir-espera-1");
        Assert.Contains("elegida", uno.Clase);
        Assert.DoesNotContain("elegida", dos.Clase);
        Assert.Matches(@"^Señora azul \d\d:\d\d · 1 producto \$", PantallaSinNavegador.Limpio(uno.Texto));
        Assert.Matches(@"^Ticket de las \d\d:\d\d \d\d:\d\d · 2 productos \$", PantallaSinNavegador.Limpio(dos.Texto));
        Assert.Contains("Tickets en espera (2)", await Texto(v));
        Assert.Contains("Enter retomar", await Texto(v));
        Assert.Contains("Esc cerrar", await Texto(v));
        Assert.Contains(b.Descripcion, Assert.Single(await VentaConCopia.RenglonesAsync(v.Pantalla)));

        // ↓ elige el segundo; ↓ otra vez da la vuelta; ↑ regresa; F5 también baja.
        await Tecla(v, "ArrowDown");
        Assert.Contains("elegida", (await v.Pantalla.PorIdAsync("elegir-espera-1")).Clase);
        await Tecla(v, "ArrowDown");
        Assert.Contains("elegida", (await v.Pantalla.PorIdAsync("elegir-espera-0")).Clase);
        await Tecla(v, "ArrowUp");
        Assert.Contains("elegida", (await v.Pantalla.PorIdAsync("elegir-espera-1")).Clase);
        await Tecla(v, "F5");
        Assert.Contains("elegida", (await v.Pantalla.PorIdAsync("elegir-espera-0")).Clase);

        // Esc lo cierra sin retomar nada.
        await Tecla(v, "Escape");
        Assert.DoesNotContain((await v.Pantalla.LeerAsync()).Elementos, e => e.Id == "elegir-espera-0");
        Assert.Contains(b.Descripcion, Assert.Single(await VentaConCopia.RenglonesAsync(v.Pantalla)));

        // F5, ↓ y Enter: retoma el segundo (los dos productos) y el que se atendía pasa a la espera.
        await Tecla(v, "F5");
        await Tecla(v, "ArrowDown");
        await v.Pantalla.TeclaAsync("elegir-espera-1", "Enter");
        Assert.DoesNotContain((await v.Pantalla.LeerAsync()).Elementos, e => e.Id == "elegir-espera-0");
        Assert.Equal(2, (await VentaConCopia.RenglonesAsync(v.Pantalla)).Count);
        Assert.Equal(2, (await Fichas(v)).Count);

        // F5 y Enter con el primero elegido: lo de antes (el que lleva más tiempo esperando).
        await Tecla(v, "F5");
        await v.Pantalla.TeclaAsync("elegir-espera-0", "Enter");
        Assert.Contains("Retomaste el ticket «Señora azul».", await Texto(v));
        Assert.StartsWith("3 ", Assert.Single(await VentaConCopia.RenglonesAsync(v.Pantalla)));
        Assert.Empty(v.Pantalla.Errores);
    }

    /// <summary>Tres en espera («Ana», «Beto» y uno sin nombre) y uno que se atiende.</summary>
    private static async Task<(ProductoVentaDto A, ProductoVentaDto B)> TresEnEsperaAsync(VentaConCopia v)
    {
        var (a, b) = v.DosDeLaCopia();
        foreach (var nombre in new[] { "Ana", "Beto", null })
        {
            await v.EscanearAsync(a.Codigo!);
            await v.EscanearAsync(b.Codigo!);
            await Tecla(v, "F6");
            if (nombre is null)
                await Tecla(v, "Escape");
            else
            {
                await v.Pantalla.EscribirAsync("nombre-espera", nombre);
                await v.Pantalla.TeclaAsync("nombre-espera", "Enter");
            }
        }
        await v.EscanearAsync(b.Codigo!);
        return (a, b);
    }

    private static async Task<bool> HayAsync(VentaConCopia v, string id) => (await v.Pantalla.LeerAsync()).Elementos.Any(e => e.Id == id);

    /// <summary>
    /// Borrar un ticket en espera desde el menú de F5 (el dueño, 2026-11-26), solo con el teclado: Supr sobre el elegido pregunta en su
    /// renglón, Esc no lo borra, Enter sí; queda en la bitácora como «Eliminar este ticket» y ya no vuelve al reiniciar. El menú sigue
    /// abierto con los que quedan; al borrar el último se cierra. El ticket que se atiende no se toca.
    /// </summary>
    [Fact]
    public async Task En_el_menu_de_F5_Supr_pregunta_Enter_borra_el_ticket_en_espera_y_Esc_no()
    {
        await using var servicio = await ServicioDePrueba.CrearAsync();
        await using var v = await VentaConCopia.AbrirAsync(servicio, _baseLocal);
        var (a, b) = await TresEnEsperaAsync(v);

        await Tecla(v, "F5");
        Assert.Contains("Tickets en espera (3)", await Texto(v));
        Assert.Contains("Supr borrar", await Texto(v));
        Assert.Equal("Borrar este ticket en espera (Supr)", (await v.Pantalla.PorIdAsync("borrar-espera-0")).Atributos["title"]?.ToString());

        // ↓ y Supr: el renglón de «Beto» pregunta; nada se borra todavía. Supr otra vez no borra: sigue preguntando.
        await Tecla(v, "ArrowDown");
        await Tecla(v, "Delete");
        var pregunta = PantallaSinNavegador.Limpio((await v.Pantalla.PorIdAsync("elegir-espera-1")).Texto);
        Assert.StartsWith("Beto", pregunta);
        Assert.Contains("¿Borrar este ticket? Enter lo borra · Esc no", pregunta);
        Assert.Contains("Enter borrar el ticket", await Texto(v));
        Assert.Contains("Esc no borrarlo", await Texto(v));
        await Tecla(v, "Delete");
        Assert.Contains("Tickets en espera (3)", await Texto(v));
        Assert.True(await HayAsync(v, "confirmar-borrar-espera"));

        // Esc: no se borra y el menú sigue abierto, con «Beto» elegido. La barra espaciadora mientras pregunta tampoco hace nada.
        await Tecla(v, "Escape");
        Assert.Contains("No se borró: el ticket sigue en espera.", await Texto(v));
        Assert.False(await HayAsync(v, "confirmar-borrar-espera"));
        Assert.Contains("elegida", (await v.Pantalla.PorIdAsync("elegir-espera-1")).Clase);
        await Tecla(v, "Delete");
        await v.Pantalla.TeclaAsync("elegir-espera-1", " ");
        Assert.Contains("Tickets en espera (3)", await Texto(v));

        // Enter: se borra «Beto». El menú sigue con los otros dos y el que seguía ya elegido.
        await v.Pantalla.TeclaAsync("elegir-espera-1", "Enter");
        Assert.Contains("Se borró el ticket en espera «Beto»", await Texto(v));
        Assert.Contains("Tickets en espera (2)", await Texto(v));
        Assert.StartsWith("Ana ", PantallaSinNavegador.Limpio((await v.Pantalla.PorIdAsync("elegir-espera-0")).Texto));
        Assert.StartsWith("Ticket de las ", PantallaSinNavegador.Limpio((await v.Pantalla.PorIdAsync("elegir-espera-1")).Texto));
        Assert.Contains("elegida", (await v.Pantalla.PorIdAsync("elegir-espera-1")).Clase);
        // El que se atiende sigue igual.
        Assert.Contains(b.Descripcion, Assert.Single(await VentaConCopia.RenglonesAsync(v.Pantalla)));
        await VentaConCopia.EsperarAsync(() => v.Contingencia.TicketsEnEspera().Count(t => t.Nombre == "Beto") == 0, "la espera guardada sin «Beto»");

        // En la bitácora, un renglón con lo que llevaba.
        using (var db = servicio.Base())
        {
            var renglon = await db.Bitacora.SingleAsync(x => x.Accion == AccionControlada.QuitarProducto);
            Assert.Contains("Eliminó el ticket en espera", renglon.Detalle);
            Assert.Contains("«Beto»", renglon.Detalle);
            Assert.Contains(a.Descripcion, renglon.Detalle);
            Assert.Contains(b.Descripcion, renglon.Detalle);
        }

        // Los otros dos, uno tras otro: Supr y Enter. Al borrar el último, el menú se cierra.
        await Tecla(v, "Delete");
        await v.Pantalla.TeclaAsync("elegir-espera-1", "Enter");
        Assert.Contains("Tickets en espera (1)", await Texto(v));
        await Tecla(v, "Delete");
        await v.Pantalla.TeclaAsync("elegir-espera-0", "Enter");
        Assert.False(await HayAsync(v, "elegir-espera-0"));
        Assert.Empty(await Fichas(v));
        Assert.Contains(b.Descripcion, Assert.Single(await VentaConCopia.RenglonesAsync(v.Pantalla)));
        await VentaConCopia.EsperarAsync(() => v.Contingencia.TicketsEnEspera().Count == 1, "solo queda guardado el que se atiende");
        using (var db = servicio.Base())
            Assert.Equal(3, await db.Bitacora.CountAsync(x => x.Accion == AccionControlada.QuitarProducto));
        // Ya sin ninguno, F5 lo dice.
        await Tecla(v, "F5");
        Assert.Contains("No hay tickets en espera.", await Texto(v));
        Assert.Empty(v.Pantalla.Errores);
    }

    /// <summary>Con el mouse: la × del renglón pregunta, «Borrar» lo borra y un clic en el renglón que pregunta solo quita la pregunta.</summary>
    [Fact]
    public async Task En_el_menu_de_F5_la_x_pregunta_y_Borrar_lo_borra()
    {
        await using var servicio = await ServicioDePrueba.CrearAsync();
        await using var v = await VentaConCopia.AbrirAsync(servicio, _baseLocal);
        await TresEnEsperaAsync(v);

        await Tecla(v, "F5");
        await v.Pantalla.OprimirAsync(await v.Pantalla.PorIdAsync("borrar-espera-2"));
        Assert.Contains("¿Borrar este ticket?", PantallaSinNavegador.Limpio((await v.Pantalla.PorIdAsync("elegir-espera-2")).Texto));
        // Un clic en ese renglón no lo retoma ni lo borra: quita la pregunta.
        await v.Pantalla.OprimirAsync(await v.Pantalla.PorIdAsync("elegir-espera-2"));
        Assert.Contains("Tickets en espera (3)", await Texto(v));
        Assert.False(await HayAsync(v, "confirmar-borrar-espera"));

        await v.Pantalla.OprimirAsync(await v.Pantalla.PorIdAsync("borrar-espera-0"));
        await v.Pantalla.OprimirAsync(await v.Pantalla.PorIdAsync("confirmar-borrar-espera"));
        Assert.Contains("Se borró el ticket en espera «Ana»", await Texto(v));
        Assert.Contains("Tickets en espera (2)", await Texto(v));
        Assert.StartsWith("Beto ", PantallaSinNavegador.Limpio((await v.Pantalla.PorIdAsync("elegir-espera-0")).Texto));

        // Otra tecla mientras pregunta (F6, con el ticket que se atiende) cierra el menú sin borrar nada.
        await Tecla(v, "Delete");
        await Tecla(v, "F6");
        await Tecla(v, "Escape");
        Assert.Equal(3, (await Fichas(v)).Count);
        using var db = servicio.Base();
        Assert.Equal(1, await db.Bitacora.CountAsync(x => x.Accion == AccionControlada.QuitarProducto));
        Assert.Empty(v.Pantalla.Errores);
    }

    /// <summary>Quien no puede quitar productos tampoco borra tickets en espera: sin ×, sin «Supr borrar», y Supr solo lo avisa.</summary>
    [Fact]
    public async Task Sin_permiso_de_quitar_el_menu_de_F5_no_borra_tickets_en_espera()
    {
        await using var servicio = await ServicioDePrueba.CrearAsync();
        var http = servicio.Fabrica.CreateClient();
        var caja = (await http.GetFromJsonAsync<List<CajaDto>>("api/cajas"))!.First();
        var dueno = await (await http.PostAsJsonAsync("api/sesion", new SolicitudEntrada("dueno", "3333"))).Content.ReadFromJsonAsync<SesionDto>();
        var cajero = await (await http.PostAsJsonAsync("api/sesion", new SolicitudEntrada("cajero", "1111"))).Content.ReadFromJsonAsync<SesionDto>();
        (await http.PutAsJsonAsync($"api/cajas/{caja.Id}/usuarios-y-permisos/{cajero!.UsuarioId}",
            new SolicitudPermisoUsuario(dueno!.UsuarioId, AccionControlada.QuitarProducto, NivelPermiso.NoPermitido))).EnsureSuccessStatusCode();
        await using var v = await VentaConCopia.AbrirAsync(servicio, _baseLocal);
        await TresEnEsperaAsync(v);

        await Tecla(v, "F5");
        Assert.False(await HayAsync(v, "borrar-espera-0"));
        Assert.DoesNotContain("Supr borrar", await Texto(v));
        await Tecla(v, "Delete");
        Assert.Contains("Eso no está entre lo que puedes hacer", await Texto(v));
        Assert.False(await HayAsync(v, "confirmar-borrar-espera"));
        Assert.Contains("Tickets en espera (3)", await Texto(v));
        // Retomar sigue sirviendo.
        await v.Pantalla.TeclaAsync("elegir-espera-0", "Enter");
        Assert.Contains("Retomaste el ticket «Ana».", await Texto(v));
        using var db = servicio.Base();
        Assert.Equal(0, await db.Bitacora.CountAsync(x => x.Accion == AccionControlada.QuitarProducto));
        Assert.Empty(v.Pantalla.Errores);
    }

    /// <summary>
    /// «Ventas del día» al lado del cliente (el dueño, 2026-11-26): el enlace dice su tecla, Ctrl+H la abre y la cierra, Esc también, y
    /// está en «/» con su tecla.
    /// </summary>
    [Fact]
    public async Task Ventas_del_dia_esta_junto_al_cliente_y_se_abre_con_Ctrl_H()
    {
        await using var servicio = await ServicioDePrueba.CrearAsync();
        await using var v = await VentaConCopia.AbrirAsync(servicio, _baseLocal);

        var todo = (await v.Pantalla.LeerAsync()).Elementos;
        var enlace = todo.Single(e => e.Id == "enlace-ventas-del-dia");
        Assert.Equal("Ventas del día Ctrl+H", PantallaSinNavegador.Limpio(enlace.Texto));
        Assert.Contains("(Ctrl+H)", enlace.Atributos["title"]?.ToString());
        // Al lado de «Público en general»: justo antes de la flecha del cliente.
        var cliente = todo.FindIndex(e => e.Clase == "vn-flecha-cli");
        Assert.Equal("Público en general", PantallaSinNavegador.Limpio(todo[cliente].Texto));
        Assert.InRange(todo.IndexOf(enlace), 0, cliente - 1);

        // Solo con el teclado: Ctrl+H la abre, Ctrl+H la cierra; otra vez, y Esc la cierra.
        Assert.False(await HayAsync(v, "ventas"));
        await Tecla(v, "Ctrl+H", null);
        await v.Pantalla.EsperarElementoAsync("ventas");
        Assert.Contains("Ventas del día", await Texto(v));
        await Tecla(v, "Ctrl+H", null);
        Assert.False(await HayAsync(v, "ventas"));
        await Tecla(v, "Ctrl+H", null);
        await v.Pantalla.EsperarElementoAsync("ventas");
        await Tecla(v, "Escape", null);
        Assert.False(await HayAsync(v, "ventas"));

        // Con el mouse, el enlace; y en «/», con su tecla.
        await v.Pantalla.OprimirAsync(await v.Pantalla.PorIdAsync("enlace-ventas-del-dia"));
        await v.Pantalla.EsperarElementoAsync("ventas");
        await Tecla(v, "Escape", null);
        await v.Pantalla.Dispatcher.InvokeAsync(() => v.Venta.Escribiendo("/"));
        Assert.Contains("Ventas del día Ctrl+H", await Texto(v));
        Assert.Empty(v.Pantalla.Errores);
    }
}
