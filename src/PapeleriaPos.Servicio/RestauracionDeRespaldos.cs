using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Hosting.WindowsServices;
using PapeleriaPos.Datos;
using PapeleriaPos.Nucleo;
using PapeleriaPos.Nucleo.Contratos;
using PapeleriaPos.Nucleo.Control;
using PapeleriaPos.Nucleo.Ventas;

namespace PapeleriaPos.Servicio;

/// <summary>Por dónde va una restauración: cada paso queda escrito en el pedido antes de dar el siguiente.</summary>
public enum PasoDeRestauracion
{
    /// <summary>Se pidió: todavía no se toca nada.</summary>
    Pedida,

    /// <summary>El respaldo ya está copiado junto a la base (<c>pos.db.restaurando</c>) y pasó la revisión rápida.</summary>
    Copiada,

    /// <summary>La base de antes (con su -wal, su -shm y sus marcas) ya está en su carpeta <c>antes-de-restaurar-…</c>.</summary>
    Apartada,

    /// <summary>La copia ya es la base: falta migrarla, anotarlo en su bitácora y lo del puente con eleventa.</summary>
    Puesta,

    /// <summary>No se pudo (el respaldo no pasó la revisión, ya no estaba…): la base se quedó como estaba.</summary>
    Fallida,
}

/// <summary>
/// El pedido de restaurar un respaldo (<c>pos.db.restaurar</c>, junto a la base): qué respaldo, quién lo pidió y por dónde va.
/// Se escribe en el disco de una vez en cada paso, así un apagón a media restauración sigue donde se quedó al volver a arrancar.
/// </summary>
/// <param name="Carpeta">El nombre de la carpeta de la base de antes (<c>antes-de-restaurar-AAAA-MM-DD_HH.mm.ss</c>), junto a la base.</param>
/// <param name="PorElScript">
/// Lo dejó <c>restaurar.ps1</c> (el servicio ni arrancaba), ya puesto: sin usuario ni caja; aquí solo falta lo de después de migrar.
/// </param>
public sealed record PedidoDeRestauracion(
    Guid Id, string Archivo, TipoRespaldo Tipo, DateTime FechaUtc, Guid UsuarioId, Guid CajaId, Guid SucursalId, DateTime PedidaUtc,
    bool ReinicioAutomatico, PasoDeRestauracion Paso = PasoDeRestauracion.Pedida, string? Carpeta = null, DateTime? HechaUtc = null,
    string? Fallo = null, bool PorElScript = false);

