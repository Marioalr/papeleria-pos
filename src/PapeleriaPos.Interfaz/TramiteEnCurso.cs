using PapeleriaPos.Impresiones;

namespace PapeleriaPos.Interfaz;

public enum EstadoTramite
{
    /// <summary>Abriendo la página oficial.</summary>
    Abriendo,

    /// <summary>La página oficial, esperando el documento: el cajero escribe y da clic.</summary>
    EnLaPagina,

    /// <summary>La página no cargó por falta de internet: no hay nada que cobrar.</summary>
    SinInternet,

    /// <summary>La página no contestó (el sitio caído) o el panel no abrió: no hay nada que cobrar.</summary>
    NoContesta,

    /// <summary>Llega el documento (la descarga, o «Ya salió») y se revisa que abra.</summary>
    Atrapando,

    /// <summary>El documento, listo para imprimir o compartir.</summary>
    Listo,

    /// <summary>Lo que dio la página no es un PDF que abra: no se cobra.</summary>
    NoAbre,

    /// <summary>Se abrió en Edge, por si algo fallaba en el panel: se cobra con «Cobrar».</summary>
    EnElNavegador,

    /// <summary>La caja del navegador (sin panel): la página se abre en otra pestaña y se cobra con «Cobrar».</summary>
    EnOtraPestana,

    Terminado,
}

/// <summary>
/// Un trámite en la caja, de que se abre a que se termina (docs/tramites.md, «La CURP, paso a paso»; 2026-10-31). Lo de Windows lo
/// hace el panel (<see cref="IPanelDeTramites"/>); aquí se decide todo lo demás, y así se prueba en Linux:
/// <list type="bullet">
/// <item><b>El folio</b> («T-0042») va en el nombre del archivo y en el del trabajo de impresión, nunca el nombre ni la CURP del cliente.</item>
/// <item><b>Atrapar:</b> lo que la página descarga se guarda en la carpeta del trámite con el nombre neutro («T-0042.pdf»), solo si es un
/// PDF, y se revisa que abra antes de ofrecer «Imprimir».</item>
/// <item><b>Imprimir:</b> la primera copia es «T-0042 · CURP» (incluida: el módulo de impresión no la cobra otra vez); las demás, aparte,
/// «T-0042 · CURP · copias extra», que salen en F2 al precio de la impresión (decisión 4 del estudio).</item>
/// <item><b>Cobrar una vez:</b> al entregar el documento (imprimirlo o compartirlo) por primera vez, su producto entra al ticket; sin
/// documento (la página no cargó, el PDF no abrió), nada.</item>
/// <item><b>Terminar:</b> se cierra el panel (se borra lo de su perfil), se borra la carpeta y se anota una línea sin datos personales:
/// «Trámite T-0042 · CURP · impreso · 1 min 12 s».</item>
/// </list>
/// Nada de lo que escribe el cajero en la página oficial pasa por aquí: ni la CURP, ni el nombre, ni el nombre original del archivo.
/// </summary>
public sealed class TramiteEnCurso : IAtrapaDocumentos
{
    /// <summary>Lo más que se imprime de una vez.</summary>
    public const int CopiasMaximas = 20;

    private readonly IPanelDeTramites? _panel;
    private readonly ArchivosDeTramites _archivos;
    private readonly Func<Task<string?>> _cobrar;
    private readonly Func<Task<bool>>? _soloCobrar;
    private readonly RegistroDiario? _registro;
    private readonly TimeProvider _reloj;
    private readonly DateTimeOffset _inicio;
    private readonly PoliticaDeNavegacion _politica;
    private bool _abierto;
    private bool _abriendo;
    private FallaDeLaPagina? _falla;
    private bool _noAbrio;
    private int _copias;
    private bool _enUsb;
    private bool _paraWhatsApp;

