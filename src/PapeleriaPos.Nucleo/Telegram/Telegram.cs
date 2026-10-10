using System.Security.Cryptography;
using PapeleriaPos.Nucleo.Control;

namespace PapeleriaPos.Nucleo.Telegram;

/// <summary>Lo que se le avisa al dueño. Cada una se puede apagar en la configuración de la sucursal.</summary>
public enum TipoAlerta
{
    DiferenciaCorte,
    Cancelacion,
    Devolucion,
    CambioPrecio,
    VentaBajoCosto,
    SalidaEfectivo,
    AutorizacionNegada,
    TransferenciaRechazada,
    CajaSinConexion,
    FueraDeHorario,
    ResumenDiario,

    /// <summary>Tinta o caja de mantenimiento bajas, un consumible cambiado o la impresora sin responder.</summary>
    Impresora,

    /// <summary>La impresora contó menos hojas de las que se mandaron: un atasco o una cancelación en su panel.</summary>
    HojasFaltantes,

    /// <summary>Un descuento arriba de lo que el cajero puede dar solo (lo autorizó el encargado o el dueño).</summary>
    Descuento,

    /// <summary>Un precio del catálogo que cambió alguien que no es el dueño.</summary>
    CambioPrecioCatalogo,

    /// <summary>Una compra cerrada con diferencias contra la nota o con productos que quedaron debajo del margen mínimo.</summary>
    Compra,

    /// <summary>Un ajuste o una merma desde el umbral (los del dueño no se le avisan).</summary>
    AjusteInventario,

    /// <summary>Una diferencia del conteo cíclico desde el umbral de ajustes.</summary>
    DiferenciaConteo,
}

/// <summary>Un chat de Telegram vinculado a un usuario (solo el dueño): recibe alertas y contesta autorizaciones.</summary>
public class ChatTelegram
{
    /// <summary>
    /// El chat de la prueba con una persona real: no es de Telegram, sus mensajes no se mandan y la caja de prueba los
    /// muestra como los avisos que le habrían llegado al dueño.
    /// </summary>
    public const long Simulado = 0;

    public Guid Id { get; set; } = Ids.Nuevo();
    public Guid UsuarioId { get; set; }
    public long ChatId { get; set; }

    /// <summary>El nombre que tiene en Telegram, para reconocerlo en la lista.</summary>
    public string? Nombre { get; set; }

    public DateTime VinculadoUtc { get; set; }
    public bool Activo { get; set; } = true;
}

/// <summary>El código que muestra la caja para vincular un chat: se le escribe al bot "/vincular 123456".</summary>
public class CodigoVinculacion
{
    public static readonly TimeSpan Vigencia = TimeSpan.FromMinutes(10);

    public Guid Id { get; set; } = Ids.Nuevo();
    public Guid UsuarioId { get; set; }
    public required string Codigo { get; set; }
    public DateTime VenceUtc { get; set; }
    public DateTime? UsadoUtc { get; set; }

    public static string Nuevo() => RandomNumberGenerator.GetInt32(0, 1_000_000).ToString("000000");
}

public enum EstadoSolicitudRemota
{
    Pendiente,
    Aprobada,
    Rechazada,
    Vencida,
    Cancelada,
}

/// <summary>
/// Una autorización pedida por Telegram desde la caja. Si el dueño la aprueba, se crea la misma
/// <see cref="Autorizacion"/> de un solo uso que da el PIN.
/// </summary>
public class SolicitudRemota
{
    public static readonly TimeSpan Espera = TimeSpan.FromMinutes(10);

    public Guid Id { get; set; } = Ids.Nuevo();
    public AccionControlada Accion { get; set; }
    public Guid SucursalId { get; set; }
    public Guid? CajaId { get; set; }
    public Guid SolicitanteId { get; set; }
    public long? MontoCentavos { get; set; }
    public required string Detalle { get; set; }
    public DateTime CreadaUtc { get; set; }
    public DateTime VenceUtc { get; set; }
    public EstadoSolicitudRemota Estado { get; set; }
    public Guid? RespondioId { get; set; }
    public DateTime? RespondidaUtc { get; set; }
    public Guid? AutorizacionId { get; set; }
}

/// <summary>
/// Un mensaje para Telegram. Se guarda antes de mandarse: sin internet espera, y no se manda dos veces. Con
/// <see cref="Clave"/>, la misma alerta no se repite (una caja sin conexión avisa una vez por cada vez que se cae).
/// </summary>
public class MensajeTelegram
{
    public Guid Id { get; set; } = Ids.Nuevo();
    public long ChatId { get; set; }
    public required string Texto { get; set; }
    public TipoAlerta? Tipo { get; set; }
    public string? Clave { get; set; }

    /// <summary>Con solicitud pendiente, el mensaje lleva los botones Aprobar y Rechazar.</summary>
    public Guid? SolicitudId { get; set; }

    public DateTime CreadoUtc { get; set; }
    public DateTime? EnviadoUtc { get; set; }

    /// <summary>El número que le dio Telegram, para cambiar el texto después (ya se aprobó, venció…).</summary>
    public long? MensajeId { get; set; }

    /// <summary>Texto con el que hay que reemplazar el que ya se mandó.</summary>
    public string? TextoNuevo { get; set; }

    public int Intentos { get; set; }
    public string? Error { get; set; }
}