/// <summary>
/// Restaurar un respaldo (Configuración › Respaldos, 2026-10-23; en eleventa, «restaurar»). La base no se cambia con el servicio
/// en marcha: se deja el pedido junto a la base y el servicio se detiene; Windows lo vuelve a arrancar
/// (<see cref="IReinicioDelServicio"/>) y, al arrancar, antes de revisar la base y de migrarla, se restaura
/// (<see cref="SiSePidio"/>):
/// <list type="number">
/// <item>el respaldo se copia junto a la base (<c>pos.db.restaurando</c>) y se le hace la revisión rápida; si no pasa, no se toca
/// nada;</item>
/// <item>la base de ahora, con su -wal, su -shm y sus marcas (la de la base dañada también), se mueve a una carpeta
/// <c>antes-de-restaurar-…</c> junto a ella: nunca se borra;</item>
/// <item>la copia pasa a ser la base. La marca de la base dañada se fue con la de antes: queda quitada solo porque la restaurada
/// pasó la revisión;</item>
/// <item>después se migra como siempre y, ya migrada (<see cref="DespuesDeMigrarAsync"/>), se anota en su bitácora y lo que el
/// puente con eleventa tenía pendiente queda como «ya se intentó».</item>
/// </list>
/// Todo va al registro. Cada paso queda escrito en el pedido: si se va la luz a la mitad, al volver sigue donde se quedó, sin
/// arrancar nunca con la base a medias. Al arrancar con la base restaurada las cajas bajan su copia completa: la base de la copia
/// (<see cref="VersionesDeLaCopia.Base"/>) es otra en cada arranque.
/// </summary>
public sealed class RestauracionDeRespaldos(
    string cadenaConexion, string carpetaRespaldos, IRevisionRapida revisionRapida, ILogger<RestauracionDeRespaldos> log, TimeProvider reloj)
{
    /// <summary>Lo que se mueve con la base a su carpeta: la base, su -wal, su -shm, su diario y las marcas contra apagones.</summary>
    private static readonly string[] LoDeLaBase = ["", "-wal", "-shm", "-journal", ".en-uso", ".danada"];

    private const string PrincipioCarpeta = "antes-de-restaurar-";

    /// <summary>Con los pasos y los tipos por su nombre: el pedido se lee en el Bloc de notas, y restaurar.ps1 escribe el suyo así.</summary>
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() },
    };

    private readonly Lock _candado = new();
    private PedidoDeRestauracion? _alArrancar;
    private RestauracionDto? _ultima;

    /// <summary>El archivo de la base; nada si está en memoria (entonces no hay qué restaurar).</summary>
    public string? Base { get; } = Archivo(cadenaConexion);

    /// <summary>El pedido (<c>pos.db.restaurar</c>), junto a la base.</summary>
    public string? Pedido => Base is null ? null : Base + ".restaurar";

    /// <summary>La copia del respaldo mientras se revisa (<c>pos.db.restaurando</c>).</summary>
    private string? Copia => Base is null ? null : Base + ".restaurando";

    /// <summary>Dónde se guardan los respaldos (los mismos de <see cref="ServicioRespaldos"/>).</summary>
    public string CarpetaRespaldos => carpetaRespaldos;

    /// <summary>
    /// La que se pidió y espera a que el servicio vuelva a arrancar, o la que se hizo (o no se pudo hacer) al arrancar esta vez:
    /// Configuración › Respaldos lo dice. Vacía si no hay ninguna.
    /// </summary>
    public RestauracionDto? Ultima
    {
        get
        {
            lock (_candado)
                return _ultima;
        }
    }

    private DateTime Ahora => reloj.GetUtcNow().UtcDateTime;

    private static string? Archivo(string cadenaConexion)
    {
        var datos = new SqliteConnectionStringBuilder(cadenaConexion);
        return datos.Mode == SqliteOpenMode.Memory || datos.DataSource is "" or ":memory:" ? null : Path.GetFullPath(datos.DataSource);
    }

    // ── La lista y el pedido (la API) ───────────────────────────────────────

    /// <summary>Los respaldos de la carpeta, el más reciente primero, con el de antes del apagón señalado (si la base está dañada).</summary>
    public IReadOnlyList<RespaldoGuardadoDto> Listar(string? deAntesDelApagon = null)
    {
        var suyo = deAntesDelApagon is null ? null : Path.GetFileName(deAntesDelApagon);
        return Respaldos.Listar(carpetaRespaldos).Select(r => r with { DeAntesDelApagon = r.Archivo == suyo }).ToList();
    }

    /// <summary>
    /// Deja el pedido de restaurar <paramref name="archivo"/> (que quien llama ya revisó quién lo pide): el respaldo tiene que estar
    /// en la carpeta y pasar la revisión rápida; si no, <see cref="InvalidOperationException"/> con el porqué y no se pide nada. Si
    /// ya había uno pedido, este lo reemplaza. El servicio lo hace al volver a arrancar (<see cref="SiSePidio"/>).
    /// </summary>
    public RestauracionDto Pedir(string archivo, Guid usuarioId, Guid cajaId, Guid sucursalId, bool reinicioAutomatico)
    {
        if (Pedido is not { } pedido)
            throw new InvalidOperationException("La base de este servicio no es un archivo: no hay qué restaurar.");
        var ruta = Respaldos.Ruta(carpetaRespaldos, archivo);
        var revision = revisionRapida.Revisar(CadenaDe(ruta));
        if (!BaseDeDatos.EstaSana(revision))
        {
            log.LogWarning("No se pidió restaurar {Respaldo}: no pasó la revisión rápida ({Revision}).", ruta, string.Join(" | ", revision));
            throw new InvalidOperationException($"El respaldo {archivo} no pasó la revisión rápida ({revision.FirstOrDefault()}): elige otro.");
        }
        var nuevo = new PedidoDeRestauracion(Guid.NewGuid(), archivo, Respaldos.TipoDe(archivo), File.GetLastWriteTimeUtc(ruta), usuarioId, cajaId,
            sucursalId, Ahora, reinicioAutomatico);
        lock (_candado)
        {
            Escribir(pedido, nuevo);
            _ultima = Dto(nuevo);
            log.LogWarning("Se pidió restaurar el respaldo {Respaldo} (usuario {Usuario}, caja {Caja}): {Cuando}.", ruta, usuarioId, cajaId,
                reinicioAutomatico ? "el servicio se detiene y Windows lo vuelve a arrancar" : "se restaura la próxima vez que arranque el servicio");
            return _ultima;
        }
    }

    /// <summary>
    /// Quita el pedido mientras el servicio no ha vuelto a arrancar (sin el reinicio automático: la caja del navegador). Devuelve el
    /// que se quitó; nada si no había.
    /// </summary>
    public RestauracionDto? Cancelar()
    {
        lock (_candado)
        {
            if (Pedido is not { } pedido || _ultima is not { Pendiente: true } pendiente || !File.Exists(pedido))
                return null;
            File.Delete(pedido);
            _ultima = null;
            log.LogWarning("Se canceló la restauración del respaldo {Respaldo} que se había pedido.", pendiente.Archivo);
            return pendiente;
        }
    }

    // ── Al arrancar ──────────────────────────────────────────────────────────

    /// <summary>
    /// Al arrancar, antes de revisar la base y de migrarla: si hay un pedido, restaura el respaldo (ver la clase). Si el respaldo no
    /// sirve (no está, no pasa la revisión rápida) no toca la base y queda dicho. Si no se puede apartar la base de antes o poner la
    /// copia (un archivo ocupado), el servicio no arranca: así nunca abre una base a medias, y al volver a arrancar sigue donde se
    /// quedó.
    /// </summary>
    public void SiSePidio()
    {
        if (Pedido is not { } archivoPedido || !File.Exists(archivoPedido))
            return;
        PedidoDeRestauracion pedido;
        try
        {
            pedido = JsonSerializer.Deserialize<PedidoDeRestauracion>(File.ReadAllText(archivoPedido), Json)
                ?? throw new JsonException("El pedido está vacío.");
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException or NotSupportedException)
        {
            // Mejor no restaurar que restaurar algo que no se entiende: se aparta y se dice.
            var apartado = $"{archivoPedido}.no-se-entendio-{HoraMexico.ALocal(Ahora):yyyy-MM-dd_HH.mm.ss}";
            log.LogError(ex, "El pedido de restaurar {Pedido} no se pudo leer: no se restaura nada y se aparta en {Apartado}.", archivoPedido, apartado);
            Mover(archivoPedido, apartado);
            return;
        }
        if (pedido.Paso is PasoDeRestauracion.Puesta or PasoDeRestauracion.Fallida)
        {
            // Ya se hizo (o no se pudo), o lo puso restaurar.ps1: lo de después de migrar lo termina DespuesDeMigrarAsync.
            if (pedido.PorElScript)
                log.LogWarning("restaurar.ps1 puso el respaldo {Archivo}; la base de antes quedó en {Carpeta}. Al arrancar se revisa la base.",
                    pedido.Archivo, CarpetaDe(pedido));
            Recordar(pedido);
            return;
        }
        var reloj = Stopwatch.StartNew();
        log.LogWarning("Se pidió restaurar el respaldo {Archivo} de {Cuando:dd/MM/yyyy HH:mm} ({Paso}): se restaura antes de abrir la base.",
            pedido.Archivo, HoraMexico.ALocal(pedido.FechaUtc), pedido.Paso);
        if (pedido.Paso == PasoDeRestauracion.Pedida)
        {
            try
            {
                CopiarYRevisar(pedido);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
            {
                // La base de ahora no se tocó: se sigue con ella, y la pantalla y la bitácora dicen por qué.
                pedido = pedido with { Paso = PasoDeRestauracion.Fallida, Fallo = ex.Message };
                Escribir(archivoPedido, pedido);
                Recordar(pedido);
                log.LogError(ex, "No se restauró el respaldo {Archivo}: {Fallo} La base de ahora se queda como estaba.", pedido.Archivo, ex.Message);
                return;
            }
            pedido = pedido with { Paso = PasoDeRestauracion.Copiada, Carpeta = pedido.Carpeta ?? CarpetaNueva() };
            Escribir(archivoPedido, pedido);
        }
        // Desde aquí, lo que falle detiene el arranque (no se abre una base a medias) y el siguiente sigue en el mismo paso.
        try
        {
            if (pedido.Paso == PasoDeRestauracion.Copiada)
            {
                ApartarLaDeAntes(pedido);
                pedido = pedido with { Paso = PasoDeRestauracion.Apartada };
                Escribir(archivoPedido, pedido);
            }
            if (pedido.Paso == PasoDeRestauracion.Apartada)
            {
                // Si la copia ya no está (se cortó justo antes), se vuelve a hacer y a revisar.
                if (!File.Exists(Copia))
                    CopiarYRevisar(pedido);
                Mover(Copia!, Base!);
                pedido = pedido with { Paso = PasoDeRestauracion.Puesta, HechaUtc = Ahora };
                Escribir(archivoPedido, pedido);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            log.LogCritical(ex, "No se pudo terminar de restaurar el respaldo {Archivo} (paso: {Paso}): el servicio no arranca para no abrir la base a "
                + "medias, y al volver a arrancar sigue donde se quedó. La base de antes está en {Carpeta}. Si sigue sin arrancar, en esta PC, como "
                + "administrador: restaurar.ps1.", pedido.Archivo, pedido.Paso, CarpetaDe(pedido));
            throw;
        }
        Recordar(pedido);
        log.LogWarning("Se restauró el respaldo {Archivo} en {Ms} ms: la base de antes (con su -wal, su -shm y sus marcas) quedó en {Carpeta}; "
            + "no se borra. Ahora se migra como siempre.", pedido.Archivo, reloj.ElapsedMilliseconds, CarpetaDe(pedido));
    }

    /// <summary>
    /// Ya migrada la base: si al arrancar se restauró un respaldo, se anota en su bitácora (con quien lo pidió, si está en esa base)
    /// y lo pendiente del puente con eleventa queda como «ya se intentó» (ver abajo); si no se pudo, se anota en la base de ahora.
    /// Después se quita el pedido. Lo que falle aquí queda en el registro y no detiene el arranque.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>El piloto con eleventa.</b> Restaurar la base del POS no toca eleventa: la bandeja de salida (<c>EnviosAEleventa</c>)
    /// regresa como estaba en el respaldo, y lo que el puente mandó después vuelve a estar pendiente (con sus intentos de entonces,
    /// muchas veces ninguno). No se manda dos veces: cada intento busca primero en eleventa, dentro de la misma transacción, la
    /// marca de lo suyo (la venta por <c>CAJA_ID</c> y <c>CREADO_EN</c>, la devolución por su ticket y <c>DEVUELTO_EN</c>, la
    /// entrada o salida por su caja, su tipo y <c>CUANDO_FUE</c>); si ya está, queda como enviado sin escribir nada. Pero hay
    /// cuatro lugares que toman «nunca se intentó» como «no llegó a eleventa»: cancelar aquí una venta que no se ha mandado (se
    /// descarta sin buscarla), «dejar por revisar» (pide anotarla a mano), la lectura de las existencias (la vuelve a restar) y la
    /// búsqueda de un folio de eleventa (la toma por ajena). Por eso aquí todo lo pendiente queda con al menos un intento: así esos
    /// cuatro también la buscan en eleventa antes. Lo que de verdad nunca llegó se manda en cuanto toca, como siempre.
    /// </para>
    /// </remarks>
    public async Task DespuesDeMigrarAsync(PosDbContext db, CancellationToken ct = default)
    {
        PedidoDeRestauracion? pedido;
        lock (_candado)
            pedido = _alArrancar;
        if (pedido is null || Pedido is not { } archivoPedido)
            return;
        try
        {
            if (pedido.Paso == PasoDeRestauracion.Puesta)
            {
                var marcados = await db.EnviosAEleventa.Where(e => e.Estado == EstadoEnvioEleventa.Pendiente && e.Intentos == 0)
                    .ExecuteUpdateAsync(s => s.SetProperty(e => e.Intentos, 1), ct);
                if (marcados > 0)
                    log.LogWarning("Lo pendiente del puente con eleventa que regresó con el respaldo ({Marcados}) quedó como «ya se intentó»: antes de "
                        + "mandarlo, descartarlo o dejarlo por revisar se busca en eleventa, por si ya había llegado.", marcados);
                await AnotarAsync(db, pedido, (pedido.PorElScript ? $"Con restaurar.ps1 (el servicio no arrancaba) se restauró {Descripcion(pedido)}"
                    : $"Restauró {Descripcion(pedido)}") + $". La base de antes quedó en {CarpetaDe(pedido)}", ct);
            }
            else if (pedido.Paso == PasoDeRestauracion.Fallida)
                await AnotarAsync(db, pedido, $"No se restauró {Descripcion(pedido)}: {pedido.Fallo}", ct);
        }
        catch (Exception ex) when (ex is SqliteException or DbUpdateException or InvalidOperationException)
        {
            log.LogError(ex, "No se pudo anotar la restauración de {Archivo} en la bitácora ni revisar lo del puente.", pedido.Archivo);
        }
        try
        {
            File.Delete(archivoPedido);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            log.LogError(ex, "No se pudo quitar el pedido {Pedido}: al volver a arrancar se anotaría otra vez.", archivoPedido);
        }
        lock (_candado)
            _alArrancar = null;
    }

    // ── Los pasos ────────────────────────────────────────────────────────────

    /// <summary>
    /// El respaldo, copiado junto a la base y escrito en el disco de una vez; después, su revisión rápida (la de la base que va a
    /// quedar). Si no pasa, se borra la copia (es solo eso: el respaldo se queda) y <see cref="InvalidOperationException"/>.
    /// </summary>
    private void CopiarYRevisar(PedidoDeRestauracion pedido)
    {
        var respaldo = Respaldos.Ruta(carpetaRespaldos, pedido.Archivo);
        var copia = Copia!;
        BorrarCopia();
        using (var origen = new FileStream(respaldo, FileMode.Open, FileAccess.Read, FileShare.Read))
        using (var destino = new FileStream(copia, FileMode.CreateNew, FileAccess.Write, FileShare.None))
        {
            origen.CopyTo(destino);
            destino.Flush(flushToDisk: true);
        }
        var revision = revisionRapida.Revisar(CadenaDe(copia));
        if (BaseDeDatos.EstaSana(revision))
        {
            log.LogInformation("El respaldo {Respaldo} se copió en {Copia} y pasó la revisión rápida.", respaldo, copia);
            return;
        }
        BorrarCopia();
        throw new InvalidOperationException($"El respaldo {pedido.Archivo} no pasó la revisión rápida ({revision.FirstOrDefault()}).");
    }

    /// <summary>
    /// La base de ahora y lo que la acompaña, a su carpeta (un cambio de nombre en el mismo disco: un momento). Lo que ya se movió en
    /// un intento anterior ya no está; lo que llegara a estar en la carpeta con el mismo nombre no se pisa.
    /// </summary>
    private void ApartarLaDeAntes(PedidoDeRestauracion pedido)
    {
        var carpeta = CarpetaDe(pedido);
        Directory.CreateDirectory(carpeta);
        foreach (var final in LoDeLaBase)
        {
            var origen = Base! + final;
            if (!File.Exists(origen))
                continue;
            var destino = Libre(Path.Combine(carpeta, Path.GetFileName(origen)));
            Mover(origen, destino);
            log.LogInformation("{Origen} → {Destino}", origen, destino);
        }
    }

    /// <summary>Cambia de nombre, con unos intentos: el antivirus puede tener abierto un momento un archivo recién copiado.</summary>
    private static void Mover(string origen, string destino)
    {
        for (var intento = 1; ; intento++)
        {
            try
            {
                File.Move(origen, destino);
                return;
            }
            catch (Exception ex) when ((ex is IOException or UnauthorizedAccessException) && intento < 5 && File.Exists(origen))
            {
                Thread.Sleep(TimeSpan.FromSeconds(1));
            }
        }
    }

    /// <summary>La ruta, o la misma con -2, -3… si ya hay algo con ese nombre: nunca se pisa nada.</summary>
    private static string Libre(string ruta)
    {
        if (!File.Exists(ruta) && !Directory.Exists(ruta))
            return ruta;
        for (var n = 2; ; n++)
        {
            var otra = $"{ruta}-{n}";
            if (!File.Exists(otra) && !Directory.Exists(otra))
                return otra;
        }
    }

    private void BorrarCopia()
    {
        foreach (var final in new[] { "", "-wal", "-shm", "-journal" })
        {
            if (File.Exists(Copia + final))
                File.Delete(Copia + final);
        }
    }

    /// <summary>La carpeta para la base de antes, junto a ella, con la hora: si ya hay una así, con -2, -3…</summary>
    private string CarpetaNueva() =>
        Path.GetFileName(Libre(Path.Combine(CarpetaDeLaBase, $"{PrincipioCarpeta}{HoraMexico.ALocal(Ahora):yyyy-MM-dd_HH.mm.ss}")));

    private string CarpetaDeLaBase => Path.GetDirectoryName(Base!) ?? ".";

    private string CarpetaDe(PedidoDeRestauracion pedido) => Path.Combine(CarpetaDeLaBase, pedido.Carpeta ?? "");

    private static string CadenaDe(string ruta) => new SqliteConnectionStringBuilder { DataSource = ruta, Pooling = false }.ToString();

    /// <summary>Escribe el pedido en el disco de una vez: aparte y con otro nombre al final, así nunca queda a medias.</summary>
    private static void Escribir(string ruta, PedidoDeRestauracion pedido)
    {
        var temporal = ruta + ".tmp";
        using (var archivo = new FileStream(temporal, FileMode.Create, FileAccess.Write, FileShare.None))
        {
            archivo.Write(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(pedido, Json)));
            archivo.Flush(flushToDisk: true);
        }
        File.Move(temporal, ruta, overwrite: true);
    }

    private void Recordar(PedidoDeRestauracion pedido)
    {
        lock (_candado)
        {
            _alArrancar = pedido;
            _ultima = Dto(pedido);
        }
    }

    private RestauracionDto Dto(PedidoDeRestauracion p) =>
        new(p.Archivo, p.Tipo, HoraMexico.ALocal(p.FechaUtc), HoraMexico.ALocal(p.PedidaUtc),
            Pendiente: p.Paso is not (PasoDeRestauracion.Puesta or PasoDeRestauracion.Fallida), p.ReinicioAutomatico,
            p.HechaUtc is { } hecha ? HoraMexico.ALocal(hecha) : null, p.Carpeta is null || p.Paso == PasoDeRestauracion.Fallida ? null : CarpetaDe(p),
            p.Fallo);

    private string Descripcion(PedidoDeRestauracion p) => Dto(p).Descripcion;

    /// <summary>
    /// A la bitácora de la base que quedó, una vez (con el Id del pedido): con quien lo pidió y su caja si están en esa base (un
    /// respaldo de antes de darlos de alta no los tiene). Lo de restaurar.ps1 no trae sucursal: va a la primera (la tienda tiene
    /// una). Sin sucursal, solo queda en el registro.
    /// </summary>
    private async Task AnotarAsync(PosDbContext db, PedidoDeRestauracion pedido, string detalle, CancellationToken ct)
    {
        if (await db.Bitacora.AnyAsync(b => b.Id == pedido.Id, ct))
            return;
        var sucursal = await db.Sucursales.AnyAsync(s => s.Id == pedido.SucursalId, ct) ? pedido.SucursalId
            : pedido.PorElScript ? await db.Sucursales.OrderBy(s => s.Nombre).Select(s => (Guid?)s.Id).FirstOrDefaultAsync(ct)
            : null;
        if (sucursal is not { } sucursalId)
        {
            log.LogWarning("La base no tiene la sucursal de quien pidió restaurar: no se anota en la bitácora. {Detalle}", detalle);
            return;
        }
        var usuario = await db.Usuarios.AnyAsync(u => u.Id == pedido.UsuarioId, ct) ? pedido.UsuarioId : (Guid?)null;
        var caja = await db.Cajas.AnyAsync(c => c.Id == pedido.CajaId, ct) ? pedido.CajaId : (Guid?)null;
        db.Bitacora.Add(new Bitacora
        {
            Id = pedido.Id, SucursalId = sucursalId, CajaId = caja, UsuarioId = usuario, Accion = AccionControlada.ConfigurarTienda,
            Detalle = detalle, Resultado = pedido.Paso == PasoDeRestauracion.Fallida ? ResultadoBitacora.Negado : ResultadoBitacora.Hecho,
            FechaUtc = pedido.HechaUtc ?? Ahora,
        });
        await db.SaveChangesAsync(ct);
    }
}

