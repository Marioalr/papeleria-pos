using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using PapeleriaPos.Impresiones.ControlPrint;
using PapeleriaPos.Nucleo;

namespace PapeleriaPos.Impresiones.Datos;

/// <summary>
/// <c>recopilar-control-print</c> (PapeleriaPos.Herramientas): junta los datos de Control Print para seguir con el módulo de
/// impresión, SOLO LEYENDO (con <see cref="ClienteControlPrint"/>). Deja en <see cref="OpcionesRecopilacion.Salida"/> una
/// carpeta con la fecha: <c>control-print.db</c> (los trabajos con todos sus campos, las copias, el resumen y las caras de
/// cada día, las cotizaciones, las lecturas de la impresora, la configuración y los usuarios), las miniaturas en PNG si se
/// piden y un README.md con lo que hay. Se puede detener (Ctrl+C) y volver a correr: sigue donde se quedó, en la misma
/// carpeta. Una recopilación que ya terminó ese día no se toca: la nueva va en «fecha-2».
/// </summary>
public sealed class RecopilacionControlPrint
{
    /// <summary>Trabajos por página: la cuarta parte de lo que pide Control Print (200), porque cada uno trae su desglose.</summary>
    public const int TrabajosPorPagina = 50;
    public const string NombreLeame = "README.md";
    private const int MaximoCotizaciones = 100_000;
    private static readonly DateTime PrimerMesPosible = new(2020, 1, 1, 6, 0, 0, DateTimeKind.Utc);
    private const string EnCurso = "en curso";
    private const string Terminada = "terminada";
    private static readonly CultureInfo Mx = CultureInfo.GetCultureInfo("es-MX");

    private readonly ClienteControlPrint _cliente;
    private readonly OpcionesRecopilacion _opciones;
    private readonly TextWriter _salida;
    private readonly TimeProvider _reloj;
    private readonly List<string> _avisos = [];
    private ArchivoRecopilacion? _archivo;
    private DateTime _hastaUtc;
    private bool _miniaturas;

    private RecopilacionControlPrint(ClienteControlPrint cliente, OpcionesRecopilacion opciones, TextWriter salida, TimeProvider reloj)
    {
        _cliente = cliente;
        _opciones = opciones;
        _salida = salida;
        _reloj = reloj;
    }

    /// <summary>La carpeta de esta recopilación, cuando ya se eligió.</summary>
    public string? Carpeta { get; private set; }

    /// <summary>Las esperas entre intentos cuando Control Print no contesta; después de la última, se detiene.</summary>
    internal TimeSpan[] Esperas { get; init; } = [TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(10)];

    internal int PorPagina { get; init; } = TrabajosPorPagina;

    /// <summary>
    /// Todo el comando: saluda (sin el código), pide el código y lo compara con la prueba del saludo, y recopila. Lo que dice
    /// va a <paramref name="salida"/>, y nunca el código. Devuelve 0 si terminó, 1 si no (lo recopilado se queda y se puede
    /// seguir) y 2 si ni empezó (falta el código).
    /// </summary>
    public static Task<int> EjecutarAsync(OpcionesRecopilacion opciones, CodigoDeConexion codigo, TextWriter salida, CancellationToken ct = default) =>
        EjecutarAsync(opciones, codigo, salida, TimeProvider.System, ct);

    internal static async Task<int> EjecutarAsync(OpcionesRecopilacion opciones, CodigoDeConexion codigo, TextWriter salida, TimeProvider reloj,
        CancellationToken ct, int porPagina = TrabajosPorPagina, TimeSpan[]? esperas = null)
    {
        salida.WriteLine($"Control Print en {opciones.Servidor}:{opciones.Puerto}. Solo se lee: no se cobra, no se cambia ni se borra nada.");
        ClienteControlPrint cliente;
        try
        {
            cliente = await ClienteControlPrint.SaludarAsync(
                new OpcionesClienteControlPrint(opciones.Servidor, opciones.Puerto) { Huella = opciones.Huella, Reloj = reloj }, ct);
        }
        catch (ErrorControlPrint ex)
        {
            salida.WriteLine(ex.Message);
            return 1;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            salida.WriteLine("Se detuvo antes de empezar: no se recopiló nada.");
            return 1;
        }
        using (cliente)
        {
            salida.WriteLine($"Contestó Control Print {cliente.VersionServidor} en {cliente.NombreServidor}. Su certificado (SHA-256): " +
                $"{ProtocoloControlPrint.HuellaLegible(cliente.Huella)}{(opciones.Huella is null ? "" : ", el de --huella")}.");
            var conCodigo = PedirCodigo(cliente, codigo, salida);
            if (conCodigo != 0)
                return conCodigo;
            salida.WriteLine("El código coincide: es el Control Print que lo conoce. Para detenerlo, Ctrl+C: lo recopilado se queda y, " +
                "al volver a correrlo hoy, sigue donde se quedó.");

            var recopilacion = new RecopilacionControlPrint(cliente, opciones, salida, reloj)
            {
                PorPagina = porPagina,
                Esperas = esperas ?? [TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(10)],
            };
            var cronometro = Stopwatch.StartNew();
            try
            {
                await recopilacion.RecopilarAsync(ct);
                salida.WriteLine($"Listo en {Duracion(cronometro.Elapsed)}: {recopilacion.Carpeta} (su {NombreLeame} dice qué hay).");
                return 0;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                recopilacion.DejarIncompleta("se detuvo a petición (Ctrl+C)");
                salida.WriteLine();
                salida.WriteLine(recopilacion.Carpeta is null
                    ? "Se detuvo: no se recopiló nada."
                    : $"Se detuvo. Lo recopilado se quedó en {recopilacion.Carpeta}; si lo vuelves a correr hoy con las mismas opciones, sigue donde se quedó.");
                return 1;
            }
            catch (ErrorControlPrint ex)
            {
                salida.WriteLine();
                salida.WriteLine(ex.Message);
                recopilacion.DejarIncompleta(ex.Message);
                if (recopilacion.Carpeta is not null)
                    salida.WriteLine($"Lo recopilado se quedó en {recopilacion.Carpeta}; al volver a correrlo hoy, sigue donde se quedó.");
                return 1;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or Microsoft.Data.Sqlite.SqliteException)
            {
                salida.WriteLine();
                salida.WriteLine($"No se pudo escribir en {recopilacion.Carpeta ?? opciones.Salida}: {ex.Message}");
                recopilacion.DejarIncompleta("no se pudo escribir en la carpeta: " + ex.Message);
                return 1;
            }
            finally
            {
                recopilacion.Cerrar();
            }
        }
    }

