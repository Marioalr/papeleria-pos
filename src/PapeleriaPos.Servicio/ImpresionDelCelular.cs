using Microsoft.EntityFrameworkCore;
using PapeleriaPos.Datos;
using PapeleriaPos.Impresion;
using PapeleriaPos.Nucleo;
using PapeleriaPos.Nucleo.Contratos;
using PapeleriaPos.Nucleo.Control;
using PapeleriaPos.Nucleo.Turnos;
using PapeleriaPos.Nucleo.Ventas;

namespace PapeleriaPos.Servicio;

/// <summary>
/// Imprimir el ticket de lo que cobra el celular (dueño, 2026-11-26; docs/celular-como-caja.md, paso 11). El celular nunca le habla a la
/// impresora: se lo pide al servicio, y el ticket sale en la impresora de tickets de <b>la caja en cuyo turno entró la venta</b> (en la
/// sucursal 2, la caja principal), sin abrir el cajón:
/// <list type="bullet">
/// <item>Si esa impresora está en la red («tcp://ip:9100»), el servicio le manda el ticket directo (<see cref="ImpresoraRed"/>), sin
/// depender de la ventana de la caja.</item>
/// <item>Si es una impresora de Windows, lo deja aquí y la ventana de esa caja lo recoge (pregunta cada 2 s, como por los tickets
/// mandados), lo pone en su cola de la impresora y dice si salió.</item>
/// </list>
/// Qué impresora tiene cada caja lo dice su ventana cuando pregunta (se guarda en <see cref="Caja.ImpresoraDeTickets"/>). Lo que está
/// en camino vive en memoria, como los tickets mandados (<see cref="BuzonDeTickets"/>): si el servicio se reinicia justo entonces, la
/// venta queda «sin imprimir» y se pide otra vez. Nada se imprime tarde: lo que la ventana no recogió en unos segundos ya no se le da.
/// </summary>
public sealed class ImpresionDelCelular(TimeProvider reloj, IConfiguration configuracion, ILoggerFactory registros)
{
    /// <summary>Una caja que preguntó hace menos que esto tiene su ventana abierta (pregunta cada 2 s).</summary>
    public static readonly TimeSpan Escuchando = TimeSpan.FromSeconds(8);

    /// <summary>Hasta cuándo se le da un ticket a la ventana: después ya no (el cliente ya se fue), y se puede pedir otra vez.</summary>
    public static readonly TimeSpan SeEntrega = TimeSpan.FromSeconds(30);

    /// <summary>Cuánto se recuerda una petición: para contestar igual si el celular la manda otra vez, y si la caja reintenta.</summary>
    public static readonly TimeSpan Recuerdo = TimeSpan.FromHours(1);

    /// <summary>Lo que el celular espera a que la ventana de la caja lo imprima (<c>Celular:SegundosParaImprimir</c>).</summary>
    public const double SegundosParaImprimir = 8;

    /// <summary>Lo que se espera a que la impresora de red conteste al conectarse (<c>Celular:SegundosParaConectarImpresora</c>).</summary>
    public const double SegundosParaConectar = 2;

    /// <summary>
    /// Antes de cada nuevo intento si la impresora de red no contesta al conectarse (puede estar imprimiendo lo de una caja): no se le
    /// mandó nada, así que no sale dos veces. Pocos: el celular está esperando la respuesta del cobro.
    /// </summary>
    private static readonly TimeSpan[] Reintentos = [TimeSpan.FromSeconds(0.5), TimeSpan.FromSeconds(1)];

    public enum Estado { Pendiente, Entregado, Mandando, Impreso, NoSalio, Retirado }

    /// <summary>Un ticket que se pidió imprimir.</summary>
    public sealed class Trabajo
    {
        public required Guid Id { get; init; }
        public required Guid VentaId { get; init; }
        public required string Folio { get; init; }
        public required Guid Destino { get; init; }
        public required string Caja { get; init; }
        public required string Celular { get; init; }
        public required Guid UsuarioId { get; init; }
        public required DateTime CreadoUtc { get; init; }

        /// <summary><see cref="DondeImprimeDto.PorRed"/> o <see cref="DondeImprimeDto.PorCaja"/>.</summary>
        public required string Por { get; init; }

