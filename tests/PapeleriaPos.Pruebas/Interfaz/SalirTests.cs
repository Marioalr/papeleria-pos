using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.JSInterop;
using PapeleriaPos.Impresion;
using PapeleriaPos.Interfaz;
using PapeleriaPos.Interfaz.Componentes;
using PapeleriaPos.Nucleo;
using PapeleriaPos.Nucleo.Contratos;
using PapeleriaPos.Nucleo.Usuarios;
using PapeleriaPos.Nucleo.Ventas;
using PapeleriaPos.Pruebas.Servicio;

namespace PapeleriaPos.Pruebas.Interfaz;

/// <summary>
/// «Salir» (Ctrl+S, docs/propuestas/corte-y-salir.md, 2026-11-20), sin navegador, contra el servicio: sustituye a la pestaña
/// «Corte» y al menú del nombre. La cajera cierra su propio turno a ciegas con su PIN (sin ver lo esperado, ni con un PIN
/// equivocado), Esc regresa sin cerrar, Ctrl+U cambia de usuario sin cerrar, y «Listo» va a «¿Quién atiende?»; el dueño ve lo
/// esperado, la venta de hoy y, al cerrar el último turno, «Cerrar el día» ahí mismo. En el piloto no hay campo. Sin turno,
/// «Reportes, sin abrir turno». Cada prueba con su propio servicio.
/// </summary>
public sealed class SalirTests
{
    private sealed record Caja(ClienteCaja Cliente, CajaDto Datos, ServiceProvider Servicios, SesionDto Cajero, SesionDto Encargado, SesionDto Dueno);

    private static async Task<Caja> PrepararAsync(ServicioDePrueba servicio)
    {
        var conexion = new ConexionCaja { Servicio = new Uri("http://localhost/") };
        var cliente = new ClienteCaja(new HttpClient(servicio.Fabrica.Server.CreateHandler()), conexion);
        var caja = (await cliente.CajasAsync()).Single();
        conexion.CajaId = caja.Id;
        var servicios = new ServiceCollection()
            .AddSingleton(conexion)
            .AddSingleton(cliente)
            .AddSingleton<IImpresora>(new SinImpresora())
            .AddSingleton<IJSRuntime>(new PaginaQueContesta())
            .AddScoped<ModuloCaja>()
            .BuildServiceProvider();
        return new Caja(cliente, caja, servicios, await cliente.EntrarAsync("cajero", "1111"), await cliente.EntrarAsync("encargado", "2222"),
            await cliente.EntrarAsync("dueno", "3333"));
    }

    /// <summary>Cuántas veces la venta pidió ir a «¿Quién atiende?» (AlSalir) y a abrir otro turno (AlCerrarTurno).</summary>
    private sealed class Salidas
    {
        public int AQuienAtiende;
        public int AOtroTurno;
    }

    private static async Task<(PantallaSinNavegador Pantalla, PantallaVenta Venta)> VentaAsync(Caja c, SesionDto quien, TurnoDto turno, Salidas salidas)
    {
        var p = new PantallaSinNavegador(c.Servicios);
        var venta = await p.AbrirAsync<PantallaVenta>(new()
        {
            [nameof(PantallaVenta.Caja)] = c.Datos,
            [nameof(PantallaVenta.Sesion)] = quien,
            [nameof(PantallaVenta.Turno)] = turno,
            [nameof(PantallaVenta.AlSalir)] = new EventCallback(null, (Action)(() => salidas.AQuienAtiende++)),
            [nameof(PantallaVenta.AlCerrarTurno)] = new EventCallback(null, (Action)(() => salidas.AOtroTurno++)),
        });
        await p.EsperarAsync(t => t.Contains("Copia B/N"), "los rápidos");
        return (p, venta);
    }

    private static Task TeclaAsync(PantallaSinNavegador p, PantallaVenta venta, string tecla) => p.Dispatcher.InvokeAsync(() => venta.Tecla(tecla, null));

    private static async Task VenderAsync(Caja c, TurnoDto turno, SesionDto quien, long monto) =>
        await c.Cliente.CobrarAsync(new SolicitudCobro(c.Datos.Id, turno.Id, quien.UsuarioId, [new LineaSolicitada(null, null, 1000, "Recuerdo", monto)],
            [new CobroSolicitado(FormaPago.Efectivo, monto)]));

