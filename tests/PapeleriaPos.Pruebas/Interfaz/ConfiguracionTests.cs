using System.Globalization;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.JSInterop;
using PapeleriaPos.Impresion;
using PapeleriaPos.Interfaz;
using PapeleriaPos.Interfaz.Componentes;
using PapeleriaPos.Nucleo.Configuracion;
using PapeleriaPos.Nucleo.Contratos;
using PapeleriaPos.Nucleo.Control;
using PapeleriaPos.Pruebas.Servicio;

namespace PapeleriaPos.Pruebas.Interfaz;

/// <summary>
/// La Configuración nueva (entrega 7) contra el servicio de verdad, sin navegador: los grupos de quien entró, el buscador y
/// cada cambio que se guarda solo, con la sesión, sin «Guardar» ni usuario y PIN.
/// </summary>
public sealed class ConfiguracionTests(ServicioDePrueba servicio) : IClassFixture<ServicioDePrueba>
{
    private sealed record Abierta(PantallaSinNavegador Pantalla, CajaDto Caja, SesionDto Sesion, HttpClient Http, Func<int> Cambios) : IAsyncDisposable
    {
        public ValueTask DisposeAsync() => ((IAsyncDisposable)Pantalla).DisposeAsync();
    }

    private async Task<Abierta> AbrirAsync(string usuario, string pin)
    {
        var http = servicio.Fabrica.CreateClient();
        var caja = (await http.GetFromJsonAsync<List<CajaDto>>("api/cajas"))!.Single();
        var sesion = (await (await http.PostAsJsonAsync("api/sesion", new SolicitudEntrada(usuario, pin))).Content.ReadFromJsonAsync<SesionDto>())!;
        var conexion = new ConexionCaja { Servicio = new Uri("http://localhost/"), CajaId = caja.Id };
        var servicios = new ServiceCollection()
            .AddSingleton(conexion)
            .AddSingleton(new ClienteCaja(servicio.Fabrica.CreateClient(), conexion))
            .AddSingleton<IImpresora>(new SinImpresora())
            .AddSingleton<IJSRuntime>(new PaginaQueContesta())
            .AddScoped<ModuloCaja>()
            .BuildServiceProvider();
        var pantalla = new PantallaSinNavegador(servicios);
        var cambios = 0;
        await pantalla.AbrirAsync<Configuracion>(new()
        {
            [nameof(Configuracion.Caja)] = caja,
            [nameof(Configuracion.Sesion)] = sesion,
            [nameof(Configuracion.AlCambiar)] = new EventCallback(null, (Action)(() => cambios++)),
        });
        await pantalla.EsperarAsync(t => !t.Contains("Cargando…"), "lo guardado");
        return new Abierta(pantalla, caja, sesion, http, () => cambios);
    }

    private static async Task<List<string>> GruposAsync(PantallaSinNavegador p) =>
        (await p.LeerAsync()).Elementos.Where(e => e.Atributos.GetValueOrDefault("role") as string == "tab").Select(e => PantallaSinNavegador.Limpio(e.Texto)).ToList();

    private static async Task GrupoAsync(PantallaSinNavegador p, string nombre) =>
        await p.OprimirAsync(await p.ElementoAsync(e => e.Atributos.GetValueOrDefault("role") as string == "tab" && PantallaSinNavegador.Limpio(e.Texto) == nombre,
            $"el grupo «{nombre}»"));

    private async Task<ConfiguracionSucursalDto> TiendaAsync(Abierta a) =>
        (await a.Http.GetFromJsonAsync<ConfiguracionSucursalDto>($"api/sucursales/{a.Caja.SucursalId}/configuracion"))!;

