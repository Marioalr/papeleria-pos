using PapeleriaPos.Interfaz;
using PapeleriaPos.Nucleo;

namespace PapeleriaPos.Pruebas.Interfaz;

// Tercera revisión de «Servicios» (servicios/curp, da5e4e3; 2026-10-31), traída al repositorio tal cual, con su arreglo. Cada prueba dice
// lo que debería pasar; en da5e4e3 fallaban (lo que citan es de ese commit).
public sealed class RevisionServicios3Tests : IDisposable
{
    private readonly string _carpeta = Path.Combine(Path.GetTempPath(), $"rev3-tramites-{Guid.NewGuid():N}");
    private readonly RelojFijo _reloj = new(new DateTimeOffset(2026, 10, 31, 17, 0, 0, TimeSpan.Zero));

    public void Dispose()
    {
        if (Directory.Exists(_carpeta))
            Directory.Delete(_carpeta, recursive: true);
    }

    /// <summary>
    /// La ventana de la caja lee lo que mandaron a imprimir los trámites al abrir, en su constructor y sin try (MainWindow.xaml.cs:244,
    /// <c>contingencia.TramitesImpresos()</c>). <see cref="Contingencia.TramitesImpresos"/> lo deserializa sin cuidarse (Contingencia.cs:565-566,
    /// AlmacenLocal.cs:573-577): un valor que no es la lista (otra versión que lo guarde de otra forma, o un archivo dañado) lanza, App.OnStartup lo
    /// atrapa y la caja dice «La caja no pudo abrir» y se cierra (App.xaml.cs:44-49), y así cada vez que se abra, hasta que alguien borre esa
    /// clave de la base local a mano.
    /// </summary>
    [Fact]
    public void Lo_que_se_recuerda_de_lo_impreso_si_no_se_puede_leer_no_impide_abrir_la_caja()
    {
        Directory.CreateDirectory(_carpeta);
        var ruta = Path.Combine(_carpeta, "caja-local.db");
        _ = new AlmacenLocal(ruta);
        using (var sqlite = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={ruta};Pooling=False"))
        {
            sqlite.Open();
            using var comando = sqlite.CreateCommand();
            comando.CommandText = "INSERT INTO Ajustes (Clave, Valor) VALUES ('tramites-impresos', $valor) "
                + "ON CONFLICT(Clave) DO UPDATE SET Valor = excluded.Valor";
            // Lo que dejaría otra versión que lo guarde con otra forma (un objeto donde se espera la lista).
            comando.Parameters.AddWithValue("$valor", "{\"version\":2,\"enviados\":[]}");
            comando.ExecuteNonQuery();
        }
        var contingencia = new Contingencia(new AlmacenLocal(ruta), new HttpClient(), new ConexionCaja { Servicio = new Uri("http://localhost/") });
        try
        {
            var error = Record.Exception(() => contingencia.TramitesImpresos());
            Assert.True(error is null,
                $"Leer lo que se recuerda de lo impreso lanzó {error?.GetType().Name}: la ventana de la caja lo lee al abrir, sin try, y la caja no abre.");
        }
        finally
        {
            contingencia.Cerrar();
        }
    }

    /// <summary>
    /// El límite de 10 s para abrir otro trámite (PantallaVenta.razor:1509-1514, PanelDeTramitesDeWindows.cs:93-98) solo suelta el panel. El
    /// trámite de antes sigue esperando su cierre: <see cref="TramiteEnCurso.TerminarAsync"/> espera <c>CerrarElPanelAsync</c> antes de borrar su
    /// carpeta (TramiteEnCurso.cs:771-774). Si ClearBrowsingDataAsync no regresa (lo que el límite supone), el PDF del cliente se queda en
    /// %LocalAppData%\PapeleriaPos\tramites hasta que se vuelva a abrir la caja, y su línea del registro nunca se escribe.
    /// </summary>
    [Fact]
    public async Task Si_el_panel_no_termina_de_cerrarse_el_pdf_del_cliente_igual_se_borra()
    {
        var panel = new PanelDeTramitesFalso();
        var archivos = new ArchivosDeTramites(Path.Combine(_carpeta, "tramites"));
        var tramite = new TramiteEnCurso(Tramites.Curp, "T-0042", panel, archivos, () => Task.FromResult<string?>(null), null,
            new RegistroDiario(Path.Combine(_carpeta, "registros"), "caja", reloj: _reloj), _reloj);
        await tramite.AbrirAsync();
        panel.Atrapa!.Cargo();
        await panel.DescargarAsync();
        Assert.True(File.Exists(Path.Combine(_carpeta, "tramites", "T-0042", "T-0042.pdf")));

        // El WebView2 no termina de limpiar su perfil.
        panel.CierreLento = new TaskCompletionSource();
        var terminando = tramite.TerminarAsync();
        var carpeta = Path.Combine(_carpeta, "tramites", "T-0042");
        var limite = DateTime.UtcNow + TramiteEnCurso.EsperaDelCierre + TimeSpan.FromSeconds(2);
        while (Directory.Exists(carpeta) && DateTime.UtcNow < limite)
            await Task.Delay(50);

        Assert.False(Directory.Exists(carpeta),
            $"El panel no terminó de cerrarse y, {TramiteEnCurso.EsperaDelCierre.TotalSeconds:0} s después, el PDF del cliente sigue en la carpeta del "
            + "trámite (se borra hasta volver a abrir la caja).");
        panel.CierreLento.SetResult();
        await terminando;
    }

    /// <summary>
    /// <see cref="ImpresorasParaTramites.ImprimeAqui"/> busca las palabras de lo que no imprime («PDF», «XPS», «FAX»…) también en el nombre del
    /// driver (Tramites.cs, ImprimeAqui: <c>$"{cola.Nombre} {cola.Driver}"</c>), y eso vale hasta para la cola del agente. Una impresora de
    /// verdad con driver XPS (Konica Minolta «…SeriesXPS», Kyocera «… XPS») por TCP/IP no se ofrece, ni siquiera siendo la que mide el agente:
    /// el trámite queda sin ninguna impresora.
    /// </summary>
    [Fact]
    public void La_cola_del_agente_con_un_driver_xps_se_ofrece()
    {
        var colas = new[] { new ColaDeLaPc("Oficina", "IP_192.168.1.50", "KONICA MINOLTA C364SeriesXPS", Tipo: TipoDePuerto.TcpIp) };

        var ofrecidas = ImpresorasParaTramites.Elegir(colas, "Oficina");

        Assert.True(ofrecidas.Count == 1 && ofrecidas[0].LaMideElAgente,
            "La cola del agente (TCP/IP, una impresora de verdad) no se ofrece porque su driver se llama «…XPS»: el trámite no tiene impresora.");
    }
}
