using PapeleriaPos.Nucleo.Contratos;

namespace PapeleriaPos.Servicio;

/// <summary>
/// Los tickets que mandan los celulares, mientras la caja de destino los recoge (docs/celular-como-caja.md, «Mandar el ticket a
/// una caja»). Viven en memoria, como las sesiones del celular: el celular espera la respuesta (unos segundos) y la caja los
/// recoge cada 2 s, así que un reinicio del servicio solo pierde lo que estaba en camino, y el celular lo dice («no lo recogió»)
/// y se manda otra vez (decidido, por revisar). También sabe qué cajas tienen la venta abierta: las que preguntan por sus tickets.
/// </summary>
public sealed class BuzonDeTickets(TimeProvider reloj)
{
    /// <summary>Una caja que preguntó hace menos que esto tiene la venta abierta (pregunta cada 2 s).</summary>
    public static readonly TimeSpan Escuchando = TimeSpan.FromSeconds(8);

    /// <summary>Cuánto se recuerda un ticket mandado: para contestar igual si el celular lo manda otra vez.</summary>
    public static readonly TimeSpan Recuerdo = TimeSpan.FromHours(1);

    public enum Estado { Pendiente, Entregado, Recibido, Retirado }

    public sealed class Envio(TicketRecibidoDto ticket, Guid origen, Guid destino, TicketMandadoDto respuesta)
    {
        public TicketRecibidoDto Ticket { get; } = ticket;
        public Guid Origen { get; } = origen;
        public Guid Destino { get; } = destino;
        public TicketMandadoDto Respuesta { get; } = respuesta;
        public Estado Estado { get; set; }
        public TaskCompletionSource Recibido { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    private readonly object _candado = new();
    private readonly Dictionary<Guid, Envio> _envios = [];
    private readonly Dictionary<Guid, (DateTime Utc, int Esperan, Guid? Atiende)> _escuchando = [];

    /// <summary>La caja preguntó por sus tickets: tiene la venta abierta, con quien atiende y cuántos esperan.</summary>
    public void Escucha(Guid caja, int esperan, Guid? atiende)
    {
        lock (_candado)
            _escuchando[caja] = (reloj.GetUtcNow().UtcDateTime, esperan, atiende);
    }

    /// <summary>Si la caja tiene la venta abierta ahora (preguntó hace poco), con quién atiende y cuántos esperan.</summary>
    public (bool Recibe, int? Esperan, Guid? Atiende) Como(Guid caja)
    {
        lock (_candado)
            return _escuchando.TryGetValue(caja, out var e) && reloj.GetUtcNow().UtcDateTime - e.Utc < Escuchando
                ? (true, e.Esperan, e.Atiende)
                : (false, null, null);
    }

    /// <summary>El que ya se mandó con ese Id (y no se retiró): mandarlo otra vez no lo duplica.</summary>
    public Envio? Ya(Guid id)
    {
        lock (_candado)
        {
            Limpiar();
            return _envios.TryGetValue(id, out var e) && e.Estado != Estado.Retirado ? e : null;
        }
    }

    public void Poner(Envio envio)
    {
        lock (_candado)
            _envios[envio.Ticket.Id] = envio;
    }

    /// <summary>Lo que espera a esa caja: pendiente o ya entregado sin confirmar (si la respuesta se perdió, se le da otra vez).</summary>
    public List<TicketRecibidoDto> Pendientes(Guid caja)
    {
        lock (_candado)
        {
            var suyos = _envios.Values.Where(e => e.Destino == caja && e.Estado is Estado.Pendiente or Estado.Entregado)
                .OrderBy(e => e.Ticket.MandadoUtc).ToList();
            foreach (var e in suyos)
                e.Estado = Estado.Entregado;
            return suyos.Select(e => e.Ticket).ToList();
        }
    }

    /// <summary>La caja ya los guardó en su espera: se le contesta al celular. Solo los de esa caja.</summary>
    public void Confirmar(Guid caja, IEnumerable<Guid> ids)
    {
        lock (_candado)
        {
            foreach (var id in ids)
            {
                if (_envios.TryGetValue(id, out var e) && e.Destino == caja && e.Estado != Estado.Recibido)
                {
                    e.Estado = Estado.Recibido;
                    e.Recibido.TrySetResult();
                }
            }
        }
    }

    /// <summary>El celular se cansó de esperar: si la caja no lo había recogido, ya no se le da. Si ya lo recogió, se queda.</summary>
    public bool Retirar(Envio envio)
    {
        lock (_candado)
        {
            if (envio.Estado != Estado.Pendiente)
                return false;
            envio.Estado = Estado.Retirado;
            _envios.Remove(envio.Ticket.Id);
            return true;
        }
    }

    private void Limpiar()
    {
        var limite = reloj.GetUtcNow().UtcDateTime - Recuerdo;
        foreach (var viejo in _envios.Values.Where(e => e.Ticket.MandadoUtc < limite).Select(e => e.Ticket.Id).ToList())
            _envios.Remove(viejo);
    }
}