    /// <summary>
    /// Cada grupo solo con su permiso («No»: no aparece), como en el prototipo. Con los usuarios de prueba: el dueño, todos;
    /// el encargado, lo del ticket, los sinónimos, la impresora (el desglose del día), el sugerido y la bitácora; el cajero
    /// no tiene la Configuración. «Usuarios y permisos» solo para quien lo tiene, y ya no hay «Reglas».
    /// </summary>
    [Theory]
    [InlineData("dueno", "3333", "Tienda,Venta y cobro,Efectivo y corte,Ticket,Impresoras e impresiones,Inventario,Usuarios y permisos,Avisos por Telegram,Cajas,Bitácora,Teclas,Acerca de")]
    [InlineData("encargado", "2222", "Tienda,Venta y cobro,Ticket,Impresoras e impresiones,Inventario,Bitácora,Teclas,Acerca de")]
    public async Task Los_grupos_son_los_de_los_permisos_de_quien_entro(string usuario, string pin, string grupos)
    {
        await using var a = await AbrirAsync(usuario, pin);

        Assert.Equal(grupos.Split(','), await GruposAsync(a.Pantalla));
        Assert.True(Configuracion.HayQueVer(a.Sesion.Ve, a.Sesion.Puede, hayActualizacion: false));
        Assert.Empty(a.Pantalla.Errores);
    }

    [Fact]
    public async Task El_cajero_no_tiene_la_configuracion_y_el_encargado_ve_solo_sus_ajustes()
    {
        var http = servicio.Fabrica.CreateClient();
        var cajero = (await (await http.PostAsJsonAsync("api/sesion", new SolicitudEntrada("cajero", "1111"))).Content.ReadFromJsonAsync<SesionDto>())!;
        await using var a = await AbrirAsync("encargado", "2222");

        Assert.False(Configuracion.HayQueVer(cajero.Ve, cajero.Puede, hayActualizacion: false));
        // La caja de prueba se actualiza desde aquí: ahí la ve cualquiera.
        Assert.True(Configuracion.HayQueVer(cajero.Ve, cajero.Puede, hayActualizacion: true));
        // En Tienda, el encargado solo tiene el logotipo (es del ticket); el nombre y el horario son de «Configuración de la tienda».
        var tienda = await a.Pantalla.TextoAsync();
        Assert.Contains("\nLogotipo", tienda);
        Assert.DoesNotContain("Nombre de la tienda", tienda);
        Assert.DoesNotContain("Horario", tienda);
        // En Venta y cobro, los sinónimos (edita el catálogo), sin las fichas ni los rápidos.
        await GrupoAsync(a.Pantalla, "Venta y cobro");
        var venta = await a.Pantalla.TextoAsync();
        Assert.Contains("Sinónimos de la búsqueda", venta);
        Assert.Contains("Días para devolver con ticket", venta);
        Assert.DoesNotContain("Tickets en espera a la vista", venta);
        Assert.DoesNotContain("\nRápidos", venta);
        // En Inventario, lo que cubre el sugerido (hace los pedidos), sin el margen (es del dueño).
        await GrupoAsync(a.Pantalla, "Inventario");
        var inventario = await a.Pantalla.TextoAsync();
        Assert.Contains("El sugerido de compra cubre", inventario);
        Assert.DoesNotContain("Margen mínimo", inventario);
    }

    [Fact]
    public async Task Cada_ajuste_dice_para_que_sirve_y_su_alcance_y_el_grupo_si_ya_se_guardo()
    {
        await using var a = await AbrirAsync("dueno", "3333");

        var texto = await a.Pantalla.TextoAsync();
        Assert.Contains("\nTienda\n", texto);
        Assert.Contains("Se guarda solo", texto);
        Assert.Contains("Nombre de la tienda", texto);
        Assert.Contains($"Sale en la entrada de cada caja. Vacío: el de la sucursal, «{a.Caja.Sucursal}».", texto);
        Assert.Contains("Toda la tienda", texto);
        // Sin «Guardar» ni usuario y PIN.
        Assert.DoesNotContain((await a.Pantalla.LeerAsync()).Elementos, e => e.Etiqueta == "button" && e.Texto.Contains("Guardar"));
        Assert.DoesNotContain("PIN", texto);
    }

