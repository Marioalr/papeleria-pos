using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using PapeleriaPos.Interfaz;

namespace PapeleriaPos.Herramientas.Simulador;

/// <summary>
/// El informe del día simulado, en Markdown: los tiempos contra las metas (docs/propuesta-interfaz.md, «Metas», y
/// docs/prueba-sucursales-linux.md, «Qué se mide y cuándo sale bien»), la memoria del servicio, el cuadre, lo que pasó y cómo
/// se corrió. Se escribe en la consola y, con <c>--salida</c>, en un archivo.
/// </summary>
public static class Informe
{
    private static readonly CultureInfo Mexico = CultureInfo.GetCultureInfo("es-MX");

    /// <summary>Una meta: de qué medición, qué dice y cuándo se cumple.</summary>
    /// <param name="Cumple">Vacío: no tiene meta propia (se mide para tener la referencia).</param>
    private sealed record Meta(string Que, string[] Claves, string Texto, Func<Medida, bool>? Cumple);

    /// <summary>Lo que espera quien atiende, contra sus metas («19 de 20»: el 95 %; la vez más lenta de cada 20 no cuenta).</summary>
    private static readonly Meta[] Metas =
    [
        new(Operacion.Escanear, [Operacion.Escanear], "La mitad ≤ 16 ms y el 99 % < 50 ms (propuesta)", m => m.Mitad <= 16 && m.P99 < 50),
        new(Operacion.CodigoALaPrincipal, [Operacion.CodigoALaPrincipal], "< 50 ms, 19 de 20 (que el servicio conteste)", m => m.P95 < 50),
        new(Operacion.Buscar, [Operacion.Buscar], "< 100 ms, 19 de 20 (propuesta; «Qué se mide»: < 300 ms)", m => m.P95 < 100),
        new(Operacion.CobrarEnLinea, [Operacion.CobrarEnLinea], "< 300 ms, 19 de 20 (propuesta; «Qué se mide»: < 500 ms)", m => m.P95 < 300),
        new(Operacion.CobrarSinConexion, [Operacion.CobrarSinConexion], "< 300 ms, 19 de 20", m => m.P95 < 300),
        new("El latido (api/estado)", ["GET api/estado"], "< 100 ms, 19 de 20 (la red «buena»; 300 ms dos veces la pone lenta)", m => m.P95 < 100),
        new(Operacion.PrecioEnLaCopia, [Operacion.PrecioEnLaCopia], "< 2 min (sincronización); con el aviso, segundos", m => m.Maximo < 120_000),
        new(Operacion.AlDiaTrasLaCaida, [Operacion.AlDiaTrasLaCaida], "< 2 min (ponerse al día tras 8 h sin conexión)", m => m.Maximo < 120_000),
        new(Operacion.AbrirCaja, [Operacion.AbrirCaja], "< 5 s (abrir la caja → lista para vender)", m => m.Maximo < 5_000),
        new("Lo que cambió desde la copia de la caja", ["GET api/cajas/{id}/cambios"], "Sin meta propia (de fondo)", null),
        new("La copia completa", ["GET api/cajas/{id}/paquete"], "Sin meta propia (casi nunca se baja)", null),
        new("Subir la bandeja, cada cosa", ["POST api/bandeja/ventas", "POST api/bandeja/bitacora", "POST api/bandeja/turnos/{id}/movimientos",
            "POST api/bandeja/turnos/{id}/corte-z", "POST api/bandeja/turnos"], "Sin meta propia (de fondo)", null),
    ];

    /// <summary>Lo demás que espera quien atiende: se mide, sin meta propia.</summary>
    private static readonly string[] LoDemas =
    [
        Operacion.PrecioCambiadoAlCobrar, Operacion.Movimiento, Operacion.BuscarFolio, Operacion.Devolver, Operacion.Cancelar, Operacion.Autorizar,
        Operacion.Quitar, Operacion.Efectivo, Operacion.RegistrarBusqueda, Operacion.AltaRapida, Operacion.CambioDePrecio, Operacion.Entrar,
        Operacion.AbrirTurno, Operacion.CorteInformativo, Operacion.CorteZ, Operacion.AltaDeLaCaja,
    ];

