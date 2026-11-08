using Microsoft.EntityFrameworkCore;
using PapeleriaPos.Interfaz;
using PapeleriaPos.Nucleo.Catalogo;
using PapeleriaPos.Pruebas.Servicio;

namespace PapeleriaPos.Pruebas.Interfaz;

/// <summary>
/// Cuarta revisión de «Servicios» (servicios/curp, 48133a8; 2026-11-01), traída al repositorio tal cual, con su arreglo. Lo que debería
/// pasar; en 48133a8 fallaba (lo que cita es de ese commit).
/// </summary>
public sealed class RevisionServicios4Tests(ServicioDePrueba servicio) : IClassFixture<ServicioDePrueba>, IDisposable
{
    private readonly string _baseLocal = Path.Combine(Path.GetTempPath(), $"rev4-venta-{Guid.NewGuid():N}.db");
    private readonly string _carpeta = Path.Combine(Path.GetTempPath(), $"rev4-tramites-{Guid.NewGuid():N}");

    public void Dispose()
    {
        ArchivosDePrueba.BorrarBase(_baseLocal);
        if (Directory.Exists(_carpeta))
            Directory.Delete(_carpeta, recursive: true);
    }

    /// <summary>
    /// Una impresión a tiempo se toma como tardía si el trámite se cierra mientras la hoja va a la cola, y entonces no se cobra
    /// (TramiteEnCurso.cs:510-516). Esc es a propósito, pero las pestañas «Corte» y «Configuración» de arriba abren su ventana con
    /// <c>Abrir</c> directo (PantallaVenta.razor:38 y :45), sin <c>PorQueNoSeDeja</c>: <c>DejarVentana</c> termina el trámite (:5610-5611).
    /// Un clic en «Corte» mientras sale la CURP la deja impresa y sin cobrar, y el aviso de arriba queda detrás de esa ventana.
    /// </summary>
    [Fact]
    public async Task Un_clic_en_Corte_mientras_se_imprime_la_curp_no_la_deja_sin_cobrar()
    {
        await using (var db = servicio.Base())
        {
            if (!await db.CodigosBarras.AnyAsync(c => c.Codigo == "CURP"))
            {
                var variante = await db.Variantes.FirstAsync(x => x.Producto!.Nombre == "Trámite" && x.Descripcion == "CURP");
                var presentacion = await db.Presentaciones.FirstAsync(p => p.VarianteId == variante.Id && p.EsBase);
                db.CodigosBarras.Add(new CodigoBarras { Codigo = "CURP", PresentacionId = presentacion.Id, Origen = OrigenCodigo.Interno });
                await db.SaveChangesAsync();
            }
        }
        await servicio.CatalogoAlDiaAsync();
        var panel = new PanelDeTramitesFalso();
        await using var v = await VentaConCopia.AbrirAsync(servicio, _baseLocal, fondoAparte: true,
            registro: new RegistroDiario(Path.Combine(_carpeta, "registros"), "caja"), tramites: panel,
            archivos: new ArchivosDeTramites(Path.Combine(_carpeta, "tramites")));
        await v.Pantalla.Dispatcher.InvokeAsync(() => v.Venta.Escaneado("/curp", null, null, false));
        await v.Pantalla.Dispatcher.InvokeAsync(() => panel.Atrapa!.Cargo());
        await v.Pantalla.Dispatcher.InvokeAsync(() => panel.DescargarAsync());
        await v.Pantalla.EsperarAsync(t => t.Contains("Documento listo"), "el documento");
        panel.ImpresionLenta = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        // «Imprimir» (la hoja va a la cola) y, mientras, un clic en la pestaña «Corte».
        var imprimiendo = v.Pantalla.OprimirAsync(await v.Pantalla.PorIdAsync("tramite-imprimir"));
        await v.Pantalla.OprimirAsync(await v.Pantalla.ElementoAsync(
            e => e.Etiqueta == "button" && PantallaSinNavegador.Limpio(e.Texto) == "Corte", "la pestaña «Corte»"));
        panel.ImpresionLenta.SetResult();
        await imprimiendo;
        Assert.Single(panel.Impresos);

        // De vuelta a la venta: la CURP que salió de la impresora tiene que estar en el ticket.
        await v.Pantalla.Dispatcher.InvokeAsync(() => v.Venta.Tecla("Escape", null));
        var ticket = await VentaConCopia.RenglonesAsync(v.Pantalla);
        Assert.True(ticket.Any(r => r.Contains("CURP")),
            "La CURP se imprimió a tiempo (nadie oprimió Esc), pero un clic en «Corte» cerró el trámite mientras salía: quedó impresa y sin cobrar.");
    }
}