        /// <summary>La primera vez que sale (la venta estaba «sin imprimir»): al salir deja de estarlo. Una reimpresión ya quedó anotada al pedirla.</summary>
        public required bool Primera { get; init; }

        /// <summary>Lo que recoge la ventana de la caja; vacío si va por la red.</summary>
        public TicketPorImprimirDto? Ticket { get; init; }

        public Estado Estado { get; set; }
        public string? Codigo { get; set; }
        public string? Motivo { get; set; }

        /// <summary>
        /// Ya se contestó que no salió (o que iba en camino) y eso quedó en la bitácora: si después sale, se anota como su primera
        /// impresión. Si salió antes de contestar el cobro, no se anota nada, como el ticket que la caja imprime al cobrar.
        /// </summary>
        public bool Tarde { get; set; }

        public TaskCompletionSource Fin { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    private readonly Lock _candado = new();
    private readonly Dictionary<Guid, Trabajo> _trabajos = [];
    private readonly Dictionary<Guid, Trabajo> _porPeticion = [];
    private readonly Dictionary<Guid, (DateTime Utc, string Impresora)> _ventanas = [];
    private readonly ILogger _registro = registros.CreateLogger("PapeleriaPos.Servicio.Celular");

    private DateTime Ahora => reloj.GetUtcNow().UtcDateTime;

    // ── La ventana de la caja ────────────────────────────────────────────────

    /// <summary>
    /// La ventana de una caja pregunta qué imprimir y dice qué impresora de tickets tiene: queda como «con la ventana abierta», su
    /// impresora se guarda si cambió, y se lleva lo que le toca (lo que va por su impresora de Windows). Lo que ya se le dio y todavía no
    /// dice si salió se le da otra vez (se perdió la respuesta): la ventana lo reconoce por su Id y no lo imprime dos veces.
    /// </summary>
    public async Task<List<TicketPorImprimirDto>> PreguntaAsync(PosDbContext db, Guid cajaId, string? impresora, CancellationToken ct)
    {
        var dice = (impresora ?? "").Trim();
        if (dice.Length > Caja.LargoImpresora)
            dice = dice[..Caja.LargoImpresora];
        bool cambio;
        lock (_candado)
        {
            cambio = !_ventanas.TryGetValue(cajaId, out var antes) || antes.Impresora != dice;
            _ventanas[cajaId] = (Ahora, dice);
        }
        // Solo al cambiar (o la primera vez desde que arrancó el servicio): la pregunta de cada 2 s no escribe en la base.
        if (cambio && await db.Cajas.SingleOrDefaultAsync(c => c.Id == cajaId && c.Activa, ct) is { } caja && caja.PonerImpresoraDeTickets(dice))
            await db.SaveChangesAsync(ct);
        lock (_candado)
        {
            var limite = Ahora - SeEntrega;
            var suyos = _trabajos.Values
                .Where(t => t.Destino == cajaId && t.Ticket is not null && t.Estado is Estado.Pendiente or Estado.Entregado && t.CreadoUtc > limite)
                .OrderBy(t => t.CreadoUtc).ToList();
            foreach (var t in suyos)
                t.Estado = Estado.Entregado;
            return suyos.Select(t => t.Ticket!).ToList();
        }
    }

    /// <summary>Si la ventana de esa caja está abierta ahora (preguntó hace poco).</summary>
    public bool VentanaAbierta(Guid cajaId)
    {
        lock (_candado)
            return _ventanas.TryGetValue(cajaId, out var v) && Ahora - v.Utc < Escuchando;
    }

    /// <summary>
    /// La ventana de la caja dice qué pasó con un ticket que recogió. Si salió y era su primera impresión, la venta deja de estar «sin
    /// imprimir». Si no salió, se queda en la cola de la impresora de esa caja («Reintentar» o «No imprimirlo»): si después sale, la
    /// ventana lo dice otra vez y entonces se marca.
    /// </summary>
    public async Task ResultadoAsync(PosDbContext db, Guid cajaId, SolicitudTicketImpreso s, CancellationToken ct)
    {
        Trabajo? trabajo;
        bool tarde;
        lock (_candado)
        {
            if (!_trabajos.TryGetValue(s.Id, out trabajo) || trabajo.Destino != cajaId || trabajo.Estado == Estado.Impreso)
                return;
            if (s.Salio)
                (trabajo.Estado, trabajo.Codigo, trabajo.Motivo) = (Estado.Impreso, null, null);
            else
            {
                (trabajo.Estado, trabajo.Motivo) = (Estado.NoSalio, string.IsNullOrWhiteSpace(s.Motivo) ? null : s.Motivo.Trim());
                trabajo.Codigo = trabajo.Motivo == ImpresoraRed.NoConfirmo ? CodigosDeImpresion.SinConfirmar : CodigosDeImpresion.NoSalio;
            }
            tarde = trabajo.Tarde;
            // Si después sale con «Reintentar», ya se había contestado que no.
            if (!s.Salio)
                trabajo.Tarde = true;
        }
        try
        {
            if (s.Salio)
                await SalioAsync(db, trabajo, tarde, ct);
        }
        finally
        {
            trabajo.Fin.TrySetResult();
        }
        Anotar(trabajo);
    }

    // ── Dónde sale ───────────────────────────────────────────────────────────

    /// <summary>«tcp://ip:9100»: la impresora de red a la que el servicio le manda directo. Null si no lo es (o está mal escrita).</summary>
    internal static (string Host, int Puerto)? DeRed(string? impresora) =>
        impresora is not null && impresora.StartsWith("tcp://", StringComparison.OrdinalIgnoreCase)
        && Uri.TryCreate(impresora, UriKind.Absolute, out var uri) && uri.Host.Length > 0
            ? (uri.Host, uri.Port > 0 ? uri.Port : 9100)
            : null;

    /// <summary>
    /// Si lo que el celular cobre en esa caja puede salir en su impresora, y por dónde. Con impresora de red no se sabe si está prendida
    /// o con papel hasta mandarle algo; con una de Windows, la ventana de esa caja tiene que estar abierta.
    /// </summary>
    public DondeImprimeDto Donde(Caja caja, bool prendido) => Donde(caja.Id, caja.Nombre, caja.ImpresoraDeTickets, prendido);

    public DondeImprimeDto Donde(Guid cajaId, string nombre, string? impresora, bool prendido)
    {
        if (!prendido)
            return new DondeImprimeDto(false, null, CodigosDeImpresion.Apagado,
                "Está apagado «Imprimir lo que cobra el celular»: se prende en la caja, en Configuración › Ticket.");
        if (impresora is null)
            return new DondeImprimeDto(false, null, CodigosDeImpresion.SinImpresora,
                $"La {nombre} todavía no dice qué impresora de tickets tiene: abre su ventana.", true);
        if (impresora.Length == 0 || (impresora.StartsWith("tcp://", StringComparison.OrdinalIgnoreCase) && DeRed(impresora) is null))
            return new DondeImprimeDto(false, null, CodigosDeImpresion.SinImpresora, $"La {nombre} no tiene impresora de tickets configurada.", true);
        if (DeRed(impresora) is not null)
            return new DondeImprimeDto(true, DondeImprimeDto.PorRed, null, $"Sale en la impresora de la {nombre}.", true);
        return VentanaAbierta(cajaId)
            ? new DondeImprimeDto(true, DondeImprimeDto.PorCaja, null, $"Sale en la impresora de la {nombre}.", true)
            : new DondeImprimeDto(false, DondeImprimeDto.PorCaja, CodigosDeImpresion.VentanaCerrada,
                $"La {nombre} no tiene su ventana abierta (o está apagada): ábrela para que imprima.", true);
    }

    // ── Lo que pide el celular ───────────────────────────────────────────────

    /// <summary>
    /// Imprime el ticket de esa venta en la impresora de la caja en cuyo turno entró, y dice qué pasó. No sale dos veces: con una
    /// petición igual (<paramref name="peticion"/>) o con otra de esa venta todavía en camino, contesta cómo va esa. Si la venta estaba
    /// «sin imprimir», es su primera impresión (sin motivo ni permiso) y al salir deja de estarlo; si ya había salido, es una reimpresión:
    /// con el permiso de reimprimir de quien la pide (y su motivo, si se le pide), y queda en la bitácora al pedirla, como en la caja.
    /// </summary>
    /// <param name="alCobrar">
    /// Viene del cobro, que no escribió «cobró sin imprimir el ticket»: si no sale antes de contestar, se anota aquí, con su porqué.
    /// </param>
    public async Task<ImpresionDelCelularDto> ImprimirAsync(PosDbContext db, Venta venta, Caja celular, Guid usuarioId, OpcionesTicket opciones,
        bool alCobrar, Guid? peticion = null, string? motivo = null, CancellationToken ct = default)
    {
        if (EnCurso(venta.Id, peticion) is { } yaPedido)
            return await EsperarAsync(db, yaPedido, venta, celular, usuarioId, alCobrar: false, ct);

        var caja = await db.Cajas.AsNoTracking().SingleAsync(c => c.Id == venta.CajaId, ct);
        var prendido = (await ApiConfiguracion.ConfiguracionAsync(db, venta.SucursalId, opciones)).ImprimirLoDelCelular;
        var donde = Donde(caja, prendido);
        if (!donde.Puede)
        {
            var no = new ImpresionDelCelularDto(ImpresionDelCelularDto.NoImpreso, donde.Codigo, donde.Mensaje, caja.Id, caja.Nombre, donde.Por);
            await SinImprimirAlCobrarAsync(db, venta, celular, caja.Nombre, usuarioId, alCobrar, no, ct);
            _registro.LogInformation("Ticket del celular · {Celular} pidió el ticket {Folio} en la {Caja} · no se imprime ({Codigo})",
                celular.Nombre, venta.FolioTexto, caja.Nombre, donde.Codigo);
            return no;
        }

        var primera = venta is { TicketSinImprimir: true, Estado: EstadoVenta.Pagada } && !venta.Lineas.Any(l => l.CantidadDevuelta.EsPositiva);
        if (!primera)
            await ReimpresionAsync(db, venta, celular, caja, usuarioId, motivo, ct);

        var ticket = await ApiCaja.TicketAsync(db, venta, opciones);
        var id = Ids.Nuevo();
        var nuevo = new Trabajo
        {
            Id = id, VentaId = venta.Id, Folio = venta.FolioTexto ?? "", Destino = caja.Id, Caja = caja.Nombre, Celular = celular.Nombre,
            UsuarioId = usuarioId, CreadoUtc = Ahora, Por = donde.Por!, Primera = primera, Tarde = !alCobrar,
            Estado = donde.Por == DondeImprimeDto.PorRed ? Estado.Mandando : Estado.Pendiente,
            Ticket = donde.Por == DondeImprimeDto.PorCaja ? new TicketPorImprimirDto(id, venta.Id, celular.Nombre, ticket) : null,
        };
        var trabajo = nuevo;
        lock (_candado)
        {
            Limpiar();
            // Otra petición de la misma venta entró mientras tanto: queda una, y esta espera a esa.
            if (EnCursoDe(venta.Id) is { } otra)
                trabajo = otra;
            else
            {
                _trabajos[nuevo.Id] = nuevo;
                if (peticion is { } suya)
                    _porPeticion[suya] = nuevo;
            }
        }
        if (ReferenceEquals(trabajo, nuevo) && DeRed(caja.ImpresoraDeTickets) is var (host, puerto))
            await MandarPorRedAsync(db, nuevo, host, puerto, ticket);
        return await EsperarAsync(db, trabajo, venta, celular, usuarioId, alCobrar && ReferenceEquals(trabajo, nuevo), ct);
    }

    /// <summary>
    /// El cobro llegó otra vez con el mismo Id (se perdió la respuesta) y pide el ticket: si la venta sigue «sin imprimir», se intenta (o se
    /// dice cómo va el que está en camino); si ya salió, no sale otro.
    /// </summary>
    public async Task<ImpresionDelCelularDto> OtraVezAlCobrarAsync(PosDbContext db, Venta venta, Caja celular, Guid usuarioId, OpcionesTicket opciones,
        CancellationToken ct = default)
    {
        var caja = await db.Cajas.AsNoTracking().SingleAsync(c => c.Id == venta.CajaId, ct);
        if (EnCurso(venta.Id, null) is null)
        {
            if (!venta.TicketSinImprimir)
                return new ImpresionDelCelularDto(ImpresionDelCelularDto.Impreso, null, $"El ticket {venta.FolioTexto} ya había salido.", caja.Id, caja.Nombre);
            // Cancelada o con algo devuelto: ya no es su primera impresión, y el cobro no pide motivos.
            if (venta.Estado != EstadoVenta.Pagada || venta.Lineas.Any(l => l.CantidadDevuelta.EsPositiva))
                return new ImpresionDelCelularDto(ImpresionDelCelularDto.NoImpreso, CodigosDeImpresion.NoSalio,
                    $"El ticket {venta.FolioTexto} se canceló o se devolvió algo de él: se reimprime, con su motivo.", caja.Id, caja.Nombre);
        }
        return await ImprimirAsync(db, venta, celular, usuarioId, opciones, alCobrar: false, ct: ct);
    }

    /// <summary>El de esa petición (aunque ya haya terminado), o el de esa venta que todavía está en camino.</summary>
    private Trabajo? EnCurso(Guid ventaId, Guid? peticion)
    {
        lock (_candado)
        {
            Limpiar();
            return peticion is { } id && _porPeticion.TryGetValue(id, out var suyo) && suyo.VentaId == ventaId ? suyo : EnCursoDe(ventaId);
        }
    }

    /// <summary>Dentro del candado. Lo que la ventana recogió y no dice si salió deja de contar al pasar <see cref="SeEntrega"/>.</summary>
    private Trabajo? EnCursoDe(Guid ventaId)
    {
        var limite = Ahora - SeEntrega;
        return _trabajos.Values.FirstOrDefault(t => t.VentaId == ventaId
            && (t.Estado == Estado.Mandando || (t.Estado is Estado.Pendiente or Estado.Entregado && t.CreadoUtc > limite)));
    }

    private void Limpiar()
    {
        var limite = Ahora - Recuerdo;
        foreach (var viejo in _trabajos.Values.Where(t => t.CreadoUtc < limite).Select(t => t.Id).ToList())
            _trabajos.Remove(viejo);
        foreach (var vieja in _porPeticion.Where(p => p.Value.CreadoUtc < limite).Select(p => p.Key).ToList())
            _porPeticion.Remove(vieja);
    }

    /// <summary>
    /// Una reimpresión pedida desde el celular: como en la caja, con el permiso «Reimprimir un ticket» de quien la pide (Puede o No: quien
    /// necesitaría autorización no puede desde el celular) y su motivo si se le pide; queda en la bitácora al pedirla.
    /// </summary>
    private static async Task ReimpresionAsync(PosDbContext db, Venta venta, Caja celular, Caja caja, Guid usuarioId, string? motivo, CancellationToken ct)
    {
        var control = new ServicioControl(db);
        var decision = await control.EvaluarAsync(usuarioId, venta.SucursalId, AccionControlada.Reimprimir, venta.TotalCentavos, ct);
        if (decision.Nivel != NivelPermiso.Permitido)
            throw new RechazoConCodigo($"El ticket {venta.FolioTexto} ya salió, y quien lo pide no tiene permiso para reimprimir tickets.",
                "no-permitido", StatusCodes.Status403Forbidden);
        if (decision.PideMotivo && string.IsNullOrWhiteSpace(motivo))
            throw new RechazoConCodigo($"El ticket {venta.FolioTexto} ya salió: para reimprimirlo, escribe el motivo.", SolicitudPrimeraImpresion.CodigoConMotivo);
        if (motivo?.Trim().Length > LargoMotivo)
            throw new RechazoConCodigo($"El motivo va hasta {LargoMotivo} letras.", SolicitudPrimeraImpresion.CodigoConMotivo);
        await control.RequerirAsync(usuarioId, venta.SucursalId, celular.Id, AccionControlada.Reimprimir, venta.TotalCentavos,
            $"Reimpresión del ticket {venta.FolioTexto}, pedida desde {celular.Nombre} en la impresora de la {caja.Nombre}", motivo: motivo,
            ventaId: venta.Id, ct: ct);
        venta.TicketSinImprimir = false;
        venta.TicketImpresiones++;
        await db.SaveChangesAsync(ct);
    }

    public const int LargoMotivo = 200;

    /// <summary>
    /// Directo a la impresora de red, sin abrir el cajón. Si no contesta al conectarse se intenta un par de veces más (no se le mandó
    /// nada); si falla después de mandarle algo, no se repite: pudo salir. No se corta si el celular deja de esperar: el papel saldría a medias.
    /// </summary>
    private async Task MandarPorRedAsync(PosDbContext db, Trabajo trabajo, string host, int puerto, TicketDto ticket)
    {
        (bool Salio, string? Codigo, string? Motivo) resultado;
        try
        {
            var datos = TicketEscPos.Generar(DisenoTicket.Armar(ticket), false);
            var impresora = new ImpresoraRed(host, puerto,
                TimeSpan.FromSeconds(configuracion.GetValue("Celular:SegundosParaConectarImpresora", SegundosParaConectar)),
                TimeSpan.FromSeconds(configuracion.GetValue("Celular:SegundosParaImprimir", SegundosParaImprimir)));
            resultado = await EnviarAsync(impresora, datos);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // No se pudo ni armar (el logo del ticket dañado): no se le mandó nada.
            resultado = (false, CodigosDeImpresion.NoSalio, $"No se pudo armar para la impresora. {ex.Message}");
        }
        bool tarde;
        lock (_candado)
        {
            (trabajo.Estado, trabajo.Codigo, trabajo.Motivo) = (resultado.Salio ? Estado.Impreso : Estado.NoSalio, resultado.Codigo, resultado.Motivo);
            tarde = trabajo.Tarde;
        }
        try
        {
            if (resultado.Salio)
                await SalioAsync(db, trabajo, tarde, CancellationToken.None);
        }
        finally
        {
            trabajo.Fin.TrySetResult();
        }
        Anotar(trabajo);
    }

    private async Task<(bool Salio, string? Codigo, string? Motivo)> EnviarAsync(ImpresoraRed impresora, byte[] datos)
    {
        for (var intento = 0; ; intento++)
        {
            try
            {
                await impresora.EnviarAsync(datos);
                return (true, null, null);
            }
            catch (ErrorAlConectar) when (intento < Reintentos.Length)
            {
                await Task.Delay(Reintentos[intento], reloj);
            }
            catch (ErrorAlConectar ex)
            {
                return (false, CodigosDeImpresion.NoContesta, ex.Message);
            }
            catch (IOException ex)
            {
                return (false, ex.Message == ImpresoraRed.SinPapel ? CodigosDeImpresion.SinPapel
                    : ex.Message == ImpresoraRed.NoConfirmo ? CodigosDeImpresion.SinConfirmar : CodigosDeImpresion.NoSalio, ex.Message);
            }
        }
    }

    /// <summary>
    /// Salió: si era su primera impresión, la venta deja de estar «sin imprimir» (<see cref="RegistroCaja.TicketSalioAsync"/>). A la
    /// bitácora solo si ya se había contestado que no (o se pidió después del cobro): el que sale al cobrar no se anota, como en la caja.
    /// </summary>
    private static async Task SalioAsync(PosDbContext db, Trabajo trabajo, bool tarde, CancellationToken ct)
    {
        if (!trabajo.Primera)
            return;
        await new RegistroCaja(db).TicketSalioAsync(trabajo.VentaId, trabajo.Destino, trabajo.UsuarioId,
            tarde ? $"Primera impresión del ticket {trabajo.Folio}: no salió al cobrar (no es una reimpresión); la pidió {trabajo.Celular} y salió en la {trabajo.Caja}"
                : null, ct);
    }

    /// <summary>
    /// Espera a que el trabajo termine (lo que va por la red ya terminó; lo de la ventana, hasta <see cref="SegundosParaImprimir"/>) y
    /// dice cómo quedó. Si la ventana no lo recogió, ya no se le da.
    /// </summary>
    private async Task<ImpresionDelCelularDto> EsperarAsync(PosDbContext db, Trabajo trabajo, Venta venta, Caja celular, Guid usuarioId, bool alCobrar,
        CancellationToken ct)
    {
        var espera = TimeSpan.FromSeconds(configuracion.GetValue("Celular:SegundosParaImprimir", SegundosParaImprimir));
        try
        {
            await trabajo.Fin.Task.WaitAsync(espera, reloj, ct);
        }
        catch (TimeoutException)
        {
        }
        var decidido = false;
        lock (_candado)
        {
            switch (trabajo.Estado)
            {
                case Estado.Pendiente:
                    // La ventana dejó de preguntar justo ahora: ya no se le da (no sale cuando el cliente ya se fue).
                    (trabajo.Estado, trabajo.Codigo) = (Estado.Retirado, CodigosDeImpresion.VentanaCerrada);
                    trabajo.Tarde = true;
                    break;
                case Estado.Entregado or Estado.Mandando:
                    trabajo.Tarde = true;
                    break;
                case Estado.Impreso or Estado.NoSalio:
                    decidido = !trabajo.Fin.Task.IsCompleted;
                    break;
            }
        }
        // Ya se sabe si salió y se está anotando en la venta: es un instante.
        if (decidido)
            await trabajo.Fin.Task.WaitAsync(ct);
        var dto = Dto(trabajo);
        if (!dto.Salio)
            await SinImprimirAlCobrarAsync(db, venta, celular, trabajo.Caja, usuarioId, alCobrar, dto, ct);
        if (trabajo.Estado == Estado.Retirado)
            Anotar(trabajo);
        return dto;
    }

    /// <summary>
    /// El cobro pidió el ticket y no salió (o no se sabe todavía): queda en la bitácora como lo que cobra el celular sin imprimir
    /// («Cobrar sin imprimir el ticket», con el celular como caja), con su porqué. La venta sigue «sin imprimir».
    /// </summary>
    private static async Task SinImprimirAlCobrarAsync(PosDbContext db, Venta venta, Caja celular, string caja, Guid usuarioId, bool alCobrar,
        ImpresionDelCelularDto impresion, CancellationToken ct)
    {
        if (!alCobrar)
            return;
        new ServicioControl(db).Registrar(venta.SucursalId, celular.Id, usuarioId, AccionControlada.CobrarSinTicket,
            $"Cobró en {celular.Nombre}, en el turno de la {caja}, sin imprimir el ticket: se pidió y "
            + (impresion.Estado == ImpresionDelCelularDto.EnCamino ? "iba en camino a la impresora" : $"no salió ({impresion.Codigo})"),
            venta.TotalCentavos, venta.Id);
        await db.SaveChangesAsync(ct);
    }

    private static ImpresionDelCelularDto Dto(Trabajo t) => t.Estado switch
    {
        Estado.Impreso => new(ImpresionDelCelularDto.Impreso, null, $"Salió en la impresora de la {t.Caja}.", t.Destino, t.Caja, t.Por),
        Estado.Entregado or Estado.Pendiente => new(ImpresionDelCelularDto.EnCamino, null,
            $"La {t.Caja} ya lo tiene y lo está imprimiendo.", t.Destino, t.Caja, t.Por),
        Estado.Mandando => new(ImpresionDelCelularDto.EnCamino, null, $"Se le está mandando a la impresora de la {t.Caja}.", t.Destino, t.Caja, t.Por),
        Estado.Retirado => new(ImpresionDelCelularDto.NoImpreso, CodigosDeImpresion.VentanaCerrada,
            $"La {t.Caja} no lo recogió: revisa que tenga su ventana abierta y pídelo otra vez.", t.Destino, t.Caja, t.Por),
        _ => new(ImpresionDelCelularDto.NoImpreso, t.Codigo ?? CodigosDeImpresion.NoSalio, t.Codigo switch
        {
            CodigosDeImpresion.NoContesta => $"La impresora de la {t.Caja} no contesta: revisa que esté prendida y conectada, y pídelo otra vez.",
            CodigosDeImpresion.SinPapel => $"Se acabó el papel de la impresora de la {t.Caja}: pon papel y pídelo otra vez.",
            CodigosDeImpresion.SinConfirmar => $"La impresora de la {t.Caja} no terminó de confirmar: mira si salió antes de pedirlo otra vez.",
            _ when t.Por == DondeImprimeDto.PorCaja =>
                $"No salió de la impresora de la {t.Caja}{(t.Motivo is { } m ? $": {m}" : ".")} En esa caja se reintenta o se deja sin imprimir.",
            _ => $"No salió de la impresora de la {t.Caja}{(t.Motivo is { } m ? $": {m}" : ".")}",
        }, t.Destino, t.Caja, t.Por),
    };

    private void Anotar(Trabajo t) =>
        _registro.LogInformation("Ticket del celular · {Celular} pidió el ticket {Folio} en la {Caja} (por {Por}) · {Estado} {Codigo}",
            t.Celular, t.Folio, t.Caja, t.Por == DondeImprimeDto.PorRed ? "la red" : "su ventana", t.Estado, t.Codigo);
}
