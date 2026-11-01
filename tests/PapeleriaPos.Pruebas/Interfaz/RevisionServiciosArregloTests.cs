using PapeleriaPos.Interfaz;
using PapeleriaPos.Nucleo;

namespace PapeleriaPos.Pruebas.Interfaz;

// Segunda revisión de «Servicios» (servicios/curp, ad0a76c: lo que se arregló de la primera; 2026-10-31), traída al repositorio tal cual,
// con su arreglo. Cada prueba dice lo que debería pasar; en ad0a76c fallaban (lo que citan es de ese commit).

/// <summary>El plan B de Edge y las impresoras que se ofrecen, sin la pantalla, con el panel de Windows de mentiras.</summary>
public sealed class RevisionServiciosArregloTests : IDisposable
{
    private readonly string _carpeta = Path.Combine(Path.GetTempPath(), $"rev2-tramites-{Guid.NewGuid():N}");
    private readonly RelojFijo _reloj = new(new DateTimeOffset(2026, 10, 31, 17, 0, 0, TimeSpan.Zero));
    private readonly PanelDeTramitesFalso _panel = new();
    private int _cobros;

    private TramiteEnCurso Tramite() =>
        new(Tramites.Curp, "T-0042", _panel, new ArchivosDeTramites(Path.Combine(_carpeta, "tramites")), () =>
        {
            _cobros++;
            return Task.FromResult<string?>(null);
        }, null, new RegistroDiario(Path.Combine(_carpeta, "registros"), "caja", reloj: _reloj), _reloj);

    public void Dispose()
    {
        if (Directory.Exists(_carpeta))
            Directory.Delete(_carpeta, recursive: true);
    }

    /// <summary>
    /// El plan B borra de inmediato lo que se elija, en cuanto abre como PDF (TramiteEnCurso.cs:560-582, ElegirPdfDescargadoAsync:
    /// <c>BorrarDescargado</c> antes de que el cajero vea el documento), con File.Delete (sin la Papelera) y de cualquier carpeta o memoria
    /// (ArchivosDeTramites.cs:45-56; el diálogo, PanelDeTramitesDeWindows.cs:365-381, solo empieza en Descargas). En una papelería, Descargas
    /// tiene los archivos de otros clientes (WhatsApp Web): si se elige uno por error, el cajero lo ve en el panel, dice que no es y cierra con
    /// Esc… y ese archivo del otro cliente ya no está en ningún lado (su copia en la carpeta del trámite también se borra al terminar).
    /// </summary>
    [Fact]
    public async Task Un_pdf_equivocado_elegido_en_Edge_no_se_pierde_si_se_cierra_sin_entregarlo()
    {
        var tramite = Tramite();
        await tramite.AbrirAsync();
        _panel.Atrapa!.NoCargo(FallaDeLaPagina.NoContesta);
        tramite.AbrirEnElNavegador();
        var descargas = Path.Combine(_carpeta, "Descargas");
        Directory.CreateDirectory(descargas);
        // El archivo de otro cliente, que todavía no se imprime (llegó por WhatsApp Web): se elige por error. Abre como PDF.
        var deOtroCliente = Path.Combine(descargas, "Tarea de historia.pdf");
        File.WriteAllBytes(deOtroCliente, PanelDeTramitesFalso.Pdf);
        _panel.Descargado = deOtroCliente;

        await tramite.ElegirPdfDescargadoAsync();
        // En el panel se ve que no es la CURP: no se imprime ni se comparte, se cierra con Esc.
        await tramite.TerminarAsync();

        Assert.Equal(0, _cobros);
        Assert.True(File.Exists(deOtroCliente),
            "Se eligió por error el PDF de otro cliente, no se entregó nada y el trámite se cerró: ese archivo se borró de Descargas (sin la "
            + "Papelera) y su copia se fue con la carpeta del trámite.");
    }

    /// <summary>
    /// <see cref="ImpresorasParaTramites.EsImpresoraDeEstaPc"/> quita las conexiones («\\PRINCIPAL\…» como nombre) y las virtuales, pero no una cola
    /// de esta PC cuyo puerto es la compartida de otra PC (un «Puerto local» llamado «\\PRINCIPAL\EPSON WF-6590 Series»: así estaba la de la
    /// caja 2 antes de pasarla al puerto TCP/IP, docs/pruebas-en-tienda.md:497-498). Lo que se imprime ahí sale por la caja principal: Control
    /// Print lo mide y su tarjeta sale «por cobrar» en F2 de la caja 2 (ControlPrintEnElServicio.cs:368-372), aunque la CURP ya se cobró.
    /// </summary>
    [Fact]
    public void Una_cola_de_esta_pc_que_imprime_en_la_compartida_de_otra_pc_no_se_ofrece()
    {
        var colas = new[]
        {
            new ColaDeLaPc("EPSON WF-6590 Series", "IP_192.168.1.197", "EPSON WF-6590 Series"),
            new ColaDeLaPc("EPSON WF-6590 (de la principal)", @"\\PRINCIPAL\EPSON WF-6590 Series", "EPSON WF-6590 Series"),
        };

        var ofrecidas = ImpresorasParaTramites.Elegir(colas, "EPSON WF-6590 Series");

        Assert.True(ofrecidas.All(i => i.Nombre != "EPSON WF-6590 (de la principal)"),
            $"Se ofrece una cola cuyo puerto es la compartida de la caja principal: {string.Join(", ", ofrecidas.Select(i => i.Nombre))}.");
    }
}