    /// <param name="folio">«T-0042» (<see cref="FolioDe"/>).</param>
    /// <param name="panel">El de la ventana de Windows; sin él (la caja del navegador), la página se abre en otra pestaña.</param>
    /// <param name="cobrar">Agrega el producto del trámite al ticket; devuelve por qué no, o vacío si entró.</param>
    /// <param name="soloCobrar">«Solo cobrar» (el rápido de siempre, cuando el cliente trae su archivo); sin él, no se ofrece.</param>
    public TramiteEnCurso(Tramite tramite, string folio, IPanelDeTramites? panel, ArchivosDeTramites archivos, Func<Task<string?>> cobrar,
        Func<Task<bool>>? soloCobrar = null, RegistroDiario? registro = null, TimeProvider? reloj = null)
    {
        Tramite = tramite;
        Folio = folio;
        _panel = panel;
        _archivos = archivos;
        _cobrar = cobrar;
        _soloCobrar = soloCobrar;
        _registro = registro;
        _reloj = reloj ?? TimeProvider.System;
        _inicio = _reloj.GetUtcNow();
        _politica = tramite.Politica();
    }

    /// <summary>«T-0042»: el folio de un trámite por su número.</summary>
    public static string FolioDe(int numero) => $"T-{Math.Max(0, numero):0000}";

    public Tramite Tramite { get; }

    public string Folio { get; }

    public EstadoTramite Estado { get; private set; } = EstadoTramite.Abriendo;

    /// <summary>La página va en el panel de la caja (la ventana de Windows), no en otra pestaña.</summary>
    public bool ConPanel => _panel is not null;

    public bool Terminado => Estado == EstadoTramite.Terminado;

    /// <summary>Su producto ya entró al ticket (una vez por trámite).</summary>
    public bool Cobrado { get; private set; }

    /// <summary>Cuántas copias se imprimieron: la primera va incluida, las demás se cobran en F2.</summary>
    public int Copias => _copias;

    public bool Impreso => _copias > 0;

    public bool Compartido => _enUsb || _paraWhatsApp;

    /// <summary>Se cobró sin que el documento pasara por la caja (en otra pestaña o en Edge).</summary>
    public bool CobradoSinDocumento { get; private set; }

    /// <summary>«Solo cobrar»: el cliente traía su archivo.</summary>
    public bool SoloCobrado { get; private set; }

    /// <summary>Ya se entregó (o se cobró): con otra tecla (F12) se termina sin perder nada.</summary>
    public bool Entregado => Impreso || Compartido || CobradoSinDocumento || SoloCobrado;

    /// <summary>Se ofrece «Solo cobrar»: hay rápido y todavía no se cobró nada.</summary>
    public bool PuedeSoloCobrar => _soloCobrar is not null && !Cobrado && !SoloCobrado && !Terminado;

    /// <summary>Las páginas del documento atrapado.</summary>
    public int Paginas { get; private set; }

    /// <summary>Lo que se le dice al cajero en el panel.</summary>
    public string? Aviso { get; private set; }

    public bool AvisoEsError { get; private set; }

    /// <summary>Imprimiendo, guardando o cobrando: los botones esperan (un doble clic no imprime dos veces).</summary>
    public bool Ocupado { get; private set; }

    /// <summary>El documento atrapado, con su nombre neutro.</summary>
    public string Documento => _archivos.DocumentoDe(Folio);

    /// <summary>El trabajo de la copia incluida: «T-0042 · CURP».</summary>
    public string NombreDelTrabajo => TrabajoDeTramite.Nombre(Folio, Tramite.Nombre);

    /// <summary>El de las copias de más: «T-0042 · CURP · copias extra».</summary>
    public string NombreDeCopiasExtra => TrabajoDeTramite.NombreDeCopiasExtra(Folio, Tramite.Nombre);

    /// <summary>Cambió algo que se ve: el panel se vuelve a pintar.</summary>
    public event Action? Cambio;

    /// <summary>Abre la página oficial en el panel; sin panel, queda para abrirla en otra pestaña.</summary>
    public async Task AbrirAsync()
    {
        if (_panel is null)
        {
            Estado = EstadoTramite.EnOtraPestana;
            Cambiar();
            return;
        }
        Estado = EstadoTramite.Abriendo;
        Cambiar();
        _abriendo = true;
        try
        {
            await _panel.AbrirAsync(Tramite.Direccion, _politica, this);
            _abierto = true;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // Todavía no se escribió nada en la página: lo que diga el error no trae datos del cliente.
            if (!Terminado)
            {
                _registro?.AnotarError($"Trámite {Folio} · {Tramite.Nombre}: el panel no abrió", ex);
                (Estado, _falla) = (EstadoTramite.NoContesta, FallaDeLaPagina.NoContesta);
                Avisar("La página oficial no abrió aquí: «Reintentar», o ábrela en el navegador.", error: true);
            }
        }
        finally
        {
            _abriendo = false;
        }
        // Se terminó mientras abría (Esc enseguida): el panel no se queda.
        if (Terminado && _abierto)
            await CerrarElPanelAsync();
        Cambiar();
    }

