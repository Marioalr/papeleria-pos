using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using PapeleriaPos.Eleventa;
using PapeleriaPos.Nucleo;
using PapeleriaPos.Nucleo.Catalogo;
using PapeleriaPos.Nucleo.Contratos;
using PapeleriaPos.Nucleo.Importacion;
using PapeleriaPos.Nucleo.Ventas;
using PapeleriaPos.Pruebas.Eleventa;

namespace PapeleriaPos.Pruebas.Servicio;

/// <summary>
/// Lo que comparten las pruebas del piloto en la caja 2 contra el servicio: su configuración (manda a eleventa y, si se pide,
/// lo lee), una caja con su turno, cobrar en efectivo y esperar a la bandeja o a la lectura, que van en segundo plano.
/// </summary>
internal static class PilotoDePrueba
{
    public const int CajaEleventa = 4;

    /// <summary>El piloto: escribe en eleventa (y lee, si se pide). Sin servidor, uno que no contesta (nadie escucha en ese puerto).</summary>
    public static (string, string)[] Ajustes(ServidorEleventa? e = null, bool leer = false) =>
    [
        ("Eleventa:Escribir", "true"), ("Eleventa:Activo", leer ? "true" : "false"),
        ("Eleventa:Servidor", e?.Servidor ?? "127.0.0.1"), ("Eleventa:Puerto", (e?.Puerto ?? 1).ToString(CultureInfo.InvariantCulture)),
        ("Eleventa:Base", e?.Base ?? @"C:\eleventa\PDVDATA.FDB"), ("Eleventa:Usuario", "SYSDBA"),
        ("Eleventa:Clave", Environment.GetEnvironmentVariable("PRUEBAS_FIREBIRD_CLAVE") is { Length: > 0 } clave ? clave : "masterkey"),
        ("Eleventa:Caja", CajaEleventa.ToString(CultureInfo.InvariantCulture)), ("Eleventa:Cajero", "7"),
        ("Eleventa:SegundosAlCobrar", "10"),
        // Que solo mande el cobro o quien lo pida, y solo lea cuando se le pida.
        ("Eleventa:SegundosEntreEnvios", "3600"), ("Eleventa:Minutos", "600"),
        // Sin respaldo propio de eleventa (lo prueban RespaldoDeEleventaTests y RespaldoDelPilotoTests).
        ("Eleventa:RespaldoObligatorio", "false"),
    ];

    public static async Task<T> Leer<T>(HttpResponseMessage r)
    {
        Assert.True(r.IsSuccessStatusCode, await r.Content.ReadAsStringAsync());
        return (await r.Content.ReadFromJsonAsync<T>())!;
    }

    public static async Task<string> RechazoAsync(HttpResponseMessage r)
    {
        Assert.False(r.IsSuccessStatusCode, "Se esperaba que no se pudiera.");
        return (await r.Content.ReadFromJsonAsync<ErrorDto>())!.Mensaje;
    }

    public sealed record Caja(HttpClient Http, CajaDto Datos, SesionDto Sesion, SesionDto Encargado, TurnoDto Turno, Guid Variante);

    /// <param name="variante">La pluma que se vende (<see cref="MapearAsync"/>).</param>
    public static Task<Caja> AbrirAsync(ServicioDePrueba servicio, Guid variante) => AbrirAsync(servicio.Fabrica.CreateClient(), variante);

    /// <summary>Con un servicio que arrancó la prueba (uno que se detiene y vuelve a arrancar: restaurar un respaldo).</summary>
    public static async Task<Caja> AbrirAsync(HttpClient http, Guid variante)
    {
        var caja = (await http.GetFromJsonAsync<List<CajaDto>>("api/cajas"))!.Single();
        var sesion = await Leer<SesionDto>(await http.PostAsJsonAsync("api/sesion", new SolicitudEntrada("cajero", "1111")));
        var encargado = await Leer<SesionDto>(await http.PostAsJsonAsync("api/sesion", new SolicitudEntrada("encargado", "2222")));
        var turno = await Leer<TurnoDto>(await http.PostAsJsonAsync("api/turnos", new SolicitudTurno(caja.Id, sesion.UsuarioId, 50000)));
        return new Caja(http, caja, sesion, encargado, turno, variante);
    }

    /// <summary>
    /// Una pluma de la caja que vino del producto 10 de eleventa (como lo deja la importación). Va antes de que arranque el
    /// servicio: si lee eleventa, su primera lectura ya la encuentra.
    /// </summary>
    public static async Task<Guid> MapearAsync(ServicioDePrueba servicio)
    {
        await using var db = servicio.Base();
        var variante = await db.Variantes.Where(v => db.Productos.Any(p => p.Id == v.ProductoId && p.Nombre.StartsWith("Pluma punto fino")))
            .OrderBy(v => v.Id).Select(v => v.Id).FirstAsync();
        db.MapeosImportacion.Add(new MapeoImportacion { Origen = SincronizacionEleventa.Origen, Entidad = nameof(Variante), IdOrigen = "10", IdNuevo = variante });
        await db.SaveChangesAsync();
        return variante;
    }