    public const long MemoriaMeta = 300L * 1024 * 1024;

    public static string Markdown(ResultadoSimulacion r, string? comando = null)
    {
        var o = r.Opciones;
        var m = r.Mediciones;
        var t = new StringBuilder();
        var cajas = r.Cajas;
        var tickets = cajas.Sum(c => c.Tickets);
        t.AppendLine($"# Día pico simulado · {o.Donde}");
        t.AppendLine();
        var caida = o.Caida is { } c0 && cajas.Count >= o.CajaDeLaCaida
            ? $", con la red de la {cajas[o.CajaDeLaCaida - 1].Nombre} caída {c0.Cuanto} desde el {c0.Inicio.ToString("P0", Mexico)} del día"
            : "";
        t.AppendLine($"{r.Inicio.ToString("dddd d 'de' MMMM 'de' yyyy, HH:mm", Mexico)}. {o.Cajas} {(o.Cajas == 1 ? "caja" : "cajas")} × {o.Tickets} tickets "
            + $"({tickets} en total) {(o.Minutos > 0 ? $"en {Numero(o.Minutos)} min" : "sin esperas")}{caida}. El servicio {r.VersionDelServicio} en {r.Servicio}"
            + (r.ServicioPropio ? ", arrancado por el simulador con una copia de la base de prueba." : "."));
        t.AppendLine();

        var conMeta = Metas.Where(x => x.Cumple is not null).Select(x => (Meta: x, Medida: m.De(x.Claves))).Where(x => x.Medida is not null).ToList();
        var cumplidas = conMeta.Count(x => x.Meta.Cumple!(x.Medida!));
        var memoriaCumple = r.MemoriaPico is { } pico ? pico < MemoriaMeta : (bool?)null;
        var errores = r.TodosLosErrores.ToList();
        t.Append(r.Cuadra ? "**Cuadra.** "
            : r.Cuadre.Count == 0 ? "**No se llegó al cuadre.** "
            : $"**NO CUADRA** ({r.Cuadre.Count(x => !x.Bien)} revisiones con diferencias). ");
        if (errores.Count > 0)
            t.Append($"**Hubo {errores.Count} {(errores.Count == 1 ? "error" : "errores")}** (abajo). ");
        t.AppendLine($"Tiempos: {cumplidas} de {conMeta.Count} metas"
            + (memoriaCumple is { } mc ? $"; memoria del servicio: {(mc ? "dentro" : "FUERA")} de la meta." : "."));
        t.AppendLine();

        // ── Tiempos ──
        t.AppendLine("## Tiempos contra las metas");
        t.AppendLine();
        t.AppendLine("Lo que espera quien atiende, sin la pantalla (lo que tarda en pintarse se mide en la caja de Windows, Configuración › Acerca de). "
            + "«19 de 20» es el 95 %: la vez más lenta de cada 20 no cuenta.");
        t.AppendLine();
        t.AppendLine("| Qué | Veces | La mitad | 19 de 20 | 99 % | La peor | Meta | ¿Cumple? |");
        t.AppendLine("|---|---:|---:|---:|---:|---:|---|:-:|");
        var primeras = false;
        foreach (var meta in Metas)
        {
            var medida = m.De(meta.Claves);
            var cumple = medida is null ? "No se dio" : meta.Cumple is null ? "—" : meta.Cumple(medida) ? "Sí" : "**No**";
            var primera = m.LaPeorFueLaPrimera(meta.Claves);
            primeras |= primera;
            t.AppendLine(Fila(meta.Que, medida, meta.Texto, cumple, primera));
        }
        if (primeras)
            t.AppendLine("\n¹ La peor fue la primera del día: lo que se usa por primera vez se compila (en el servicio, si se acaba de arrancar, y en cada caja).");
        t.AppendLine();
        var demas = LoDemas.Select(clave => (clave, Medida: m.De(clave))).Where(x => x.Medida is not null).ToList();
        if (demas.Count > 0)
        {
            t.AppendLine("Lo demás, sin meta propia:");
            t.AppendLine();
            t.AppendLine("| Qué | Veces | La mitad | 19 de 20 | 99 % | La peor |");
            t.AppendLine("|---|---:|---:|---:|---:|---:|");
            foreach (var (clave, medida) in demas)
                t.AppendLine($"| {clave} | {medida!.Veces} | {Ms(medida.Mitad)} | {Ms(medida.P95)} | {Ms(medida.P99)} | {Ms(medida.Maximo)} |");
            t.AppendLine();
        }

        // ── El servicio ──
        t.AppendLine("## El servicio");
        t.AppendLine();
        if (r.MemoriaAntes is null && r.MemoriaPico is null)
            t.AppendLine("Su memoria no se midió: el servicio no es un proceso de esta PC que conozca el simulador (con --servicio, dale su proceso con --pid).");
        else
        {
            t.AppendLine("| | Antes del día | Lo más alto | Al terminar | Meta |");
            t.AppendLine("|---|---:|---:|---:|---|");
            t.AppendLine($"| Memoria (lo que ocupa en la RAM) | {Mb(r.MemoriaAntes)} | {Mb(r.MemoriaPico)} | {Mb(r.MemoriaDespues)} | "
                + "< 300 MB y sin crecer de un día a otro: "
                + $"{(memoriaCumple == true ? "Sí" : memoriaCumple == false ? "**No**" : "—")} |");
        }
        if (r.ProcesadorDelServicio is { } cpu && r.DuracionDelDia is { } duracion && duracion > TimeSpan.Zero)
            t.AppendLine($"\nProcesador: el servicio usó {Segundos(cpu)} en los {Duracion(duracion)} del día "
                + $"({(cpu / duracion).ToString("P1", Mexico)} de un núcleo); "
                + $"el simulador, {Segundos(r.ProcesadorDelSimulador ?? TimeSpan.Zero)} (en la misma PC, se lo quita al servicio).");
        var copia = m.De("GET api/cajas/{id}/paquete");
        if (copia is not null)
            t.AppendLine($"\nLa copia completa: {r.ProductosEnLaCopia:N0} productos y {r.CodigosEnLaCopia:N0} códigos, "
                + $"{Kb(m.Bytes("GET api/cajas/{id}/paquete") / Math.Max(1, copia.Veces))} cada vez (se bajó {copia.Veces} {(copia.Veces == 1 ? "vez" : "veces")}: "
                + "una por caja al abrir y las que hicieron falta después). La base de cada caja al terminar: "
                + string.Join(", ", r.BaseLocal.OrderBy(x => x.Key).Select(x => $"{Kb(x.Value)} la {cajas.First(c => c.Numero == x.Key).Nombre}")) + ".");
        t.AppendLine();

        // ── Cuadre ──
        t.AppendLine("## Cuadre");
        t.AppendLine();
        if (r.Cuadre.Count == 0)
            t.AppendLine("No se llegó al cuadre (ver los errores).");
        foreach (var revision in r.Cuadre)
        {
            if (revision.Bien)
                t.AppendLine($"- **Sí.** {revision.Que}. {string.Join(" ", revision.Detalles)}");
            else
            {
                t.AppendLine($"- **NO.** {revision.Que}:");
                foreach (var detalle in revision.Detalles.Take(30))
                    t.AppendLine($"  - {detalle}");
                if (revision.Detalles.Count > 30)
                    t.AppendLine($"  - … y {revision.Detalles.Count - 30} más.");
            }
        }
        t.AppendLine();
        if (errores.Count > 0)
        {
            t.AppendLine("### Errores");
            t.AppendLine();
            foreach (var error in errores.Take(50))
                t.AppendLine($"- {error}");
            if (errores.Count > 50)
                t.AppendLine($"- … y {errores.Count - 50} más.");
            t.AppendLine();
        }

        // ── Lo que pasó ──
        t.AppendLine("## Lo que pasó");
        t.AppendLine();
        if (cajas.Count == 0)
            t.AppendLine("No se llegó a abrir ninguna caja.");
        else
            LoQuePaso(t, cajas);
        var notas = r.Notas.Concat(cajas.SelectMany(c => c.Notas)).ToList();
        if (notas.Count > 0)
        {
            t.AppendLine("Notas:");
            t.AppendLine();
            foreach (var nota in notas.Take(20))
                t.AppendLine($"- {nota}");
            if (notas.Count > 20)
                t.AppendLine($"- … y {notas.Count - 20} más.");
            t.AppendLine();
        }
        if (r.Sucesos.Count > 0)
        {
            t.AppendLine("Lo que pasó en el día (la hora es desde que empezó):");
            t.AppendLine();
            foreach (var (cuando, que) in r.Sucesos)
                t.AppendLine($"- {Hora(cuando)} {que}");
            t.AppendLine();
        }

        // ── Cada llamada ──
        var rutas = m.Claves.Where(k => k.StartsWith("GET ", StringComparison.Ordinal) || k.StartsWith("POST ", StringComparison.Ordinal)
                                        || k.StartsWith("PUT ", StringComparison.Ordinal))
            .Select(k => (Ruta: k, Medida: m.De(k), Fallas: m.Fallas(k)))
            .OrderByDescending(x => x.Medida?.Veces ?? 0).ThenBy(x => x.Ruta, StringComparer.Ordinal)
            .ToList();
        if (rutas.Count > 0)
        {
            t.AppendLine("## Cada llamada a la caja principal");
            t.AppendLine();
            t.AppendLine("De las cajas simuladas durante el día (también lo de fondo: el latido, el aviso, los cambios, la bandeja), hasta tener la respuesta "
                + "completa. Del aviso (SSE), lo que tarda en conectarse. Las fallas son las que no contestaron (la caída, un plazo vencido).");
            t.AppendLine();
            t.AppendLine("| Ruta | Veces | La mitad | 19 de 20 | 99 % | La peor | Pesa, en promedio | Fallas |");
            t.AppendLine("|---|---:|---:|---:|---:|---:|---:|---:|");
            foreach (var (ruta, medida, fallas) in rutas)
                t.AppendLine(medida is null
                    ? $"| `{ruta}` | 0 | | | | | | {fallas} |"
                    : $"| `{ruta}` | {medida.Veces} | {Ms(medida.Mitad)} | {Ms(medida.P95)} | {Ms(medida.P99)} | {Ms(medida.Maximo)} | "
                      + $"{Kb(m.Bytes(ruta) / Math.Max(1, medida.Veces))} | {(fallas == 0 ? "" : fallas.ToString(Mexico))} |");
            if (m.Rechazos.Count > 0)
                t.AppendLine("\nContestaron con un error (lo esperado: 404, un código que no existe; 403, lo que pide la autorización del encargado; "
                    + "409, un precio que cambió): "
                    + string.Join("; ", m.Rechazos.OrderBy(x => x.Key, StringComparer.Ordinal).Select(x => $"`{x.Key}` {x.Value}")) + ".");
            t.AppendLine();
        }

        // ── Cómo se corrió ──
        t.AppendLine("## Cómo se corrió");
        t.AppendLine();
        t.AppendLine($"- **La PC:** {LaPc()}.");
        t.AppendLine($"- **El servicio:** {r.Servicio}, versión {r.VersionDelServicio}" + (r.Base is { } b ? $"; la base: {b}." : "."));
        t.AppendLine($"- **Las cajas:** {string.Join(", ", cajas.Select(c => $"{c.Nombre} (serie {c.Caja.Serie})"))}; vende «{o.Cajero.Usuario}», cancela y corta "
            + $"«{o.Encargado.Usuario}» (con su PIN), las da de alta y cambia el precio «{o.Dueno.Usuario}». Fondo de {Formato.Dinero(o.FondoCentavos)}. "
            + "Cada caja con "
            + "su base local (caja-local.db) en una carpeta temporal, sus dos conexiones y los plazos de la caja (leer 800 ms, cobrar 2.5 s).");
        t.AppendLine("- **La red:** " + (r.Servicio.IsLoopback
            ? "la caja 1, como la de la misma PC del servicio (solo si contesta); las demás, como la caja 2 por Wi-Fi (buena, lenta o caída según el latido). "
            : "todas como la caja 2 por Wi-Fi (buena, lenta o caída según el latido). ")
            + "La caída es un interruptor en la red de esa caja: no se conecta, y lo que iba a medias (también el aviso) se corta.");
        t.AppendLine($"- **El día:** tickets de 1 a 8 renglones de lo más vendido (como la papelería ficticia), con su cantidad, algunos por nombre y los rápidos; "
            + "efectivo con billetes, tarjeta, transferencia y mixto; una entrada y dos salidas de efectivo por caja; cancelaciones y devoluciones (de otra caja "
            + $"si hay); al {o.CambioDePrecio.ToString("P0", Mexico)} del día el dueño cambia el precio de lo más vendido a media venta; al "
            + $"{o.ProductoNuevo.ToString("P0", Mexico)} la caja 1 da de alta un producto y la última lo vende; al final el corte de cada turno. "
            + $"Semilla {o.Semilla}: "
            + "con la misma base, el mismo día.");
        t.AppendLine($"- **Duración:** el día, {Duracion(r.DuracionDelDia ?? TimeSpan.Zero)}; "
            + $"todo (el alta, las copias, el corte y el cuadre), {Duracion(r.Total)}.");
        if (comando is not null)
            t.AppendLine($"- **Comando:** `{comando}`");
        return t.ToString();
    }