    [Fact]
    public async Task El_buscador_encuentra_ajustes_de_todos_los_grupos_y_dice_de_cual_es()
    {
        await using var a = await AbrirAsync("dueno", "3333");

        await a.Pantalla.EscribirAsync("cfg-busca", "fondo");
        var fondo = await a.Pantalla.TextoAsync();
        Assert.Contains("Ajustes con «fondo»", fondo);
        Assert.Contains("1 encontrado", fondo);
        Assert.Contains("Fondo sugerido al abrir turno", fondo);
        Assert.Contains("Efectivo y corte · «Abrir turno» lo propone", fondo);
        // Se cambia ahí mismo, sin ir a su grupo.
        Assert.NotNull(await a.Pantalla.PorIdAsync("aj-fondo"));

        // Sin acentos ni mayúsculas, y por el nombre del grupo.
        await a.Pantalla.EscribirAsync("cfg-busca", "RAPIDOS");
        Assert.Contains("\nRápidos", await a.Pantalla.TextoAsync());
        await a.Pantalla.EscribirAsync("cfg-busca", "ticket");
        var ticket = await a.Pantalla.TextoAsync();
        Assert.Contains("Encabezado", ticket);
        Assert.Contains("Pie", ticket);

        // Lo que está en un grupo propio lleva a él.
        await a.Pantalla.EscribirAsync("cfg-busca", "impresora");
        Assert.Contains("IP de la impresora", await a.Pantalla.TextoAsync());
        await a.Pantalla.OprimirAsync(await a.Pantalla.BotonAsync("Abrir «Impresoras e impresiones»"));
        Assert.Equal("", (await a.Pantalla.PorIdAsync("cfg-busca")).Valor);
        Assert.Equal("on", (await a.Pantalla.ElementoAsync(e => e.Id == "cfg-impresoras", "el grupo")).Clase);

        await a.Pantalla.EscribirAsync("cfg-busca", "zzz");
        Assert.Contains("Ningún ajuste se llama así.", await a.Pantalla.TextoAsync());
        Assert.Empty(a.Pantalla.Errores);
    }

    [Fact]
    public async Task Lo_escrito_se_guarda_al_salir_del_campo_o_con_Enter_y_dice_a_que_hora()
    {
        await using var a = await AbrirAsync("dueno", "3333");
        var antes = await TiendaAsync(a);
        try
        {
            await GrupoAsync(a.Pantalla, "Efectivo y corte");
            await a.Pantalla.EscribirAsync("aj-fondo", "$450");
            await a.Pantalla.SalirAsync("aj-fondo");

            Assert.Equal(45000, (await TiendaAsync(a)).FondoSugeridoCentavos);
            Assert.Contains("Guardado a las", await a.Pantalla.TextoAsync());
            Assert.Equal("450", (await a.Pantalla.PorIdAsync("aj-fondo")).Valor);
            // La venta se entera (vuelve a pedir lo suyo).
            Assert.Equal(1, a.Cambios());

            // Con Enter, como al salir.
            await GrupoAsync(a.Pantalla, "Tienda");
            await a.Pantalla.EscribirAsync("aj-nombre", "Papelería La Prueba");
            await a.Pantalla.TeclaAsync("aj-nombre", "Enter");
            Assert.Equal("Papelería La Prueba", (await TiendaAsync(a)).NombreTienda);

            // Vacío: sin fondo sugerido.
            await GrupoAsync(a.Pantalla, "Efectivo y corte");
            await a.Pantalla.EscribirAsync("aj-fondo", "");
            await a.Pantalla.SalirAsync("aj-fondo");
            Assert.Null((await TiendaAsync(a)).FondoSugeridoCentavos);
            Assert.Empty(a.Pantalla.Errores);
        }
        finally
        {
            await RegresarAsync(a, antes);
        }
    }

