namespace PapeleriaPos.Nucleo.Usuarios;

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
    public Rol Rol { get; set; } = Rol.Cajero;

    /// <summary>Vacío para el dueño, que ve todas.</summary>
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

    /// <summary>Encargado y dueño pueden hacer el corte y, más adelante, autorizar.</summary>
    public bool PuedeHacerCorte => Rol is Rol.Encargado or Rol.Dueno;
}
