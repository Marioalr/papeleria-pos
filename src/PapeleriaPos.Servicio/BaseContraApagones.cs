using System.Diagnostics;
using System.Text;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using PapeleriaPos.Datos;
using PapeleriaPos.Nucleo;
using PapeleriaPos.Nucleo.Telegram;
using PapeleriaPos.Servicio.Telegram;

namespace PapeleriaPos.Servicio;

/// <summary>La revisión rápida de la base (<see cref="BaseDeDatos.RevisionRapida"/>). Se cambia en las pruebas para simular una base dañada.</summary>
public interface IRevisionRapida
{
    IReadOnlyList<string> Revisar(string cadenaConexion);
}

public sealed class RevisionRapidaSqlite : IRevisionRapida
{
    public IReadOnlyList<string> Revisar(string cadenaConexion) => BaseDeDatos.RevisionRapida(cadenaConexion);
}

/// <summary>
/// La revisión completa de un respaldo antes de restaurarlo (<see cref="BaseDeDatos.RevisionCompleta"/>, revisión del PR #136). Se
/// cambia en las pruebas para simular un respaldo dañado.
/// </summary>
public interface IRevisionCompleta
{
    IReadOnlyList<string> Revisar(string cadenaConexion);
}

public sealed class RevisionCompletaSqlite : IRevisionCompleta
{
    public IReadOnlyList<string> Revisar(string cadenaConexion) => BaseDeDatos.RevisionCompleta(cadenaConexion);
}

/// <summary>
/// El espacio libre en el disco de una carpeta, para no empezar a restaurar un respaldo que no cabe (revisión del PR #136). Se
/// cambia en las pruebas para simular un disco casi lleno.
/// </summary>
public interface IEspacioEnDisco
{
    /// <summary>Los bytes libres en el disco de <paramref name="carpeta"/>; vacío si Windows no lo dice (una carpeta de la red).</summary>
    long? Libre(string carpeta);
}