    /// <summary>Lo contado, Enter, el PIN y Enter, como en la caja.</summary>
    private static async Task ContarYCerrarAsync(PantallaSinNavegador p, string contado, string pin)
    {
        await p.EsperarElementoAsync("corte-contado");
        await p.EscribirAsync("corte-contado", contado);
        await p.TeclaAsync("corte-contado", "Enter");
        await p.EsperarElementoAsync("corte-pin");
        await p.EscribirAsync("corte-pin", pin);
        await p.TeclaAsync("corte-pin", "Enter");
    }

    [Fact]
    public async Task La_cajera_cierra_su_turno_a_ciegas_con_su_pin_y_listo_va_a_quien_atiende()
    {
        await using var servicio = await ServicioDePrueba.CrearAsync();
        var c = await PrepararAsync(servicio);
        var turno = await c.Cliente.AbrirTurnoAsync(c.Datos.Id, c.Cajero.UsuarioId, 50000);
        await VenderAsync(c, turno, c.Cajero, 12000);
        var salidas = new Salidas();
        var (p, venta) = await VentaAsync(c, c.Cajero, turno, salidas);
        await using var _ = p;

        // Arriba ya no hay pestaña «Corte» ni menú del nombre: un solo botón, «Salir de» quien atiende, con la hora del turno y su
        // tecla (2026-11-21: el nombre y «Salir» hacían lo mismo).
        var elementos = (await p.LeerAsync()).Elementos;
        Assert.DoesNotContain(elementos, e => e.Etiqueta == "button" && PantallaSinNavegador.Limpio(e.Texto) == "Corte");
        Assert.Matches(@"^C Salir de Cajero de prueba turno desde \d\d:\d\d Ctrl\+S$", PantallaSinNavegador.Limpio((await p.PorIdAsync("boton-salir")).Texto));
        Assert.Single(elementos, e => e.Etiqueta == "button" && e.Clase?.Split(' ').Contains("vn-usuario") == true);

        // Ctrl+S abre «Salir»: el turno, el campo, «Tu turno» (corte X, sin el efectivo) y lo demás abajo. Nada de lo esperado.
        await TeclaAsync(p, venta, "Ctrl+S");
        await p.EsperarElementoAsync("corte-contado");
        var texto = await p.TextoAsync();
        Assert.StartsWith("Turno de Cajero de prueba · Caja 1 · desde las ", PantallaSinNavegador.Limpio((await p.PorIdAsync("salir-turno")).Texto));
        Assert.Contains("Cerrar tu turno", texto);
        Assert.Contains("No se muestra cuánto debería haber hasta cerrar.", texto);
        Assert.StartsWith("Tu turno · corte X", PantallaSinNavegador.Limpio((await p.PorIdAsync("salir-tu-turno")).Texto));
        Assert.DoesNotContain("Debería haber", texto);
        Assert.DoesNotContain("$620", texto);
        Assert.DoesNotContain("Que entre a esta caja", texto);
        Assert.DoesNotContain((await p.LeerAsync()).Elementos, e => e.Id is "corte-esperado" or "salir-venta-de-hoy");
        Assert.NotNull(await p.PorIdAsync("salir-cambiar"));
        Assert.NotNull(await p.PorIdAsync("salir-volver"));
        // En el navegador no hay «Minimizar» ni «Cerrar la caja».
        Assert.DoesNotContain((await p.LeerAsync()).Elementos, e => e.Id is "salir-minimizar" or "salir-cerrar-caja");

        // Esc regresa a la venta sin cerrar nada.
        await TeclaAsync(p, venta, "Escape");
        Assert.DoesNotContain((await p.LeerAsync()).Elementos, e => e.Id == "corte-contado");
        Assert.Equal(turno.Id, (await c.Cliente.TurnoAsync(c.Datos.Id))?.Id);
        Assert.Equal((0, 0), (salidas.AQuienAtiende, salidas.AOtroTurno));

        // El botón también abre «Salir». Lo contado, Enter: pide el PIN; uno equivocado no cierra ni enseña lo esperado.
        await p.OprimirAsync(await p.PorIdAsync("boton-salir"));
        await ContarYCerrarAsync(p, "600", "0000");
        await p.EsperarElementoAsync("corte-error");
        Assert.Equal(FichasEntrada.PinIncorrecto(Usuario.IntentosPermitidos - 1), PantallaSinNavegador.Limpio((await p.PorIdAsync("corte-error")).Texto));
        Assert.DoesNotContain("Debería haber", await p.TextoAsync());
        Assert.DoesNotContain("Esperado", await p.TextoAsync());
        Assert.Equal(turno.Id, (await c.Cliente.TurnoAsync(c.Datos.Id))?.Id);
        Assert.Equal("", (await p.PorIdAsync("corte-pin")).Valor ?? "");

        // Con su PIN se cierra: lo contado, lo esperado y el faltante a su nombre.
        await p.EscribirAsync("corte-pin", "1111");
        await p.TeclaAsync("corte-pin", "Enter");
        await p.EsperarAsync(t => t.Contains("Turno de Cajero de prueba cerrado"), "el turno cerrado");
        texto = PantallaSinNavegador.Limpio(await p.TextoAsync());
        Assert.Contains("Contado $600.00", texto);
        Assert.Contains("Esperado $620.00", texto);
        Assert.Contains("Faltante −$20.00 a nombre de Cajero de prueba", texto);
        Assert.Null(await c.Cliente.TurnoAsync(c.Datos.Id));
        // Ya cerrado, «Cambiar de usuario» y «Volver a la venta» se van: «Listo» (Enter) y «Abrir otro turno».
        Assert.DoesNotContain((await p.LeerAsync()).Elementos, e => e.Id is "salir-cambiar" or "salir-volver" or "salir-cerrar-dia");
        Assert.NotNull(await p.PorIdAsync("salir-otro-turno"));

        // «Listo» va a «¿Quién atiende?» (no a abrir otro turno suyo).
        await p.OprimirAsync(await p.PorIdAsync("salir-listo"));
        Assert.Equal((1, 0), (salidas.AQuienAtiende, salidas.AOtroTurno));
        Assert.Empty(p.Errores);
    }