    /// <summary>El lugar del ticket en la página de la caja: ahí va la página oficial.</summary>
    public void Colocar(LugarEnPantalla lugar)
    {
        if (!Terminado)
            _panel?.Colocar(lugar);
    }

    /// <summary>«Reintentar»: la página oficial otra vez.</summary>
    public async Task ReintentarAsync()
    {
        if (_panel is null || Ocupado || Estado is not (EstadoTramite.SinInternet or EstadoTramite.NoContesta or EstadoTramite.NoAbre or EstadoTramite.EnLaPagina))
            return;
        if (!_abierto)
        {
            await AbrirAsync();
            return;
        }
        Estado = EstadoTramite.Abriendo;
        Limpiar();
        Cambiar();
        try
        {
            await _panel.IrAAsync(Tramite.Direccion);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            (Estado, _falla) = (EstadoTramite.NoContesta, FallaDeLaPagina.NoContesta);
            Avisar("La página oficial no abrió: inténtalo en un momento, o ábrela en el navegador.", error: true);
        }
        Cambiar();
    }

    // ── Lo que dice el panel (IAtrapaDocumentos) ──

    void IAtrapaDocumentos.Cargo()
    {
        if (Estado is EstadoTramite.Abriendo or EstadoTramite.SinInternet or EstadoTramite.NoContesta)
            (Estado, _falla) = (EstadoTramite.EnLaPagina, null);
            Limpiar();
        Cambiar();
    }

    void IAtrapaDocumentos.NoCargo(FallaDeLaPagina falla)
    {
        // Con el documento ya aquí, que la página falle después no cambia nada.
        if (Estado is EstadoTramite.Atrapando or EstadoTramite.Listo or EstadoTramite.Terminado or EstadoTramite.EnElNavegador)
            return;
        _falla = falla;
        Estado = falla == FallaDeLaPagina.SinInternet ? EstadoTramite.SinInternet : EstadoTramite.NoContesta;
        Avisar(falla == FallaDeLaPagina.SinInternet
            ? "Sin internet: los trámites no funcionan y no hay nada que cobrar. La venta sigue: lo demás se cobra como siempre."
            : "La página oficial no contesta (puede estar caída): no hay nada que cobrar. «Reintentar» en un momento, o ábrela en el navegador.",
            error: true);
        Cambiar();
    }

    void IAtrapaDocumentos.Bloqueado()
    {
        if (Terminado)
            return;
        Avisar($"Ese enlace va fuera de {Tramite.DireccionALaVista}: aquí no se abre.", error: false);
        Cambiar();
    }

    string? IAtrapaDocumentos.DondeGuardar(string? extension, string? tipo)
    {
        if (Terminado)
            return null;
        if (!string.Equals(extension, ".pdf", StringComparison.OrdinalIgnoreCase) && !string.Equals(tipo, "application/pdf", StringComparison.OrdinalIgnoreCase))
        {
            Avisar("La página quiso descargar algo que no es un PDF: no se guardó.", error: true);
            Cambiar();
            return null;
        }
        var ruta = _archivos.NuevoDocumento(Folio);
        _politica.PermitirArchivo(ruta);
        (Estado, Paginas) = (EstadoTramite.Atrapando, 0);
        Limpiar();
        Cambiar();
        return ruta;
    }

    async Task IAtrapaDocumentos.DescargadoAsync(string archivo)
    {
        // Solo el que se le dio: otro archivo no se toca.
        if (!string.Equals(Path.GetFullPath(archivo), Documento, StringComparison.OrdinalIgnoreCase))
            return;
        if (Terminado)
        {
            // Llegó cuando ya se había terminado: no se queda.
            _archivos.Borrar(Folio);
            return;
        }
        await RevisarAsync();
    }