    [Fact]
    public async Task Si_no_se_puede_lo_dice_junto_al_ajuste_y_regresa_a_lo_guardado()
    {
        await using var a = await AbrirAsync("dueno", "3333");
        var antes = await TiendaAsync(a);
        await GrupoAsync(a.Pantalla, "Venta y cobro");

        // Lo revisa el servicio: más de 6 fichas no.
        await a.Pantalla.EscribirAsync("aj-fichas", "9");
        await a.Pantalla.SalirAsync("aj-fichas");
        Assert.Contains("Se ven de 0 a 6 fichas; los demás tickets en espera van en «Más».", await a.Pantalla.TextoAsync());
        Assert.Equal(antes.FichasEnEspera.ToString(), (await a.Pantalla.PorIdAsync("aj-fichas")).Valor);

        // Lo revisa la pantalla: lo que no es número.
        await GrupoAsync(a.Pantalla, "Efectivo y corte");
        await a.Pantalla.EscribirAsync("aj-limite", "mucho");
        await a.Pantalla.SalirAsync("aj-limite");
        Assert.Contains("«mucho» no es una cantidad: escribe los pesos, 500 por ejemplo.", await a.Pantalla.TextoAsync());
        Assert.Equal(antes.LimiteEfectivoCentavos is { } l ? (l / 100m).ToString("0.##", CultureInfo.InvariantCulture) : "",
            (await a.Pantalla.PorIdAsync("aj-limite")).Valor);

        Assert.Equal(antes, await TiendaAsync(a));
        Assert.Equal(0, a.Cambios());
    }

    [Fact]
    public async Task Tras_una_pausa_al_escribir_se_guarda_sin_salir_del_campo()
    {
        await using var a = await AbrirAsync("dueno", "3333");
        var antes = await TiendaAsync(a);
        try
        {
            await a.Pantalla.EscribirAsync("aj-direccion", "Calle Ficticia 123, Centro");

            var limite = DateTime.UtcNow.AddSeconds(10);
            while ((await TiendaAsync(a)).Direccion != "Calle Ficticia 123, Centro")
            {
                Assert.True(DateTime.UtcNow < limite, "No se guardó tras la pausa.");
                await Task.Delay(50);
            }
            await a.Pantalla.EsperarAsync(t => t.Contains("Guardado a las"), "«Guardado a las»");
        }
        finally
        {
            await RegresarAsync(a, antes);
        }
    }

    [Fact]
    public async Task Al_cerrar_la_ventana_con_algo_escrito_se_guarda()
    {
        var a = await AbrirAsync("dueno", "3333");
        var antes = await TiendaAsync(a);
        try
        {
            // Esc con el cursor en el campo: la ventana se va sin que se salga de él.
            await a.Pantalla.EscribirAsync("aj-telefono", "55 0000 0000");
            await ((IAsyncDisposable)a.Pantalla).DisposeAsync();

            Assert.Equal("55 0000 0000", (await TiendaAsync(a)).Telefono);
        }
        finally
        {
            await RegresarAsync(a, antes);
        }
    }

    [Fact]
    public async Task El_ticket_se_ve_como_sale_mientras_se_escribe_y_un_renglon_largo_no_se_guarda()
    {
        await using var a = await AbrirAsync("encargado", "2222");
        await GrupoAsync(a.Pantalla, "Ticket");
        var guardado = (await a.Http.GetFromJsonAsync<ConfiguracionTicketDto>($"api/sucursales/{a.Caja.SucursalId}/ticket"))!;

        Assert.Contains("Así sale", await a.Pantalla.TextoAsync());
        await a.Pantalla.EscribirAsync("aj-encabezado", "PAPELERÍA DE PRUEBA\nCalle 5");
        Assert.Contains("PAPELERÍA DE PRUEBA", await a.Pantalla.TextoAsync());

        await a.Pantalla.EscribirAsync("aj-encabezado", new string('x', 49));
        await a.Pantalla.SalirAsync("aj-encabezado");

        Assert.Contains($"«{new string('x', 49)}» tiene más de 48 caracteres: no cabe en el renglón.", await a.Pantalla.TextoAsync());
        Assert.Equal(string.Join('\n', guardado.Encabezado), (await a.Pantalla.PorIdAsync("aj-encabezado")).Valor);
        Assert.Equal(guardado.Encabezado,
            (await a.Http.GetFromJsonAsync<ConfiguracionTicketDto>($"api/sucursales/{a.Caja.SucursalId}/ticket"))!.Encabezado);
        Assert.NotNull(await a.Pantalla.BotonAsync("Imprimir uno de prueba"));
    }