    /// <summary>Lo que hizo cada caja (y todas juntas), y su caída de la red si la hubo.</summary>
    private static void LoQuePaso(StringBuilder t, IReadOnlyList<CajaSimulada> cajas)
    {
        t.AppendLine($"| | {string.Join(" | ", cajas.Select(c => c.Nombre))} |{(cajas.Count > 1 ? " Todas |" : "")}");
        t.AppendLine($"|---|{string.Concat(cajas.Select(_ => "---:|"))}{(cajas.Count > 1 ? "---:|" : "")}");
        void Renglon(string que, Func<CajaSimulada, long> cuanto)
        {
            var valores = cajas.Select(cuanto).ToList();
            var todas = cajas.Count > 1 ? $" {valores.Sum().ToString("N0", Mexico)} |" : "";
            t.AppendLine($"| {que} | {string.Join(" | ", valores.Select(v => v.ToString("N0", Mexico)))} |{todas}");
        }
        Renglon("Tickets", c => c.Tickets);
        Renglon("Cobrados en línea", c => c.Ventas.Count(v => v.EnLinea));
        Renglon("Cobrados sin conexión (con la copia)", c => c.Ventas.Count(v => !v.EnLinea));
        Renglon("Renglones", c => c.Renglones);
        Renglon("Escaneos", c => c.Escaneos);
        Renglon("Búsquedas por nombre", c => c.Busquedas);
        Renglon("Rápidos (copias, CURP)", c => c.Rapidos);
        Renglon("Artículos comunes", c => c.ArticulosComunes);
        Renglon("Quitados del ticket (bitácora)", c => c.Quitados);
        Renglon("Cobros en que cambió un precio", c => c.PreciosCambiadosAlCobrar);
        Renglon("Cobros que no se supo si quedaron", c => c.CobrosSinConfirmar);
        Renglon("Cancelados", c => c.Devoluciones.Count(d => d.EsCancelacion));
        Renglon("Devoluciones hechas aquí", c => c.Devoluciones.Count(d => !d.EsCancelacion));
        Renglon("Entradas y salidas de efectivo", c => c.Movimientos.Count);
        Renglon("Retiros (el aviso de la caja)", c => c.Retiros);
        Renglon("Vendido (sin las canceladas), $", c => c.Ventas.Where(v => !v.Cancelada).Sum(v => v.TotalCentavos) / 100);
        Renglon("Veces que la red quedó lenta", c => c.CambiosDeRed.Count(x => x.Estado == EstadoRed.Lenta));
        Renglon("Veces que la red quedó caída", c => c.CambiosDeRed.Count(x => x.Estado == EstadoRed.Caida));
        t.AppendLine();
        foreach (var caja in cajas.Where(x => x.Caida is not null))
        {
            var cd = caja.Caida!;
            t.AppendLine($"**La caída de la red de la {caja.Nombre}:** se cayó a los {Hora(cd.Inicio)}"
                + (cd.LoNoto is { } noto
                    ? $"; la caja dejó de trabajar con la caja principal {Duracion(noto - cd.Inicio)} después (su primer cobro o latido sin respuesta)"
                    : "; la caja no lo notó")
                + (cd.Fin is { } fin ? $"; volvió a los {Hora(fin)}{(cd.TerminoConElDia ? " (con el fin de su día)" : "")}" : "")
                + (cd.AlDia is { } alDia && cd.Fin is { } f
                    ? $", y {Duracion(alDia - f)} después ya había subido su bandeja y trabajaba en línea"
                    : ", y no se puso al día")
                + $". Cobró sin conexión {caja.Ventas.Count(v => !v.EnLinea)} tickets en el día.");
            t.AppendLine();
        }
    }