    void IAtrapaDocumentos.NoSeDescargo()
    {
        if (Estado != EstadoTramite.Atrapando)
            return;
        Estado = EstadoTramite.EnLaPagina;
        Avisar("La descarga no terminó: da «Descargar pdf» otra vez.", error: true);
        Cambiar();
    }

    /// <summary>Que sea un PDF y que abra; entonces se enseña. Si no, se borra y no se cobra.</summary>
    private async Task RevisarAsync()
    {
        Estado = EstadoTramite.Atrapando;
        Limpiar();
        Cambiar();
        int? paginas = null;
        if (ArchivosDeTramites.EsPdf(Documento))
        {
            try
            {
                paginas = await _panel!.PaginasAsync(Documento);
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                paginas = null;
            }
        }
        if (Terminado)
            return;
        if (paginas is not > 0)
        {
            _noAbrio = true;
            _archivos.Borrar(Folio);
            Estado = EstadoTramite.NoAbre;
            Avisar("Lo que dio la página no es un PDF que abra (pasó en agosto de 2026): no se cobra. Da «Descargar pdf» otra vez, o ábrela en el navegador.",
                error: true);
            Cambiar();
            return;
        }
        (Paginas, Estado) = (paginas.Value, EstadoTramite.Listo);
        try
        {
            await _panel!.MostrarDocumentoAsync(Documento);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // Sin verlo aquí también se imprime y se comparte.
            Avisar("El documento no se pudo enseñar aquí, pero se puede imprimir y compartir.", error: false);
        }
        Cambiar();
    }

    // ── Lo que hace el cajero ──

    /// <summary>«Ya salió»: la página enseña el documento sin descargarlo (una página para imprimir); se guarda como PDF y sigue igual.</summary>
    public async Task YaSalioAsync()
    {
        if (_panel is null || Ocupado || Estado is not (EstadoTramite.EnLaPagina or EstadoTramite.NoAbre))
            return;
        var destino = _archivos.NuevoDocumento(Folio);
        _politica.PermitirArchivo(destino);
        (Estado, Ocupado) = (EstadoTramite.Atrapando, true);
        Limpiar();
        Cambiar();
        bool guardada;
        try
        {
            guardada = await _panel.GuardarPaginaComoPdfAsync(destino);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            guardada = false;
        }
        Ocupado = false;
        if (Terminado)
            return;
        if (!guardada)
        {
            Estado = EstadoTramite.EnLaPagina;
            Avisar("No se pudo guardar la página como PDF: da «Descargar pdf» en la página, o ábrela en el navegador.", error: true);
            Cambiar();
            return;
        }
        await RevisarAsync();
    }

    /// <summary>Las impresoras de la PC; la primera es la que vigila el agente de impresión.</summary>
    public IReadOnlyList<string> Impresoras() => _panel?.Impresoras() ?? [];

    /// <summary>
    /// «Imprimir»: la primera copia del trámite como «T-0042 · CURP» (incluida) y, si se piden más, aparte como «T-0042 · CURP · copias
    /// extra» (en F2, al precio de la impresión). Con la primera que sale, el trámite entra al ticket.
    /// </summary>
    public async Task ImprimirAsync(string? impresora, int copias)
    {
        if (_panel is null || Estado != EstadoTramite.Listo || Ocupado)
            return;
        if (string.IsNullOrWhiteSpace(impresora))
        {
            Avisar("Elige en qué impresora sale.", error: true);
            Cambiar();
            return;
        }
        copias = Math.Clamp(copias, 1, CopiasMaximas);
        Ocupado = true;
        Limpiar();
        Cambiar();
        var extra = 0;
        try
        {
            var incluida = _copias == 0 ? 1 : 0;
            if (incluida == 1)
            {
                await _panel.ImprimirAsync(Documento, impresora, NombreDelTrabajo, 1);
                _copias++;
            }
            // Con la primera que sale entra al ticket; si no entró antes (no había producto), se vuelve a intentar.
            await CobrarUnaVezAsync();
            if (copias - incluida is var deMas and > 0)
            {
                await _panel.ImprimirAsync(Documento, impresora, NombreDeCopiasExtra, deMas);
                (_copias, extra) = (_copias + deMas, deMas);
            }
            if (Cobrado)
                Avisar(extra == 0 ? $"Impreso en «{impresora}»."
                    : $"Impreso en «{impresora}». {(extra == 1 ? "La copia de más sale" : $"Las {extra} copias de más salen")} en F2 al precio de la impresión: agrégala{(extra == 1 ? "" : "s")} al ticket.",
                    error: false);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            Avisar($"No salió en «{impresora}»: {ex.Message}", error: true);
        }
        finally
        {
            Ocupado = false;
            Cambiar();
        }
    }

