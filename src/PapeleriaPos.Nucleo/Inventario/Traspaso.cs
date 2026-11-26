using PapeleriaPos.Nucleo.Catalogo;

namespace PapeleriaPos.Nucleo.Inventario;

/// <summary>De qué lado está esta sucursal en un traspaso: lo manda (<see cref="Salida"/>) o lo recibe (<see cref="Entrada"/>).</summary>
public enum SentidoTraspaso
{
    Salida,
    Entrada,
}

/// <summary>
/// Por dónde va un traspaso. La base los guarda por su nombre: no se renombran. Del lado que manda: <see cref="Borrador"/> (se
/// arma la lista) → <see cref="Enviado"/> (se exportó el archivo y la mercancía salió) o <see cref="Descartado"/>. Del lado que
/// recibe: <see cref="PorRecibir"/> (se importó el archivo) → <see cref="EnRevision"/> (alguien sin el permiso marcó cambios y
/// espera a quien lo tiene) → <see cref="Recibido"/> (la mercancía entró).
/// </summary>
public enum EstadoTraspaso
{
    Borrador,
    Enviado,
    PorRecibir,
    EnRevision,
    Recibido,
    Descartado,
}

/// <summary>
/// Un traspaso de mercancía entre sucursales, <b>provisional</b> (dueño, 2026-11-26): mientras no exista el servidor central
/// (docs/servidor-remoto.md), cada sucursal tiene su propia base y lo que viaja de una a otra es un archivo
/// (<see cref="ArchivoDeTraspaso"/>), en una memoria USB o como se pueda. La sucursal que lo tramita arma la lista y la exporta
/// (ahí sale su mercancía); la que recibe importa el archivo, revisa lo que llegó y lo recibe (ahí entra). Cada base guarda su
/// lado, con el mismo Id (el del archivo): así el mismo archivo no se importa dos veces, ni en la sucursal de donde salió.
/// Es el documento de sus movimientos del kardex (<see cref="TipoMovimiento.TraspasoSalida"/> y
/// <see cref="TipoMovimiento.TraspasoEntrada"/>). Cuando exista el servidor, esto se reemplaza (docs/traspasos.md dice qué quitar).
/// </summary>
public class Traspaso
{
    /// <summary>Hasta cuántos renglones lleva un traspaso: más es un error (o un inventario completo, que no va así).</summary>
    public const int RenglonesMaximos = 500;

    /// <summary>Lo más largo del nombre de la otra sucursal y de la nota.</summary>
    public const int SucursalMaxima = 60;

    public const int NotaMaxima = 200;

    /// <summary>El mismo en las dos sucursales: lo pone la caja que arma la lista y viaja en el archivo.</summary>
    public Guid Id { get; init; } = Ids.Nuevo();

    /// <summary>La sucursal de esta base.</summary>
    public Guid SucursalId { get; init; }

    public SentidoTraspaso Sentido { get; init; }
    public EstadoTraspaso Estado { get; set; }

    /// <summary>La otra sucursal, por su nombre: a dónde va (lo escribe quien lo arma) o de dónde vino (lo dice el archivo).</summary>
    public string OtraSucursal { get; set; } = "";

    public string? Nota { get; set; }

    /// <summary>La huella del archivo (SHA-256, <see cref="ArchivoDeTraspaso"/>); vacía mientras es un borrador.</summary>
    public string? Huella { get; set; }

    /// <summary>
    /// El archivo tal como se exportó o se importó (su texto): del lado que manda, para volver a guardarlo si se perdió la memoria;
    /// del lado que recibe, lo que decía al llegar, aunque después se cambie lo recibido.
    /// </summary>
    public string? Archivo { get; set; }

    /// <summary>Quién armó la lista o quién importó el archivo.</summary>
    public Guid UsuarioId { get; init; }

    public DateTime CreadoUtc { get; init; }

    /// <summary>Quién lo exportó (salida) o quién lo recibió (entrada), y cuándo.</summary>
    public Guid? CerroId { get; set; }

    public DateTime? CerradoUtc { get; set; }

    /// <summary>Del lado que recibe: quién marcó los cambios para revisión, y cuándo.</summary>
    public Guid? MarcoId { get; set; }

    public DateTime? MarcadoUtc { get; set; }

    /// <summary>
    /// Si al exportar o al recibir se movieron las existencias. En el piloto con eleventa, no: el inventario se lleva allá y el
    /// traspaso solo es el documento.
    /// </summary>
    public bool MovioExistencias { get; set; }

    public List<TraspasoRenglon> Renglones { get; set; } = [];

    /// <summary>La clave para comparar por teléfono que el archivo es el mismo en las dos sucursales («4F2A-91C3»).</summary>
    public string? Clave => Huella is { } huella ? ArchivoDeTraspaso.Clave(huella) : null;

    /// <summary>Todavía se puede cambiar: el borrador de quien manda, o lo que quien recibe no ha recibido.</summary>
    public bool Abierto => Estado is EstadoTraspaso.Borrador or EstadoTraspaso.PorRecibir or EstadoTraspaso.EnRevision;
}

/// <summary>Un renglón de un traspaso: un producto y cuántos, en su unidad base.</summary>
public class TraspasoRenglon
{
    public Guid Id { get; init; } = Ids.Nuevo();
    public Guid TraspasoId { get; set; }
    public int Orden { get; set; }

    /// <summary>
    /// El producto en esta base. Del lado que recibe, vacío si ninguno de sus códigos está en este catálogo («sin empatar»): lo
    /// empata quien revisa, o se queda sin recibir.
    /// </summary>
    public Guid? VarianteId { get; set; }

    /// <summary>Del lado que recibe: el producto con que se empató solo, por su código, al importar; vacío si no se empató.</summary>
    public Guid? VarianteDelArchivoId { get; init; }

    /// <summary>Su código principal y su nombre, como los dice el archivo (del lado que manda, como estaban al armarlo).</summary>
    public string? Codigo { get; set; }

    public string Nombre { get; set; } = "";
    public UnidadBase Unidad { get; set; }

    /// <summary>Lo que dice el documento: lo que se manda. En lo que se agregó al recibir (no venía en el archivo), cero.</summary>
    public Cantidad Enviada { get; set; }

    /// <summary>Del lado que recibe: lo que llegó. Empieza igual a <see cref="Enviada"/>; lo cambia quien revisa.</summary>
    public Cantidad Recibida { get; set; }

    /// <summary>Lo que anota quien revisa («llegó mojado», «venían 2 de menos»).</summary>
    public string? Nota { get; set; }

    /// <summary>Se agregó al recibir: llegó y no venía en el archivo.</summary>
    public bool Agregado { get; init; }

    /// <summary>Del lado que recibe: cambió contra lo que decía el archivo (otra cantidad, otro producto, o no venía).</summary>
    public bool Cambio => Agregado || Recibida != Enviada || VarianteId != VarianteDelArchivoId;
}