    [Fact]
    public async Task Los_rapidos_se_cambian_ahi_mismo_y_la_venta_se_entera()
    {
        await using var a = await AbrirAsync("dueno", "3333");
        var url = $"api/sucursales/{a.Caja.SucursalId}/rapidos";
        var antes = (await a.Http.GetFromJsonAsync<List<Rapido>>(url))!;
        await GrupoAsync(a.Pantalla, "Venta y cobro");
        await a.Pantalla.EsperarAsync(t => t.Contains("Alt+2"), "los rápidos");
        try
        {
            // El nombre del botón, al salir del campo.
            await a.Pantalla.EscribirAsync("rapido-nombre-1", "CURP (10)");
            await a.Pantalla.SalirAsync("rapido-nombre-1");
            Assert.Equal(["Copia B/N", "CURP (10)"], (await a.Http.GetFromJsonAsync<List<Rapido>>(url))!.Select(r => r.Nombre));

            // Bajar el primero: cambian de tecla.
            await a.Pantalla.OprimirAsync(await a.Pantalla.ElementoAsync(e => e.Atributos.GetValueOrDefault("aria-label") as string == "Bajar", "«Bajar»"));
            Assert.Equal(["CURP (10)", "Copia B/N"], (await a.Http.GetFromJsonAsync<List<Rapido>>(url))!.Select(r => r.Nombre));
            Assert.Equal(2, a.Cambios());
            Assert.Contains("Guardado a las", await a.Pantalla.TextoAsync());

            // Lo que no se puede, junto a los rápidos: dos con el mismo nombre.
            await a.Pantalla.EscribirAsync("rapido-nombre-0", "Copia B/N");
            await a.Pantalla.SalirAsync("rapido-nombre-0");
            Assert.Contains("Ya hay un rápido que se llama «Copia B/N»: ponle otro nombre.", await a.Pantalla.TextoAsync());
            Assert.Equal("CURP (10)", (await a.Pantalla.PorIdAsync("rapido-nombre-0")).Valor);
            Assert.Empty(a.Pantalla.Errores);
        }
        finally
        {
            var dueno = (await (await a.Http.PostAsJsonAsync("api/sesion", new SolicitudEntrada("dueno", "3333"))).Content.ReadFromJsonAsync<SesionDto>())!;
            (await a.Http.PutAsJsonAsync($"api/cajas/{a.Caja.Id}/configuracion/rapidos", new SolicitudRapidos(dueno.UsuarioId, antes))).EnsureSuccessStatusCode();
        }
    }