    /// <summary>«Guardar en USB»: con el nombre para el cliente (CURP.pdf). La primera vez que se entrega, el trámite entra al ticket.</summary>
    public async Task GuardarEnUsbAsync()
    {
        if (_panel is null || Estado != EstadoTramite.Listo || Ocupado)
            return;
        Ocupado = true;
        Limpiar();
        Cambiar();
        try
        {
            if (await _panel.GuardarEnUsbAsync(Documento, Tramite.NombreDelDocumento))
            {
                _enUsb = true;
                await CobrarUnaVezAsync();
                if (Cobrado)
                    Avisar($"Guardado en la USB como «{Tramite.NombreDelDocumento}».", error: false);
            }
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            Avisar($"No se guardó: {ex.Message}", error: true);
        }
        finally
        {
            Ocupado = false;
            Cambiar();
        }
    }

    /// <summary>
    /// «Copiar para WhatsApp» (a mano, como hoy): el archivo con el nombre para el cliente, en el portapapeles, para pegarlo en su chat.
    /// La primera vez que se entrega, el trámite entra al ticket.
    /// </summary>
    public async Task CopiarParaWhatsAppAsync()
    {
        if (_panel is null || Estado != EstadoTramite.Listo || Ocupado)
            return;
        Ocupado = true;
        Limpiar();
        Cambiar();
        try
        {
            _panel.CopiarArchivo(_archivos.ParaCompartir(Folio, Tramite.NombreDelDocumento));
            _paraWhatsApp = true;
            await CobrarUnaVezAsync();
            if (Cobrado)
                Avisar($"Copiado: pégalo con Ctrl+V en el chat del cliente en WhatsApp. Después bórralo del chat de la tienda («Eliminar para mí»); "
                    + "al terminar el trámite se quita de aquí.", error: false);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            Avisar($"No se copió: {ex.Message}", error: true);
        }
        finally
        {
            Ocupado = false;
            Cambiar();
        }
    }

    /// <summary>«Abrir en el navegador», por si algo falla aquí: Edge, InPrivate. Desde ahí, el documento no pasa por la caja: «Cobrar».</summary>
    public void AbrirEnElNavegador()
    {
        if (_panel is null || Terminado)
            return;
        _panel.AbrirEnElNavegador(Tramite.Direccion);
        if (Estado is not (EstadoTramite.Atrapando or EstadoTramite.Listo))
            Estado = EstadoTramite.EnElNavegador;
        Avisar($"Se abrió en Edge (InPrivate). Al salir el documento ahí, imprímelo desde Edge, cobra aquí con «Cobrar {Tramite.Nombre}» y borra el PDF de "
            + "Descargas.", error: false);
        Cambiar();
    }

    /// <summary>«Cobrar»: el trámite se hizo en otra pestaña (la caja del navegador) o en Edge. Una vez.</summary>
    public async Task CobrarSinDocumentoAsync()
    {
        if (Ocupado || Cobrado || Estado is not (EstadoTramite.EnOtraPestana or EstadoTramite.EnElNavegador))
            return;
        Ocupado = true;
        Limpiar();
        Cambiar();
        try
        {
            await CobrarUnaVezAsync();
            CobradoSinDocumento = Cobrado;
            if (Cobrado)
                Avisar($"La {Tramite.Nombre} ya está en el ticket. Borra el PDF de Descargas: el navegador no deja que la caja lo borre.", error: false);
        }
        finally
        {
            Ocupado = false;
            Cambiar();
        }
    }

    /// <summary>«Solo cobrar»: el cliente trae su archivo; se vende lo del rápido, como antes. Devuelve si entró al ticket.</summary>
    public async Task<bool> SoloCobrarAsync()
    {
        if (_soloCobrar is null || Ocupado || !PuedeSoloCobrar)
            return false;
        Ocupado = true;
        Cambiar();
        try
        {
            SoloCobrado = await _soloCobrar();
            return SoloCobrado;
        }
        finally
        {
            Ocupado = false;
            Cambiar();
        }
    }

