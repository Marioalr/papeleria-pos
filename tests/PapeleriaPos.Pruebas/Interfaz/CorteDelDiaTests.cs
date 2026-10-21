using System.Globalization;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Components;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.JSInterop;
using PapeleriaPos.Datos;
using PapeleriaPos.Impresion;
using PapeleriaPos.Interfaz;
using PapeleriaPos.Interfaz.Componentes;
using PapeleriaPos.Nucleo;
using PapeleriaPos.Nucleo.Contratos;
using PapeleriaPos.Nucleo.Control;
using PapeleriaPos.Nucleo.Ventas;
using PapeleriaPos.Pruebas.Servicio;

namespace PapeleriaPos.Pruebas.Interfaz;

/// <summary>
/// El corte del día como el de eleventa (2026-10-17), en la pantalla, sin navegador: se mueve de día con ← → o la fecha (el
/// de otro día solo se ve), cada quien ve lo que le toca (las ventas por departamento, todos los que ven el corte; la
/// utilidad, solo quien la ve), se imprime lo que se ve (también el de otro día) y, en la venta, el recordatorio de cerrar
/// el día sale a la hora de cierre y se va al cerrarlo. Cada prueba con su propio servicio.
/// </summary>
public sealed class CorteDelDiaTests
{
    private sealed record Caja(ClienteCaja Cliente, CajaDto Datos, ServiceProvider Servicios, SesionDto Cajero, SesionDto Encargado, SesionDto Dueno);

    /// <param name="red">Opcional: lo que se pone entre la caja y el servicio (para ver o cambiar lo que pasa).</param>
    private static async Task<Caja> PrepararAsync(ServicioDePrueba servicio, IImpresora? impresora = null, DelegatingHandler? red = null)
    {
        var conexion = new ConexionCaja { Servicio = new Uri("http://localhost/") };
        HttpMessageHandler alServicio = servicio.Fabrica.Server.CreateHandler();
        if (red is not null)
            (red.InnerHandler, alServicio) = (alServicio, red);
        var cliente = new ClienteCaja(new HttpClient(alServicio), conexion);
        var caja = (await cliente.CajasAsync()).Single();
        conexion.CajaId = caja.Id;
        var servicios = new ServiceCollection()
            .AddSingleton(conexion)
            .AddSingleton(cliente)
            .AddSingleton(impresora ?? new SinImpresora())
            .AddSingleton<IJSRuntime>(new PaginaQueContesta())
            .AddScoped<ModuloCaja>()
            .BuildServiceProvider();
        return new Caja(cliente, caja, servicios, await cliente.EntrarAsync("cajero", "1111"), await cliente.EntrarAsync("encargado", "2222"),
            await cliente.EntrarAsync("dueno", "3333"));
    }

    /// <summary>Una impresora que guarda lo que le llega, para leer lo que salió en el papel.</summary>
    private sealed class ImpresoraQueGuarda : IImpresora
    {
        private readonly List<byte[]> _enviados = [];

        public bool MuestraVistaPrevia => false;

        /// <summary>Lo impreso, como texto (la página 850 de la impresora).</summary>
        public string Texto
        {
            get
            {
                Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
                lock (_enviados)
                    return string.Concat(_enviados.Select(Encoding.GetEncoding(850).GetString));
            }
        }

        public Task EnviarAsync(byte[] escPos, CancellationToken ct = default)
        {
            lock (_enviados)
                _enviados.Add(escPos);
            return Task.CompletedTask;
        }
    }

    /// <summary>
    /// La red de la caja con una medianoche de mentiras: el siguiente «el de hoy» que se pide (sin fecha) llega con la fecha
    /// que se le dice, como si se hubiera pedido ese día antes de las 00:00 y ya fuera el siguiente. Guarda lo que la caja
    /// manda al cerrar el día y cuenta los cortes del día que pide.
    /// </summary>
    private sealed class MedianocheDeMentiras : DelegatingHandler
    {
        private DateOnly? _antes;