    [Fact]
    public async Task El_margen_y_lo_que_cubre_el_sugerido_se_cambian_en_inventario()
    {
        await using var a = await AbrirAsync("dueno", "3333");
        await GrupoAsync(a.Pantalla, "Inventario");
        var margenes = (await a.Http.GetFromJsonAsync<MargenesDto>($"api/sucursales/{a.Caja.SucursalId}/margenes"))!;
        var departamento = margenes.Departamentos[0];
        var sugerido = (await a.Http.GetFromJsonAsync<ParametrosSugeridoDto>($"api/sucursales/{a.Caja.SucursalId}/sugerido/parametros"))!;
        try
        {
            await a.Pantalla.EscribirAsync("aj-margen", "35");
            await a.Pantalla.SalirAsync("aj-margen");
            await a.Pantalla.EscribirAsync($"aj-margen-{departamento.DepartamentoId}", "40");
            await a.Pantalla.SalirAsync($"aj-margen-{departamento.DepartamentoId}");
            await a.Pantalla.EscribirAsync("aj-cobertura", "21");
            await a.Pantalla.SalirAsync("aj-cobertura");

            var ahora = (await a.Http.GetFromJsonAsync<MargenesDto>($"api/sucursales/{a.Caja.SucursalId}/margenes"))!;
            Assert.Equal(3500, ahora.MargenMinimoCentesimas);
            Assert.Equal(4000, ahora.Departamentos.Single(d => d.DepartamentoId == departamento.DepartamentoId).MargenMinimoCentesimas);
            Assert.Equal(21, (await a.Http.GetFromJsonAsync<ParametrosSugeridoDto>($"api/sucursales/{a.Caja.SucursalId}/sugerido/parametros"))!.CoberturaDias);
            Assert.Empty(a.Pantalla.Errores);
        }
        finally
        {
            (await a.Http.PutAsJsonAsync($"api/sucursales/{a.Caja.SucursalId}/margenes", new SolicitudMargenes(a.Sesion.UsuarioId, margenes))).EnsureSuccessStatusCode();
            (await a.Http.PutAsJsonAsync($"api/sucursales/{a.Caja.SucursalId}/sugerido/parametros", new SolicitudParametrosSugerido(a.Sesion.UsuarioId, sugerido)))
                .EnsureSuccessStatusCode();
        }
    }

    [Fact]
    public async Task Teclas_y_acerca_de_solo_dicen()
    {
        await using var a = await AbrirAsync("dueno", "3333");

        await GrupoAsync(a.Pantalla, "Teclas");
        var teclas = await a.Pantalla.TextoAsync();
        Assert.Contains("Por ahora no se cambian.", teclas);
        Assert.Contains("\nF12\nCobrar", teclas);
        Assert.Contains("\nCtrl+U\nCambiar de usuario", teclas);
        await GrupoAsync(a.Pantalla, "Acerca de");
        Assert.Contains("Papelería POS", await a.Pantalla.TextoAsync());
        Assert.DoesNotContain("Actualización", await a.Pantalla.TextoAsync());
    }

    [Fact]
    public async Task Con_flechas_se_recorren_los_grupos()
    {
        await using var a = await AbrirAsync("dueno", "3333");

        await a.Pantalla.DispararAsync(await a.Pantalla.PorIdAsync("cfg-tienda"), "onkeydown", new Microsoft.AspNetCore.Components.Web.KeyboardEventArgs { Key = "ArrowDown" });
        Assert.Equal(("on", "0"), ((await a.Pantalla.PorIdAsync("cfg-venta")).Clase, (await a.Pantalla.PorIdAsync("cfg-venta")).Atributos["tabindex"]?.ToString()));
        Assert.Equal("-1", (await a.Pantalla.PorIdAsync("cfg-tienda")).Atributos["tabindex"]?.ToString());
        await a.Pantalla.DispararAsync(await a.Pantalla.PorIdAsync("cfg-venta"), "onkeydown", new Microsoft.AspNetCore.Components.Web.KeyboardEventArgs { Key = "End" });
        Assert.Equal("on", (await a.Pantalla.PorIdAsync("cfg-acerca")).Clase);
    }

    /// <summary>Deja la configuración de la tienda como estaba: las pruebas de esta clase comparten el servicio.</summary>
    private static async Task RegresarAsync(Abierta a, ConfiguracionSucursalDto antes)
    {
        var dueno = (await (await a.Http.PostAsJsonAsync("api/sesion", new SolicitudEntrada("dueno", "3333"))).Content.ReadFromJsonAsync<SesionDto>())!;
        (await a.Http.PutAsJsonAsync($"api/cajas/{a.Caja.Id}/configuracion", new SolicitudConfiguracionSucursal(dueno.UsuarioId, antes))).EnsureSuccessStatusCode();
    }
}
