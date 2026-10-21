using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using PapeleriaPos.Datos;
using PapeleriaPos.Herramientas.Ficticios;
using PapeleriaPos.Herramientas.Importacion;
using PapeleriaPos.Herramientas.Prueba;
using PapeleriaPos.Nucleo;
using PapeleriaPos.Nucleo.Catalogo;

namespace PapeleriaPos.Herramientas.Simulador;

/// <summary>
/// Un servicio propio para el simulador (<c>--iniciar-servicio</c>): el ejecutable del servicio de siempre, en un puerto libre
/// de esta PC, solo para ella (127.0.0.1), con su base en una carpeta temporal. Sin Telegram, sin eleventa, sin buscar cajas en
/// la red ni leer la impresora; con sus respaldos (también el de cada corte Z), en esa carpeta. Se detiene bien al terminar
/// (se le cierra la entrada, como la caja de la prueba) y lo que escribió queda en <c>servicio.log</c>.
/// </summary>
public sealed class ServicioLocal : IAsyncDisposable
{
    private readonly StreamWriter _registro;

    private ServicioLocal(Process proceso, Uri url, StreamWriter registro, string rutaRegistro)
    {
        (Proceso, Url, _registro, Registro) = (proceso, url, registro, rutaRegistro);
    }

    public Process Proceso { get; }
    public Uri Url { get; }

    /// <summary>Dónde quedó lo que escribió el servicio.</summary>
    public string Registro { get; }

    /// <param name="ejecutable">El servicio: PapeleriaPos.Servicio.exe (o .dll, que se corre con dotnet).</param>
    /// <param name="baseDeDatos">La base que usa (ya lista: una copia de la de prueba).</param>
    /// <param name="nucleos">
    /// Para parecerse a una PC más chica (la caja principal de la tienda) en una con muchos núcleos: el servicio solo usa esos (su
    /// afinidad) y .NET cree que son todos los que hay (DOTNET_PROCESSOR_COUNT; el recolector de memoria del servidor arma un
    /// montón por núcleo). Cada núcleo sigue siendo el de esta PC. Vacío: los de esta PC.
    /// </param>
    public static async Task<ServicioLocal> IniciarAsync(string ejecutable, string baseDeDatos, string carpeta, int? nucleos = null, CancellationToken ct = default)
    {
        var ruta = Path.GetFullPath(ejecutable);
        if (!File.Exists(ruta))
            throw new InvalidOperationException($"No existe el servicio «{ruta}»: compílalo primero (dotnet build src/PapeleriaPos.Servicio -c Release).");
        var puerto = PuertoLibre();
        var url = new Uri($"http://127.0.0.1:{puerto}/");
        var dll = ruta.EndsWith(".dll", StringComparison.OrdinalIgnoreCase);
        var info = new ProcessStartInfo(dll ? "dotnet" : ruta)
        {
            UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = Path.GetDirectoryName(ruta)!,
            RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8,
        };
        if (dll)
            info.ArgumentList.Add(ruta);
        foreach (var argumento in new[]
                 {
                     $"--Urls=http://127.0.0.1:{puerto}", $"--Caja:Servicio=http://127.0.0.1:{puerto}",
                     $"--ConnectionStrings:Pos=Data Source={Path.GetFullPath(baseDeDatos)}",
                     // Dice que es de prueba (api/estado) y se detiene bien al cerrarle la entrada.
                     "--Prueba:Activa=true", "--Prueba:DetenerAlCerrarEntrada=true",
                     // Nada hacia afuera: ni Telegram, ni eleventa, ni la red de la tienda, ni la impresora.
                     "--Telegram:Activo=false", "--Eleventa:Activo=false", "--Eleventa:Escribir=false", "--Descubrimiento:Activo=false",
                     "--Impresora:Vigilar=false", $"--Respaldos:Carpeta={Path.Combine(Path.GetFullPath(carpeta), "respaldos")}",
                 })
            info.ArgumentList.Add(argumento);
        info.Environment["ASPNETCORE_ENVIRONMENT"] = "Production";
        if (nucleos is { } cuantos)
            info.Environment["DOTNET_PROCESSOR_COUNT"] = cuantos.ToString(System.Globalization.CultureInfo.InvariantCulture);
        var rutaRegistro = Path.Combine(carpeta, "servicio.log");
        var registro = new StreamWriter(rutaRegistro, append: false, new UTF8Encoding(false)) { AutoFlush = true };
        var proceso = new Process { StartInfo = info };
        proceso.OutputDataReceived += (_, e) => Anotar(registro, e.Data);
        proceso.ErrorDataReceived += (_, e) => Anotar(registro, e.Data is null ? null : $"(error) {e.Data}");
        if (!proceso.Start())
            throw new InvalidOperationException("No se pudo arrancar el servicio.");
        proceso.BeginOutputReadLine();
        proceso.BeginErrorReadLine();
        var servicio = new ServicioLocal(proceso, url, registro, rutaRegistro);
        if (nucleos is { } usados && (OperatingSystem.IsWindows() || OperatingSystem.IsLinux()))
        {
            try
            {
                proceso.ProcessorAffinity = (nint)((1L << Math.Min(usados, Environment.ProcessorCount)) - 1);
            }
            catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or PlatformNotSupportedException or InvalidOperationException)
            {
                // Sin la afinidad, al menos .NET cree que tiene esos núcleos.
            }
        }

        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(2) };
        var limite = Stopwatch.StartNew();
        while (true)
        {
            if (proceso.HasExited)
            {
                await servicio.DisposeAsync();
                throw new InvalidOperationException($"El servicio se cerró al arrancar (código {proceso.ExitCode}); lo que escribió está en {rutaRegistro}.");
            }
            try
            {
                using var respuesta = await http.GetAsync(new Uri(url, "api/estado"), ct);
                if (respuesta.IsSuccessStatusCode)
                    return servicio;
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException && !ct.IsCancellationRequested)
            {
                // Todavía no contesta.
            }
            if (limite.Elapsed > TimeSpan.FromMinutes(2))
            {
                await servicio.DisposeAsync();
                throw new InvalidOperationException($"El servicio no contestó en 2 minutos; lo que escribió está en {rutaRegistro}.");
            }
            await Task.Delay(250, ct);
        }
    }

    private static void Anotar(StreamWriter registro, string? linea)
    {
        if (linea is null)
            return;
        lock (registro)
            registro.WriteLine(linea);
    }

    private static int PuertoLibre()
    {
        var escucha = new TcpListener(IPAddress.Loopback, 0);
        escucha.Start();
        try
        {
            return ((IPEndPoint)escucha.LocalEndpoint).Port;
        }
        finally
        {
            escucha.Stop();
        }
    }

    /// <summary>Le cierra la entrada para que se detenga bien (quita la marca de la base contra apagones); si no, a la fuerza.</summary>
    public async ValueTask DisposeAsync()
    {
        try
        {
            if (!Proceso.HasExited)
            {
                try
                {
                    Proceso.StandardInput.Close();
                }
                catch (IOException)
                {
                    // Ya se estaba cerrando.
                }
                using var espera = new CancellationTokenSource(TimeSpan.FromSeconds(20));
                try
                {
                    await Proceso.WaitForExitAsync(espera.Token);
                }
                catch (OperationCanceledException)
                {
                    Proceso.Kill(entireProcessTree: true);
                    await Proceso.WaitForExitAsync();
                }
            }
        }
        catch (InvalidOperationException)
        {
            // Ya había terminado.
        }
        lock (_registro)
            _registro.Dispose();
        Proceso.Dispose();
    }
}