/// <summary>
/// El reinicio del servicio para restaurar un respaldo. Como servicio de Windows se detiene y Windows lo vuelve a arrancar (las
/// acciones de recuperación que pone <c>instalar-servicio.ps1</c>: a los 5 s, otra vez a los 5 s y luego al minuto). En la caja
/// del navegador (desarrollo) no hay quién lo vuelva a arrancar: la restauración espera a la próxima vez que se arranque.
/// </summary>
public interface IReinicioDelServicio
{
    /// <summary>Windows lo vuelve a arrancar solo (corre como servicio de Windows).</summary>
    bool Automatico { get; }

    /// <summary>Se detiene para que Windows lo vuelva a arrancar; nada si no es <see cref="Automatico"/>.</summary>
    void Reiniciar();
}

/// <summary>
/// El de verdad. Se detiene con calma (las cajas que esperaban terminan, el puente con eleventa deja de mandar, la base se cierra
/// y se quita la marca de que está en uso) y, al final, termina el proceso con el código 1 antes de decirle a Windows que se
/// detuvo (<see cref="AlDetenerse"/>): para Windows es una falla, y con sus acciones de recuperación lo vuelve a arrancar. Si se
/// detuviera como siempre, el servicio de Windows de .NET le avisaría «detenido» con el código 0 y Windows no haría nada.
/// </summary>
public sealed class ReinicioComoServicioDeWindows(IHostApplicationLifetime vida, ILogger<ReinicioComoServicioDeWindows> log) : IReinicioDelServicio
{
    private int _pedido;

    public bool Automatico { get; } = WindowsServiceHelpers.IsWindowsService();

    /// <summary>Se pidió y ya se está deteniendo para eso.</summary>
    public bool Pedido => Volatile.Read(ref _pedido) == 1;

    public void Reiniciar()
    {
        if (!Automatico || Interlocked.Exchange(ref _pedido, 1) == 1)
            return;
        log.LogWarning("El servicio se detiene para restaurar un respaldo: Windows lo vuelve a arrancar en unos segundos.");
        vida.StopApplication();
    }

    /// <summary>
    /// Cuando ya se detuvo todo (el último aviso del apagado, antes de que el servicio de Windows de .NET le diga a Windows que se
    /// detuvo): si se pidió el reinicio, lo que tiene que quedar (<paramref name="antes"/>: quitar la marca de que la base está en
    /// uso) y salir con 1.
    /// </summary>
    public void AlDetenerse(Action antes)
    {
        if (!Pedido)
            return;
        antes();
        SqliteConnection.ClearAllPools();
        log.LogWarning("Detenido para restaurar: sale con el código 1 para que Windows lo vuelva a arrancar.");
        Environment.Exit(1);
    }
}
