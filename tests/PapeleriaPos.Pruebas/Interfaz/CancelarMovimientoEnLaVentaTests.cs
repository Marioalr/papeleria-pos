using System.Net.Http.Json;
using PapeleriaPos.Impresion;
using PapeleriaPos.Interfaz;
using PapeleriaPos.Nucleo.Contratos;
using PapeleriaPos.Nucleo.Control;
using PapeleriaPos.Nucleo.Turnos;
using PapeleriaPos.Pruebas.Impresion;
using PapeleriaPos.Pruebas.Servicio;

namespace PapeleriaPos.Pruebas.Interfaz;

/// <summary>
/// F7/F8 enseñan las entradas y salidas del turno y, a quien tiene «Cancelar una entrada o salida», «Cancelar» en cada una
/// (como en eleventa, 2026-10-22): el motivo se escribe debajo, Enter la cancela, el cajón se abre y la cuenta de la caja ya
/// no la cuenta. Quien no lo tiene las ve y se le dice qué hacer; con «Con autorización», se autoriza ahí mismo. En el piloto
/// con eleventa se ven, pero no se cancelan, y la pantalla dice por qué.
/// </summary>
public sealed class CancelarMovimientoEnLaVentaTests(ServicioDePrueba servicio) : IClassFixture<ServicioDePrueba>, IDisposable
{
    private readonly string _baseLocal = Path.Combine(Path.GetTempPath(), $"caja-cancelar-mov-{Guid.NewGuid():N}.db");

    public void Dispose() => ArchivosDePrueba.BorrarBase(_baseLocal);

    /// <summary>Una entrada de quien entró a la venta, en su turno, por la caja (como F7); regresa su Id.</summary>
    private static async Task<Guid> EntradaAsync(VentaConCopia v, long monto, string motivo)
    {
        var id = Guid.NewGuid();
        await v.Cliente.MovimientoAsync(v.Venta.Turno.Id, new SolicitudMovimiento(TipoMovimientoCaja.Entrada, monto, motivo, v.Cajero.UsuarioId, id));
        return id;
    }

    /// <summary>F7 y, ya cargada, el renglón de esa entrada.</summary>
    private static async Task<string> AbrirConAsync(VentaConCopia v, Guid id)
    {
        await v.Pantalla.Dispatcher.InvokeAsync(() => v.Venta.Tecla("F7", null));
        return PantallaSinNavegador.Limpio((await v.Pantalla.EsperarElementoAsync($"movimiento-{id}")).Texto);
    }

    /// <summary>El botón de ese renglón (no el de otro), si lo tiene.</summary>
    private static async Task<ElementoDibujado?> BotonDelMovimientoAsync(VentaConCopia v, Guid id, string boton)
    {
        var elementos = (await v.Pantalla.LeerAsync()).Elementos;
        var desde = elementos.FindIndex(e => e.Id == $"movimiento-{id}");
        Assert.True(desde >= 0, "No está la entrada.");
        return elementos.Skip(desde + 1).TakeWhile(e => e.Id?.StartsWith("movimiento-", StringComparison.Ordinal) != true)
            .FirstOrDefault(e => e.Etiqueta == "button" && PantallaSinNavegador.Limpio(e.Texto) == boton);
    }

    private static async Task CancelarConMotivoAsync(VentaConCopia v, Guid id, string motivo)
    {
        await v.Pantalla.OprimirAsync(await BotonDelMovimientoAsync(v, id, "Cancelar") ?? throw new Xunit.Sdk.XunitException("No tiene «Cancelar»."));
        await v.Pantalla.EscribirAsync("motivo-cancelar-movimiento", motivo);
        await v.Pantalla.TeclaAsync("motivo-cancelar-movimiento", "Enter");
    }

    [Fact]
    public async Task Quien_puede_cancela_una_con_su_motivo_y_se_abre_el_cajon()
    {
        var impresora = new TicketeraDePrueba();
        await using var v = await VentaConCopia.AbrirAsync(servicio, _baseLocal, quien: ("encargado", "2222"), impresora: impresora);
        var turno = v.Venta.Turno.Id;
        var id = await EntradaAsync(v, 20000, "Cambio en monedas");
        var antes = v.Contingencia.Efectivo(turno).EsperadoCentavos;

        Assert.Contains("Entrada · $200.00", await AbrirConAsync(v, id));

        // Sin motivo, no: lo pide ahí mismo.
        await v.Pantalla.OprimirAsync((await BotonDelMovimientoAsync(v, id, "Cancelar"))!);
        await v.Pantalla.TeclaAsync("motivo-cancelar-movimiento", "Enter");
        Assert.Contains("Escribe por qué se cancela.", await v.Pantalla.TextoAsync());

        await v.Pantalla.EscribirAsync("motivo-cancelar-movimiento", "Se registró dos veces");
        await v.Pantalla.TeclaAsync("motivo-cancelar-movimiento", "Enter");

        var renglon = PantallaSinNavegador.Limpio((await v.Pantalla.PorIdAsync($"movimiento-{id}")).Texto);
        Assert.Contains("Cancelada", renglon);
        Assert.Contains("por Encargado de prueba: Se registró dos veces", renglon);
        Assert.Null(await BotonDelMovimientoAsync(v, id, "Cancelar"));
        Assert.Contains("ya no cuenta en el corte", await v.Pantalla.TextoAsync());
        // El cajón, como al registrarla; y la cuenta de esta caja (la del corte sin conexión) ya no la cuenta.
        await TicketeraDePrueba.EsperarAsync(() => impresora.Salieron.Count == 1, "el cajón");
        Assert.Equal(TicketEscPos.AbrirCajon(), impresora.Mandados.Single());
        Assert.Equal(antes - 20000, v.Contingencia.Efectivo(turno).EsperadoCentavos);
        Assert.Empty(v.Pantalla.Errores);
    }