/// <summary>La base de prueba para el servicio del simulador: una copia de la que se indica, o una nueva como la de <c>preparar-prueba</c>.</summary>
public static class BaseDePrueba
{
    /// <summary>
    /// El catálogo, <paramref name="veces"/> veces: cada producto activo otra vez con su nombre y «· 2», «· 3»…, sus
    /// presentaciones, sus precios y sus códigos (con «-2», «-3»… para que no se repitan), sin existencias ni ventas. Así la
    /// papelería ficticia (1,458 productos en la copia) llega al tamaño del catálogo de la tienda (unos 6,000): la copia de las
    /// cajas, su búsqueda y la memoria del servicio pesan como allá, y se sigue vendiendo lo mismo.
    /// </summary>
    public static async Task MultiplicarCatalogoAsync(PosDbContext db, int veces, CancellationToken ct = default)
    {
        var productos = await db.Productos.AsNoTracking().Where(p => p.Activo)
            .Include(p => p.Variantes).ThenInclude(v => v.Presentaciones).ThenInclude(p => p.Codigos).ToListAsync(ct);
        var precios = (await db.Precios.AsNoTracking().ToListAsync(ct)).ToLookup(p => p.VarianteId);
        for (var n = 2; n <= veces; n++)
        {
            foreach (var p in productos)
            {
                var producto = new Producto
                {
                    Nombre = $"{p.Nombre} · {n}", MarcaId = p.MarcaId, DepartamentoId = p.DepartamentoId, Tipo = p.Tipo, Impuesto = p.Impuesto,
                };
                db.Productos.Add(producto);
                foreach (var v in p.Variantes.Where(v => v.Activa))
                {
                    var variante = new Variante { ProductoId = producto.Id, Descripcion = v.Descripcion, UnidadBase = v.UnidadBase };
                    db.Variantes.Add(variante);
                    var nuevas = new Dictionary<Guid, Guid>();
                    foreach (var pr in v.Presentaciones)
                    {
                        var presentacion = new Presentacion { VarianteId = variante.Id, Nombre = pr.Nombre, Factor = pr.Factor, EsBase = pr.EsBase };
                        db.Presentaciones.Add(presentacion);
                        nuevas[pr.Id] = presentacion.Id;
                        foreach (var c in pr.Codigos)
                            db.CodigosBarras.Add(new CodigoBarras { Codigo = $"{c.Codigo}-{n}", PresentacionId = presentacion.Id, Origen = c.Origen });
                    }
                    foreach (var precio in precios[v.Id])
                        db.Precios.Add(new Precio
                        {
                            VarianteId = variante.Id, PresentacionId = precio.PresentacionId is { } pid ? nuevas[pid] : null, SucursalId = precio.SucursalId,
                            CantidadMinima = precio.CantidadMinima, PrecioCentavos = precio.PrecioCentavos,
                        });
                }
            }
        }
        await db.SaveChangesAsync(ct);
    }