    private static string Fila(string que, Medida? medida, string meta, string cumple, bool laPrimera) => medida is null
        ? $"| {que} | 0 | | | | | {meta} | {cumple} |"
        : $"| {que} | {medida.Veces} | {Ms(medida.Mitad)} | {Ms(medida.P95)} | {Ms(medida.P99)} | {Ms(medida.Maximo)}{(laPrimera ? " ¹" : "")} | {meta} | {cumple} |";

    /// <summary>«0.02 ms», «1.5 ms», «123 ms», «1.2 s», «1 min 5 s».</summary>
    public static string Ms(double ms) => ms switch
    {
        < 1 => $"{ms.ToString("0.00", Mexico)} ms",
        < 10 => $"{ms.ToString("0.0", Mexico)} ms",
        < 1_000 => $"{ms.ToString("0", Mexico)} ms",
        < 60_000 => $"{(ms / 1000).ToString("0.0", Mexico)} s",
        _ => $"{(int)(ms / 60_000)} min {(int)(ms % 60_000 / 1000)} s",
    };

    private static string Duracion(TimeSpan t) => Ms(t.TotalMilliseconds);

    private static string Segundos(TimeSpan t) => $"{t.TotalSeconds.ToString("0.0", Mexico)} s";

    private static string Hora(TimeSpan t) => $"{(int)t.TotalMinutes:00}:{t.Seconds:00}";

