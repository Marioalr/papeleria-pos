namespace PapeleriaPos.Impresiones.ControlPrint;

/// <summary>Por qué no se pudo leer de Control Print.</summary>
public enum FallaControlPrint
{
    /// <summary>No contestó a tiempo o no se pudo conectar (la PC apagada, sin red, Control Print cerrado, el firewall).</summary>
    SinConexion,
    /// <summary>El código escrito no tiene 16 letras y números.</summary>
    CodigoIncompleto,
    /// <summary>La prueba del saludo no coincide con el código escrito: el código está mal (o no es ese servidor).</summary>
    CodigoNoCoincide,
    /// <summary>El servidor rechazó el código (401): se cambió en Ajustes → Red.</summary>
    CodigoRechazado,
    /// <summary>El servidor presentó un certificado distinto del fijado: no se le manda nada.</summary>
    CertificadoDistinto,
    /// <summary>El servidor habla otra versión del protocolo.</summary>
    VersionDistinta,
    /// <summary>Control Print pide una sesión (o un usuario con permiso) para eso, y el POS no inicia sesión.</summary>
    SinPermiso,
    /// <summary>Eso ya no existe en Control Print (404).</summary>
    NoExiste,
    /// <summary>Control Print contestó con un error o con algo que no se entiende.</summary>
    Servidor,
}

/// <summary>
/// No se pudo leer de Control Print. El mensaje se puede mostrar tal cual al dueño o al cajero, y nunca lleva el código de
/// conexión.
/// </summary>
public sealed class ErrorControlPrint(FallaControlPrint falla, string mensaje, Exception? interna = null) : Exception(mensaje, interna)
{
    public FallaControlPrint Falla { get; } = falla;
}