    [Fact]
    public async Task Ctrl_u_en_salir_cambia_de_usuario_sin_cerrar_y_esc_despues_del_corte_es_listo()
    {
        await using var servicio = await ServicioDePrueba.CrearAsync();
        var c = await PrepararAsync(servicio);
        var turno = await c.Cliente.AbrirTurnoAsync(c.Datos.Id, c.Cajero.UsuarioId, 50000);
        var salidas = new Salidas();
        var (p, venta) = await VentaAsync(c, c.Cajero, turno, salidas);
        await using var _ = p;

        // Desde una página (Clientes) también abre: Ctrl+S es de todas.
        await p.OprimirAsync(await p.ElementoAsync(e => e.Etiqueta == "button" && PantallaSinNavegador.Limpio(e.Texto) == "Clientes", "la pestaña Clientes"));
        await TeclaAsync(p, venta, "Ctrl+S");
        await p.EsperarElementoAsync("corte-contado");
        // Ctrl+U: a «¿Quién atiende?», con el turno abierto.
        await TeclaAsync(p, venta, "Ctrl+U");
        Assert.Equal((1, 0), (salidas.AQuienAtiende, salidas.AOtroTurno));
        Assert.Equal(turno.Id, (await c.Cliente.TurnoAsync(c.Datos.Id))?.Id);

        // «Cambiar de usuario» en «Salir», lo mismo.
        await TeclaAsync(p, venta, "Ctrl+S");
        await p.OprimirAsync(await p.EsperarElementoAsync("salir-cambiar"));
        Assert.Equal((2, 0), (salidas.AQuienAtiende, salidas.AOtroTurno));

        // Cerrado el turno, Esc es «Listo»; «Abrir otro turno» va a abrir otro.
        await TeclaAsync(p, venta, "Ctrl+S");
        await p.EsperarElementoAsync("corte-contado");
        await ContarYCerrarAsync(p, "500", "1111");
        await p.EsperarAsync(t => t.Contains("Turno de Cajero de prueba cerrado"), "el turno cerrado");
        Assert.Contains("Cuadra", await p.TextoAsync());
        await TeclaAsync(p, venta, "Escape");
        Assert.Equal((3, 0), (salidas.AQuienAtiende, salidas.AOtroTurno));
        Assert.Empty(p.Errores);
    }