    /// <summary>
    /// Una copia de <paramref name="inicial"/> hecha con SQLite (una base abierta por otro programa también se copia completa y
    /// sin estorbarle), o, sin ella, la papelería ficticia de un año con la caja, los usuarios y los clientes de la prueba.
    /// Devuelve de dónde salió, para el informe.
    /// </summary>
    /// <param name="catalogoPor">Si se pide, el catálogo esas veces (<see cref="MultiplicarCatalogoAsync"/>).</param>
    public static async Task<string> PrepararAsync(string ruta, string? inicial, string carpeta, TextWriter salida, int catalogoPor = 1,
        CancellationToken ct = default)
    {
        var descripcion = await CopiarOArmarAsync(ruta, inicial, carpeta, salida, ct);
        if (catalogoPor <= 1)
            return descripcion;
        salida.WriteLine($"Copiando el catálogo hasta {catalogoPor} veces…");
        await using (var db = new PosDbContext(BaseDeDatos.Opciones($"Data Source={ruta};Pooling=False")))
        {
            await BaseDeDatos.PrepararAsync(db, ct);
            await MultiplicarCatalogoAsync(db, catalogoPor, ct);
        }
        return $"{descripcion}, con el catálogo copiado {catalogoPor} veces (del tamaño del de la tienda, sin existencias ni ventas en las copias)";
    }

    private static async Task<string> CopiarOArmarAsync(string ruta, string? inicial, string carpeta, TextWriter salida, CancellationToken ct)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(ruta))!);
        if (inicial is not null)
        {
            if (!File.Exists(inicial))
                throw new InvalidOperationException($"No existe la base {inicial}.");
            var deOrigen = new SqliteConnectionStringBuilder { DataSource = inicial, Mode = SqliteOpenMode.ReadOnly, Pooling = false };
            using (var origen = new SqliteConnection(deOrigen.ToString()))
            using (var destino = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = ruta, Pooling = false }.ToString()))
            {
                origen.Open();
                destino.Open();
                origen.BackupDatabase(destino);
            }
            return $"una copia de {Path.GetFullPath(inicial)}";
        }
        salida.WriteLine("Armando la base de prueba (la papelería ficticia de un año, como preparar-prueba)…");
        var opciones = new OpcionesFicticios();
        var datos = GeneradorFicticios.Generar(opciones);
        // Como generar-ficticios: con el reloj fijo, la misma semilla da la misma base.
        var cierre = new DateTimeOffset(HoraMexico.AUtc(opciones.UltimoDia.ToDateTime(new TimeOnly(22, 0))));
        await using (var db = new PosDbContext(BaseDeDatos.Opciones($"Data Source={ruta};Pooling=False")) { Reloj = new RelojFijo(cierre) })
        {
            await BaseDeDatos.PrepararAsync(db, ct);
            await GuardadoLote.GuardarAsync(datos, db, ct);
        }
        await using (var db = new PosDbContext(BaseDeDatos.Opciones($"Data Source={ruta};Pooling=False")))
        {
            await BaseDeDatos.PrepararAsync(db, ct);
            await PreparacionPrueba.PrepararAsync(db, Path.Combine(carpeta, "ejemplos"), ct);
        }
        return $"la papelería ficticia de un año (semilla {opciones.Semilla}), con la caja, los usuarios y los clientes de la prueba";
    }
}
