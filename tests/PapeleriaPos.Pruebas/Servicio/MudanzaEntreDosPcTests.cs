using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using PapeleriaPos.Datos;
using PapeleriaPos.Interfaz;
using PapeleriaPos.Nucleo.Contratos;
using PapeleriaPos.Nucleo.Control;
using PapeleriaPos.Nucleo.Turnos;
using PapeleriaPos.Nucleo.Ventas;
using PapeleriaPos.Servicio;
using static PapeleriaPos.Pruebas.Servicio.RestaurarRespaldoTests;

namespace PapeleriaPos.Pruebas.Servicio;

/// <summary>
/// La mudanza de la base a otra PC (2026-11-11), del lado del servicio y con el servicio de verdad en memoria: en la PC vieja, con la
/// marca que deja <c>mudanza-sacar</c> el servicio ya no arranca (ni toca su base) hasta que se cancele; en la PC nueva, la base del
/// paquete puesta como la pone <c>restaurar.ps1 -PorMudanza</c> conoce la llave de la caja de antes, trae sus ventas, deja lo pendiente
/// del puente como «ya se intentó» y dice en su bitácora de qué PC vino. Un pedido sin <c>porMudanza</c> se anota como siempre. Y la
/// caja de verdad de la PC vieja (su conexión, su base local y su contingencia), que cobró con su copia a media mudanza, sube lo suyo
/// al servicio nuevo sin pedir alta.
/// </summary>
public sealed class MudanzaEntreDosPcTests : IDisposable
{
    private const string Equipo = "CAJA-2";

    /// <summary>La USB: fuera de los datos de las dos PC.</summary>
    private readonly string _usb = Path.Combine(Path.GetTempPath(), $"mudanza-usb-{Guid.NewGuid():N}");

    private string Paquete => Path.Combine(_usb, "mudanza");

    public void Dispose() => ArchivosDePrueba.BorrarCarpeta(_usb);