    private static int PedirCodigo(ClienteControlPrint cliente, CodigoDeConexion codigo, TextWriter salida)
    {
        if (codigo.SePuedeVolverAPedir)
            salida.WriteLine($"""

                Escribe el código de conexión de Control Print. Está en {cliente.NombreServidor}: Control Print → Ajustes → Red,
                «Código de conexión» (16 letras y números, como ABCD-EFGH-JKLM-NPQR; los guiones y las mayúsculas dan igual).
                No se ve al escribirlo, no se guarda en ningún lado y solo viaja a Control Print, cifrado.
                """);
        else if (codigo.DeDonde is { } deDonde)
            salida.WriteLine($"El código de conexión: {deDonde}.");
        for (var intento = 1; intento <= 3; intento++)
        {
            var texto = codigo.Leer();
            if (string.IsNullOrWhiteSpace(texto))
            {
                salida.WriteLine($"Falta el código de conexión: escríbelo cuando lo pida, o ponlo en la variable {OpcionesRecopilacion.VariableCodigo}.");
                return 2;
            }
            try
            {
                if (cliente.UsarCodigo(texto))
                    return 0;
                salida.WriteLine(cliente.MensajeCodigoNoCoincide);
            }
            catch (ErrorControlPrint ex) when (ex.Falla == FallaControlPrint.CodigoIncompleto)
            {
                salida.WriteLine(ex.Message);
            }
            if (!codigo.SePuedeVolverAPedir)
                return 1;
            if (intento < 3)
                salida.WriteLine("Escríbelo otra vez.");
        }
        return 1;
    }

    // ── La recopilación ──────────────────────────────────────────────────────────────────────────────────────────

    private async Task RecopilarAsync(CancellationToken ct)
    {
        AbrirCarpeta();
        var archivo = _archivo!;

        await ConfiguracionYUsuariosAsync(archivo, ct);
        var inicioUtc = await TrabajosAsync(archivo, ct);
        await DiasAsync(archivo, ct);

        var desdeCotizaciones = _opciones.Desde is null ? new DateTime(2000, 1, 1, 0, 0, 0, DateTimeKind.Utc) : inicioUtc ?? _hastaUtc;
        if (await LeerSiSePuedeAsync("las cotizaciones", c => _cliente.CotizacionesAsync(desdeCotizaciones, null, MaximoCotizaciones, c), ct)
            is { } todasLasCotizaciones)
        {
            var cotizaciones = todasLasCotizaciones.Where(c => c.CreadaUtc < _hastaUtc).ToList();
            archivo.GuardarCotizaciones(cotizaciones);
            if (todasLasCotizaciones.Count >= MaximoCotizaciones)
                Avisar($"Se pidieron hasta {MaximoCotizaciones.ToString("N0", Mx)} cotizaciones y llegaron todas esas: puede haber más viejas.");
            _salida.WriteLine($"Cotizaciones: {cotizaciones.Count.ToString("N0", Mx)}.");
        }

        DateTime? desdeLecturas = _opciones.Desde is null ? null : inicioUtc;
        if (await LeerSiSePuedeAsync("las lecturas de la impresora", c => _cliente.LecturasAsync(desdeLecturas, c), ct) is { } todasLasLecturas)
        {
            var lecturas = todasLasLecturas.Where(l => l.TomadaUtc < _hastaUtc).ToList();
            archivo.CambiarLecturas(desdeLecturas, lecturas);
            _salida.WriteLine($"Lecturas de la impresora: {lecturas.Count.ToString("N0", Mx)}.");
        }

        if (_miniaturas)
            await MiniaturasAsync(archivo, ct);

        archivo.GuardarValor("estado", Terminada);
        archivo.GuardarValor("terminada_utc", ArchivoRecopilacion.Fecha(_reloj.GetUtcNow().UtcDateTime));
        archivo.GuardarValor("motivo", null);
        EscribirLeame(null);
    }