public sealed class EspacioEnDisco : IEspacioEnDisco
{
    public long? Libre(string carpeta)
    {
        try
        {
            return new DriveInfo(Path.GetFullPath(carpeta)).AvailableFreeSpace;
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }
}

/// <summary>
/// La base contra apagones (entrega 10; en la tienda no hay no-break). Al arrancar, el servicio deja una marca junto a la
/// base y la quita al detenerse bien. Si al arrancar la marca sigue ahí, la vez anterior se apagó de golpe (un apagón,
/// la PC apagada del botón, el proceso cerrado a la fuerza) y, antes de seguir escribiendo, se hace la revisión rápida de
/// SQLite. Si no sale bien, queda en el registro y en el estado del servicio (la caja lo muestra en la franja), se avisa
/// al dueño por Telegram y se dejan de hacer respaldos (<see cref="ServicioRespaldos"/>): así se usa el de antes del
/// apagón antes de seguir encima de una base dañada. La venta no se detiene; lo decide el dueño. La base dañada deja su
/// propia marca, que solo se quita cuando otra revisión sale bien: mientras tanto, cada arranque vuelve a revisar y a
/// avisar, aunque el servicio se haya detenido bien (la tienda apaga las PC cada noche).
/// </summary>
/// <param name="telegram">Solo si Telegram está activo: para avisar directo cuando la base ni siquiera se puede migrar.</param>
public sealed class RevisionAlArrancar(
    string cadenaConexion, string carpetaRespaldos, IRevisionRapida revisionRapida, ILogger<RevisionAlArrancar> log, ITelegram? telegram = null)
{
    /// <summary>Cómo empieza lo que dice el estado del servicio (y la franja de la caja) mientras la base esté dañada.</summary>
    public const string AvisoBaseDanada = "La base no pasó la revisión después de apagarse de golpe";

    /// <summary>Lo más que se espera a Telegram cuando la base ni se puede migrar (el servicio no arranca de todos modos).</summary>
    private static readonly TimeSpan EsperaTelegram = TimeSpan.FromSeconds(20);

    /// <summary>El archivo de la base; nada si está en memoria.</summary>
    private readonly string? _base = Archivo(cadenaConexion);

    /// <summary>La marca de que el servicio está corriendo (<c>pos.db.en-uso</c>); nada si la base está en memoria.</summary>
    public string? Marca => _base is null ? null : _base + ".en-uso";

    /// <summary>
    /// La marca de la base dañada (<c>pos.db.danada</c>): se deja cuando la revisión no sale bien y solo se quita cuando
    /// otra sale bien (después de restaurar el respaldo). Nada si la base está en memoria.
    /// </summary>
    public string? MarcaDanada => _base is null ? null : _base + ".danada";

    public DateTime ArrancoUtc { get; private set; }

    /// <summary>La vez anterior el servicio no se detuvo bien: quedó la marca.</summary>
    public bool SeApagoDeGolpe { get; private set; }

    /// <summary>Al arrancar seguía la marca de la base dañada: un arranque anterior ya la encontró así.</summary>
    public bool SeguiaDanada { get; private set; }

    /// <summary>Lo que dijo la revisión rápida; nada si no hizo falta revisar.</summary>
    public IReadOnlyList<string>? Revision { get; private set; }

    /// <summary>
    /// El respaldo más reciente al revisar: el de antes del apagón, porque mientras la base esté dañada no se hacen otros.
    /// Nada si no hizo falta revisar o si no hay ninguno.
    /// </summary>
    public string? Respaldo { get; private set; }

    public bool BaseDanada => Revision is { } r && !BaseDeDatos.EstaSana(r);

    /// <summary>Para el estado del servicio: nada si todo está bien.</summary>
    public string? Aviso => !BaseDanada ? null
        : Respaldo is { } respaldo
            ? $"{AvisoBaseDanada}: avisa al dueño, hay que restaurar el respaldo de antes del apagón ({Path.GetFileName(respaldo)}) en Configuración › Respaldos."
            : $"{AvisoBaseDanada} y no hay respaldo: avisa al dueño.";

    private static string? Archivo(string cadenaConexion)
    {
        var datos = new SqliteConnectionStringBuilder(cadenaConexion);
        return datos.Mode == SqliteOpenMode.Memory || datos.DataSource is "" or ":memory:" ? null : Path.GetFullPath(datos.DataSource);
    }

    /// <summary>
    /// Al arrancar, antes de migrar: si quedó la marca (o la de la base dañada), revisa la base. Después deja la marca de
    /// este arranque, escrita en el disco de una vez (si se va la luz un segundo después, ya está).
    /// </summary>
    public void Revisar()
    {
        ArrancoUtc = DateTime.UtcNow;
        if (Marca is not { } marca || MarcaDanada is not { } marcaDanada)
            return;
        SeApagoDeGolpe = File.Exists(marca);
        SeguiaDanada = File.Exists(marcaDanada);
        if (SeApagoDeGolpe || SeguiaDanada)
        {
            if (SeApagoDeGolpe)
                log.LogWarning("El servicio no se detuvo bien la vez anterior ({Anterior}): se revisa la base antes de seguir.", Leer(marca));
            else
                log.LogWarning("La base no había pasado la revisión ({Anterior}): se vuelve a revisar.", Leer(marcaDanada));
            Respaldo = UltimoRespaldo();
            var reloj = Stopwatch.StartNew();
            Revision = revisionRapida.Revisar(cadenaConexion);
            if (BaseDanada)
            {
                log.LogError("La base no pasó la revisión rápida (PRAGMA quick_check, {Ms} ms). Hay que restaurar el respaldo de antes del apagón ({Respaldo}); "
                    + "mientras tanto no se hacen respaldos. Encontró: {Revision}", reloj.ElapsedMilliseconds, NombreRespaldo(), string.Join(" | ", Revision));
                // La primera vez; si ya estaba, se queda como está (con la fecha y los avisos que lleva).
                if (!SeguiaDanada)
                    Escribir(marcaDanada, $"no pasó la revisión el {ArrancoUtc:O}: {Revision.FirstOrDefault()}",
                        "si el servicio se detiene, al volver a arrancar no se sabrá que la base sigue dañada");
            }
            else
            {
                log.LogInformation("La base pasó la revisión rápida en {Ms} ms.", reloj.ElapsedMilliseconds);
                if (SeguiaDanada && Quitar(marcaDanada))
                    log.LogInformation("La base ya pasó la revisión (se restauró el respaldo): se quitó la marca de la base dañada.");
            }
        }
        Escribir(marca, $"arrancó {ArrancoUtc:O}, proceso {Environment.ProcessId}", "después de un apagón no se sabrá que hay que revisar la base");
    }

    /// <summary>
    /// Se detuvo bien (al apagar Windows también): todo lo guardado ya está en el disco, se quita la marca. La de la base
    /// dañada se queda: el siguiente arranque vuelve a revisar.
    /// </summary>
    public void Detenido()
    {
        if (Marca is { } marca)
            Quitar(marca);
    }

    /// <summary>
    /// Después de <see cref="Revisar"/>: migra la base (si se cortó a media migración, antes quita el candado que quedó
    /// puesto) y, si salió dañada, deja el aviso para el dueño en la cola de Telegram, uno al día mientras siga así (si ni
    /// eso se puede guardar, queda en el registro); al final lee de vuelta los ajustes de la conexión
    /// (<c>synchronous=FULL</c>). Si está tan dañada que ni se puede migrar, el servicio no arranca: lo dice el registro
    /// (el de eventos de Windows), se avisa directo por Telegram si se alcanzan a leer los chats, y las cajas venden sin
    /// conexión hasta que se restaure el respaldo.
    /// </summary>
    public async Task PrepararAsync(PosDbContext db, CancellationToken ct = default)
    {
        if (SeApagoDeGolpe)
        {
            try
            {
                if (BaseDeDatos.QuitarCandadoDeMigraciones(cadenaConexion))
                    log.LogWarning("Se quitó el candado de las migraciones que dejó el arranque anterior a medias: si no, esta esperaría para siempre.");
            }
            catch (SqliteException ex)
            {
                log.LogWarning(ex, "No se pudo revisar el candado de las migraciones.");
            }
        }
        try
        {
            await BaseDeDatos.PrepararAsync(db, ct);
        }
        catch (SqliteException ex) when (BaseDanada)
        {
            log.LogCritical(ex, "La base está tan dañada que el servicio no puede arrancar. Hay que restaurar el respaldo de antes del apagón ({Respaldo}).",
                NombreRespaldo());
            await AvisarSinColaAsync(db, ct);
            throw;
        }
        if (BaseDanada)
        {
            try
            {
                var chats = AlertasTelegram.Chats(db);
                if (chats.Count == 0)
                    log.LogWarning("No hay ningún chat de Telegram vinculado: el aviso de la base dañada solo queda aquí y en la caja.");
                else if (AlertasTelegram.Agregar(db, chats, TipoAlerta.BaseDanada, TextoAlerta(arranca: true), $"base-danada:{Hoy}"))
                    await db.SaveChangesAsync(ct);
            }
            catch (Exception ex) when (ex is SqliteException or DbUpdateException or InvalidOperationException)
            {
                log.LogError(ex, "No se pudo dejar el aviso de la base dañada en la cola de Telegram.");
            }
        }
        if (await BaseDeDatos.RevisarAjustesAsync(db, ct) is { } problema)
            log.LogError("La base no quedó con los ajustes contra apagones: {Problema}", problema);
    }

    /// <summary>La fecha de este arranque en la tienda: el aviso por Telegram sale una vez al día mientras la base siga dañada.</summary>
    private string Hoy => $"{HoraMexico.ALocal(ArrancoUtc):yyyy-MM-dd}";

    /// <summary>
    /// Si la base ni se puede migrar, la cola de Telegram no sirve: vive en esa base y el servicio no va a arrancar. Se manda
    /// directo a los chats que se alcancen a leer, una vez al día (queda anotado en la marca de la base dañada): Windows
    /// vuelve a arrancar el servicio cada minuto y, recién vuelta la luz, puede no haber internet todavía; entonces se
    /// intenta en el siguiente arranque.
    /// </summary>
    private async Task AvisarSinColaAsync(PosDbContext db, CancellationToken ct)
    {
        if (telegram is not { Configurado: true } || MarcaDanada is not { } marcaDanada)
            return;
        var avisado = $"avisado por Telegram el {Hoy}";
        if (Leer(marcaDanada).Contains(avisado, StringComparison.Ordinal))
            return;
        List<long> chats;
        try
        {
            chats = AlertasTelegram.Chats(db);
        }
        catch (Exception ex) when (ex is SqliteException or InvalidOperationException)
        {
            log.LogWarning(ex, "Tampoco se pudieron leer los chats de Telegram de la base: el aviso solo queda aquí.");
            return;
        }
        if (chats.Count == 0)
        {
            log.LogWarning("No hay ningún chat de Telegram vinculado: el aviso de la base dañada solo queda aquí.");
            return;
        }
        using var limite = CancellationTokenSource.CreateLinkedTokenSource(ct);
        limite.CancelAfter(EsperaTelegram);
        var texto = TextoAlerta(arranca: false);
        var enviados = 0;
        foreach (var chat in chats)
        {
            try
            {
                await telegram.EnviarAsync(chat, texto, null, limite.Token);
                enviados++;
            }
            catch (Exception ex) when (ex is TelegramException or OperationCanceledException)
            {
                log.LogWarning(ex, "No se pudo avisar por Telegram al chat {Chat} (¿todavía sin internet?): se intenta en el siguiente arranque.", chat);
            }
        }
        if (enviados > 0)
        {
            try
            {
                File.AppendAllText(marcaDanada, Environment.NewLine + avisado);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                log.LogWarning(ex, "No se pudo anotar el aviso en {Marca}: en el siguiente arranque se vuelve a mandar.", marcaDanada);
            }
        }
    }

    /// <param name="arranca">El servicio sigue (con la base dañada) o no puede arrancar.</param>
    private string TextoAlerta(bool arranca)
    {
        var texto = new StringBuilder(AlertasTelegram.Titulo(TipoAlerta.BaseDanada)).AppendLine();
        texto.AppendLine(SeguiaDanada && !SeApagoDeGolpe
            ? "La base sigue sin pasar la revisión rápida al arrancar el servicio (todavía no se restaura el respaldo). Encontró:"
            : "El servicio se apagó de golpe (un apagón o un cierre forzado) y, al volver a arrancar, la revisión rápida de la base encontró:");
        foreach (var renglon in Revision!.Take(3))
            texto.AppendLine($"«{renglon}»");
        if (!arranca)
            texto.AppendLine("Está tan dañada que el servicio no puede arrancar: las cajas venden sin conexión hasta que se restaure.");
        texto.AppendLine(Respaldo is { } respaldo
            ? $"Hay que restaurar el respaldo de antes del apagón: {Path.GetFileName(respaldo)} ({HoraMexico.ALocal(File.GetLastWriteTimeUtc(respaldo)):dd/MM/yyyy HH:mm}), en {carpetaRespaldos}."
                + (arranca ? " Mientras tanto no se hacen respaldos nuevos: saldrían dañados." : "")
            : $"Hay que restaurar un respaldo, pero no hay ninguno en {carpetaRespaldos}.");
        // Las dos formas (2026-10-23): la pantalla, con el servicio en marcha, y restaurar.ps1 si ni arranca. Las dos apartan la
        // base dañada con su -wal y su -shm (SQLite aplicaría ese -wal encima del respaldo) y no la borran.
        var nombre = Path.GetFileName(_base);
        texto.Append(arranca
            ? "Para restaurarlo: en una caja, Configuración › Respaldos › «Restaurar un respaldo»; la caja principal se reinicia sola. "
            : "Para restaurarlo: en la caja principal, como administrador, restaurar.ps1 (en C:\\Program Files\\PapeleriaPos\\Servicio). ");
        texto.Append($"La base dañada ({nombre}, con su -wal y su -shm) se aparta en una carpeta, por si hay que rescatar algo. "
            + "Al arrancar se revisa otra vez y, si ya está bien, se quita el aviso.");
        return texto.ToString();
    }

    private string? UltimoRespaldo()
    {
        try
        {
            return Respaldos.Ultima(carpetaRespaldos);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            log.LogWarning(ex, "No se pudo buscar el último respaldo en {Carpeta}.", carpetaRespaldos);
            return null;
        }
    }

    private string NombreRespaldo() => Respaldo ?? $"no hay ninguno en {carpetaRespaldos}";

    /// <summary>Escribe la marca en el disco de una vez; si no se puede, lo dice el registro con <paramref name="siNo"/>.</summary>
    private void Escribir(string ruta, string contenido, string siNo)
    {
        try
        {
            using var archivo = new FileStream(ruta, FileMode.Create, FileAccess.Write, FileShare.Read);
            archivo.Write(Encoding.UTF8.GetBytes(contenido));
            archivo.Flush(flushToDisk: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            log.LogWarning(ex, "No se pudo dejar la marca {Marca}: {SiNo}.", ruta, siNo);
        }
    }

    private bool Quitar(string ruta)
    {
        try
        {
            File.Delete(ruta);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            log.LogWarning(ex, "No se pudo quitar la marca {Marca}: al arrancar se revisará la base sin hacer falta.", ruta);
            return false;
        }
    }

    private static string Leer(string marca)
    {
        try
        {
            return File.ReadAllText(marca);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return marca;
        }
    }
}
