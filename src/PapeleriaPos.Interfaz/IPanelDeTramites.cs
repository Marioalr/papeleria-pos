namespace PapeleriaPos.Interfaz;

/// <summary>
/// Lo que solo hace la ventana de Windows para un trámite (docs/tramites.md, «El diseño recomendado»; 2026-10-31), delgado a
/// propósito: la página oficial en un WebView2 con su propio perfil, encima del lugar del ticket; atrapar lo que descarga; enseñar e
/// imprimir el PDF con el nombre del trabajo; guardarlo en una USB o dejarlo en el portapapeles; y al cerrar, borrar lo del perfil.
/// Lo demás (el folio, el nombre neutro, revisar que sea un PDF, cobrar una vez, borrar el archivo, el registro) lo decide
/// <see cref="TramiteEnCurso"/>, que se prueba en Linux con uno de mentiras. La caja del navegador no tiene ninguno: ahí la página se
/// abre en otra pestaña.
/// </summary>
public interface IPanelDeTramites
{
    /// <summary>
    /// Enseña la página oficial en el lugar del ticket (<see cref="Colocar"/>), con un perfil aparte de la caja y de Edge (InPrivate,
    /// sin autollenado ni contraseñas), sin los atajos del navegador (imprimir o guardar la página la dejarían con los datos del
    /// cliente fuera del trámite) y sin permisos (cámara, ubicación, avisos). Solo navega a donde <paramref name="politica"/> deja
    /// (la página, sus marcos y sus ventanas nuevas, que se quedan en el panel). Lo que pasa se lo dice a <paramref name="atrapa"/>.
    /// </summary>
    Task AbrirAsync(Uri direccion, PoliticaDeNavegacion politica, IAtrapaDocumentos atrapa);

    /// <summary>Dónde va la página: el lugar del ticket en la página de la caja, en sus píxeles; vacío (sin ancho) la oculta.</summary>
    void Colocar(LugarEnPantalla lugar);

    /// <summary>Vuelve a la dirección («Reintentar»).</summary>
    Task IrAAsync(Uri direccion);

    /// <summary>«Ya salió»: lo que se ve en la página, como PDF, en <paramref name="destino"/>. Devuelve si se guardó.</summary>
    Task<bool> GuardarPaginaComoPdfAsync(string destino);

    /// <summary>Cuántas páginas tiene el PDF si abre; vacío si no abre (dañado, con contraseña o sin páginas).</summary>
    Task<int?> PaginasAsync(string archivo);

    /// <summary>
    /// Enseña el documento en el panel, con el visor de PDF sin sus botones de guardar ni imprimir (lo hacen los del POS), y regresa
    /// el teclado a la caja: Enter imprime.
    /// </summary>
    Task MostrarDocumentoAsync(string archivo);

    /// <summary>
    /// La cola que vigila el agente de impresión de esta caja (<c>ColaImpresion</c> en caja.json, la WF-6590): la única donde las copias de
    /// más llegan solas a F2. Vacía si la caja no vigila ninguna.
    /// </summary>
    string? ColaDelAgente { get; }

    /// <summary>
    /// Las colas de impresión de esta PC como las dice Windows, sin la de los tickets. Cuáles se ofrecen para un trámite lo decide
    /// <see cref="ImpresorasParaTramites"/>.
    /// </summary>
    IReadOnlyList<ColaDeLaPc> Colas();

    /// <summary>
    /// Manda el PDF a la impresora con ese nombre de trabajo («T-0042 · CURP»: así lo ve la cola de Windows y el agente de impresión,
    /// nunca con el nombre del cliente) y esas copias. De un documento de más de <see cref="TramiteEnCurso.PaginasMaximas"/> páginas,
    /// esas primeras (el trámite lo dice antes).
    /// </summary>
    Task ImprimirAsync(string archivo, string impresora, string nombreDelTrabajo, int copias);

    /// <summary>
    /// «Guardar en USB»: directo en la memoria USB conectada, con <paramref name="nombre"/> (sin pisar lo que ya tenga: «CURP (2).pdf»), sin
    /// diálogo, así nunca queda en una carpeta de la PC. Sin memoria USB no guarda nada y lo dice. Solo escribe en ella; no abre nada de
    /// lo que trae.
    /// </summary>
    Task<GuardadoEnUsb> GuardarEnUsbAsync(string archivo, string nombre);

    /// <summary>«Copiar para WhatsApp»: el archivo en el portapapeles, para pegarlo en el chat; marcado para no quedar en su historial.</summary>
    void CopiarArchivo(string archivo);