    /// <summary>Su producto al ticket, solo la primera vez. Si no entra, lo dice y se puede volver a intentar entregándolo otra vez.</summary>
    private async Task CobrarUnaVezAsync()
    {
        if (Cobrado)
            return;
        string? porQueNo;
        try
        {
            porQueNo = await _cobrar();
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            porQueNo = ex.Message;
        }
        Cobrado = porQueNo is null;
        if (porQueNo is not null)
            Avisar($"No se agregó la {Tramite.Nombre} al ticket: {porQueNo}", error: true);
    }

    /// <summary>
    /// Termina el trámite (Esc, F12 después de entregarlo, cambiar de usuario, cerrar la caja): se cierra el panel (con lo de su
    /// perfil), se borra la carpeta del trámite y se anota una línea sin datos del cliente. Lo que no se pudo borrar se borra al abrir
    /// la caja.
    /// </summary>
    public async Task TerminarAsync()
    {
        if (Terminado)
            return;
        var antes = Estado;
        Estado = EstadoTramite.Terminado;
        Cambiar();
        if (_abierto || _abriendo)
            await CerrarElPanelAsync();
        var borrado = _archivos.Borrar(Folio);
        _registro?.Anotar(Resumen(antes, borrado));
    }

    private async Task CerrarElPanelAsync()
    {
        if (_panel is null)
            return;
        try
        {
            await _panel.CerrarAsync();
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // Sin el mensaje: podría traer una dirección de la página.
            _registro?.AnotarSinRepetir("Trámites: el panel no se cerró bien", ex.GetType().Name);
        }
    }

    /// <summary>«Trámite T-0042 · CURP · impreso · 1 min 12 s»: lo que pasó, sin nada del cliente.</summary>
    private string Resumen(EstadoTramite antes, bool borrado)
    {
        var partes = new List<string>();
        if (Impreso)
            partes.Add(_copias == 1 ? "impreso" : $"impreso ({_copias} copias)");
        if (_enUsb)
            partes.Add("guardado en USB");
        if (_paraWhatsApp)
            partes.Add("copiado para WhatsApp");
        if (CobradoSinDocumento)
            partes.Add(antes == EstadoTramite.EnOtraPestana ? "cobrado (en otra pestaña)" : "cobrado (en Edge)");
        if (SoloCobrado)
            partes.Add("solo cobrar");
        if (partes.Count == 0)
            partes.Add(_noAbrio ? "sin documento (el PDF no abrió)"
                : _falla == FallaDeLaPagina.SinInternet ? "sin documento (sin internet)"
                : _falla == FallaDeLaPagina.NoContesta ? "sin documento (la página no contestó)"
                : "sin documento");
        var que = partes.Count == 1 ? partes[0] : $"{string.Join(", ", partes[..^1])} y {partes[^1]}";
        if ((Impreso || Compartido) && !Cobrado)
            que += ", sin cobrar (no entró al ticket)";
        var linea = $"Trámite {Folio} · {Tramite.Nombre} · {que} · {Duracion(_reloj.GetUtcNow() - _inicio)}";
        return borrado ? linea : $"{linea} · su archivo se borra al abrir la caja";
    }

    /// <summary>«45 s», «1 min 12 s», «1 h 3 min».</summary>
    public static string Duracion(TimeSpan tiempo)
    {
        if (tiempo < TimeSpan.Zero)
            tiempo = TimeSpan.Zero;
        return tiempo.TotalMinutes < 1 ? $"{(int)tiempo.TotalSeconds} s"
            : tiempo.TotalHours < 1 ? $"{(int)tiempo.TotalMinutes} min {tiempo.Seconds} s"
            : $"{(int)tiempo.TotalHours} h {tiempo.Minutes} min";
    }

    private void Avisar(string texto, bool error) => (Aviso, AvisoEsError) = (texto, error);

    private void Limpiar() => (Aviso, AvisoEsError) = (null, false);

    private void Cambiar() => Cambio?.Invoke();
}