    [Fact]
    public async Task El_cajero_las_ve_y_se_le_dice_que_no_lo_tiene_permitido()
    {
        await using var v = await VentaConCopia.AbrirAsync(servicio, _baseLocal);
        var id = await EntradaAsync(v, 10000, "Cambio");

        await AbrirConAsync(v, id);

        Assert.Null(await BotonDelMovimientoAsync(v, id, "Cancelar"));
        Assert.Contains("Cancelar una entrada o salida no lo tienes permitido: que entre quien puede (Ctrl+U), o haz la contraria (F7 o F8).",
            PantallaSinNavegador.Limpio(await v.Pantalla.TextoAsync()));
        Assert.Empty(v.Pantalla.Errores);
    }

    /// <summary>Si el dueño le pone «Con autorización» al cajero, se pide ahí mismo (quien autoriza, su PIN) y se cancela.</summary>
    [Fact]
    public async Task Con_autorizacion_la_autoriza_el_encargado_ahi_mismo()
    {
        var http = servicio.Fabrica.CreateClient();
        var caja = (await http.GetFromJsonAsync<List<CajaDto>>("api/cajas"))!.First();
        async Task<SesionDto> Entrar(string usuario, string pin) =>
            (await (await http.PostAsJsonAsync("api/sesion", new SolicitudEntrada(usuario, pin))).Content.ReadFromJsonAsync<SesionDto>())!;
        var (dueno, cajero) = (await Entrar("dueno", "3333"), await Entrar("cajero", "1111"));
        async Task PermisoAsync(NivelPermiso nivel) =>
            (await http.PutAsJsonAsync($"api/cajas/{caja.Id}/usuarios-y-permisos/{cajero.UsuarioId}",
                new SolicitudPermisoUsuario(dueno.UsuarioId, AccionControlada.CancelarMovimiento, nivel))).EnsureSuccessStatusCode();
        await PermisoAsync(NivelPermiso.RequiereAutorizacion);
        try
        {
            await using var v = await VentaConCopia.AbrirAsync(servicio, _baseLocal);
            var id = await EntradaAsync(v, 15000, "Cambio");
            await AbrirConAsync(v, id);

            await CancelarConMotivoAsync(v, id, "Era de otra caja");
            await v.Pantalla.EscribirAsync("autoriza-pin", "2222");
            await v.Pantalla.TeclaAsync("autoriza-pin", "Enter");

            var renglon = PantallaSinNavegador.Limpio((await v.Pantalla.EsperarElementoAsync($"movimiento-{id}")).Texto);
            Assert.Contains("por Cajero de prueba: Era de otra caja", renglon);
            await using var db = servicio.Base();
            var bitacora = db.Bitacora.Where(b => b.Accion == AccionControlada.CancelarMovimiento && b.Detalle.EndsWith("Motivo: Era de otra caja")).ToList();
            Assert.Equal(ResultadoBitacora.Autorizado, Assert.Single(bitacora).Resultado);
            Assert.Empty(v.Pantalla.Errores);
        }
        finally
        {
            await PermisoAsync(NivelPermiso.NoPermitido);
        }
    }

    [Fact]
    public async Task En_el_piloto_se_ven_pero_no_se_cancelan_y_dice_por_que()
    {
        await using var piloto = await ServicioDePrueba.CrearAsync(
            ("Eleventa:Escribir", "true"), ("Eleventa:Servidor", "127.0.0.1"), ("Eleventa:Puerto", "1"), ("Eleventa:Base", @"C:\eleventa\PDVDATA.FDB"),
            ("Eleventa:Clave", "masterkey"), ("Eleventa:Caja", "4"), ("Eleventa:Cajero", "7"), ("Eleventa:SegundosEntreEnvios", "3600"),
            ("Eleventa:RespaldoObligatorio", "false"));
        await using var v = await VentaConCopia.AbrirAsync(piloto, _baseLocal, quien: ("encargado", "2222"));
        var id = await EntradaAsync(v, 10000, "Cambio");

        await AbrirConAsync(v, id);

        Assert.Null(await BotonDelMovimientoAsync(v, id, "Cancelar"));
        Assert.Contains(PilotoConEleventa.NoSeCancelaMovimiento, PantallaSinNavegador.Limpio(await v.Pantalla.TextoAsync()));
        Assert.Empty(v.Pantalla.Errores);
    }
}