    /// <summary>
    /// La carpeta de hoy (en hora de México): si tiene una recopilación de este mismo servidor que no terminó, sigue en ella;
    /// si no existe o está vacía, ahí empieza una; si no (una terminada u otra cosa), en «fecha-2», «fecha-3»…
    /// </summary>
    private void AbrirCarpeta()
    {
        var ahora = _reloj.GetUtcNow().UtcDateTime;
        var fecha = HoraMexico.ALocal(ahora).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        var desde = _opciones.Desde?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) ?? "";
        Directory.CreateDirectory(_opciones.Salida);
        for (var n = 1; ; n++)
        {
            var carpeta = Path.GetFullPath(Path.Combine(_opciones.Salida, n == 1 ? fecha : $"{fecha}-{n}"));
            if (!Directory.Exists(carpeta) || !Directory.EnumerateFileSystemEntries(carpeta).Any())
            {
                Directory.CreateDirectory(carpeta);
                Carpeta = carpeta;
                _archivo = ArchivoRecopilacion.Abrir(carpeta);
                _hastaUtc = ahora;
                _miniaturas = _opciones.Miniaturas;
                foreach (var (clave, valor) in new (string, string?)[]
                         {
                             ("estado", EnCurso), ("servidor", _opciones.Servidor), ("puerto", _opciones.Puerto.ToString(CultureInfo.InvariantCulture)),
                             ("nombre_servidor", _cliente.NombreServidor), ("version_control_print", _cliente.VersionServidor),
                             ("version_protocolo", ProtocoloControlPrint.Version.ToString(CultureInfo.InvariantCulture)), ("huella", _cliente.Huella),
                             ("desde", desde), ("hasta_utc", ArchivoRecopilacion.Fecha(ahora)), ("empezada_utc", ArchivoRecopilacion.Fecha(ahora)),
                             ("miniaturas", _miniaturas ? "si" : "no"), ("equipo", Environment.MachineName), ("version_pos", VersionDelPos()),
                         })
                    _archivo.GuardarValor(clave, valor);
                _salida.WriteLine($"Carpeta: {carpeta}");
                return;
            }
            var anterior = ArchivoRecopilacion.LeerRecopilacion(carpeta);
            if (anterior is not null && anterior.GetValueOrDefault("estado") == EnCurso
                && string.Equals(anterior.GetValueOrDefault("servidor"), _opciones.Servidor, StringComparison.OrdinalIgnoreCase)
                && anterior.GetValueOrDefault("puerto") == _opciones.Puerto.ToString(CultureInfo.InvariantCulture)
                && anterior.GetValueOrDefault("huella") == _cliente.Huella
                && anterior.GetValueOrDefault("desde") == desde
                && DateTime.TryParse(anterior.GetValueOrDefault("hasta_utc"), CultureInfo.InvariantCulture,
                    DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var hasta))
            {
                Carpeta = carpeta;
                _archivo = ArchivoRecopilacion.Abrir(carpeta);
                _hastaUtc = hasta;
                // Las miniaturas, si se pidieron alguna de las veces.
                _miniaturas = _opciones.Miniaturas || anterior.GetValueOrDefault("miniaturas") == "si";
                _archivo.GuardarValor("miniaturas", _miniaturas ? "si" : "no");
                if (anterior.GetValueOrDefault("avisos") is { Length: > 0 } avisos)
                    _avisos.AddRange(JsonSerializer.Deserialize<List<string>>(avisos) ?? []);
                _salida.WriteLine($"Carpeta: {carpeta} (sigue la recopilación que se detuvo; llega hasta las {HoraLocal(hasta)}, como la primera vez).");
                return;
            }
        }
    }

    private async Task ConfiguracionYUsuariosAsync(ArchivoRecopilacion archivo, CancellationToken ct)
    {
        var ajustes = await LeerSiSePuedeAsync("los ajustes", c => _cliente.ConfiguracionJsonAsync(ConfiguracionControlPrint.Ajustes, c), ct);
        var costos = await LeerSiSePuedeAsync("los costos", c => _cliente.ConfiguracionJsonAsync(ConfiguracionControlPrint.Costos, c), ct);
        var calibracion = await LeerSiSePuedeAsync("la calibración", _cliente.CalibracionJsonAsync, ct);
        var quitados = new List<string>();
        foreach (var (clave, json) in new[] { ("ajustes", ajustes), ("costos", costos), ("calibracion", calibracion) })
            if (json is not null)
                quitados.AddRange(archivo.GuardarConfiguracion(clave, json).Select(campo => $"{clave}.{campo}"));
        if (quitados.Count > 0)
            Avisar($"De la configuración se quitaron campos que parecen secretos: {string.Join(", ", quitados)}.");
        if (ajustes is not null && VisibilidadDe(ajustes) == "OwnOnly")
            Avisar("Control Print está en «cada quien ve sus trabajos» (Ajustes): sin sesión solo deja ver los del invitado («Sin sesión»), " +
                "y el POS no inicia sesión en Control Print. Los trabajos, el resumen de cada día y las cotizaciones de aquí son solo los del invitado.");

        var usuarios = await LeerSiSePuedeAsync("los usuarios", c => _cliente.UsuariosAsync(true, c), ct);
        if (usuarios is not null)
            archivo.CambiarUsuarios(usuarios);
        if (await LeerSiSePuedeAsync("cómo está el servidor", _cliente.SaludAsync, ct) is { } salud)
        {
            archivo.GuardarValor("salud_impresora", $"{(salud.ImpresoraBien ? "bien" : "con problema")}: {salud.Impresora}");
            archivo.GuardarValor("salud_monitor", $"{(salud.MonitorBien ? "bien" : "con problema")}: {salud.Monitor}");
        }
        if (await LeerSiSePuedeAsync("sus contadores de cambios", c => _cliente.CambiosAsync(VersionesControlPrint.Ninguna, TimeSpan.Zero, c), ct)
            is { } versiones)
            archivo.GuardarValor("versiones_de_cambios", JsonSerializer.Serialize(versiones));
        var leida = new[] { costos is null ? null : "costos", ajustes is null ? null : "ajustes", calibracion is null ? null : "calibración" }
            .OfType<string>().ToList();
        _salida.WriteLine($"Configuración ({(leida.Count == 0 ? "ninguna" : string.Join(", ", leida))}) y {usuarios?.Count ?? 0} usuarios: listo.");
        foreach (var aviso in _avisos)
            _salida.WriteLine($"Aviso: {aviso}");
    }

    /// <summary>Los trabajos, mes por mes (hora de México), de <see cref="PorPagina"/> en <see cref="PorPagina"/>. Devuelve desde cuándo.</summary>
    private async Task<DateTime?> TrabajosAsync(ArchivoRecopilacion archivo, CancellationToken ct)
    {
        DateTime? inicioUtc;
        if (_opciones.Desde is { } desde)
        {
            inicioUtc = HoraMexico.AUtc(desde.ToDateTime(TimeOnly.MinValue));
        }
        else if (archivo.Valor("inicio_utc") is { Length: > 0 } guardado)
        {
            inicioUtc = guardado == "-" ? null : DateTime.Parse(guardado, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal);
        }
        else
        {
            // El más viejo es el último por número; por si una PC mandó tarde trabajos más viejos, antes del primer mes va la
            // ventana «antes» (casi siempre vacía).
            var todos = await LeerAsync(c => _cliente.BuscarTrabajosAsync(new BusquedaTrabajosControlPrint { HastaUtc = _hastaUtc, Limite = 1 }, c), ct);
            if (todos.Total == 0)
            {
                inicioUtc = null;
            }
            else
            {
                var ultimo = await LeerAsync(c => _cliente.BuscarTrabajosAsync(
                    new BusquedaTrabajosControlPrint { HastaUtc = _hastaUtc, Limite = 1, Saltar = todos.Total - 1 }, c), ct);
                inicioUtc = ultimo.Trabajos.Count > 0 ? ultimo.Trabajos[0].DetectadoUtc : _hastaUtc;
                // Una fecha absurda no arma miles de meses vacíos: lo de antes cae en «antes».
                if (inicioUtc < PrimerMesPosible)
                    inicioUtc = PrimerMesPosible;
            }
            archivo.GuardarValor("inicio_utc", inicioUtc is { } i ? ArchivoRecopilacion.Fecha(i) : "-");
        }
        if (inicioUtc is null || inicioUtc >= _hastaUtc)
        {
            _salida.WriteLine($"Control Print no tiene trabajos {(_opciones.Desde is null ? "" : "desde ese día ")}(hasta las {HoraLocal(_hastaUtc)}).");
            return inicioUtc;
        }

        var ventanas = Ventanas(inicioUtc.Value).ToList();
        _salida.WriteLine($"Trabajos desde el {Dia(inicioUtc.Value)} hasta las {HoraLocal(_hastaUtc)}, por mes y de {PorPagina} en {PorPagina}:");
        var terminadas = archivo.VentanasTerminadas();
        foreach (var (nombre, desdeUtc, hastaUtc) in ventanas)
        {
            if (terminadas.Contains(nombre))
                continue;
            if (nombre != "antes")
                _salida.Write($"  {nombre} ");
            var total = await PaginarAsync(archivo, desdeUtc, hastaUtc, ct);
            if (archivo.ContarTrabajos(desdeUtc, hastaUtc) < total)
                // Algo se movió mientras se leía (un trabajo que llegó tarde o se borró): otra vuelta, que no repite nada.
                total = await PaginarAsync(archivo, desdeUtc, hastaUtc, ct);
            var guardados = archivo.ContarTrabajos(desdeUtc, hastaUtc);
            if (guardados < total)
                Avisar($"En {nombre}, Control Print dijo {total.ToString("N0", Mx)} trabajos y se guardaron {guardados.ToString("N0", Mx)} (cambió algo mientras se leía).");
            archivo.TerminarVentana(nombre, desdeUtc, hastaUtc, guardados, _reloj.GetUtcNow().UtcDateTime);
            if (nombre != "antes")
                _salida.WriteLine($"{guardados.ToString("N0", Mx),8}");
            else if (guardados > 0)
                _salida.WriteLine($"  antes del {Dia(desdeUtc ?? hastaUtc)}: {guardados.ToString("N0", Mx)}");
        }
        _salida.WriteLine($"  {archivo.Contar("SELECT COUNT(*) FROM trabajos").ToString("N0", Mx)} trabajos.");
        return inicioUtc;
    }

    /// <summary>Del mes del inicio al de <see cref="_hastaUtc"/>; sin --desde, antes va «antes» (todo lo anterior).</summary>
    private IEnumerable<(string Nombre, DateTime? DesdeUtc, DateTime HastaUtc)> Ventanas(DateTime inicioUtc)
    {
        var local = HoraMexico.ALocal(inicioUtc);
        var mes = new DateTime(local.Year, local.Month, 1);
        if (_opciones.Desde is null)
            yield return ("antes", null, HoraMexico.AUtc(mes));
        // Con --desde, el primer mes empieza ese día.
        var desde = _opciones.Desde is null ? HoraMexico.AUtc(mes) : inicioUtc;
        while (desde < _hastaUtc)
        {
            var siguiente = HoraMexico.AUtc(mes.AddMonths(1));
            var hasta = siguiente < _hastaUtc ? siguiente : _hastaUtc;
            yield return (mes.ToString("yyyy-MM", CultureInfo.InvariantCulture), desde, hasta);
            desde = hasta;
            mes = mes.AddMonths(1);
        }
    }

    private async Task<int> PaginarAsync(ArchivoRecopilacion archivo, DateTime? desdeUtc, DateTime hastaUtc, CancellationToken ct)
    {
        var saltar = 0;
        var paginas = 0;
        int total;
        do
        {
            var busqueda = new BusquedaTrabajosControlPrint { DesdeUtc = desdeUtc, HastaUtc = hastaUtc, Limite = PorPagina, Saltar = saltar };
            var pagina = await LeerAsync(c => _cliente.BuscarTrabajosAsync(busqueda, c), ct);
            total = pagina.Total;
            if (pagina.Trabajos.Count == 0)
                break;
            archivo.GuardarTrabajos(pagina.Trabajos);
            saltar += pagina.Trabajos.Count;
            if (++paginas % 20 == 0)
                _salida.Write('.');
        }
        while (saltar < total);
        return total;
    }

    /// <summary>El resumen y las caras que da Control Print de cada día con trabajos: para comparar las cuentas del módulo con las suyas.</summary>
    private async Task DiasAsync(ArchivoRecopilacion archivo, CancellationToken ct)
    {
        var dias = archivo.DiasSinResumen();
        if (dias.Count == 0)
            return;
        _salida.WriteLine($"Resumen y caras de cada día ({dias.Count.ToString("N0", Mx)} días)…");
        var cronometro = Stopwatch.StartNew();
        var fallas = new FallasSeguidas("el resumen o las caras", "día", "días");
        for (var i = 0; i < dias.Count && !fallas.Basta; i++)
        {
            var dia = dias[i];
            var desde = HoraMexico.AUtc(dia.ToDateTime(TimeOnly.MinValue));
            var hasta = HoraMexico.AUtc(dia.AddDays(1).ToDateTime(TimeOnly.MinValue));
            try
            {
                var resumen = await LeerAsync(c => _cliente.ResumenDelDiaAsync(dia, null, c), ct, reintentarErrores: false);
                var caras = await LeerAsync(c => _cliente.CarasAsync(desde, hasta, c), ct, reintentarErrores: false);
                archivo.GuardarDia(dia, resumen, caras);
                fallas.Bien();
            }
            catch (ErrorControlPrint ex) when (SeSalta(ex))
            {
                fallas.Mal(ex);
            }
            if (cronometro.Elapsed > TimeSpan.FromSeconds(10))
            {
                _salida.WriteLine($"  {(i + 1).ToString("N0", Mx)} de {dias.Count.ToString("N0", Mx)}");
                cronometro.Restart();
            }
        }
        if (fallas.Aviso() is { } aviso)
            Avisar(aviso);
        _salida.WriteLine("  listo.");
    }

    private async Task MiniaturasAsync(ArchivoRecopilacion archivo, CancellationToken ct)
    {
        var pendientes = archivo.TrabajosSinMiniatura();
        var carpeta = Path.Combine(Carpeta!, "miniaturas");
        Directory.CreateDirectory(carpeta);
        foreach (var temporal in Directory.EnumerateFiles(carpeta, "*.tmp"))
            File.Delete(temporal);
        if (pendientes.Count == 0)
            return;
        _salida.WriteLine($"Miniaturas: {pendientes.Count.ToString("N0", Mx)} trabajos (una petición por trabajo)…");
        var lote = new List<(long, string)>();
        var cronometro = Stopwatch.StartNew();
        var fallas = new FallasSeguidas("la miniatura", "trabajo", "trabajos");
        for (var i = 0; i < pendientes.Count && !fallas.Basta; i++)
        {
            var id = pendientes[i];
            byte[]? png;
            try
            {
                png = await LeerAsync(c => _cliente.MiniaturaAsync(id, c), ct, reintentarErrores: false);
                fallas.Bien();
            }
            catch (ErrorControlPrint ex) when (SeSalta(ex))
            {
                // Se queda sin pedir (vacía en «miniatura»): otra recopilación la vuelve a pedir.
                fallas.Mal(ex);
                continue;
            }
            var ruta = "";
            if (png is { Length: > 0 })
            {
                // Las de Control Print son PNG; si alguna no lo fuera, se guarda tal cual con otra extensión.
                var nombre = $"{id}{(EsPng(png) ? ".png" : ".bin")}";
                var destino = Path.Combine(carpeta, nombre);
                await File.WriteAllBytesAsync(destino + ".tmp", png, ct);
                File.Move(destino + ".tmp", destino, overwrite: true);
                ruta = "miniaturas/" + nombre;
            }
            lote.Add((id, ruta));
            if (lote.Count == 50)
            {
                archivo.GuardarMiniaturas(lote);
                lote.Clear();
            }
            if (cronometro.Elapsed > TimeSpan.FromSeconds(10))
            {
                _salida.WriteLine($"  {(i + 1).ToString("N0", Mx)} de {pendientes.Count.ToString("N0", Mx)}");
                cronometro.Restart();
            }
        }
        archivo.GuardarMiniaturas(lote);
        if (fallas.Aviso() is { } aviso)
            Avisar(aviso);
        _salida.WriteLine("  listo.");
    }

    private static bool EsPng(byte[] datos) =>
        datos.Length > 8 && datos.AsSpan(0, 8).SequenceEqual(new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A });

    /// <summary>
    /// Una lectura con reintentos: si Control Print no contesta (o, con <paramref name="reintentarErrores"/>, contesta con un
    /// error, como una base ocupada), espera y vuelve a intentar (<see cref="Esperas"/>); después de la última, el error (lo
    /// recopilado se queda). Lo demás (el código rechazado, otro certificado) no se reintenta.
    /// </summary>
    private async Task<T> LeerAsync<T>(Func<CancellationToken, Task<T>> leer, CancellationToken ct, bool reintentarErrores = true)
    {
        for (var intento = 0; ; intento++)
        {
            try
            {
                return await leer(ct);
            }
            catch (ErrorControlPrint ex) when (intento < Esperas.Length
                                               && (ex.Falla == FallaControlPrint.SinConexion || (reintentarErrores && ex.Falla == FallaControlPrint.Servidor)))
            {
                _salida.WriteLine();
                _salida.WriteLine(ex.Falla == FallaControlPrint.SinConexion
                    ? $"  Control Print no contestó; otro intento en {Esperas[intento].TotalSeconds.ToString("N0", Mx)} s…"
                    : $"  {ex.Message} Otro intento en {Esperas[intento].TotalSeconds.ToString("N0", Mx)} s…");
                await Task.Delay(Esperas[intento], ct);
                _cliente.ReintentarYa();
            }
        }
    }

    /// <summary>
    /// Lo que no son los trabajos: si Control Print contesta con un error aun después de reintentar, o pide una sesión, se
    /// sigue sin eso y queda el aviso (si fallara siempre, la recopilación no podría terminar nunca). Sin conexión, se detiene.
    /// </summary>
    private async Task<T?> LeerSiSePuedeAsync<T>(string que, Func<CancellationToken, Task<T>> leer, CancellationToken ct) where T : class?
    {
        try
        {
            return await LeerAsync(leer, ct);
        }
        catch (ErrorControlPrint ex) when (SeSalta(ex))
        {
            Avisar($"No se pudo leer {que}: {ex.Message}");
            _salida.WriteLine($"Aviso: no se pudo leer {que} ({ex.Message}); se sigue sin eso.");
            return null;
        }
    }

    /// <summary>Un error de Control Print con lo que no son los trabajos, que se puede saltar. Sin conexión, el código o el certificado, no.</summary>
    private static bool SeSalta(ErrorControlPrint ex) =>
        ex.Falla is FallaControlPrint.Servidor or FallaControlPrint.SinPermiso or FallaControlPrint.NoExiste;

    /// <summary>
    /// Las fallas de una cosa por día o por trabajo: cada una se salta, y después de <see cref="Maximo"/> seguidas se deja de
    /// pedir (Control Print no la da: tardaría horas en fallar una por una).
    /// </summary>
    private sealed class FallasSeguidas(string que, string uno, string varios)
    {
        private const int Maximo = 20;
        private int _seguidas;
        private int _total;
        private string? _primera;

        public bool Basta => _seguidas >= Maximo;

        public void Bien() => _seguidas = 0;

        public void Mal(ErrorControlPrint ex)
        {
            _seguidas++;
            _total++;
            _primera ??= ex.Message;
        }

        public string? Aviso() => _total == 0 ? null : Basta
            ? $"Control Print no dio {que} de {Maximo} {varios} seguidos y se dejó de pedir ({_primera}). Lo que falta se pide en otra recopilación."
            : $"Control Print no dio {que} de {_total.ToString("N0", Mx)} {(_total == 1 ? uno : varios)} ({_primera}): se quedaron sin eso.";
    }

    private void Avisar(string aviso)
    {
        if (_avisos.Contains(aviso))
            return;
        _avisos.Add(aviso);
        _archivo?.GuardarValor("avisos", JsonSerializer.Serialize(_avisos));
    }

    private void DejarIncompleta(string motivo)
    {
        if (_archivo is null)
            return;
        try
        {
            _archivo.GuardarValor("motivo", motivo);
            EscribirLeame(motivo);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or Microsoft.Data.Sqlite.SqliteException)
        {
            _salida.WriteLine($"No se pudo escribir el {NombreLeame}: {ex.Message}");
        }
    }

    private void Cerrar()
    {
        _archivo?.Dispose();
        _archivo = null;
    }

    // ── El README ────────────────────────────────────────────────────────────────────────────────────────────────

    private void EscribirLeame(string? motivoIncompleta)
    {
        var a = _archivo!;
        string N(long n) => n.ToString("N0", Mx);
        string Lista(IEnumerable<(string Nombre, long Cuantos)> cuentas) =>
            string.Join(" · ", cuentas.Select(c => $"{(c.Nombre.Length == 0 ? "(sin)" : c.Nombre)} {N(c.Cuantos)}"));
        var trabajos = a.Contar("SELECT COUNT(*) FROM trabajos");
        var copias = a.Contar("SELECT COUNT(*) FROM copias");
        var (primerDia, ultimoDia) = a.RangoDeDias();
        var empezada = Leida(a.Valor("empezada_utc"));
        var terminada = Leida(a.Valor("terminada_utc"));
        var conMiniatura = a.Contar("SELECT COUNT(*) FROM trabajos WHERE miniatura LIKE 'miniaturas/%'");
        var sinMiniatura = a.Contar("SELECT COUNT(*) FROM trabajos WHERE miniatura = ''");
        var faltanMiniaturas = a.Contar("SELECT COUNT(*) FROM trabajos WHERE miniatura IS NULL");
        var desconocidos = a.CamposDesconocidos();

        var texto = new StringBuilder();
        texto.AppendLine($"# Datos de Control Print, {(empezada is { } e ? HoraMexico.ALocal(e).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) : "")}");
        texto.AppendLine();
        texto.AppendLine($"Recopilados con `recopilar-control-print` (Papelería POS {a.Valor("version_pos")}) desde {a.Valor("equipo")}, **solo leyendo**: no se");
        texto.AppendLine("cobró, cambió ni borró nada en Control Print. No llevan el código de conexión, contraseñas ni sesiones.");
        texto.AppendLine();
        texto.AppendLine($"- **Servidor:** {a.Valor("nombre_servidor")} ({a.Valor("servidor")}:{a.Valor("puerto")}), Control Print {a.Valor("version_control_print")}, " +
                         $"protocolo {a.Valor("version_protocolo")}.");
        texto.AppendLine($"- **Su certificado (SHA-256):** {ProtocoloControlPrint.HuellaLegible(a.Valor("huella") ?? "")}, comprobado con el código de conexión.");
        texto.AppendLine(motivoIncompleta is null
            ? $"- **Completa:** empezó a las {HoraLocal(empezada)} y terminó a las {HoraLocal(terminada)} (hora de México)."
            : $"- **Incompleta:** {motivoIncompleta.TrimEnd().TrimEnd('.')}. Empezó a las {HoraLocal(empezada)}. Para terminarla, vuelve a correr " +
              "`recopilar-control-print` hoy con las mismas opciones: sigue donde se quedó.");
        texto.AppendLine($"- **Rango:** los trabajos detectados {(a.Valor("desde") is { Length: > 0 } desde ? $"desde el {desde} (`--desde`)" : "desde el primero")} " +
                         $"hasta las {HoraLocal(_hastaUtc)} (hora de México)" +
                         (primerDia is null ? ": no hay ninguno." : $"; hay del {primerDia} al {ultimoDia}."));
        texto.AppendLine();
        texto.AppendLine("## Qué hay");
        texto.AppendLine();
        texto.AppendLine("| Qué | Cuántos | Dónde |");
        texto.AppendLine("|---|---|---|");
        texto.AppendLine($"| Trabajos | {N(trabajos)} | `{ArchivoRecopilacion.NombreArchivo}`, tabla `trabajos`: todos los campos que manda Control Print |");
        texto.AppendLine($"| · impresiones de las PC | {N(trabajos - copias)} | `tipo = 'Impresion'` |");
        texto.AppendLine($"| · copias del panel de la impresora | {N(copias)} | la vista `copias` |");
        texto.AppendLine($"| · por estado | {Lista(a.ContarPor("estado"))} | `estado` |");
        texto.AppendLine($"| · por cobro | {Lista(a.ContarPor("cobro"))} | `cobro`, con `motivo_no_vendida` y `nota_no_vendida` |");
        texto.AppendLine($"| Días con resumen y caras | {N(a.Contar("SELECT COUNT(*) FROM resumen_por_dia"))} | tablas `resumen_por_dia` (el resumen del día de Control Print) y `caras_por_dia` (para el contador) |");
        texto.AppendLine($"| Cotizaciones | {N(a.Contar("SELECT COUNT(*) FROM cotizaciones"))} | tabla `cotizaciones` |");
        texto.AppendLine($"| Lecturas de la impresora | {N(a.Contar("SELECT COUNT(*) FROM lecturas_impresora"))} | tabla `lecturas_impresora`: tinta, caja de mantenimiento y contadores |");
        texto.AppendLine($"| Usuarios | {N(a.Contar("SELECT COUNT(*) FROM usuarios"))} | tabla `usuarios`: número, nombre, perfil y si está activo, nada más |");
        texto.AppendLine($"| Configuración | {string.Join(", ", a.ContarPor("clave", "configuracion").Select(c => c.Nombre))} | tabla `configuracion`: el JSON de Control Print |");
        texto.AppendLine(_miniaturas
            ? $"| Miniaturas | {N(conMiniatura)} ({N(sinMiniatura)} trabajos sin miniatura{(faltanMiniaturas > 0 ? $", {N(faltanMiniaturas)} sin pedir todavía" : "")}) | carpeta `miniaturas`, `<número de trabajo>.png`; columna `miniatura` |"
            : "| Miniaturas | no se pidieron | con `--miniaturas` |");
        texto.AppendLine();
        texto.AppendLine("## Cómo leerla");
        texto.AppendLine();
        texto.AppendLine("- Las fechas `*_utc` van en UTC (ISO 8601); `dia` y `hora_local`, en hora de México.");
        texto.AppendLine("- El dinero va en pesos, como en Control Print: `precio` es el calculado, `precio_puesto` el que se puso (a mano, especial, solo");
        texto.AppendLine("  lo que salió, cotizado o el de las copias) y `precio_a_cobrar` lo que se cobra.");
        texto.AppendLine("- `desglose_json` es el desglose de Control Print: los costos por canal, los ajustes con que salió y la tinta de cada página.");
        texto.AppendLine("- `otros_json`: lo que mandó Control Print y esta versión del POS no conoce. El nombre de cada columna en Control Print está en");
        texto.AppendLine("  `src/Impresiones/PapeleriaPos.Impresiones/ControlPrint/ContratosControlPrint.cs`.");
        texto.AppendLine("- Por ejemplo: `sqlite3 control-print.db \"SELECT dia, COUNT(*), SUM(precio_a_cobrar) FROM trabajos WHERE cobro = 'Cobrado' GROUP BY dia\"`");
        var avisos = _avisos.ToList();
        if (desconocidos.Count > 0)
            avisos.Add($"Control Print mandó campos que esta versión del POS no conoce (en `otros_json`): {string.Join(", ", desconocidos)}.");
        if (avisos.Count > 0)
        {
            texto.AppendLine();
            texto.AppendLine("## Avisos");
            texto.AppendLine();
            foreach (var aviso in avisos)
                texto.AppendLine($"- {aviso}");
        }
        var ruta = Path.Combine(Carpeta!, NombreLeame);
        File.WriteAllText(ruta + ".tmp", texto.ToString(), new UTF8Encoding(false));
        File.Move(ruta + ".tmp", ruta, overwrite: true);
    }

    // ── Formatos ─────────────────────────────────────────────────────────────────────────────────────────────────

    private static DateTime? Leida(string? fecha) =>
        DateTime.TryParse(fecha, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var f) ? f : null;

    private static string HoraLocal(DateTime? utc) =>
        utc is { } f ? HoraMexico.ALocal(f).ToString("HH:mm:ss 'del' yyyy-MM-dd", CultureInfo.InvariantCulture) : "(sin hora)";

    private static string Dia(DateTime utc) => ArchivoRecopilacion.Dia(utc);

    private static string Duracion(TimeSpan duracion) =>
        duracion.TotalMinutes >= 1 ? $"{(int)duracion.TotalMinutes} min {duracion.Seconds} s" : $"{Math.Max(1, (int)Math.Round(duracion.TotalSeconds))} s";

    private static string VersionDelPos() =>
        (typeof(RecopilacionControlPrint).Assembly.GetCustomAttributes(typeof(System.Reflection.AssemblyInformationalVersionAttribute), false)
            .OfType<System.Reflection.AssemblyInformationalVersionAttribute>().FirstOrDefault()?.InformationalVersion ?? "0.0.0").Split('+')[0];

    /// <summary>De los ajustes de Control Print, quién ve los trabajos («All» u «OwnOnly»).</summary>
    private static string? VisibilidadDe(string ajustes)
    {
        try
        {
            using var documento = JsonDocument.Parse(ajustes);
            return documento.RootElement.ValueKind == JsonValueKind.Object
                   && documento.RootElement.TryGetProperty("JobsVisibility", out var visibilidad)
                ? visibilidad.ValueKind == JsonValueKind.Number ? (visibilidad.GetInt32() == 1 ? "OwnOnly" : "All") : visibilidad.GetString()
                : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
