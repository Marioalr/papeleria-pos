namespace PapeleriaPos.Eleventa;

/// <summary>
/// Solo del piloto, para convivir con eleventa (se va con la bandeja al dejar eleventa): lo que la bandeja está mandando
/// a eleventa en este momento y cómo le fue a cada intento. Entre que eleventa lo guarda y que aquí se anota como enviado
/// pasan unos milisegundos (o más, con la red lenta), y una lectura de eleventa en ese rato ya lo ve allá aunque aquí siga
/// pendiente. La lectura junta lo que se mandó (o se anotó a mano) mientras leía (<see cref="Vigilar"/>) y esa vez no
/// ajusta la existencia de sus productos.
/// </summary>
public sealed class EnviosEnCurso
{
    private readonly Lock _candado = new();
    private readonly List<HashSet<Guid>> _vigilancias = [];
    private readonly HashSet<Guid> _rechazados = [];
    private Guid? _mandando;

    /// <summary>Un envío que sale hacia eleventa: cuenta hasta que se desecha, ya con lo que pasó anotado aquí.</summary>
    public IDisposable Mandando(Guid envioId)
    {
        lock (_candado)
        {
            _mandando = envioId;
            // Cómo le va a este intento todavía no se sabe.
            _rechazados.Remove(envioId);
            Ver(envioId);
        }
        return new Fin(this);
    }

    /// <summary>
    /// Eleventa contestó que no (no fue la red): deshizo lo que llevaba y no está allá, así que la lectura lo descuenta
    /// como lo pendiente aunque la bandeja siga detenida en eso. Vale hasta el siguiente intento.
    /// </summary>
    public void Rechazado(Guid envioId)
    {
        lock (_candado)
            _rechazados.Add(envioId);
    }

    /// <summary>
    /// Alguien lo registró allá a mano y lo anotó en la bandeja (no pasa por <see cref="Mandando"/>): una lectura a medias
    /// no sabe si eleventa ya lo tenía cuando lo leyó.
    /// </summary>
    public void AnotadoAMano(Guid envioId)
    {
        lock (_candado)
            Ver(envioId);
    }

    private void Ver(Guid envioId)
    {
        foreach (var vistos in _vigilancias)
            vistos.Add(envioId);
    }

    /// <summary>Junta lo que se está mandando ahora y lo que se mande (o se anote a mano) desde aquí, hasta desecharla.</summary>
    public Vigilancia Vigilar()
    {
        var vigilancia = new Vigilancia(this);
        lock (_candado)
        {
            if (_mandando is { } ya)
                vigilancia.Vistos.Add(ya);
            _vigilancias.Add(vigilancia.Vistos);
        }
        return vigilancia;
    }

    public sealed class Vigilancia(EnviosEnCurso enCurso) : IDisposable
    {
        internal HashSet<Guid> Vistos { get; } = [];

        /// <summary>Los envíos que se mandaron (o se estaban mandando) desde que empezó, y los que se anotaron a mano.</summary>
        public IReadOnlySet<Guid> Mandados()
        {
            lock (enCurso._candado)
                return Vistos.ToHashSet();
        }

        /// <summary>
        /// Lo que eleventa rechazó en su último intento (<see cref="Rechazado"/>). De lo que falló por la red, o de antes
        /// de arrancar el servicio, no se sabe si entró.
        /// </summary>
        public IReadOnlySet<Guid> Rechazados()
        {
            lock (enCurso._candado)
                return enCurso._rechazados.ToHashSet();
        }

        public void Dispose()
        {
            lock (enCurso._candado)
                enCurso._vigilancias.Remove(Vistos);
        }
    }

    private sealed class Fin(EnviosEnCurso enCurso) : IDisposable
    {
        public void Dispose()
        {
            lock (enCurso._candado)
                enCurso._mandando = null;
        }
    }
}