    /// <summary>Plumas (las del producto 10 de eleventa) y, si se pide, un artículo común de $15, en efectivo.</summary>
    public static async Task<TicketDto> CobrarAsync(Caja c, int plumas = 1, bool comun = false)
    {
        List<LineaSolicitada> lineas = [new(c.Variante, null, plumas * 1000L)];
        if (comun)
            lineas.Add(new LineaSolicitada(null, null, 1000, "Recuerdo", 1500));
        return await Leer<TicketDto>(await c.Http.PostAsJsonAsync("api/ventas", new SolicitudCobro(c.Datos.Id, c.Turno.Id, c.Sesion.UsuarioId,
            lineas, [new CobroSolicitado(FormaPago.Efectivo, 100000)])));
    }

    public static async Task<EstadoEnviosEleventaDto> EnviosAsync(Caja c) => (await c.Http.GetFromJsonAsync<EstadoEnviosEleventaDto>("api/eleventa/envios"))!;

    /// <summary>Que la bandeja mande ya lo pendiente y esperar a que no quede nada (el estado se lee en dos consultas: las dos).</summary>
    public static async Task<EstadoEnviosEleventaDto> MandarAsync(Caja c)
    {
        Assert.Equal(HttpStatusCode.Accepted, (await c.Http.PostAsync("api/eleventa/enviar", null)).StatusCode);
        return await EsperarAsync(() => EnviosAsync(c), x => x.Pendientes == 0 && x.PorAtender.All(e => e.Estado != EstadoEnvioEleventa.Pendiente));
    }

    /// <summary>La lectura de eleventa, ya: espera a que termine la que se pidió.</summary>
    public static async Task LeerAsync(Caja c)
    {
        var antes = (await c.Http.GetFromJsonAsync<EstadoEleventaDto>("api/eleventa"))!.Lecturas;
        Assert.Equal(HttpStatusCode.Accepted, (await c.Http.PostAsync("api/eleventa/leer", null)).StatusCode);
        var despues = await EsperarAsync(async () => (await c.Http.GetFromJsonAsync<EstadoEleventaDto>("api/eleventa"))!, x => x.Lecturas > antes);
        Assert.True(despues.Lecturas > antes && despues.Bien, despues.Error);
    }

    /// <summary>La primera lectura, la de cuando arranca el servicio.</summary>
    public static Task EsperarPrimeraLecturaAsync(Caja c) =>
        EsperarAsync(async () => (await c.Http.GetFromJsonAsync<EstadoEleventaDto>("api/eleventa"))!, x => x.Lecturas >= 1);

    /// <summary>
    /// Espera a que el servicio llegue a algo que hace en segundo plano, hasta un minuto: con todas las pruebas a la vez, el
    /// Firebird de prueba tarda en abrir cada conexión.
    /// </summary>
    public static async Task<T> EsperarAsync<T>(Func<Task<T>> leer, Func<T, bool> listo)
    {
        var limite = DateTime.UtcNow.AddMinutes(1);
        while (true)
        {
            var valor = await leer();
            if (listo(valor) || DateTime.UtcNow > limite)
                return valor;
            await Task.Delay(100);
        }
    }

    public static DateTime HoraLocal(int minutosAtras)
    {
        var hora = HoraMexico.ALocal(DateTime.UtcNow).AddMinutes(-minutosAtras);
        return new DateTime(hora.Ticks - hora.Ticks % TimeSpan.TicksPerSecond);
    }

    /// <summary>
    /// Un eleventa de prueba con la principal y la caja 2, el cajero 7, la pluma (producto 10, $9, 50 en existencia) y un
    /// folio anterior; si se pide, la caja 2 ya abierta en eleventa (su turno).
    /// </summary>
    public static async Task<(EleventaDePrueba Eleventa, int? Turno)> EleventaAsync(bool abierta = true)
    {
        var e = await EleventaDePrueba.CrearAsync();
        await e.EjecutarAsync("INSERT INTO CAJAS (ID, NOMBRE, ULTIMA_IP) VALUES (1, 'Principal', '10.0.0.10')");
        await e.EjecutarAsync("INSERT INTO CAJAS (ID, NOMBRE, ULTIMA_IP) VALUES (4, 'Caja 2', '10.0.0.19')");
        await e.EjecutarAsync("INSERT INTO USUARIOS (ID, NOMBRE_COMPLETO, USUARIO) VALUES (7, 'Ana Ruiz', 'cajera2')");
        await e.EjecutarAsync("INSERT INTO DEPARTAMENTOS (ID, NOMBRE) VALUES (5, 'Papelería')");
        await e.EjecutarAsync("INSERT INTO DEPARTAMENTOS (ID, NOMBRE) VALUES (9, @nombre)", ("nombre", EnvioEleventa.DepartamentoComunes));
        await e.ProductoAsync(10, "PLU-FINA", "Pluma punto fino", depto: 5, costo: 4, precio: 9, existencia: 50);
        await e.EjecutarAsync("INSERT INTO VENTATICKETS (FOLIO, CAJA_ID, CAJERO_ID, OPERACION_ID, ESTA_ABIERTO, VENDIDO_EN) VALUES (41, 1, 1, -1, 'f', @cuando)",
            ("cuando", HoraLocal(180)));
        int? turno = abierta ? await e.AbrirCajaAsync(CajaEleventa, cajero: 7, HoraLocal(120), fondo: 300) : null;
        return (e, turno);
    }
}
