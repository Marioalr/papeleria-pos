namespace PapeleriaPos.Nucleo.Usuarios;

/// <summary>
/// De antes de la entrega 1: ya no hay roles, cada usuario tiene sus permisos (<see cref="Control.PermisoUsuario"/>). Nada
/// decide con esto: solo dice qué permisos recibe, al migrar, un usuario de antes (<see cref="Control.PermisosDeRoles"/>).
/// </summary>
public enum Rol
{
    Cajero,
    Encargado,
    Dueno,
}

public enum ResultadoPin
{
    Correcto,
    Incorrecto,
    /// <summary>Demasiados intentos fallidos: hay que esperar.</summary>
    Bloqueado,

    /// <summary>
    /// Todavía no tiene PIN (vino de eleventa, 2026-10-25): no entra hasta que el dueño se lo ponga, y no cuenta como intento
    /// fallido (<see cref="FichasEntrada.SinPin"/>).
    /// </summary>
    SinPin,
}

/// <summary>Quien usa el POS. En la caja entra con su usuario y un PIN corto, para cambiar de cajero rápido.</summary>
public class Usuario : IVersionado
{
    public const int IntentosPermitidos = 5;
    public static readonly TimeSpan TiempoDeBloqueo = TimeSpan.FromMinutes(5);

    public Guid Id { get; set; } = Ids.Nuevo();
    public required string Nombre { get; set; }

    /// <summary>Único: con él se entra.</summary>
    public required string NombreUsuario { get; set; }

    public string PinHash { get; private set; } = "";

    /// <summary>De antes de la entrega 1 (ver <see cref="Usuarios.Rol"/>): la columna se queda, pero ya no decide nada.</summary>
    public Rol Rol { get; set; } = Rol.Cajero;

    /// <summary>
    /// La primera cuenta: la que se crea al instalar, con todos los permisos (al migrar, el dueño). Solo se dice en
    /// «Usuarios y permisos»; no tiene nada que los demás no puedan tener.
    /// </summary>
    public bool PrimeraCuenta { get; set; }

    /// <summary>Vacío: entra en todas las sucursales (el dueño de antes).</summary>
    public Guid? SucursalId { get; set; }

    /// <summary>
    /// El <c>USUARIOS.ID</c> de eleventa de quien vino de allá (importar-eleventa o la lectura del piloto, 2026-10-25): con él se
    /// le anota en eleventa lo que hace en la caja 2. Uno por sucursal. Vacío: se dio de alta aquí (lo suyo va a eleventa a
    /// nombre del cajero de la configuración, <c>Eleventa:Cajero</c>). De eleventa solo se traen el número y el nombre, nunca su
    /// clave.
    /// </summary>
    public int? EleventaId { get; set; }

    public bool Activo { get; set; } = true;
    public int IntentosFallidos { get; private set; }
    public DateTime? BloqueadoHastaUtc { get; private set; }

    public long Version { get; set; }
    public DateTime ModificadoUtc { get; set; }

    /// <summary>
    /// Ya tiene PIN. Los que vienen de eleventa llegan sin él (2026-10-25): su ficha sale en la entrada, pero no entran ni
    /// autorizan hasta que el dueño se lo pone en «Usuarios y permisos» (o con <c>pin-usuario</c>).
    /// </summary>
    public bool TienePin => PinHash.Length > 0;

    /// <summary>Un PIN nuevo empieza sin intentos fallidos ni espera.</summary>
    public void EstablecerPin(string pin)
    {
        PinHash = Pin.Hash(pin);
        (IntentosFallidos, BloqueadoHastaUtc) = (0, null);
    }

    public ResultadoPin VerificarPin(string pin, DateTime ahoraUtc)
    {
        // Sin PIN no hay nada que adivinar: no suma intentos (si no, se bloquearía antes de tener uno).
        if (!TienePin)
            return ResultadoPin.SinPin;
        if (BloqueadoHastaUtc is { } hasta && ahoraUtc < hasta)
            return ResultadoPin.Bloqueado;

        if (Activo && Pin.Verificar(pin, PinHash))
        {
            IntentosFallidos = 0;
            BloqueadoHastaUtc = null;
            return ResultadoPin.Correcto;
        }

        IntentosFallidos++;
        if (IntentosFallidos >= IntentosPermitidos)
        {
            BloqueadoHastaUtc = ahoraUtc + TiempoDeBloqueo;
            IntentosFallidos = 0;
            return ResultadoPin.Bloqueado;
        }
        return ResultadoPin.Incorrecto;
    }
}