    [Fact]
    public async Task El_dueno_ve_lo_esperado_la_venta_de_hoy_y_cierra_el_dia_al_cerrar_el_ultimo_turno()
    {
        await using var servicio = await ServicioDePrueba.CrearAsync();
        var c = await PrepararAsync(servicio);
        // Antes, la cajera cerró el suyo con un faltante; ahora el dueño tiene el último turno abierto.
        var deLaCajera = await c.Cliente.AbrirTurnoAsync(c.Datos.Id, c.Cajero.UsuarioId, 50000);
        await VenderAsync(c, deLaCajera, c.Cajero, 10000);
        await c.Cliente.CorteZAsync(deLaCajera.Id, new SolicitudCorteZ(c.Cajero.UsuarioId, 58000, Pin: "1111"));
        var turno = await c.Cliente.AbrirTurnoAsync(c.Datos.Id, c.Dueno.UsuarioId, 58000);
        await VenderAsync(c, turno, c.Dueno, 2000);
        var salidas = new Salidas();
        var (p, venta) = await VentaAsync(c, c.Dueno, turno, salidas);
        await using var _ = p;

        await TeclaAsync(p, venta, "Ctrl+S");
        var esperado = await p.EsperarElementoAsync("corte-esperado");
        Assert.Contains("Debería haber $600.00", PantallaSinNavegador.Limpio(esperado.Texto));
        await p.EscribirAsync("corte-contado", "590");
        Assert.Contains("Faltan $10.00.", PantallaSinNavegador.Limpio((await p.PorIdAsync("corte-esperado")).Texto));
        // A la derecha, la venta de hoy de todas las cajas, con los turnos: el de la cajera con su faltante y este, abierto.
        await p.EsperarAsync(t => t.Contains("Venta de hoy · todas las cajas"), "la venta de hoy");
        var hoy = PantallaSinNavegador.Limpio((await p.PorIdAsync("salir-venta-de-hoy")).Texto);
        Assert.Contains("Vendido hoy · 2 tickets $120.00", hoy);
        Assert.Contains("Cajero de prueba", hoy);
        Assert.Contains("−$20.00", hoy);
        Assert.Contains("este turno", hoy);
        Assert.Contains("Utilidad bruta", hoy);
        Assert.NotNull(await p.PorIdAsync("salir-a-reportes"));
        Assert.DoesNotContain((await p.LeerAsync()).Elementos, e => e.Id == "salir-tu-turno");

        // Cierra el suyo (con su PIN): era el último abierto, y «Cerrar el día» sale ahí mismo.
        await ContarYCerrarAsync(p, "600", "3333");
        await p.EsperarElementoAsync("salir-cerrar-dia");
        Assert.Contains("Era el último turno abierto", await p.TextoAsync());
        Assert.Equal("Listo, sin cerrar el día", PantallaSinNavegador.Limpio((await p.PorIdAsync("salir-listo")).Texto));
        var cerrar = await p.PorIdAsync("salir-dia-boton");
        Assert.False(cerrar.Desactivado);
        await p.OprimirAsync(cerrar);
        await p.EsperarAsync(t => t.Contains("Se cerró a las"), "el día cerrado");
        Assert.Empty(p.Errores);
    }

    [Fact]
    public async Task Otra_caja_con_su_turno_abierto_no_deja_cerrar_el_dia_en_salir()
    {
        await using var servicio = await ServicioDePrueba.CrearAsync();
        var c = await PrepararAsync(servicio);
        var turno = await c.Cliente.AbrirTurnoAsync(c.Datos.Id, c.Dueno.UsuarioId, 50000);
        // Un turno que sigue abierto en esta caja después (de mentiras: el de otra caja también cuenta): el del cajero en otra caja.
        var otraCaja = await OtraCajaAsync(servicio, c);
        await c.Cliente.AbrirTurnoAsync(otraCaja, c.Cajero.UsuarioId, 30000);
        var salidas = new Salidas();
        var (p, venta) = await VentaAsync(c, c.Dueno, turno, salidas);
        await using var _ = p;

        await TeclaAsync(p, venta, "Ctrl+S");
        await p.EsperarElementoAsync("corte-contado");
        await ContarYCerrarAsync(p, "500", "3333");
        await p.EsperarAsync(t => t.Contains("Turno de Dueño de prueba cerrado"), "el turno cerrado");
        // No era el último: sin «Cerrar el día»; «Listo» como siempre.
        Assert.DoesNotContain((await p.LeerAsync()).Elementos, e => e.Id == "salir-cerrar-dia");
        Assert.Equal("Listo Enter", PantallaSinNavegador.Limpio((await p.PorIdAsync("salir-listo")).Texto));
        Assert.Empty(p.Errores);
    }

    /// <summary>Una segunda caja dada de alta en la sucursal (directo en la base de la prueba).</summary>
    private static async Task<Guid> OtraCajaAsync(ServicioDePrueba servicio, Caja c)
    {
        await using var db = servicio.Base();
        var otra = new PapeleriaPos.Nucleo.Turnos.Caja { SucursalId = c.Datos.SucursalId, Nombre = "Caja 2", Serie = "B" };
        db.Cajas.Add(otra);
        await db.SaveChangesAsync();
        return otra.Id;
    }

