using PapeleriaPos.Interfaz;
using PapeleriaPos.Nucleo;
using PapeleriaPos.Pruebas.Servicio;

namespace PapeleriaPos.Pruebas.Interfaz;

// Revisión independiente de «Servicios» (servicios/curp, 7c8e143; 2026-10-31), traída al repositorio con su arreglo (y
// revision-servicios.mjs en CajaJs/). Cada prueba dice lo que debería pasar; en 7c8e143 fallaban. Un solo cambio a lo que esperaban: en
// Edge el aviso ya no dice «Cobrar CURP» sino «Elegir el PDF descargado» (el plan B del estudio, que pidió la misma revisión).

/// <summary>caja.js: el Enter del lector de teclado con el cursor en un botón de los paneles nuevos (CajaJs/revision-servicios.mjs).</summary>
public sealed class RevisionServiciosCajaJsTests
{
    [FactConNode]
    public void El_lector_de_teclado_no_oprime_los_botones_de_los_paneles_del_tramite_y_de_servicios()
    {
        var raiz = RaizDelRepositorio();
        var copia = Path.Combine(Path.GetTempPath(), $"caja-{Guid.NewGuid():N}.mjs");
        File.Copy(Path.Combine(raiz, "src", "PapeleriaPos.Interfaz", "wwwroot", "caja.js"), copia);
        try
        {
            var (codigo, salida) = Node.Correr(Path.Combine(raiz, "tests", "PapeleriaPos.Pruebas", "Interfaz", "CajaJs", "revision-servicios.mjs"), copia);

            Assert.True(codigo == 0, salida);
            Assert.Contains("todo ok", salida);
        }
        finally
        {
            File.Delete(copia);
        }
    }

    private static string RaizDelRepositorio()
    {
        for (var carpeta = new DirectoryInfo(AppContext.BaseDirectory); carpeta is not null; carpeta = carpeta.Parent)
            if (File.Exists(Path.Combine(carpeta.FullName, "src", "PapeleriaPos.Interfaz", "wwwroot", "caja.js")))
                return carpeta.FullName;
        throw new DirectoryNotFoundException("No encontré la raíz del repositorio.");
    }
}

/// <summary>El trámite sin la pantalla (<see cref="TramiteEnCurso"/>), con el panel de Windows de mentiras.</summary>
public sealed class RevisionServiciosTramiteTests : IDisposable
{
    private readonly string _carpeta = Path.Combine(Path.GetTempPath(), $"rev-tramites-{Guid.NewGuid():N}");
    private readonly RelojFijo _reloj = new(new DateTimeOffset(2026, 10, 31, 17, 0, 0, TimeSpan.Zero));
    private readonly PanelDeTramitesFalso _panel = new();
    private string? _porQueNoSeCobra;

    private TramiteEnCurso Tramite() =>
        new(Tramites.Curp, "T-0042", _panel, new ArchivosDeTramites(_carpeta),
            () => Task.FromResult(_porQueNoSeCobra), null, new RegistroDiario(Path.Combine(_carpeta, "registros"), "caja", reloj: _reloj), _reloj);

    public void Dispose()
    {
        if (Directory.Exists(_carpeta))
            Directory.Delete(_carpeta, recursive: true);
    }

    /// <summary>
    /// La venta deja el panel del trámite con F12 (y cualquier tecla) en cuanto <see cref="TramiteEnCurso.Entregado"/> (PantallaVenta.razor:904).
    /// Si la CURP no entró al ticket al imprimir, no debería contar como entregado: F12 lo terminaría sin cobrarla, y la única forma de
    /// volver a intentarlo es imprimir o compartir otra vez (una copia de más, que va a F2).
    /// </summary>
    [Fact]
    public async Task Impreso_sin_que_la_curp_entrara_al_ticket_no_se_puede_dejar_con_F12()
    {
        _porQueNoSeCobra = "no se pudo agregar.";
        var tramite = Tramite();
        await tramite.AbrirAsync();
        _panel.Atrapa!.Cargo();
        await _panel.DescargarAsync();

        await tramite.ImprimirAsync("EPSON WF-6590 Series", 1);

        Assert.True(tramite.Impreso);
        Assert.False(tramite.Cobrado);
        Assert.False(tramite.Entregado, "Se imprimió la CURP del cliente y no entró al ticket, pero cuenta como entregada: F12 cierra el trámite sin cobrarla.");
    }

    /// <summary>
    /// <see cref="IAtrapaDocumentos.Cargo"/>: la sangría dice que Limpiar() va dentro del if, pero va fuera (TramiteEnCurso.cs:225-228). Cada
    /// vez que la página del panel termina de cargar algo, se borra lo que se le dijo al cajero, aunque el estado no cambie.
    /// </summary>
    [Fact]
    public async Task Lo_que_se_le_dice_al_cajero_no_se_borra_cuando_la_pagina_termina_de_cargar_otra_cosa()
    {
        var tramite = Tramite();
        await tramite.AbrirAsync();
        _panel.Atrapa!.Cargo();

        // «Abrir en el navegador»: el aviso dice cómo sigue (el plan B: «Elegir el PDF descargado», que la caja borra de Descargas).
        tramite.AbrirEnElNavegador();
        var enEdge = tramite.Aviso;
        Assert.Contains("Elegir el PDF descargado", enEdge);
        // La página del panel sigue abierta y termina de cargar algo (otra búsqueda, su propio cambio de página).
        _panel.Atrapa!.Cargo();
        Assert.Equal(EstadoTramite.EnElNavegador, tramite.Estado);
        Assert.Equal(enEdge, tramite.Aviso);
    }
}

/// <summary>«/» en la venta de verdad: lo que ya se hacía con un filtro corto.</summary>
public sealed class RevisionServiciosVentaTests(ServicioDePrueba servicio) : IClassFixture<ServicioDePrueba>, IDisposable
{
    private readonly string _baseLocal = Path.Combine(Path.GetTempPath(), $"rev-servicios-venta-{Guid.NewGuid():N}.db");

    public void Dispose() => ArchivosDePrueba.BorrarBase(_baseLocal);

    /// <summary>
    /// Las dos acciones nuevas van terceras y cuartas en «/» (PantallaVenta.razor:1312-1315) y se buscan por todo su nombre
    /// («Servicios: trámites en su página oficial», «CURP: el trámite en gob.mx/curp»): un filtro corto que ya elegía otra acción ahora elige
    /// la CURP. «/el» y Enter era «Elegir cliente del ticket»; ahora abre el trámite (folio nuevo y gob.mx encima del ticket).
    /// </summary>
    [Theory]
    [InlineData("/el", "Elegir cliente del ticket")]
    [InlineData("/tra", "Mostrador y temporada")]
    public async Task Un_filtro_corto_sigue_eligiendo_la_accion_de_antes(string filtro, string antes)
    {
        await using var v = await VentaConCopia.AbrirAsync(servicio, _baseLocal, fondoAparte: true);

        await v.Pantalla.Dispatcher.InvokeAsync(() => v.Venta.Escribiendo(filtro));

        var primera = PantallaSinNavegador.Limpio((await v.Pantalla.PorIdAsync("acc-0")).Texto);
        Assert.True(primera.StartsWith(antes, StringComparison.Ordinal), $"«{filtro}» y Enter hacía «{antes}»; ahora hace «{primera}».");
    }
}