        /// <summary>Lo que mandó la caja en cada «Cerrar el día».</summary>
        public List<SolicitudCierreDia> Cierres { get; } = [];

        /// <summary>Los cortes del día que pidió la caja.</summary>
        public int Pedidos { get; private set; }

        public void ElSiguienteEsDe(DateOnly dia) => _antes = dia;

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage solicitud, CancellationToken ct)
        {
            var ruta = solicitud.RequestUri!.AbsolutePath;
            if (solicitud.Method == HttpMethod.Post && ruta.EndsWith("/corte-del-dia/cierre", StringComparison.Ordinal))
                Cierres.Add(JsonSerializer.Deserialize<SolicitudCierreDia>(await solicitud.Content!.ReadAsStringAsync(ct), JsonSerializerOptions.Web)!);
            var respuesta = await base.SendAsync(solicitud, ct);
            if (solicitud.Method != HttpMethod.Get || !ruta.EndsWith("/corte-del-dia", StringComparison.Ordinal))
                return respuesta;
            Pedidos++;
            if (_antes is { } antes && !solicitud.RequestUri.Query.Contains("fecha=", StringComparison.Ordinal) && respuesta.IsSuccessStatusCode)
            {
                _antes = null;
                var dia = JsonNode.Parse(await respuesta.Content.ReadAsStringAsync(ct))!;
                dia["fecha"] = antes.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
                respuesta.Content = JsonContent.Create(dia);
            }
            return respuesta;
        }
    }

    private static DateOnly Hoy => DateOnly.FromDateTime(HoraMexico.ALocal(DateTime.UtcNow));

    /// <summary>Como lo dice la pantalla: «Jueves 16 de octubre» (con el año si es de otro).</summary>
    private static string Titulo(DateOnly fecha)
    {
        var dia = fecha.ToString(fecha.Year == Hoy.Year ? "dddd d 'de' MMMM" : "dddd d 'de' MMMM 'de' yyyy", Formato.Mexico);
        return char.ToUpper(dia[0], Formato.Mexico) + dia[1..];
    }

    /// <summary>El corte (el de la venta y el del turno de otro cajero), en «Del día · todas las cajas».</summary>
    private static async Task<PantallaSinNavegador> DelDiaAsync(Caja c, SesionDto quien, TurnoDto turno)
    {
        var p = new PantallaSinNavegador(c.Servicios);
        await p.AbrirAsync<CorteCaja>(new()
        {
            [nameof(CorteCaja.Turno)] = turno,
            [nameof(CorteCaja.Sesion)] = quien,
            [nameof(CorteCaja.Caja)] = c.Datos.Nombre,
        });
        await p.EsperarAsync(t => !t.Contains("Cargando el turno…"), "el corte del turno");
        await p.OprimirAsync(await p.BotonAsync("Del día · todas las cajas"));
        await p.EsperarAsync(t => t.Contains("Vendido hoy"), "el corte del día");
        return p;
    }

    private static async Task<string> TituloDelDiaAsync(PantallaSinNavegador p) =>
        PantallaSinNavegador.Limpio((await p.ElementoAsync(e => e.Clase == "vn-corte-dia", "el día que se ve")).Texto);

    private static async Task<bool> HayBotonAsync(PantallaSinNavegador p, string texto) =>
        (await p.LeerAsync()).Elementos.Any(e => e.Etiqueta == "button" && PantallaSinNavegador.Limpio(e.Texto).StartsWith(texto, StringComparison.Ordinal));

    /// <summary>Lo que salió en la vista previa de «Imprimir» (sin impresora).</summary>
    private static async Task<string> ImpresoAsync(PantallaSinNavegador p)
    {
        await p.OprimirAsync(await p.BotonAsync("Imprimir"));
        return (await p.EsperarElementoAsync("corte-dia-impreso")).Texto;
    }

    /// <summary>
    /// Ayer: un turno del cajero con $25 en efectivo, cortado, a horas fijas (abrió a las 10:00, cobró a las 12:00 y se cortó a
    /// las 18:00): la prueba no depende de la hora en que corre.
    /// </summary>
    private static async Task TurnoDeAyerAsync(ServicioDePrueba servicio, Caja c, DateOnly ayer)
    {
        DateTime A(int hora) => HoraMexico.AUtc(ayer.ToDateTime(new TimeOnly(hora, 0)));
        var turno = await c.Cliente.AbrirTurnoAsync(c.Datos.Id, c.Cajero.UsuarioId, 50000);
        await c.Cliente.CobrarAsync(new SolicitudCobro(c.Datos.Id, turno.Id, c.Cajero.UsuarioId, [new LineaSolicitada(null, null, 1000, "Recuerdo", 2500)],
            [new CobroSolicitado(FormaPago.Efectivo, 2500)]));
        await c.Cliente.CorteZAsync(turno.Id, new SolicitudCorteZ(c.Encargado.UsuarioId, 52500));
        await using var db = servicio.Base();
        var deAyer = await db.Turnos.SingleAsync(t => t.Id == turno.Id);
        (deAyer.AbiertoUtc, deAyer.CerradoUtc) = (A(10), A(18));
        foreach (var venta in await db.Ventas.Where(v => v.TurnoId == turno.Id).ToListAsync())
        {
            venta.CreadaUtc = A(12);
            db.Entry(venta).Property(v => v.PagadaUtc).CurrentValue = A(12);
        }
        foreach (var corte in await db.Cortes.Where(x => x.TurnoId == turno.Id).ToListAsync())
            corte.FechaUtc = A(18);
        await db.SaveChangesAsync();
    }

    [Fact]
    public async Task Se_mueve_de_dia_con_las_flechas_y_la_fecha_y_el_de_otro_dia_solo_se_ve()
    {
        await using var servicio = await ServicioDePrueba.CrearAsync();
        var c = await PrepararAsync(servicio);
        var (hoy, ayer) = (Hoy, Hoy.AddDays(-1));
        await TurnoDeAyerAsync(servicio, c, ayer);
        var turno = await c.Cliente.AbrirTurnoAsync(c.Datos.Id, c.Dueno.UsuarioId, 50000);
        await using var p = await DelDiaAsync(c, c.Dueno, turno);

        // Hoy, como siempre: con «Cerrar el día» (espera al turno abierto) y sin día siguiente.
        Assert.Equal($"Hoy, {hoy.ToString("dddd d 'de' MMMM", Formato.Mexico)}", await TituloDelDiaAsync(p));
        Assert.True((await p.BotonAsync("Día siguiente")).Desactivado);
        Assert.True(await HayBotonAsync(p, "Cerrar el día"));
        Assert.Contains("Falta cerrar el turno de Dueño de prueba (Caja 1).", await p.TextoAsync());

        // ← : ayer, solo para verlo, con su turno y lo que vendió; sin «Cerrar el día» ni lo que falta para cerrarlo.
        await p.TeclaAsync("corte-dia", "ArrowLeft");
        await p.EsperarAsync(t => t.Contains("Es de un día que ya pasó: solo se ve y se imprime."), "el corte de ayer");
        var texto = await p.TextoAsync();
        Assert.Equal(Titulo(ayer), await TituloDelDiaAsync(p));
        Assert.Contains("Vendido ese día", texto);
        Assert.Contains("Ese día no se cerró", texto);
        Assert.Contains("Cajero de prueba", texto);
        Assert.DoesNotContain("Dueño de prueba", texto);
        Assert.DoesNotContain("Falta cerrar el turno", texto);
        Assert.False(await HayBotonAsync(p, "Cerrar el día"));
        Assert.False((await p.BotonAsync("Día siguiente")).Desactivado);
        Assert.Equal(ayer.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), (await p.PorIdAsync("corte-fecha")).Valor);

        // → : otra vez hoy, con sus reglas; y otro → no pasa de hoy.
        await p.TeclaAsync("corte-dia", "ArrowRight");
        await p.EsperarAsync(t => t.Contains("Vendido hoy"), "el corte de hoy");
        await p.TeclaAsync("corte-dia", "ArrowRight");
        Assert.True(await HayBotonAsync(p, "Cerrar el día"));
        Assert.StartsWith("Hoy, ", await TituloDelDiaAsync(p));

        // La fecha: cualquier día que ya pasó (antier no tuvo turnos); «Hoy» regresa.
        await p.DispararAsync(await p.PorIdAsync("corte-fecha"), "onchange",
            new ChangeEventArgs { Value = hoy.AddDays(-2).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) });
        await p.EsperarAsync(t => t.Contains("Ese día no se abrió ningún turno."), "el corte de antier");
        Assert.Equal(Titulo(hoy.AddDays(-2)), await TituloDelDiaAsync(p));
        await p.OprimirAsync(await p.BotonAsync("Hoy"));
        await p.EsperarAsync(t => t.Contains("Vendido hoy"), "el corte de hoy");

        // Un día que no ha llegado es hoy, y la fecha vuelve a decirlo; vacía, también dice el día que se ve.
        string Iso(DateOnly dia) => dia.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        await p.DispararAsync(await p.PorIdAsync("corte-fecha"), "onchange", new ChangeEventArgs { Value = Iso(hoy.AddDays(3)) });
        await p.EsperarAsync(t => t.Contains("Vendido hoy"), "el corte de hoy");
        Assert.Equal(Iso(hoy), (await p.PorIdAsync("corte-fecha")).Valor);
        await p.TeclaAsync("corte-dia", "ArrowLeft");
        await p.EsperarAsync(t => t.Contains("Es de un día que ya pasó"), "el corte de ayer");
        await p.DispararAsync(await p.PorIdAsync("corte-fecha"), "onchange", new ChangeEventArgs { Value = "" });
        Assert.Equal(Iso(ayer), (await p.PorIdAsync("corte-fecha")).Valor);
        Assert.Equal(Titulo(ayer), await TituloDelDiaAsync(p));
        Assert.Empty(p.Errores);
    }

    /// <summary>
    /// Pasada la medianoche con el corte del día a la vista, ese día ya no se cierra (revisión del 2026-10-17): al revisarlo
    /// (cada minuto) se vuelve a pedir y queda solo para verlo, sin «Cerrar el día»; y si alguien alcanza a oprimirlo antes, la
    /// caja manda el día que se ve, el servicio no cierra nada (ni ese día ni el nuevo) y la pantalla dice por qué y lo deja
    /// solo para verlo. El de hoy se cierra como siempre, con su fecha. La medianoche es de mentiras: el primer «el de hoy»
    /// llega con la fecha de ayer, como si se hubiera pedido antes de las 00:00.
    /// </summary>
    [Fact]
    public async Task Pasada_la_medianoche_el_dia_que_se_ve_ya_no_se_cierra()
    {
        await using var servicio = await ServicioDePrueba.CrearAsync();
        var red = new MedianocheDeMentiras();
        var c = await PrepararAsync(servicio, red: red);
        var (hoy, ayer) = (Hoy, Hoy.AddDays(-1));
        string Iso(DateOnly dia) => dia.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        async Task<int> CierresAsync()
        {
            await using var db = servicio.Base();
            return await db.Bitacora.CountAsync(b => b.Accion == AccionControlada.CorteDelDia && b.Resultado == ResultadoBitacora.Hecho);
        }
        // El corte del día, pedido antes de la medianoche: «hoy» es ayer, y se puede cerrar (no hay turnos abiertos).
        async Task<(PantallaSinNavegador Pantalla, CorteDelDia Corte)> DeAntesDeLaMedianocheAsync()
        {
            red.ElSiguienteEsDe(ayer);
            var p = new PantallaSinNavegador(c.Servicios);
            var corte = await p.AbrirAsync<CorteDelDia>(new()
            {
                [nameof(CorteDelDia.CajaId)] = c.Datos.Id,
                [nameof(CorteDelDia.Sesion)] = c.Dueno,
            });
            await p.EsperarAsync(t => t.Contains("Vendido hoy"), "el corte de «hoy»");
            Assert.Equal(Iso(ayer), (await p.PorIdAsync("corte-fecha")).Valor);
            Assert.False((await p.BotonAsync("Cerrar el día")).Desactivado);
            return (p, corte);
        }

        // Al revisarlo (lo que hace cada minuto): ya no es ese día; se vuelve a pedir, solo para verlo.
        var (p, corte) = await DeAntesDeLaMedianocheAsync();
        await using (p)
        {
            await p.Dispatcher.InvokeAsync(corte.RevisarElDiaAsync);
            await p.EsperarAsync(t => t.Contains("Es de un día que ya pasó: solo se ve y se imprime."), "el de ayer, solo para verlo");
            Assert.Equal(Titulo(ayer), await TituloDelDiaAsync(p));
            Assert.False(await HayBotonAsync(p, "Cerrar el día"));
            // Con el de hoy a la vista, y todavía hoy, no se vuelve a pedir.
            await p.OprimirAsync(await p.BotonAsync("Ver el de hoy"));
            await p.EsperarAsync(t => t.Contains("Vendido hoy"), "el de hoy");
            var pedidos = red.Pedidos;
            await p.Dispatcher.InvokeAsync(corte.RevisarElDiaAsync);
            Assert.Equal(pedidos, red.Pedidos);
            Assert.Equal(Iso(hoy), (await p.PorIdAsync("corte-fecha")).Valor);
            Assert.True(await HayBotonAsync(p, "Cerrar el día"));
            Assert.Empty(red.Cierres);
            Assert.Empty(p.Errores);
        }

        // Si alguien oprime «Cerrar el día» antes de esa revisión: la caja manda el día que se ve y el servicio no lo cierra.
        (p, corte) = await DeAntesDeLaMedianocheAsync();
        await using (p)
        {
            await p.OprimirAsync(await p.BotonAsync("Cerrar el día"));
            await p.EsperarAsync(t => t.Contains(RegistroCorteDelDia.SoloHoySeCierra), "por qué no se cerró");
            Assert.Equal(ayer, Assert.Single(red.Cierres).Fecha);
            Assert.Contains("Es de un día que ya pasó: solo se ve y se imprime.", await p.TextoAsync());
            Assert.Equal(Titulo(ayer), await TituloDelDiaAsync(p));
            Assert.False(await HayBotonAsync(p, "Cerrar el día"));
            // Ni ese día ni el nuevo quedaron cerrados.
            Assert.Equal(0, await CierresAsync());

            // El de hoy sí, con su fecha.
            await p.OprimirAsync(await p.BotonAsync("Ver el de hoy"));
            await p.EsperarAsync(t => t.Contains("Vendido hoy"), "el de hoy");
            Assert.DoesNotContain("Día cerrado", await p.TextoAsync());
            await p.OprimirAsync(await p.BotonAsync("Cerrar el día"));
            await p.EsperarAsync(t => t.Contains("Día cerrado a las"), "el día cerrado");
            Assert.Equal(hoy, red.Cierres[1].Fecha);
            Assert.Equal(1, await CierresAsync());
            Assert.Empty(p.Errores);
        }
    }

    [Fact]
    public async Task Cada_quien_ve_lo_suyo_y_se_imprime_lo_que_ve()
    {
        await using var servicio = await ServicioDePrueba.CrearAsync();
        var papel = new ImpresoraQueGuarda();
        var c = await PrepararAsync(servicio);
        var conImpresora = await PrepararAsync(servicio, papel);
        // El encargado ve el corte del día, pero no la utilidad.
        await c.Cliente.CambiarPermisoAsync(c.Datos.Id, c.Encargado.UsuarioId, new SolicitudPermisoUsuario(c.Dueno.UsuarioId, AccionControlada.CorteDelDia, NivelPermiso.Permitido));
        var (hoy, ayer) = (Hoy, Hoy.AddDays(-1));
        await TurnoDeAyerAsync(servicio, c, ayer);
        // Hoy: una pluma, una copia y un artículo común.
        var turno = await c.Cliente.AbrirTurnoAsync(c.Datos.Id, c.Cajero.UsuarioId, 50000);
        var pluma = (await c.Cliente.BuscarAsync("pluma punto fino", c.Datos.SucursalId)).First().VarianteId;
        var copia = (await c.Cliente.BuscarAsync("copia blanco y negro", c.Datos.SucursalId)).First().VarianteId;
        await c.Cliente.CobrarAsync(new SolicitudCobro(c.Datos.Id, turno.Id, c.Cajero.UsuarioId,
            [new LineaSolicitada(pluma, null, 1000), new LineaSolicitada(copia, null, 2000), new LineaSolicitada(null, null, 1000, "Recuerdo", 1500)],
            [new CobroSolicitado(FormaPago.Efectivo, 100000)]));

        // El dueño: la utilidad arriba y abajo, también la de cada departamento, con el artículo común aparte.
        await using (var p = await DelDiaAsync(c, c.Dueno, turno))
        {
            var texto = await p.TextoAsync();
            Assert.Contains("Utilidad bruta", texto);
            Assert.Contains("Ventas por departamento", texto);
            // Con las columnas de la utilidad y el margen de cada departamento.
            var encabezado = (await p.LeerAsync()).Elementos.First(e => e.Etiqueta == "tr" && e.Texto.Contains("Ventas por departamento"));
            Assert.Contains("Utilidad", encabezado.Texto);
            Assert.Contains("Margen", encabezado.Texto);
            Assert.Contains("Escritura", texto);
            Assert.Contains("Servicios", texto);
            Assert.Contains("Artículo común", texto);
            Assert.Contains("Utilidad del día", texto);
            Assert.Contains("todavía no tienen costo", texto);
            // Lo impreso es lo que ve: con las ventas por departamento y la utilidad.
            var impreso = await ImpresoAsync(p);
            Assert.Contains("CORTE DEL DÍA", impreso);
            Assert.Contains($"Todas las cajas · {hoy.ToString("dddd dd/MM/yyyy", Formato.Mexico)}", impreso);
            Assert.Contains("Lo imprimió: Dueño de prueba", impreso);
            Assert.Contains("El día todavía no se cierra.", impreso);
            Assert.Contains("UTILIDAD", impreso);
            Assert.Contains("VENTAS POR DEPARTAMENTO", impreso);
            Assert.Contains("Escritura", impreso);
            // El de ayer también se imprime: el suyo, que no se cerró.
            await p.TeclaAsync("corte-dia", "ArrowLeft");
            await p.EsperarAsync(t => t.Contains("Es de un día que ya pasó"), "el corte de ayer");
            Assert.DoesNotContain("Así sale impreso", await p.TextoAsync());
            var deAyer = await ImpresoAsync(p);
            Assert.Contains($"Todas las cajas · {ayer.ToString("dddd dd/MM/yyyy", Formato.Mexico)}", deAyer);
            Assert.Contains("Ese día no se cerró.", deAyer);
            Assert.Contains("Cajero de prueba", deAyer);
            Assert.Empty(p.Errores);
        }

        // El encargado: el corte del día con las ventas por departamento, como en eleventa, pero sin nada de la utilidad (ni
        // la de cada departamento), ni en la pantalla ni en el papel.
        await using (var p = await DelDiaAsync(c, c.Encargado, turno))
        {
            var texto = await p.TextoAsync();
            Assert.Contains("Formas de pago y movimientos", texto);
            Assert.Contains("Ventas por departamento", texto);
            Assert.Contains("Escritura", texto);
            Assert.Contains("Servicios", texto);
            Assert.Contains("Artículo común", texto);
            Assert.DoesNotContain("tilidad", texto);
            Assert.DoesNotContain("Margen", texto);
            Assert.DoesNotContain("sin costo", texto);
            var tabla = await p.ElementoAsync(e => e.Etiqueta == "table" && e.Texto.Contains("Ventas por departamento"), "las ventas por departamento");
            Assert.DoesNotContain("restringido", tabla.Clase);
            var impreso = await ImpresoAsync(p);
            Assert.Contains("CORTE DEL DÍA", impreso);
            Assert.Contains("VENTAS POR DEPARTAMENTO", impreso);
            Assert.Contains("Escritura", impreso);
            Assert.DoesNotContain("UTILIDAD", impreso);
            Assert.DoesNotContain("Utilidad", impreso);
            Assert.Empty(p.Errores);
        }

        // El cajero no tiene el corte del día.
        await using (var p = new PantallaSinNavegador(c.Servicios))
        {
            await p.AbrirAsync<CorteCaja>(new() { [nameof(CorteCaja.Turno)] = turno, [nameof(CorteCaja.Sesion)] = c.Cajero, [nameof(CorteCaja.Caja)] = c.Datos.Nombre });
            await p.EsperarAsync(t => t.Contains("Corte X · informativo"), "el corte X");
            Assert.False(await HayBotonAsync(p, "Del día"));
        }

        // Con impresora: va por la cola de la impresora de tickets (como el corte del turno) al papel, en la página 850, y lo
        // dice.
        await using (var p = await DelDiaAsync(conImpresora, conImpresora.Dueno, turno))
        {
            await p.OprimirAsync(await p.BotonAsync("Imprimir"));
            await p.EsperarAsync(t => t.Contains($"Corte del día {hoy.ToString("dd/MM", Formato.Mexico)} impreso"), "lo impreso");
            Assert.Contains("CORTE DEL DÍA", papel.Texto);
            Assert.Contains("UTILIDAD", papel.Texto);
            Assert.Contains("Lo imprimió: Dueño de prueba", papel.Texto);
            Assert.DoesNotContain("Así sale impreso", await p.TextoAsync());
            Assert.Empty(p.Errores);
        }
    }

    /// <summary>
    /// «Imprimir» va por la misma cola que el ticket y el corte del turno (entrega 5, parte 3): la pantalla no espera a la
    /// impresora; si no sale (se acabó el papel), lo dice arriba del corte con «Reintentar» y «No imprimirlo», y con
    /// «Reintentar» sale una sola vez.
    /// </summary>
    [Fact]
    public async Task Imprimirlo_va_por_la_cola_y_si_no_sale_se_reintenta()
    {
        await using var servicio = await ServicioDePrueba.CrearAsync();
        var sinPapel = true;
        var impresora = new PapeleriaPos.Pruebas.Impresion.TicketeraDePrueba
        {
            Falla = _ => Volatile.Read(ref sinPapel) ? new IOException("Se acabó el papel de la impresora de tickets.") : null,
        };
        var c = await PrepararAsync(servicio, impresora);
        var turno = await c.Cliente.AbrirTurnoAsync(c.Datos.Id, c.Cajero.UsuarioId, 50000);
        var titulo = $"Corte del día {Hoy.ToString("dd/MM", Formato.Mexico)}";

        await using var p = await DelDiaAsync(c, c.Dueno, turno);
        await p.OprimirAsync(await p.BotonAsync("Imprimir"));
        await p.EsperarAsync(t => t.Contains($"No salió «{titulo}»: Se acabó el papel de la impresora de tickets. El corte del día no cambia."),
            "el aviso de que no salió");
        Assert.Equal([titulo], ColaDeImpresion.De(impresora).Pendientes.Select(x => x.Titulo));
        Assert.Empty(impresora.Salieron);
        Assert.DoesNotContain("Así sale impreso", await p.TextoAsync());

        Volatile.Write(ref sinPapel, false);
        await p.OprimirAsync(await p.BotonAsync("Reintentar"));
        await p.EsperarAsync(t => t.Contains($"{titulo} impreso"), "el corte del día impreso");
        Assert.Single(impresora.Salieron);
        Assert.Empty(ColaDeImpresion.De(impresora).Pendientes);
        Assert.Empty(p.Errores);
    }

    [Fact]
    public async Task En_la_venta_el_recordatorio_sale_a_la_hora_de_cierre_y_se_va_al_cerrar_el_dia()
    {
        // Las 21:30 de hoy, para el servicio: ya pasó la hora de cierre (21:00, la de siempre).
        var reloj = new RelojFijo(new DateTimeOffset(HoraMexico.AUtc(Hoy.ToDateTime(new TimeOnly(21, 30))), TimeSpan.Zero));
        await using var servicio = await ServicioDePrueba.CrearAsync(s =>
        {
            s.RemoveAll<TimeProvider>();
            s.AddSingleton<TimeProvider>(reloj);
        });
        var c = await PrepararAsync(servicio);
        Dictionary<string, object?> Venta(SesionDto quien, TurnoDto turno) => new()
        {
            [nameof(PantallaVenta.Caja)] = c.Datos,
            [nameof(PantallaVenta.Sesion)] = quien,
            [nameof(PantallaVenta.Turno)] = turno,
        };

        // El cajero no cierra el día: a él no le sale.
        var delCajero = await c.Cliente.AbrirTurnoAsync(c.Datos.Id, c.Cajero.UsuarioId, 50000);
        await using (var p = new PantallaSinNavegador(c.Servicios))
        {
            await p.AbrirAsync<PantallaVenta>(Venta(c.Cajero, delCajero));
            await p.EsperarAsync(t => t.Contains("Escanea el primer producto"), "la venta");
            Assert.DoesNotContain("Falta cerrar el día", await p.TextoAsync());
            Assert.Empty(p.Errores);
        }
        await c.Cliente.CorteZAsync(delCajero.Id, new SolicitudCorteZ(c.Encargado.UsuarioId, 50000));

        // El dueño, en su venta: el aviso, con los demás del panel, sin ventana.
        var delDueno = await c.Cliente.AbrirTurnoAsync(c.Datos.Id, c.Dueno.UsuarioId, 50000);
        await using var venta = new PantallaSinNavegador(c.Servicios);
        await venta.AbrirAsync<PantallaVenta>(Venta(c.Dueno, delDueno));
        await venta.EsperarAsync(t => t.Contains("Falta cerrar el día."), "el recordatorio");
        var aviso = await venta.ElementoAsync(e => e.Clase?.Split(' ').Contains("vn-recordatorio-dia") == true, "el recordatorio");
        Assert.Contains("La tienda cierra a las 21:00: haz el corte de los turnos y ciérralo en «Corte › Del día».", PantallaSinNavegador.Limpio(aviso.Texto));
        // Sin ventana encima: es un aviso, no hay que cerrarlo para seguir cobrando.
        Assert.DoesNotContain((await venta.LeerAsync()).Elementos, e => e.Clase?.Contains("fondo-ventana") == true);

        // «Ir al corte»: su turno (a ciegas, con Enter) y luego el día, que ya se puede cerrar.
        await venta.OprimirAsync(await venta.BotonAsync("Ir al corte"));
        await venta.EsperarElementoAsync("corte-contado");
        await venta.EscribirAsync("corte-contado", "500");
        await venta.TeclaAsync("corte-contado", "Enter");
        await venta.EsperarAsync(t => t.Contains("Turno de Dueño de prueba cerrado"), "el corte del turno");
        Assert.Contains("Falta cerrar el día.", await venta.TextoAsync());
        await venta.OprimirAsync(await venta.BotonAsync("Ver el corte del día"));
        await venta.EsperarAsync(t => t.Contains("Junta los cortes de todas las cajas"), "el corte del día");
        var cerrar = await venta.BotonAsync("Cerrar el día");
        Assert.False(cerrar.Desactivado);
        await venta.OprimirAsync(cerrar);

        // Cerrado el día, el recordatorio se va sin esperar al siguiente minuto.
        await venta.EsperarAsync(t => t.Contains("Día cerrado") && !t.Contains("Falta cerrar el día."), "el día cerrado, sin el recordatorio");
        Assert.Empty(venta.Errores);
    }
}