    private static string Sha256De(string archivo) => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(archivo)));

    /// <summary>mudanza-sacar en esa PC (su servicio ya detenido), el sábado 14 a las 08:15 de la tienda; sin red: su servicio «no contesta».</summary>
    private Task<ResultadoDeMudanza> SacarAsync(PrincipalQueSeRestaura p, OpcionesEleventa? eleventa = null) =>
        new MudanzaDeLaBase($"Data Source={p.Ruta};Pooling=False", p.CarpetaRespaldos, eleventa ?? new OpcionesEleventa())
        {
            Equipo = Equipo,
            Ips = () => ["192.0.2.72"],
            Reloj = new RelojDePrueba(new DateTime(2026, 11, 14, 14, 15, 7, DateTimeKind.Utc)),
            ElServicioContesta = (_, _) => Task.FromResult(false),
        }.SacarAsync(Paquete);

    // ── La PC vieja: con la marca, el servicio no arranca ────────────────────

    /// <summary>
    /// Como <c>NoArrancarSinLaBase</c>: con <c>pos.db.mudada</c> junto a la base el servicio no arranca, y lo dice (en su registro y, con
    /// la excepción que termina el programa, en el Visor de eventos). Antes de todo lo demás: el servicio ni se arma, y la base no se toca
    /// (ni la marca de «en uso», ni el -wal). Cancelada la mudanza (el instalador le cambia el nombre a la marca), arranca como siempre.
    /// </summary>
    [Fact]
    public async Task Con_la_marca_el_servicio_no_arranca_ni_toca_la_base_y_cancelada_la_mudanza_arranca_como_siempre()
    {
        await using var p = await PrincipalQueSeRestaura.CrearAsync();
        ArchivosDePrueba.SoltarBase(p.Ruta);
        Assert.Null((await SacarAsync(p)).Error);
        var marca = p.Ruta + ".mudada";
        var esperado = $"La base de esta PC se sacó para mudarla a otra PC el 14/11/2026 08:15. Este servicio no arranca mientras exista {marca}. Para "
            + "terminar o cancelar la mudanza: corre el instalador en esta PC.";

        // La guarda, sola: lo dice como error crítico y sale la excepción con lo mismo.
        var registro = new RegistroEnMemoria();
        var noArranca = Assert.Throws<InvalidOperationException>(() =>
            MudanzaDeLaBase.NoArrancarConLaMarca($"Data Source={p.Ruta};Pooling=False", registro.CreateLogger(typeof(MudanzaDeLaBase).FullName!)));
        Assert.Equal(esperado, noArranca.Message);
        Assert.Equal((LogLevel.Critical, esperado), Assert.Single(registro.Lineas.Select(l => (l.Nivel, l.Texto))));

        // El servicio entero: no arranca, deja el porqué en su registro del día y no toca nada.
        var (baseAntes, archivosAntes) = (Sha256De(p.Ruta), Directory.GetFiles(p.Carpeta).Order(StringComparer.Ordinal).ToList());
        var registros = Path.Combine(_usb, "registros-de-la-pc-vieja");
        var seArmo = false;
        var alArrancar = Assert.ThrowsAny<Exception>(() => p.Arrancar(_ => seArmo = true, ("Registro:Activo", "true"), ("Registro:Carpeta", registros)));
        Assert.Contains(esperado, alArrancar.ToString());
        // Ni se llegó a armar el servicio: no hay tareas de fondo ni nadie atendiendo.
        Assert.False(seArmo);
        Assert.Equal(baseAntes, Sha256De(p.Ruta));
        var archivosDespues = Directory.GetFiles(p.Carpeta).Order(StringComparer.Ordinal).ToList();
        Assert.Equal(archivosAntes, archivosDespues);
        Assert.DoesNotContain(archivosDespues, a => a.EndsWith(".en-uso", StringComparison.Ordinal) || a.EndsWith("-wal", StringComparison.Ordinal));
        // En el registro del día, como error grave (el mismo renglón va al Visor de eventos de Windows). Sin la ruta de la marca: el
        // registro tacha lo que parece un secreto, y en Linux la carpeta de trabajo (la variable PWD) lo parece por su nombre.
        var delDia = string.Concat(Directory.EnumerateFiles(registros, "servicio-*.log").Select(File.ReadAllText));
        Assert.Contains("  Grave  MudanzaDeLaBase  La base de esta PC se sacó para mudarla a otra PC el 14/11/2026 08:15. Este servicio no arranca mientras exista ", delDia);
        Assert.Contains("pos.db.mudada. Para terminar o cancelar la mudanza: corre el instalador en esta PC.", delDia);

        // Una marca que no se puede leer cuenta igual (dice cuándo se escribió el archivo).
        var buena = await File.ReadAllBytesAsync(marca);
        await File.WriteAllTextAsync(marca, "{ esto no es una marca");
        Assert.StartsWith("La base de esta PC se sacó para mudarla a otra PC el ", Assert.Throws<InvalidOperationException>(() =>
            MudanzaDeLaBase.NoArrancarConLaMarca($"Data Source={p.Ruta}", new RegistroEnMemoria().CreateLogger("prueba"))).Message);
        Assert.ThrowsAny<Exception>(() => p.Arrancar());
        await File.WriteAllBytesAsync(marca, buena);

        // pin-usuario no cambia: sigue sirviendo con la marca (es otra orden del mismo programa, no arranca el servicio).
        var (salida, errores) = (new StringWriter(), new StringWriter());
        var pines = new Queue<string>(["2468", "2468"]);
        Assert.Equal(0, await PinUsuarioDelServicio.CorrerAsync($"Data Source={p.Ruta};Pooling=False", ["--usuario", "dueno"], _ => pines.Dequeue(), 3, salida, errores));
        Assert.Equal("", errores.ToString());

        // «Cancelar la mudanza» en el instalador: la marca cambia de nombre y el servicio vuelve a arrancar con su base.
        File.Move(marca, marca + ".cancelada");
        var http = p.Arrancar();
        var estado = await Leer<EstadoServicioDto>(await http.GetAsync("api/estado"));
        Assert.False(estado.TiendaNueva);
        await Leer<SesionDto>(await http.PostAsJsonAsync("api/sesion", new SolicitudEntrada("dueno", "2468")));
    }

    /// <summary>
    /// «Terminar la mudanza» aparta la base y deja la marca para el final: si se corta a la mitad, la marca sigue y la base ya no está.
    /// El servicio tampoco arranca así, y no hace una base vacía en su lugar. Sin la marca, la guarda no dice nada.
    /// </summary>
    [Fact]
    public void Con_la_marca_y_sin_la_base_tampoco_arranca_ni_hace_una_base_vacia()
    {
        var carpeta = Path.Combine(_usb, "datos");
        Directory.CreateDirectory(carpeta);
        var (ruta, registro) = (Path.Combine(carpeta, "pos.db"), new RegistroEnMemoria());

        // Sin la marca: nada que decir (tampoco con la base en memoria).
        MudanzaDeLaBase.NoArrancarConLaMarca($"Data Source={ruta}", registro.CreateLogger("prueba"));
        MudanzaDeLaBase.NoArrancarConLaMarca("Data Source=:memory:", registro.CreateLogger("prueba"));
        Assert.Null(MudanzaDeLaBase.MarcaQueExiste($"Data Source={ruta}"));
        Assert.Null(MudanzaDeLaBase.ArchivoDeLaMarca("Data Source=:memory:"));
        Assert.Empty(registro.Lineas);

        File.WriteAllText(ruta + ".mudada", """{ "paquete": "0f8fad5bd9cb469fa16570867728950e", "tienda": "Matriz", "cuandoUtc": "2026-11-14T14:15:07Z" }""");

        Assert.Equal(ruta + ".mudada", MudanzaDeLaBase.MarcaQueExiste($"Data Source={ruta}"));
        var noArranca = Assert.Throws<InvalidOperationException>(() => MudanzaDeLaBase.NoArrancarConLaMarca($"Data Source={ruta}", registro.CreateLogger("prueba")));
        Assert.Equal($"La base de esta PC se sacó para mudarla a otra PC el 14/11/2026 08:15. Este servicio no arranca mientras exista {ruta}.mudada. Para "
            + "terminar o cancelar la mudanza: corre el instalador en esta PC.", noArranca.Message);
        Assert.Equal(["pos.db.mudada"], Directory.EnumerateFileSystemEntries(carpeta).Select(Path.GetFileName));
    }

    // ── La PC nueva: el pedido de restaurar.ps1 dice que fue una mudanza ─────

    /// <summary>
    /// Deja esa PC como la deja <c>restaurar.ps1</c> (instalacion/restaurar.ps1) con ese respaldo de su carpeta: la base de antes aparte,
    /// el respaldo como pos.db, la marca para revisarla al arrancar y su pedido ya puesto, escrito como lo escribe PowerShell
    /// (ConvertTo-Json). <paramref name="porMudanza"/> va tal cual en el JSON (<c>"CAJA-2"</c>, <c>null</c>…); sin él, el pedido no lleva
    /// ese campo, como los de antes de la mudanza. Devuelve la carpeta de la base de antes.
    /// </summary>
    private static async Task<string> ComoLoDejaRestaurarPs1Async(string carpetaDeDatos, string respaldo, string? porMudanza, bool porElScript = true)
    {
        var ruta = Path.Combine(carpetaDeDatos, "pos.db");
        var carpeta = Path.Combine(carpetaDeDatos, "antes-de-restaurar-2026-11-14_08.20.11");
        Directory.CreateDirectory(carpeta);
        foreach (var final in new[] { "", "-wal", "-shm" })
        {
            if (File.Exists(ruta + final))
                File.Move(ruta + final, Path.Combine(carpeta, "pos.db" + final));
        }
        File.Copy(respaldo, ruta);
        var archivo = Path.GetFileName(respaldo);
        await File.WriteAllTextAsync(ruta + ".danada", $"restaurar.ps1 puso el respaldo del 14/11/2026 a las 08:15 (otro) ({archivo}): se revisa al arrancar.");
        var ahora = DateTime.UtcNow.ToString("o");
        await File.WriteAllTextAsync(ruta + ".restaurar", $$"""
            {
                "id":  "{{Guid.NewGuid()}}",
                "archivo":  "{{archivo}}",
                "tipo":  "Otro",
                "fechaUtc":  "{{File.GetLastWriteTimeUtc(respaldo):o}}",
                "usuarioId":  "00000000-0000-0000-0000-000000000000",
                "cajaId":  "00000000-0000-0000-0000-000000000000",
                "sucursalId":  "00000000-0000-0000-0000-000000000000",
                "pedidaUtc":  "{{ahora}}",
                "reinicioAutomatico":  true,
                "paso":  "Puesta",
                "carpeta":  "{{Path.GetFileName(carpeta)}}",
                "hechaUtc":  "{{ahora}}",
                "fallo":  null,
                "porElScript":  {{(porElScript ? "true" : "false")}}{{(porMudanza is null ? "" : $",\n    \"porMudanza\":  {porMudanza}")}}
            }
            """);
        return carpeta;
    }

    /// <summary>
    /// Lo de después de migrar, como al arrancar el servicio (Program.cs): lee el pedido, migra y lo termina. Devuelve lo que quedó en la
    /// bitácora por ese pedido.
    /// </summary>
    private static async Task<List<Bitacora>> TerminarElPedidoAsync(PrincipalQueSeRestaura p)
    {
        var restauracion = Restauracion(p);
        restauracion.SiSePidio();
        await using (var db = new PosDbContext(BaseDeDatos.Opciones($"Data Source={p.Ruta};Pooling=False")))
        {
            await BaseDeDatos.PrepararAsync(db);
            Assert.True(await restauracion.DespuesDeMigrarAsync(db));
        }
        Assert.False(File.Exists(p.Ruta + ".restaurar"));
        await using var base_ = p.Servicio.Base();
        return await base_.Bitacora.AsNoTracking().Where(b => b.Accion == AccionControlada.ConfigurarTienda && b.Detalle.Contains("respaldo")).ToListAsync();
    }

    /// <summary>
    /// Con <c>porMudanza</c> (el equipo de donde salió la base), la bitácora dice de quién es la base y no que el servicio no arrancaba.
    /// Lo demás, igual que cualquier restauración: lo pendiente del puente queda como «ya se intentó».
    /// </summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task El_pedido_con_porMudanza_anota_de_que_pc_se_mudo_la_base(bool porElScript)
    {
        await using var p = await PrincipalQueSeRestaura.CrearAsync();
        var ventas = await DosVentasAsync(p);
        await p.DetenerAsync();
        Guid pendiente, yaIntentada;
        await using (var db = p.Servicio.Base())
        {
            var (uno, otro) = (new EnvioAEleventa { Tipo = TipoEnvioEleventa.Venta, VentaId = ventas[0], CreadoUtc = DateTime.UtcNow },
                new EnvioAEleventa { Tipo = TipoEnvioEleventa.Venta, VentaId = ventas[1], CreadoUtc = DateTime.UtcNow, Intentos = 3 });
            db.EnviosAEleventa.AddRange(uno, otro);
            await db.SaveChangesAsync();
            (pendiente, yaIntentada) = (uno.Id, otro.Id);
        }
        var archivo = p.Respaldar(Hace(5), "mudanza-0815");
        var deAntes = await ComoLoDejaRestaurarPs1Async(p.Carpeta, Path.Combine(p.CarpetaRespaldos, archivo), "\"CAJA-2\"", porElScript);

        var anotada = Assert.Single(await TerminarElPedidoAsync(p));

        Assert.Equal($"Se mudó aquí la base de CAJA-2 (respaldo {archivo}). La base de antes quedó en {deAntes}", anotada.Detalle);
        // Nadie de esta base la pidió: sin usuario ni caja, en la sucursal de la tienda, como hecha.
        await using var base_ = p.Servicio.Base();
        Assert.Equal(((Guid?)null, (Guid?)null, await base_.Sucursales.Select(s => s.Id).SingleAsync(), ResultadoBitacora.Hecho),
            (anotada.UsuarioId, anotada.CajaId, anotada.SucursalId, anotada.Resultado));
        var envios = await base_.EnviosAEleventa.AsNoTracking().ToDictionaryAsync(e => e.Id, e => e.Intentos);
        Assert.Equal((1, 3), (envios[pendiente], envios[yaIntentada]));
    }

    /// <summary>
    /// Un pedido sin <c>porMudanza</c> (los de restaurar.ps1 de siempre), o con él vacío, se anota exactamente como antes de la mudanza:
    /// «Con restaurar.ps1 (el servicio no arrancaba) se restauró…».
    /// </summary>
    [Theory]
    [InlineData(null)]
    [InlineData("null")]
    [InlineData("\"\"")]
    [InlineData("\"   \"")]
    public async Task Un_pedido_sin_porMudanza_se_anota_como_siempre(string? porMudanza)
    {
        await using var p = await PrincipalQueSeRestaura.CrearAsync();
        ArchivosDePrueba.SoltarBase(p.Ruta);
        var archivo = p.Respaldar(Hace(45), "z-2131");
        var respaldo = Path.Combine(p.CarpetaRespaldos, archivo);
        var deAntes = await ComoLoDejaRestaurarPs1Async(p.Carpeta, respaldo, porMudanza);
        // Lo que decía la bitácora antes de la mudanza, armado aquí con el mismo respaldo.
        var descripcion = new RestauracionDto(archivo, TipoRespaldo.Otro, PapeleriaPos.Nucleo.HoraMexico.ALocal(File.GetLastWriteTimeUtc(respaldo)), default,
            Pendiente: false, ReinicioAutomatico: true).Descripcion;

        var anotada = Assert.Single(await TerminarElPedidoAsync(p));

        Assert.Equal($"Con restaurar.ps1 (el servicio no arrancaba) se restauró {descripcion}. La base de antes quedó en {deAntes}", anotada.Detalle);
        Assert.StartsWith("Con restaurar.ps1 (el servicio no arrancaba) se restauró el respaldo del ", anotada.Detalle);
        Assert.DoesNotContain("mudó", anotada.Detalle);
    }

    /// <summary>
    /// El pedido que deja el propio servicio (Configuración › Respaldos) lleva el campo nuevo vacío y se sigue leyendo; uno de una versión
    /// anterior del programa, sin el campo, también.
    /// </summary>
    [Fact]
    public async Task El_pedido_de_la_pantalla_sigue_igual_con_el_campo_nuevo_vacio()
    {
        await using var p = await PrincipalQueSeRestaura.CrearAsync();
        var (dueno, caja, sucursal) = await QuienAsync(p);
        ArchivosDePrueba.SoltarBase(p.Ruta);
        var archivo = p.Respaldar(Hace(30), "ahora-0930");

        Restauracion(p).Pedir(archivo, dueno, caja, sucursal, reinicioAutomatico: true);

        var escrito = await File.ReadAllTextAsync(p.Ruta + ".restaurar");
        Assert.Contains("\"porMudanza\": null", escrito);
        // Lo último del pedido: lo que ya lo leía por posición o por nombre no cambia.
        Assert.EndsWith("\"porMudanza\": null\n}", escrito.ReplaceLineEndings("\n").TrimEnd());
        var anotada = Assert.Single(await TerminarElPedidoAsync(p));
        Assert.StartsWith("Restauró el respaldo del ", anotada.Detalle);
        Assert.Equal((dueno, caja), (anotada.UsuarioId!.Value, anotada.CajaId!.Value));
    }

    // ── De una PC a la otra ──────────────────────────────────────────────────

    private static readonly IReadOnlyList<(IPAddress, int)> RedDeLaTienda = [(IPAddress.Parse("192.0.2.10"), 24)];

    /// <summary>En la red de la prueba nadie contesta a la búsqueda por UDP: la caja solo tiene la dirección que le dejó el instalador.</summary>
    private sealed class NadieMasEnLaRed : IBuscadorDeLaPrincipal
    {
        public Task<IReadOnlyList<Uri>> BuscarAsync(TimeSpan espera, CancellationToken ct = default) => Task.FromResult<IReadOnlyList<Uri>>([]);
    }

    /// <summary>
    /// De punta a punta, lo que le toca al servicio y a la caja. La PC vieja tiene la tienda, su caja dada de alta con su llave (la caja
    /// de verdad: su conexión, su base local y su contingencia), un turno abierto, tres ventas y lo de una pendiente de llegar a
    /// eleventa. <c>mudanza-sacar</c> deja el paquete; en la PC nueva (un servicio recién instalado, sin tienda) la base del paquete se
    /// pone como la pone <c>restaurar.ps1 -PorMudanza</c> y ese servicio arranca con ella: es la misma tienda, conoce la llave de la caja
    /// de antes (le prueba quién es y la deja entrar, sin alta ni PIN), tiene sus ventas y su turno, lo pendiente quedó «ya se intentó» y
    /// su bitácora dice de dónde vino. La llave propia del servicio no se muda: la nueva PC hace la suya. Mientras, la caja de la PC
    /// vieja cobró con su copia; ya «Otra caja» (su misma base local, con la dirección de la principal y su llave pública en lugar de
    /// las de antes), sube lo que cobró a su turno de siempre, una sola vez y sin pedir alta, y sigue su serie. Y la PC vieja, con su
    /// marca, ya no arranca.
    /// </summary>
    [Fact]
    public async Task La_base_que_sale_en_el_paquete_llega_a_la_otra_pc_con_la_llave_de_la_caja_sus_ventas_y_lo_pendiente()
    {
        // ── La PC vieja (la caja 2 de hoy) ──
        await using var vieja = await PrincipalQueSeRestaura.CrearAsync();
        var ventas = await DosVentasAsync(vieja);
        var http = vieja.EnMarcha!.CreateClient();
        var caja = (await http.GetFromJsonAsync<List<CajaDto>>("api/cajas"))!.Single();
        var alta = await Leer<AltaCajaDto>(await http.PostAsJsonAsync("api/alta", new SolicitudAltaCaja("dueno", "3333", caja.SucursalId, caja.Id, null, null, Equipo)));
        var turno = await Leer<TurnoDto>(await http.GetAsync($"api/cajas/{caja.Id}/turno"));
        var tienda = (await Leer<EstadoServicioDto>(await http.GetAsync("api/estado"))).Sucursales;

        // La caja de verdad de esa PC, ya con su alta: le habla a su servicio (en la misma PC), entra el cajero y cobra en línea, en el
        // turno que ya estaba abierto. Ese cobro viaja en el paquete.
        var baseLocal = Path.Combine(vieja.Carpeta, "caja-local.db");
        var conSuServicio = new ConexionCaja
        {
            Servicio = new Uri("http://localhost/"), CajaId = caja.Id, Llave = alta.Llave, LlaveDelServicio = alta.LlaveDelServicio, PideAlta = true,
        };
        var deLaCaja = new HttpClient(vieja.ParaLasCajas, disposeHandler: false);
        var enLaVieja = new Contingencia(new AlmacenLocal(baseLocal), deLaCaja, conSuServicio);
        Guid cajero, pendiente;
        Guid? copiaDeLaVieja;
        Func<int, SolicitudCobro> cobro;
        TicketDto enLinea, conLaCopia;
        string rutaDeLaBandeja, loQueLlevaLaBandeja;
        try
        {
            var cliente = new ClienteCaja(deLaCaja, conSuServicio, enLaVieja);
            // Hasta que conteste y baje su copia (con la PC ocupada, un latido puede pasarse de su plazo: el siguiente lo logra).
            for (var latido = 1; latido <= 10 && !(enLaVieja.EnLinea && enLaVieja.Paquete is not null); latido++)
                await enLaVieja.LatidoAsync();
            Assert.True(enLaVieja.EnLinea);
            Assert.Equal(caja.Id, enLaVieja.Paquete?.Caja.Id);
            copiaDeLaVieja = enLaVieja.Paquete!.Base;
            Assert.NotNull(copiaDeLaVieja);
            cajero = (await cliente.EntrarAsync("cajero", "1111")).UsuarioId;
            Assert.Equal(turno.Id, (await cliente.TurnoAsync(caja.Id))!.Id);
            var busqueda = (await cliente.BuscarAsync("pluma punto fino", caja.SucursalId)).First(r => r.Codigo is not null);
            var pluma = (await cliente.PorCodigoAsync(busqueda.Codigo!, caja.SucursalId))!;
            cobro = piezas => new SolicitudCobro(caja.Id, turno.Id, cajero,
                [new LineaSolicitada(pluma.VarianteId, pluma.Presentacion.Id, piezas * 1000L, pluma.Descripcion)], [new CobroSolicitado(FormaPago.Efectivo, 100000)]);
            enLinea = await cliente.CobrarAsync(cobro(1));
            Assert.False(enLinea.EnContingencia);

            // El instalador detiene el servicio (y lo deja deshabilitado) antes de sacar la base.
            await vieja.DetenerAsync();
            await using (var db = vieja.Servicio.Base())
            {
                var envio = new EnvioAEleventa { Tipo = TipoEnvioEleventa.Venta, VentaId = ventas[1], CreadoUtc = DateTime.UtcNow };
                db.EnviosAEleventa.Add(envio);
                await db.SaveChangesAsync();
                pendiente = envio.Id;
            }

            var sacada = await SacarAsync(vieja,
                new OpcionesEleventa { Activo = true, Escribir = true, Servidor = "192.0.2.106", Base = @"C:\eleventa\PDVDATA.FDB", Caja = 3, Cajero = 7 });
            Assert.Null(sacada.Error);

            // Entre el paso 1 y el 3 la caja de esa PC sigue abierta: su servicio ya no contesta, cobra con su copia y lo guarda para
            // subirlo. Lo que lleva la bandeja se lee ahora: ya subido, el renglón se queda sin su cuerpo.
            await enLaVieja.LatidoAsync();
            Assert.False(enLaVieja.EnLinea);
            conLaCopia = await cliente.CobrarAsync(cobro(2));
            Assert.True(conLaCopia.EnContingencia);
            Assert.Equal(1, enLaVieja.Pendientes);
            Assert.Equal(Folio(enLinea) + 1, Folio(conLaCopia));
            (rutaDeLaBandeja, loQueLlevaLaBandeja) = await DeLaBandejaAsync(baseLocal, conLaCopia.VentaId!.Value);
        }
        finally
        {
            // Antes de «Terminar la mudanza» el instalador cierra la caja.
            enLaVieja.Cerrar(TimeSpan.FromSeconds(20));
        }

        var claves = ClavesEscritas.De(Path.Combine(Paquete, "mudanza.txt"));
        Assert.Equal((string.Join("; ", tienda), Equipo, "si", "si", "si", "A=3", "1", "3"),
            (claves["tienda"], claves["caja.1.equipo"], claves["caja.1.con-llave"], claves["caja.1.turno-abierto"], claves["eleventa-escribir"],
                claves["eleventa-cajas"], claves["eleventa-pendientes"], claves["ventas"]));

        // ── La PC nueva (la principal): el servicio recién instalado, con su base vacía, y la base del paquete puesta con restaurar.ps1 ──
        var nueva = Path.Combine(_usb, "datos-de-la-pc-nueva");
        Directory.CreateDirectory(Path.Combine(nueva, "respaldos"));
        await using (var vacia = new PosDbContext(BaseDeDatos.Opciones($"Data Source={Path.Combine(nueva, "pos.db")};Pooling=False")))
            await BaseDeDatos.PrepararAsync(vacia);
        // instalar-pc.ps1 copia la base del paquete a respaldos\ (revisando su SHA-256) y llama a restaurar.ps1 -PorMudanza <equipo>.
        var respaldo = Path.Combine(nueva, "respaldos", claves["archivo"]);
        File.Copy(Path.Combine(Paquete, claves["archivo"]), respaldo);
        Assert.Equal(claves["sha256"], Sha256De(respaldo).ToLowerInvariant());
        var deAntes = await ComoLoDejaRestaurarPs1Async(nueva, respaldo, $"\"{claves["equipo"]}\"");
        await using var servicio = new FabricaDePrueba(b =>
        {
            b.UseSetting("ConnectionStrings:Pos", $"Data Source={Path.Combine(nueva, "pos.db")};Pooling=False").UseSetting("Respaldos:Activo", "false")
                .UseSetting("Telegram:Activo", "false");
            // Como la ve la caja de la otra PC: por la red de la tienda, no desde la misma PC.
            b.ConfigureServices(s =>
            {
                s.AddSingleton<IStartupFilter>(new EnLaRedDeLaTienda("192.0.2.72", "192.0.2.10"));
                s.AddSingleton(new RedLocalDelServicio(() => RedDeLaTienda));
            });
        });
        var desdeLaCaja = servicio.CreateClient();

        // Es la misma tienda, ya no una tienda nueva, y la base pasó la revisión.
        var estado = await Leer<EstadoServicioDto>(await desdeLaCaja.GetAsync("api/estado"));
        Assert.Equal((false, null), (estado.TiendaNueva, estado.AvisoBase));
        Assert.Equal(tienda, estado.Sucursales);
        Assert.Equal(claves["tienda"], string.Join("; ", estado.Sucursales));
        Assert.False(File.Exists(Path.Combine(nueva, "pos.db.restaurar")));

        // La prueba de identidad de siempre: la principal nueva conoce la llave de ESTA caja (su huella viajó en la base)…
        var reto = Descubrimiento.NuevoReto();
        var identidad = await Leer<IdentidadServicioDto>(await desdeLaCaja.GetAsync($"api/identidad?caja={caja.Id}&reto={reto}"));
        Assert.True(Descubrimiento.EsLaPrueba(identidad.Prueba, Descubrimiento.Prueba(LlaveCaja.Huella(alta.Llave), caja.Id, reto, "192.0.2.10:5080")));
        // …y firma con su propia llave, que no es la de la PC vieja: pos.llave no se muda.
        Assert.True(Descubrimiento.FirmaValida(identidad.LlaveDelServicio, Descubrimiento.MensajeFirmado(caja.Id, reto, "192.0.2.10:5080", identidad.Prueba), identidad.Firma));
        Assert.NotEqual(alta.LlaveDelServicio, identidad.LlaveDelServicio);

        // Sin llave, desde otra PC, no entra; con la llave de la caja de antes, sí: sin alta ni PIN, y con su turno abierto de siempre.
        Assert.Equal(HttpStatusCode.Unauthorized, (await desdeLaCaja.GetAsync($"api/cajas/{caja.Id}/turno")).StatusCode);
        desdeLaCaja.DefaultRequestHeaders.Add(ClienteCaja.EncabezadoLlave, alta.Llave);
        Assert.Equal(turno.Id, (await Leer<TurnoDto>(await desdeLaCaja.GetAsync($"api/cajas/{caja.Id}/turno"))).Id);

        var enLaNueva = $"Data Source={Path.Combine(nueva, "pos.db")};Pooling=False";
        await using (var db = new PosDbContext(BaseDeDatos.Opciones(enLaNueva)))
        {
            // Sus ventas, con la que la caja cobró en línea (la que cobró después con su copia todavía no llega); lo pendiente del puente,
            // como «ya se intentó» (antes de mandarlo se busca en eleventa); y la bitácora.
            Assert.Equal(ventas.Append(enLinea.VentaId!.Value).Order(), (await db.Ventas.Where(v => v.PagadaUtc != null).Select(v => v.Id).ToListAsync()).Order());
            var envio = await db.EnviosAEleventa.AsNoTracking().SingleAsync(e => e.Id == pendiente);
            Assert.Equal((EstadoEnvioEleventa.Pendiente, 1), (envio.Estado, envio.Intentos));
            var anotada = await db.Bitacora.AsNoTracking().SingleAsync(b => b.Detalle.StartsWith("Se mudó aquí"));
            Assert.Equal($"Se mudó aquí la base de {Equipo} (respaldo {claves["archivo"]}). La base de antes quedó en {deAntes}", anotada.Detalle);
        }
        // La base vacía de la instalación quedó apartada, no borrada.
        Assert.True(File.Exists(Path.Combine(deAntes, "pos.db")));

        // ── La caja de la PC vieja, ya «Otra caja»: su misma base local, y caja.json como lo deja «Terminar la mudanza» (la dirección de
        // la principal y la llave pública de su servicio; el alta, la de siempre; ya sin servicio en esa PC). Como al abrirla: sin
        // comprobar todavía. ──
        var principal = new Uri("http://192.0.2.10:5080/");
        var conLaPrincipal = new ConexionCaja
        {
            Servicio = principal, DireccionDeConfianza = principal, CajaId = caja.Id, Llave = alta.Llave, LlaveDelServicio = identidad.LlaveDelServicio,
            PideAlta = true, ServicioEnEstaPc = false, SinComprobar = true,
        };
        var porLaRed = new HttpClient(servicio.Server.CreateHandler());
        var reencuentro = new ReencuentroDeLaPrincipal(conLaPrincipal, new NadieMasEnLaRed(), porLaRed, redes: () => RedDeLaTienda);
        var otraCaja = new Contingencia(new AlmacenLocal(baseLocal), porLaRed, conLaPrincipal, redDeLaCaja: new RedDeLaCaja(conLaPrincipal, reencuentro: reencuentro));
        var pidioAlta = new List<string>();
        otraCaja.SinAlta += porque =>
        {
            lock (pidioAlta)
                pidioAlta.Add(porque);
        };
        try
        {
            // Trae lo que cobró con su copia. La principal prueba quién es antes de recibir la llave (tiene 2 s para contestar: con la PC
            // ocupada, se le vuelve a pedir en el siguiente latido) y la caja sube lo suyo.
            Assert.Equal((1, 0), (otraCaja.Pendientes, otraCaja.ConError));
            for (var latido = 1; latido <= 10 && !(otraCaja.EnLinea && otraCaja.Pendientes == 0 && otraCaja.Paquete?.Base == estado.Base); latido++)
                await otraCaja.LatidoAsync();

            // Sin pedir alta: la principal nueva probó ser la suya (con la llave de esta caja, y firmando con la llave que dejó el
            // instalador), y la caja le volvió a mandar su llave.
            Assert.Empty(pidioAlta);
            Assert.Null(reencuentro.AltaPorque);
            Assert.False(conLaPrincipal.SinComprobar);
            Assert.Equal(identidad.LlaveDelServicio, conLaPrincipal.LlaveDelServicio);
            Assert.Equal(principal, conLaPrincipal.Servicio);
            Assert.True(otraCaja.EnLinea);
            Assert.Equal((0, 0), (otraCaja.Pendientes, otraCaja.ConError));
            // Notó que la base es otra (la del servicio nuevo) y bajó su copia completa de ahí.
            Assert.NotEqual(copiaDeLaVieja, estado.Base);
            Assert.Equal(estado.Base, otraCaja.Paquete?.Base);
            // Lo que cobró con su copia entró una vez, en su turno de siempre (que en la base mudada sigue abierto) y con su folio.
            async Task<List<(Guid Turno, string? Folio, bool EnContingencia)>> LaQueSubioAsync()
            {
                await using var db = new PosDbContext(BaseDeDatos.Opciones(enLaNueva));
                return (await db.Ventas.AsNoTracking().Where(v => v.Id == conLaCopia.VentaId).ToListAsync()).Select(v => (v.TurnoId, v.FolioTexto, v.EnContingencia)).ToList();
            }
            Assert.Equal([(turno.Id, conLaCopia.Folio, true)], await LaQueSubioAsync());
            await using (var db = new PosDbContext(BaseDeDatos.Opciones(enLaNueva)))
            {
                var suTurno = await db.Turnos.AsNoTracking().SingleAsync(t => t.CajaId == caja.Id && t.CerradoUtc == null);
                Assert.Equal(turno.Id, suTurno.Id);
                Assert.Equal(4, await db.Ventas.CountAsync(v => v.TurnoId == turno.Id && v.PagadaUtc != null));
            }
            // Si se hubiera perdido la respuesta, la caja lo mandaría otra vez: sigue siendo una.
            Assert.Equal(HttpStatusCode.NoContent,
                (await desdeLaCaja.PostAsync(rutaDeLaBandeja, new StringContent(loQueLlevaLaBandeja, System.Text.Encoding.UTF8, "application/json"))).StatusCode);
            Assert.Equal([(turno.Id, conLaCopia.Folio, true)], await LaQueSubioAsync());

            // Y sigue cobrando en línea con la principal, en ese turno y con el folio que sigue.
            var cliente = new ClienteCaja(porLaRed, conLaPrincipal, otraCaja);
            Assert.Equal(turno.Id, (await cliente.TurnoAsync(caja.Id))!.Id);
            var siguiente = await cliente.CobrarAsync(cobro(1));
            Assert.False(siguiente.EnContingencia);
            Assert.Equal(Folio(conLaCopia) + 1, Folio(siguiente));
            Assert.Empty(pidioAlta);
        }
        finally
        {
            otraCaja.Cerrar(TimeSpan.FromSeconds(20));
        }

        // ── Y la PC vieja, con su marca, ya no arranca: nunca dos vivas con la misma base ──
        Assert.Contains("se sacó para mudarla a otra PC", Assert.ThrowsAny<Exception>(() => vieja.Arrancar()).ToString());
    }
}