    [Fact]
    public async Task En_el_piloto_salir_no_tiene_campo_y_dice_que_el_corte_es_en_eleventa()
    {
        await using var servicio = await ServicioDePrueba.CrearAsync();
        var c = await PrepararAsync(servicio);
        var turno = await c.Cliente.AbrirTurnoAsync(c.Datos.Id, c.Cajero.UsuarioId, 50000);
        var p = new PantallaSinNavegador(c.Servicios);
        await using var _ = p;
        // Como la pone la venta en el piloto: con el valor en cascada.
        await p.AbrirAsync<CorteEnElPilotoDePrueba>(new()
        {
            [nameof(CorteEnElPilotoDePrueba.Turno)] = turno,
            [nameof(CorteEnElPilotoDePrueba.Sesion)] = c.Cajero,
        });
        await p.EsperarElementoAsync("salir-piloto");
        Assert.Equal(PilotoConEleventa.CorteEnEleventa, PantallaSinNavegador.Limpio((await p.PorIdAsync("salir-piloto")).Texto));
        Assert.DoesNotContain((await p.LeerAsync()).Elementos, e => e.Id is "corte-contado" or "corte-pin");
        Assert.Empty(p.Errores);
    }

    [Fact]
    public async Task Sin_turno_reportes_sin_abrir_turno_para_quien_los_ve()
    {
        await using var servicio = await ServicioDePrueba.CrearAsync();
        var c = await PrepararAsync(servicio);
        // Lo ofrece quien ve Reportes: el dueño, y el encargado (hace el corte del turno de otro: ve el historial de cortes). El cajero no.
        Assert.True(new PermisosDePagina(c.Dueno, null).VeReportes);
        Assert.True(new PermisosDePagina(c.Encargado, null).VeReportes);
        Assert.False(new PermisosDePagina(c.Cajero, null).VeReportes);
        await using (var p = new PantallaSinNavegador(c.Servicios))
        {
            await p.AbrirAsync<AbrirTurno>(new()
            {
                [nameof(AbrirTurno.Caja)] = c.Datos,
                [nameof(AbrirTurno.Sesion)] = c.Dueno,
                [nameof(AbrirTurno.AlReportes)] = new EventCallback(null, (Action)(() => { })),
            });
            Assert.Equal("Reportes, sin abrir turno", PantallaSinNavegador.Limpio((await p.EsperarElementoAsync("reportes-sin-turno")).Texto));
        }

        // En «sin vender», Reportes abre en «Venta de hoy», con el corte del día, sin turno.
        var admin = new PantallaSinNavegador(c.Servicios);
        await using var __ = admin;
        await admin.AbrirAsync<Administracion>(new()
        {
            [nameof(Administracion.Caja)] = c.Datos,
            [nameof(Administracion.Sesion)] = c.Dueno,
            [nameof(Administracion.PaginaInicial)] = Pagina.Reportes,
        });
        await admin.EsperarElementoAsync("corte-dia");
        Assert.StartsWith("Venta de hoy · ", PantallaSinNavegador.Limpio((await admin.PorIdAsync("rep-titulo")).Texto));
        Assert.False((await admin.PorIdAsync("cerrar-dia-boton")).Desactivado);
        Assert.Empty(admin.Errores);
    }
}

/// <summary>«Salir» como lo pone la venta en el piloto con eleventa: con el valor en cascada.</summary>
public sealed class CorteEnElPilotoDePrueba : ComponentBase
{
    [Parameter] public TurnoDto Turno { get; set; } = null!;
    [Parameter] public SesionDto Sesion { get; set; } = null!;

    protected override void BuildRenderTree(Microsoft.AspNetCore.Components.Rendering.RenderTreeBuilder builder)
    {
        builder.OpenComponent<CascadingValue<bool>>(0);
        builder.AddAttribute(1, "Name", PilotoConEleventa.Cascada);
        builder.AddAttribute(2, "Value", true);
        builder.AddAttribute(3, "ChildContent", (RenderFragment)(adentro =>
        {
            adentro.OpenComponent<CorteCaja>(0);
            adentro.AddAttribute(1, nameof(CorteCaja.Turno), Turno);
            adentro.AddAttribute(2, nameof(CorteCaja.Sesion), Sesion);
            adentro.CloseComponent();
        }));
        builder.CloseComponent();
    }
}