    private static string Numero(double n) => n.ToString("0.#", Mexico);

    private static string Mb(long? bytes) => bytes is { } b ? $"{(b / 1024d / 1024).ToString("0", Mexico)} MB" : "—";

    private static string Kb(long bytes) => bytes switch
    {
        0 => "",
        < 1024 => $"{bytes} B",
        < 1024 * 1024 => $"{(bytes / 1024d).ToString("0.#", Mexico)} KB",
        _ => $"{(bytes / 1024d / 1024).ToString("0.0", Mexico)} MB",
    };

    /// <summary>El procesador, los núcleos, la memoria, el sistema y .NET de esta PC.</summary>
    public static string LaPc()
    {
        string? procesador = null;
        try
        {
            if (OperatingSystem.IsLinux() && File.Exists("/proc/cpuinfo"))
                procesador = File.ReadLines("/proc/cpuinfo").FirstOrDefault(l => l.StartsWith("model name", StringComparison.Ordinal))?.Split(':', 2)[1].Trim();
            else if (OperatingSystem.IsWindows())
                procesador = Microsoft.Win32.Registry.GetValue(@"HKEY_LOCAL_MACHINE\HARDWARE\DESCRIPTION\System\CentralProcessor\0",
                                 "ProcessorNameString", null) as string
                             ?? Environment.GetEnvironmentVariable("PROCESSOR_IDENTIFIER");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            // Sin el nombre del procesador, lo demás.
        }
        var memoria = GC.GetGCMemoryInfo().TotalAvailableMemoryBytes / 1024d / 1024 / 1024;
        return $"{procesador?.Trim() ?? "procesador sin nombre"}, {Environment.ProcessorCount} núcleos, {memoria.ToString("0.#", Mexico)} GB, "
            + $"{RuntimeInformation.OSDescription.Trim()}, {RuntimeInformation.FrameworkDescription}";
    }
}
