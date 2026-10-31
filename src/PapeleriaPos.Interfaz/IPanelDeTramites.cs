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

    /// <summary>Las impresoras de esta PC; primero la que vigila el agente de impresión (la WF-6590), si está.</summary>
    IReadOnlyList<string> Impresoras();

    /// <summary>
    /// Manda el PDF a la impresora con ese nombre de trabajo («T-0042 · CURP»: así lo ve la cola de Windows y el agente de impresión,
    /// nunca con el nombre del cliente) y esas copias.
    /// </summary>
    Task ImprimirAsync(string archivo, string impresora, string nombreDelTrabajo, int copias);

    /// <summary>
    /// «Guardar en USB»: el «Guardar como» de Windows, empezando en la memoria USB si hay una, con <paramref name="nombre"/>. Solo
    /// escribe en ella; no abre nada de lo que trae. Devuelve si se guardó.
    /// </summary>
    Task<bool> GuardarEnUsbAsync(string archivo, string nombre);

    /// <summary>«Copiar para WhatsApp»: el archivo en el portapapeles, para pegarlo en el chat; marcado para no quedar en su historial.</summary>
    void CopiarArchivo(string archivo);

    /// <summary>«Abrir en el navegador», por si algo falla aquí: una ventana InPrivate de Edge.</summary>
    void AbrirEnElNavegador(Uri direccion);

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