    /// <summary>«Abrir en el navegador», por si algo falla aquí: una ventana InPrivate de Edge.</summary>
    void AbrirEnElNavegador(Uri direccion);

    /// <summary>
    /// «Elegir el PDF descargado» (el plan B, con Edge): el «Abrir» de Windows, en Descargas, solo PDF. Devuelve la ruta elegida, o vacío si
    /// se canceló. El trámite lo copia con su nombre neutro; el original solo se borra al entregarlo y si está en
    /// <see cref="CarpetaDeDescargas"/>.
    /// </summary>
    Task<string?> ElegirPdfDescargadoAsync();

    /// <summary>
    /// La carpeta Descargas del usuario de Windows (donde guarda Edge): lo único de donde el trámite borra el PDF elegido, y solo después de
    /// entregarlo (segunda revisión de servicios/curp). Vacía si no se sabe: entonces no se borra nada.
    /// </summary>
    string? CarpetaDeDescargas { get; }

    /// <summary>
    /// Quita la página: borra lo del perfil del panel (cookies, caché, historial, descargas, lo escrito), lo suelta y quita del
    /// portapapeles lo que se copió. El archivo lo borra quien lo pidió.
    /// </summary>
    Task CerrarAsync();
}

/// <summary>Lo que el panel le dice al trámite. Nunca el nombre original del archivo ni una dirección: pueden traer datos del cliente.</summary>
public interface IAtrapaDocumentos
{
    /// <summary>La página cargó.</summary>
    void Cargo();

    /// <summary>La página no cargó: sin internet, o el sitio no contestó.</summary>
    void NoCargo(FallaDeLaPagina falla);

    /// <summary>Se quiso ir a un sitio que no es del trámite: no se abrió.</summary>
    void Bloqueado();

    /// <summary>
    /// La página empezó a descargar algo: dónde guardarlo (con su nombre neutro), o vacío para no dejarlo (lo que no es un PDF). Solo se
    /// dice su extensión y su tipo.
    /// </summary>
    string? DondeGuardar(string? extension, string? tipo);

    /// <summary>La descarga terminó en <paramref name="archivo"/> (el que se dio).</summary>
    Task DescargadoAsync(string archivo);

    /// <summary>La descarga no terminó.</summary>
    void NoSeDescargo();

    /// <summary>
    /// Esc o F12 con el cursor en la página oficial («Escape», «F12»). Devuelve si la caja la tomó (y la página ya no la recibe): F12 siempre;
    /// Esc solo la segunda seguida (segunda revisión: la primera es de la página, que la usa para cerrar sus listas, y la caja dice «Esc otra
    /// vez para terminar el trámite»).
    /// </summary>
    bool Tecla(string tecla);
}

/// <summary>Una cola de impresión de la PC, como la dice Windows.</summary>
/// <param name="Puerto">
/// Su puerto («IP_192.168.1.197», «USB001», «PORTPROMPT:» para guardar como archivo, «SHRFAX:» el fax, «\\PRINCIPAL\…» un puerto local que
/// imprime en la compartida de otra PC).
/// </param>
/// <param name="DeOtraPc">Una conexión a la compartida desde otra PC («\\PRINCIPAL\…»).</param>
/// <param name="Tipo">De qué clase es su puerto, como lo dice el registro de Windows (TCP/IP, WSD, compartida, local…), si se supo.</param>
public sealed record ColaDeLaPc(string Nombre, string? Puerto = null, string? Driver = null, bool DeOtraPc = false, TipoDePuerto? Tipo = null);

/// <summary>Lo que pasó con «Guardar en USB»: dónde quedó y en qué memoria («KINGSTON (E:)»), o por qué no se guardó.</summary>
public sealed record GuardadoEnUsb(string? Donde, string? PorQueNo = null, string? Unidad = null)
{
    public static GuardadoEnUsb SinMemoria { get; } = new(null, "Conecta la memoria USB del cliente y vuelve a oprimir «Guardar en USB».");
}

/// <summary>Un rectángulo de la página de la caja, en sus píxeles (CSS).</summary>
public readonly record struct LugarEnPantalla(double X, double Y, double Ancho, double Alto)
{
    public static LugarEnPantalla Oculto { get; } = default;

    public bool SeVe => Ancho >= 1 && Alto >= 1;
}

public enum FallaDeLaPagina
{
    /// <summary>No hay internet (o no se encuentra el sitio).</summary>
    SinInternet,

    /// <summary>Hay internet pero el sitio no contestó o contestó con error.</summary>
    NoContesta,
}
