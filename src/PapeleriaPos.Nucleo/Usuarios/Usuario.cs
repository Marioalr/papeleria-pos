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

    public bool Activo { get; set; } = true;
    public int IntentosFallidos { get; private set; }
    public DateTime? BloqueadoHastaUtc { get; private set; }

    public long Version { get; set; }
    public DateTime ModificadoUtc { get; set; }

    public void EstablecerPin(string pin) => PinHash = Pin.Hash(pin);

    public ResultadoPin VerificarPin(string pin, DateTime ahoraUtc)
    {
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
