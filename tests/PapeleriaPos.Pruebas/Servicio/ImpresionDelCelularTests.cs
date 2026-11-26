using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Json;
using System.Net.Sockets;
using Microsoft.EntityFrameworkCore;
using PapeleriaPos.Impresion;
using PapeleriaPos.Interfaz;
using PapeleriaPos.Nucleo;
using PapeleriaPos.Nucleo.Contratos;
using PapeleriaPos.Nucleo.Control;
using PapeleriaPos.Nucleo.Ventas;

namespace PapeleriaPos.Pruebas.Servicio;

/// <summary>
/// Imprimir el ticket de lo que cobra el celular (dueño, 2026-11-26; docs/celular-como-caja.md, paso 11). El celular nunca le habla a la
/// impresora: lo pide en el cobro (<c>imprimir</c>) o después (<c>POST …/ventas/{id}/imprimir</c>), y sale en la impresora de la caja en
/// cuyo turno entró: con una de red, el servicio se la manda directo (aquí, un puerto TCP de esta PC que recibe los bytes); con una de
/// Windows, la recoge la ventana de la caja. Sin impresora de verdad.
/// </summary>
public sealed class ImpresionDelCelularTests
{
    private const string Codigo = "36000291452";

    private static async Task<T> Leer<T>(HttpResponseMessage r)
    {
        Assert.True(r.IsSuccessStatusCode, await r.Content.ReadAsStringAsync());
        return (await r.Content.ReadFromJsonAsync<T>())!;
    }

    private static async Task<ErrorDto> Error(HttpResponseMessage r, HttpStatusCode estado)
    {
        Assert.True(estado == r.StatusCode, $"{r.StatusCode}: {await r.Content.ReadAsStringAsync()}");
        return (await r.Content.ReadFromJsonAsync<ErrorDto>())!;
    }

    /// <summary>
    /// Una impresora de red de mentiras (un puerto TCP de esta PC): recibe cada papel hasta la pregunta del papel (GS r 1) y contesta
    /// con <see cref="Estado"/> (0, bien; 0x0C, sin papel), o cierra sin contestar si es null (no confirmó).
    /// </summary>
    private sealed class ImpresoraDeMentiras : IAsyncDisposable
    {
        private readonly TcpListener _escucha = new(IPAddress.Loopback, 0);
        private readonly CancellationTokenSource _fin = new();
        private readonly Task _atiende;

        public ImpresoraDeMentiras()
        {
            _escucha.Start();
            Puerto = ((IPEndPoint)_escucha.LocalEndpoint).Port;
            _atiende = AtenderAsync();
        }

        public int Puerto { get; }
        public string Direccion => $"tcp://127.0.0.1:{Puerto}";
        public byte? Estado { get; set; } = 0;
        public ConcurrentQueue<byte[]> Papeles { get; } = new();

        private async Task AtenderAsync()
        {
            try
            {
                while (!_fin.IsCancellationRequested)
                {
                    using var cliente = await _escucha.AcceptTcpClientAsync(_fin.Token);
                    var canal = cliente.GetStream();
                    var datos = new List<byte>();
                    var buffer = new byte[4096];
                    while (!(datos.Count >= 3 && datos[^3] == 0x1D && datos[^2] == (byte)'r' && datos[^1] == 1))
                    {
                        var n = await canal.ReadAsync(buffer, _fin.Token);
                        if (n == 0)
                            break;
                        datos.AddRange(buffer.AsSpan(0, n));
                    }
                    Papeles.Enqueue(datos.Take(Math.Max(0, datos.Count - 3)).ToArray());
                    if (Estado is { } estado)
                        await canal.WriteAsync(new[] { estado }, _fin.Token);
                }
            }
            catch (Exception ex) when (ex is OperationCanceledException or SocketException or ObjectDisposedException or IOException)
            {
            }
        }

        public async ValueTask DisposeAsync()
        {
            await _fin.CancelAsync();
            _escucha.Stop();
            await _atiende.ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
            _fin.Dispose();
        }
    }

    /// <summary>La caja principal con su turno abierto (del encargado), el celular dado de alta y un producto de $23.50.</summary>
    private sealed record Tienda(ServicioDePrueba Servicio, HttpClient Principal, CajaDto Caja, TurnoDto Turno, HttpClient Celular, Guid CelularId,
        ProductoVentaDto Producto, SesionDto Cajero, SesionDto Encargado, SesionDto Dueno) : IAsyncDisposable
    {
        public async ValueTask DisposeAsync() => await Servicio.DisposeAsync();

        /// <summary>Cobra uno del producto con $30: $23.50, en efectivo $24, cambio $6.</summary>
        public Task<HttpResponseMessage> Cobrar(Guid id, bool? imprimir, Guid? cajero = null) => Celular.PostAsJsonAsync($"api/cajas/{CelularId}/cobrar",
            new SolicitudCobroDelCelular(id, [new RenglonMandado(Producto.VarianteId, null, null, 1000)], [new CobroSolicitado(FormaPago.Efectivo, 3000)],
                CajeroId: cajero, Imprimir: imprimir));

        public Task<HttpResponseMessage> Imprimir(Guid ventaId, SolicitudImprimirDelCelular? s = null) =>
            Celular.PostAsJsonAsync($"api/cajas/{CelularId}/ventas/{ventaId}/imprimir", s ?? new SolicitudImprimirDelCelular());

        /// <summary>«Imprimir lo que cobra el celular», en Configuración › Ticket (lo prende el dueño).</summary>
        public async Task PrenderAsync(bool prendido = true) =>
            Assert.Equal(prendido, (await Leer<ConfiguracionTicketDto>(await Principal.PutAsJsonAsync($"api/cajas/{Caja.Id}/configuracion/ticket/imprimir-lo-del-celular",
                new SolicitudImprimirLoDelCelular(Dueno.UsuarioId, prendido)))).ImprimirLoDelCelular);

        /// <summary>La ventana de la caja pregunta qué imprimir y dice qué impresora de tickets tiene.</summary>
        public async Task<List<TicketPorImprimirDto>> VentanaAsync(string impresora) =>
            (await Principal.GetFromJsonAsync<List<TicketPorImprimirDto>>($"api/cajas/{Caja.Id}/por-imprimir?impresora={Uri.EscapeDataString(impresora)}"))!;

        public async Task SalioAsync(Guid id, bool salio, string? motivo = null) =>
            (await Principal.PostAsJsonAsync($"api/cajas/{Caja.Id}/por-imprimir", new SolicitudTicketImpreso(id, salio, motivo))).EnsureSuccessStatusCode();

        public async Task<DondeImprimeDto> DondeAsync() =>
            (await Celular.GetFromJsonAsync<List<CajaParaMandarDto>>($"api/cajas/{CelularId}/para-mandar"))!.Single(c => c.Id == Caja.Id).Impresion!;

        public async Task<Venta> VentaAsync(Guid id)
        {
            await using var db = Servicio.Base();
            return await db.Ventas.AsNoTracking().SingleAsync(v => v.Id == id);
        }

        /// <summary>Lo que quedó en la bitácora de esa venta, en orden.</summary>
        public async Task<List<(AccionControlada Accion, string Detalle, Guid? Caja)>> BitacoraAsync(Guid ventaId)
        {
            await using var db = Servicio.Base();
            return (await db.Bitacora.AsNoTracking().Where(b => b.VentaId == ventaId).ToListAsync()).OrderBy(b => b.FechaUtc)
                .Select(b => (b.Accion, b.Detalle, b.CajaId)).ToList();
        }
    }

    private static async Task<Tienda> TiendaAsync(params (string, string)[] ajustes)
    {
        var servicio = await ServicioDePrueba.CrearAsync(ajustes);
        var principal = servicio.Fabrica.CreateClient();
        var caja = (await principal.GetFromJsonAsync<List<CajaDto>>("api/cajas"))!.Single();
        var cajero = await Leer<SesionDto>(await principal.PostAsJsonAsync("api/sesion", new SolicitudEntrada("cajero", "1111")));
        var encargado = await Leer<SesionDto>(await principal.PostAsJsonAsync("api/sesion", new SolicitudEntrada("encargado", "2222")));
        var dueno = await Leer<SesionDto>(await principal.PostAsJsonAsync("api/sesion", new SolicitudEntrada("dueno", "3333")));
        var turno = await Leer<TurnoDto>(await principal.PostAsJsonAsync("api/turnos", new SolicitudTurno(caja.Id, encargado.UsuarioId, 50000)));

        var celular = servicio.Fabrica.CreateClient();
        var alta = await Leer<AltaCajaDto>(await celular.PostAsJsonAsync("api/alta",
            new SolicitudAltaCaja("dueno", "3333", caja.SucursalId, null, "Celular de Luis", "CEL", "Pixel 8")));
        celular.DefaultRequestHeaders.Add(ClienteCaja.EncabezadoLlave, alta.Llave);

        var departamento = (await principal.GetFromJsonAsync<OpcionesCatalogoDto>("api/catalogo/opciones"))!.Departamentos[0].Id;
        await Leer<FichaProductoDto>(await principal.PostAsJsonAsync("api/catalogo/productos", new SolicitudGuardarProducto(encargado.UsuarioId, caja.SucursalId,
            new FichaProductoDto(null, "Corrector de cinta", departamento, "Marca de Prueba", PapeleriaPos.Nucleo.Catalogo.TipoProducto.Mercancia,
                PapeleriaPos.Nucleo.Catalogo.Impuesto.Iva16, null, true, false,
                [
                    new VarianteFichaDto(null, "", PapeleriaPos.Nucleo.Catalogo.UnidadBase.Pieza, true,
                        [new PresentacionFichaDto(null, "Pieza", 1000, true, [new CodigoFichaDto(Codigo)])], [new PrecioFichaDto(null, 1000, 2350)]),
                ]))));
        var producto = await Leer<ProductoVentaDto>(await celular.GetAsync($"api/productos/codigo/{Codigo}?sucursalId={caja.SucursalId}"));
        return new Tienda(servicio, principal, caja, turno, celular, alta.Caja.Id, producto, cajero, encargado, dueno);
    }

    [Fact]
    public async Task Con_impresora_de_red_el_servicio_la_manda_directo_y_la_venta_deja_de_estar_sin_imprimir()
    {
        await using var t = await TiendaAsync();
        await using var impresora = new ImpresoraDeMentiras();
        await t.PrenderAsync();
        // La ventana de la caja dice su impresora una vez; con una de red no recoge nada: el servicio manda directo.
        Assert.Empty(await t.VentanaAsync(impresora.Direccion));
        Assert.Equal(new DondeImprimeDto(true, DondeImprimeDto.PorRed, null, $"Sale en la impresora de la {t.Caja.Nombre}.", true), await t.DondeAsync());

        var id = Ids.Nuevo();
        var cobrado = await Leer<CobroDelCelularDto>(await t.Cobrar(id, imprimir: true));
        Assert.Equal(new ImpresionDelCelularDto(ImpresionDelCelularDto.Impreso, null, $"Salió en la impresora de la {t.Caja.Nombre}.", t.Caja.Id, t.Caja.Nombre,
            DondeImprimeDto.PorRed), cobrado.Impresion);
        // El mismo ticket que imprime la caja (el que contesta el cobro), sin el pulso del cajón.
        var papel = Assert.Single(impresora.Papeles);
        Assert.Equal(TicketEscPos.Generar(DisenoTicket.Armar(cobrado.Ticket), false), papel);
        Assert.NotEqual(TicketEscPos.Generar(DisenoTicket.Armar(cobrado.Ticket), true), papel);
        Assert.True(cobrado.Ticket.AbreCajon);
        // Ya salió: no está «sin imprimir», cuenta una impresión, y no hay nada que anotar (como el que la caja imprime al cobrar).
        var venta = await t.VentaAsync(id);
        Assert.Equal((false, 1), (venta.TicketSinImprimir, venta.TicketImpresiones));
        Assert.Empty(await t.BitacoraAsync(id));

        // El cobro otra vez (se perdió la respuesta): ni cobra ni imprime otra vez.
        var otraVez = await Leer<CobroDelCelularDto>(await t.Cobrar(id, imprimir: true));
        Assert.True(otraVez.YaCobrada);
        Assert.Equal((ImpresionDelCelularDto.Impreso, $"El ticket {cobrado.Folio} ya había salido."), (otraVez.Impresion!.Estado, otraVez.Impresion.Mensaje));
        Assert.Single(impresora.Papeles);

        // Pedirlo otra vez ya es una reimpresión: al encargado no se le pide motivo; queda en la bitácora, con el celular.
        var peticion = new SolicitudImprimirDelCelular(Ids.Nuevo(), t.Encargado.UsuarioId);
        var reimpreso = await Leer<ImpresionDelCelularDto>(await t.Imprimir(id, peticion));
        Assert.Equal(ImpresionDelCelularDto.Impreso, reimpreso.Estado);
        Assert.Equal(2, impresora.Papeles.Count);
        // La misma petición otra vez (se perdió la respuesta): no sale otro papel.
        Assert.Equal(reimpreso, await Leer<ImpresionDelCelularDto>(await t.Imprimir(id, peticion)));
        Assert.Equal(2, impresora.Papeles.Count);
        Assert.Equal((false, 2), ((await t.VentaAsync(id)).TicketSinImprimir, (await t.VentaAsync(id)).TicketImpresiones));
        Assert.Equal([(AccionControlada.Reimprimir, $"Reimpresión del ticket {cobrado.Folio}, pedida desde Celular de Luis en la impresora de la {t.Caja.Nombre}",
            (Guid?)t.CelularId)], await t.BitacoraAsync(id));

        // Al cajero sí se le pide motivo para reimprimir (su permiso): sin él, no sale; con él, sí.
        var sinMotivo = await Error(await t.Imprimir(id, new SolicitudImprimirDelCelular(Ids.Nuevo(), t.Cajero.UsuarioId)), HttpStatusCode.BadRequest);
        Assert.Equal(("con-motivo", $"El ticket {cobrado.Folio} ya salió: para reimprimirlo, escribe el motivo."), (sinMotivo.Codigo, sinMotivo.Mensaje));
        Assert.Equal(2, impresora.Papeles.Count);
        Assert.True((await Leer<ImpresionDelCelularDto>(await t.Imprimir(id, new SolicitudImprimirDelCelular(Ids.Nuevo(), t.Cajero.UsuarioId, "El cliente lo perdió")))).Salio);
        Assert.Equal(3, impresora.Papeles.Count);
        Assert.EndsWith(". Motivo: El cliente lo perdió", (await t.BitacoraAsync(id))[^1].Detalle);
    }

    [Fact]
    public async Task Sin_papel_apagada_o_sin_confirmar_lo_dice_y_la_venta_sigue_sin_imprimir_hasta_que_sale()
    {
        await using var t = await TiendaAsync();
        await t.PrenderAsync();
        var id = Ids.Nuevo();
        await using (var impresora = new ImpresoraDeMentiras { Estado = 0x0C })
        {
            await t.VentanaAsync(impresora.Direccion);
            // Sin papel: se cobró igual; el ticket no salió, y lo dice.
            var cobrado = await Leer<CobroDelCelularDto>(await t.Cobrar(id, imprimir: true));
            Assert.False(cobrado.YaCobrada);
            Assert.Equal(new ImpresionDelCelularDto(ImpresionDelCelularDto.NoImpreso, CodigosDeImpresion.SinPapel,
                $"Se acabó el papel de la impresora de la {t.Caja.Nombre}: pon papel y pídelo otra vez.", t.Caja.Id, t.Caja.Nombre, DondeImprimeDto.PorRed), cobrado.Impresion);
            var venta = await t.VentaAsync(id);
            Assert.Equal((true, 0), (venta.TicketSinImprimir, venta.TicketImpresiones));
            Assert.Equal([(AccionControlada.CobrarSinTicket,
                $"Cobró en Celular de Luis, en el turno de la {t.Caja.Nombre}, sin imprimir el ticket: se pidió y no salió (sin-papel)", (Guid?)t.CelularId)],
                await t.BitacoraAsync(id));

            // No confirmó (cerró sin contestar): pudo salir; no se manda otra vez solo.
            impresora.Estado = null;
            var sinConfirmar = await Leer<ImpresionDelCelularDto>(await t.Imprimir(id));
            Assert.Equal((ImpresionDelCelularDto.NoImpreso, CodigosDeImpresion.SinConfirmar,
                $"La impresora de la {t.Caja.Nombre} no terminó de confirmar: mira si salió antes de pedirlo otra vez."),
                (sinConfirmar.Estado, sinConfirmar.Codigo, sinConfirmar.Mensaje));
            Assert.Equal(2, impresora.Papeles.Count);
            Assert.True((await t.VentaAsync(id)).TicketSinImprimir);

            // Ya con papel: pedirlo después es su primera impresión (sin motivo, aunque lo pida el cajero) y deja de estar sin imprimir.
            impresora.Estado = 0;
            var impreso = await Leer<ImpresionDelCelularDto>(await t.Imprimir(id, new SolicitudImprimirDelCelular(Ids.Nuevo(), t.Cajero.UsuarioId)));
            Assert.Equal(ImpresionDelCelularDto.Impreso, impreso.Estado);
            Assert.Equal((false, 1), ((await t.VentaAsync(id)).TicketSinImprimir, (await t.VentaAsync(id)).TicketImpresiones));
            var bitacora = await t.BitacoraAsync(id);
            Assert.Equal(2, bitacora.Count);
            Assert.Equal((AccionControlada.Reimprimir, $"Primera impresión del ticket {cobrado.Folio}: no salió al cobrar (no es una reimpresión); "
                + $"la pidió Celular de Luis y salió en la {t.Caja.Nombre}", (Guid?)t.Caja.Id), bitacora[1]);
        }

        // La impresora apagada (nadie en ese puerto): no se le mandó nada; el cobro se contesta igual.
        var otra = Ids.Nuevo();
        var apagada = await Leer<CobroDelCelularDto>(await t.Cobrar(otra, imprimir: true));
        Assert.Equal((ImpresionDelCelularDto.NoImpreso, CodigosDeImpresion.NoContesta,
            $"La impresora de la {t.Caja.Nombre} no contesta: revisa que esté prendida y conectada, y pídelo otra vez."),
            (apagada.Impresion!.Estado, apagada.Impresion.Codigo, apagada.Impresion.Mensaje));
        Assert.True((await t.VentaAsync(otra)).TicketSinImprimir);
        // Y desde la caja, su primera impresión sigue sin pedir motivo.
        await Leer<TicketDto>(await t.Principal.PostAsJsonAsync($"api/ventas/{otra}/primera-impresion", new SolicitudPrimeraImpresion(t.Cajero.UsuarioId, t.Caja.Id)));
    }

    [Fact]
    public async Task Apagado_o_sin_impresora_no_imprime_y_dice_por_que()
    {
        await using var t = await TiendaAsync();
        // Como llega: apagado. La casilla del celular sale apagada con su razón.
        var apagado = "Está apagado «Imprimir lo que cobra el celular»: se prende en la caja, en Configuración › Ticket.";
        Assert.Equal(new DondeImprimeDto(false, null, CodigosDeImpresion.Apagado, apagado, false), await t.DondeAsync());
        // Una app de antes (no dice si imprimir): como siempre, sin imprimir y con su renglón en la bitácora.
        var deAntes = Ids.Nuevo();
        Assert.Equal(new ImpresionDelCelularDto(ImpresionDelCelularDto.NoPedida, null, "No se pidió imprimir el ticket."),
            (await Leer<CobroDelCelularDto>(await t.Cobrar(deAntes, imprimir: null))).Impresion);
        Assert.Equal([(AccionControlada.CobrarSinTicket, $"Cobró en Celular de Luis, en el turno de la {t.Caja.Nombre}, sin imprimir el ticket", (Guid?)t.CelularId)],
            await t.BitacoraAsync(deAntes));
        // Lo pide y está apagado: se cobra, no sale, y dice por qué.
        var pedido = Ids.Nuevo();
        var noSale = (await Leer<CobroDelCelularDto>(await t.Cobrar(pedido, imprimir: true))).Impresion!;
        Assert.Equal((ImpresionDelCelularDto.NoImpreso, CodigosDeImpresion.Apagado, apagado, (Guid?)t.Caja.Id), (noSale.Estado, noSale.Codigo, noSale.Mensaje, noSale.CajaId));
        Assert.Equal([(AccionControlada.CobrarSinTicket,
            $"Cobró en Celular de Luis, en el turno de la {t.Caja.Nombre}, sin imprimir el ticket: se pidió y no salió (apagado)", (Guid?)t.CelularId)],
            await t.BitacoraAsync(pedido));
        Assert.True((await t.VentaAsync(pedido)).TicketSinImprimir);
        // Y pedirlo después, igual.
        Assert.Equal(CodigosDeImpresion.Apagado, (await Leer<ImpresionDelCelularDto>(await t.Imprimir(pedido))).Codigo);

        // Prendido (queda en la bitácora), pero la ventana de la caja todavía no dice qué impresora tiene.
        await t.PrenderAsync();
        await using (var db = t.Servicio.Base())
            Assert.True(await db.Bitacora.AnyAsync(b => b.Accion == AccionControlada.ConfigurarTicket && b.UsuarioId == t.Dueno.UsuarioId
                && b.Detalle == "Prendió «Imprimir lo que cobra el celular»"));
        Assert.Equal(new DondeImprimeDto(false, null, CodigosDeImpresion.SinImpresora,
            $"La {t.Caja.Nombre} todavía no dice qué impresora de tickets tiene: abre su ventana.", true), await t.DondeAsync());
        // La ventana abre sin impresora (el ticket se ve en pantalla).
        Assert.Empty(await t.VentanaAsync(""));
        var sinImpresora = new DondeImprimeDto(false, null, CodigosDeImpresion.SinImpresora, $"La {t.Caja.Nombre} no tiene impresora de tickets configurada.", true);
        Assert.Equal(sinImpresora, await t.DondeAsync());
        // Prendido y sin decir nada el celular: se intenta (es lo automático), y dice que no hay impresora.
        var automatico = (await Leer<CobroDelCelularDto>(await t.Cobrar(Ids.Nuevo(), imprimir: null))).Impresion!;
        Assert.Equal((ImpresionDelCelularDto.NoImpreso, sinImpresora.Codigo, sinImpresora.Mensaje), (automatico.Estado, automatico.Codigo, automatico.Mensaje));
        // Y el celular puede decir que no, aunque esté prendido.
        Assert.Equal(ImpresionDelCelularDto.NoPedida, (await Leer<CobroDelCelularDto>(await t.Cobrar(Ids.Nuevo(), imprimir: false))).Impresion!.Estado);
        // Una dirección de red mal escrita cuenta como sin impresora.
        await t.VentanaAsync("tcp://");
        Assert.Equal(sinImpresora, await t.DondeAsync());

        // Solo quien tiene «Configurar el ticket» lo prende o lo apaga.
        var sinPermiso = await Error(await t.Principal.PutAsJsonAsync($"api/cajas/{t.Caja.Id}/configuracion/ticket/imprimir-lo-del-celular",
            new SolicitudImprimirLoDelCelular(t.Cajero.UsuarioId, false)), HttpStatusCode.Forbidden);
        Assert.Equal("no-permitido", sinPermiso.Codigo);
        await t.PrenderAsync(false);
        Assert.Equal(CodigosDeImpresion.Apagado, (await t.DondeAsync()).Codigo);
    }

    [Fact]
    public async Task Con_impresora_de_Windows_la_recoge_la_ventana_de_la_caja_y_dice_si_salio()
    {
        await using var t = await TiendaAsync();
        await t.PrenderAsync();
        // La ventana de la caja está abierta (pregunta) y su impresora es de Windows: sale ahí.
        Assert.Empty(await t.VentanaAsync("POS-80C"));
        Assert.Equal(new DondeImprimeDto(true, DondeImprimeDto.PorCaja, null, $"Sale en la impresora de la {t.Caja.Nombre}.", true), await t.DondeAsync());

        // El cobro espera a que la ventana lo recoja y diga que salió.
        var id = Ids.Nuevo();
        var cobro = t.Cobrar(id, imprimir: true);
        var recogido = Assert.Single(await RecogerAsync(t));
        Assert.Equal((id, "Celular de Luis"), (recogido.VentaId, recogido.De));
        Assert.False(cobro.IsCompleted);
        // Si pregunta otra vez antes de decir que salió (se perdió la respuesta), es el mismo: la ventana no lo imprime dos veces.
        Assert.Equal(recogido.Id, Assert.Single(await t.VentanaAsync("POS-80C")).Id);
        await t.SalioAsync(recogido.Id, true);
        var cobrado = await Leer<CobroDelCelularDto>(await cobro);
        Assert.Equal(new ImpresionDelCelularDto(ImpresionDelCelularDto.Impreso, null, $"Salió en la impresora de la {t.Caja.Nombre}.", t.Caja.Id, t.Caja.Nombre,
            DondeImprimeDto.PorCaja), cobrado.Impresion);
        // El mismo ticket que contesta el cobro.
        Assert.Equal((cobrado.Folio, cobrado.Ticket.TotalCentavos, id), (recogido.Ticket.Folio, recogido.Ticket.TotalCentavos, recogido.Ticket.VentaId!.Value));
        Assert.Equal((false, 1), ((await t.VentaAsync(id)).TicketSinImprimir, (await t.VentaAsync(id)).TicketImpresiones));
        Assert.Empty(await t.BitacoraAsync(id));
        Assert.Empty(await t.VentanaAsync("POS-80C"));

        // No salió de la impresora de la caja (allá se queda en su cola): lo dice, y la venta sigue sin imprimir.
        var otra = Ids.Nuevo();
        cobro = t.Cobrar(otra, imprimir: true);
        recogido = Assert.Single(await RecogerAsync(t));
        await t.SalioAsync(recogido.Id, false, "La impresora POS-80C está sin conexión.");
        var noSalio = (await Leer<CobroDelCelularDto>(await cobro)).Impresion!;
        Assert.Equal((ImpresionDelCelularDto.NoImpreso, CodigosDeImpresion.NoSalio,
            $"No salió de la impresora de la {t.Caja.Nombre}: La impresora POS-80C está sin conexión. En esa caja se reintenta o se deja sin imprimir."),
            (noSalio.Estado, noSalio.Codigo, noSalio.Mensaje));
        Assert.True((await t.VentaAsync(otra)).TicketSinImprimir);
        Assert.Empty(await t.VentanaAsync("POS-80C"));
        // En la caja le dieron «Reintentar» y salió: la ventana lo dice y la venta deja de estar sin imprimir, con su renglón.
        await t.SalioAsync(recogido.Id, true);
        Assert.Equal((false, 1), ((await t.VentaAsync(otra)).TicketSinImprimir, (await t.VentaAsync(otra)).TicketImpresiones));
        var bitacora = await t.BitacoraAsync(otra);
        Assert.Equal([AccionControlada.CobrarSinTicket, AccionControlada.Reimprimir], bitacora.Select(b => b.Accion));
        Assert.EndsWith("sin imprimir el ticket: se pidió y no salió (no-salio)", bitacora[0].Detalle);
        Assert.StartsWith("Primera impresión del ticket", bitacora[1].Detalle);
        // Decirlo dos veces no cuenta dos impresiones.
        await t.SalioAsync(recogido.Id, true);
        Assert.Equal(1, (await t.VentaAsync(otra)).TicketImpresiones);
    }

    /// <summary>La ventana pregunta (como cada 2 s) hasta que le llega algo.</summary>
    private static async Task<List<TicketPorImprimirDto>> RecogerAsync(Tienda t, string impresora = "POS-80C")
    {
        var limite = DateTime.UtcNow.AddSeconds(20);
        while (true)
        {
            if (await t.VentanaAsync(impresora) is { Count: > 0 } llegaron)
                return llegaron;
            Assert.True(DateTime.UtcNow < limite, "La ventana de la caja no recibió el ticket del celular.");
            await Task.Delay(30);
        }
    }

    [Fact]
    public async Task Si_la_ventana_de_la_caja_no_esta_abierta_o_no_alcanza_a_decir_que_salio_lo_dice()
    {
        // El celular espera poco a la ventana: aquí, un segundo.
        await using var t = await TiendaAsync(("Celular:SegundosParaImprimir", "1"));
        await t.PrenderAsync();
        // La caja tiene una impresora de Windows (lo dijo su ventana otro día), pero hoy su ventana no está abierta.
        await using (var db = t.Servicio.Base())
        {
            (await db.Cajas.SingleAsync(c => c.Id == t.Caja.Id)).PonerImpresoraDeTickets("POS-80C");
            await db.SaveChangesAsync();
        }
        var cerrada = new DondeImprimeDto(false, DondeImprimeDto.PorCaja, CodigosDeImpresion.VentanaCerrada,
            $"La {t.Caja.Nombre} no tiene su ventana abierta (o está apagada): ábrela para que imprima.", true);
        Assert.Equal(cerrada, await t.DondeAsync());
        var id = Ids.Nuevo();
        var sinVentana = (await Leer<CobroDelCelularDto>(await t.Cobrar(id, imprimir: true))).Impresion!;
        Assert.Equal((ImpresionDelCelularDto.NoImpreso, cerrada.Codigo, cerrada.Mensaje), (sinVentana.Estado, sinVentana.Codigo, sinVentana.Mensaje));
        Assert.True((await t.VentaAsync(id)).TicketSinImprimir);

        // Abre la ventana: lo de antes no le llega (no se imprime tarde); se pide otra vez y ahora sí lo recoge, pero no alcanza a
        // decir que salió antes de que el celular deje de esperar: va en camino.
        Assert.Empty(await t.VentanaAsync("POS-80C"));
        var pedido = t.Imprimir(id);
        var recogido = Assert.Single(await RecogerAsync(t));
        var enCamino = await Leer<ImpresionDelCelularDto>(await pedido);
        Assert.Equal((ImpresionDelCelularDto.EnCamino, (string?)null, $"La {t.Caja.Nombre} ya lo tiene y lo está imprimiendo.", DondeImprimeDto.PorCaja),
            (enCamino.Estado, enCamino.Codigo, enCamino.Mensaje, enCamino.Por));
        Assert.True((await t.VentaAsync(id)).TicketSinImprimir);
        // Pedirlo otra vez mientras va en camino no manda otro papel a la caja.
        Assert.Equal(ImpresionDelCelularDto.EnCamino, (await Leer<ImpresionDelCelularDto>(await t.Imprimir(id))).Estado);
        Assert.Equal(recogido.Id, Assert.Single(await t.VentanaAsync("POS-80C")).Id);
        // Sale: la venta deja de estar sin imprimir, y preguntar otra vez por él dice que ya salió.
        await t.SalioAsync(recogido.Id, true);
        Assert.False((await t.VentaAsync(id)).TicketSinImprimir);

        // La ventana estaba abierta pero no lo recogió a tiempo (se cerró justo entonces): ya no se le da después.
        var otra = Ids.Nuevo();
        var noRecogido = (await Leer<CobroDelCelularDto>(await t.Cobrar(otra, imprimir: true))).Impresion!;
        Assert.Equal((ImpresionDelCelularDto.NoImpreso, CodigosDeImpresion.VentanaCerrada,
            $"La {t.Caja.Nombre} no lo recogió: revisa que tenga su ventana abierta y pídelo otra vez."), (noRecogido.Estado, noRecogido.Codigo, noRecogido.Mensaje));
        Assert.Empty(await t.VentanaAsync("POS-80C"));
        Assert.EndsWith("sin imprimir el ticket: se pidió y no salió (ventana-cerrada)", (await t.BitacoraAsync(otra)).Single().Detalle);
    }

    [Fact]
    public async Task Imprimir_despues_solo_con_la_llave_de_un_celular_y_una_venta_de_su_sucursal()
    {
        await using var t = await TiendaAsync();
        await t.PrenderAsync();
        var id = Ids.Nuevo();
        await Leer<CobroDelCelularDto>(await t.Cobrar(id, imprimir: false));

        // Sin la llave del celular, no.
        var sinLlave = await Error(await t.Principal.PostAsJsonAsync($"api/cajas/{t.CelularId}/ventas/{id}/imprimir", new SolicitudImprimirDelCelular()),
            HttpStatusCode.Unauthorized);
        Assert.Equal(("sin-llave", "Solo un celular dado de alta pide tickets a la impresora de una caja."), (sinLlave.Codigo, sinLlave.Mensaje));
        // Con el id de otra caja en la ruta, tampoco.
        Assert.Equal("otra-caja", (await Error(await t.Celular.PostAsJsonAsync($"api/cajas/{t.Caja.Id}/ventas/{id}/imprimir", new SolicitudImprimirDelCelular()),
            HttpStatusCode.Forbidden)).Codigo);
        // Una venta que no existe.
        var noEsta = await Error(await t.Imprimir(Ids.Nuevo()), HttpStatusCode.NotFound);
        Assert.Equal(("no-esta", "Esa venta no está en la caja principal: revisa que se haya cobrado."), (noEsta.Codigo, noEsta.Mensaje));
        // Un usuario que ya no cobra aquí.
        Assert.Equal("cajero", (await Error(await t.Imprimir(id, new SolicitudImprimirDelCelular(CajeroId: Ids.Nuevo())), HttpStatusCode.BadRequest)).Codigo);
        // Sin cuerpo también se puede pedir (quien cobró la venta): aquí no hay impresora, y lo dice sin error.
        var sinCuerpo = await Leer<ImpresionDelCelularDto>(await t.Celular.PostAsync($"api/cajas/{t.CelularId}/ventas/{id}/imprimir", null));
        Assert.Equal((ImpresionDelCelularDto.NoImpreso, CodigosDeImpresion.SinImpresora), (sinCuerpo.Estado, sinCuerpo.Codigo));

        // Reimprimir (ya salió) con alguien que no puede reimprimir: no sale.
        await using var impresora = new ImpresoraDeMentiras();
        await t.VentanaAsync(impresora.Direccion);
        Assert.True((await Leer<ImpresionDelCelularDto>(await t.Imprimir(id))).Salio);
        await using (var db = t.Servicio.Base())
        {
            var permiso = await db.PermisosUsuario.SingleAsync(p => p.UsuarioId == t.Cajero.UsuarioId && p.Accion == AccionControlada.Reimprimir);
            permiso.Nivel = NivelPermiso.NoPermitido;
            await db.SaveChangesAsync();
        }
        var noPuede = await Error(await t.Imprimir(id, new SolicitudImprimirDelCelular(Ids.Nuevo(), t.Cajero.UsuarioId, "Lo pidieron")), HttpStatusCode.Forbidden);
        Assert.Equal("no-permitido", noPuede.Codigo);
        Assert.Single(impresora.Papeles);
    }

    /// <summary>Una impresora de Windows de mentiras para la cola de la caja: guarda lo que recibe, o falla mientras se le diga.</summary>
    private sealed class ImpresoraDeLaCaja : IImpresora
    {
        public ConcurrentQueue<byte[]> Papeles { get; } = new();
        public string? Falla { get; set; }
        public bool MuestraVistaPrevia => false;

        public Task EnviarAsync(byte[] escPos, CancellationToken ct = default)
        {
            if (Falla is { } falla)
                throw new IOException(falla);
            Papeles.Enqueue(escPos);
            return Task.CompletedTask;
        }
    }

    /// <summary>
    /// La ventana de la caja de verdad (<see cref="ImpresionesDelCelular"/>, lo que arranca la caja al abrir): pregunta sola, pone el
    /// ticket en la cola de su impresora sin abrir el cajón y dice si salió; si no sale, se queda en la cola y, con «Reintentar», lo dice.
    /// </summary>
    [Fact]
    public async Task La_ventana_de_la_caja_lo_pone_en_la_cola_de_su_impresora_y_dice_si_salio_tambien_tras_reintentar()
    {
        await using var t = await TiendaAsync();
        await t.PrenderAsync();
        var impresora = new ImpresoraDeLaCaja();
        var cola = new ColaDeImpresion(impresora, esperas: []);
        var conexion = new ConexionCaja { Servicio = new Uri("http://localhost/"), CajaId = t.Caja.Id };
        var canal = new CanalDelCelular(t.Servicio.Fabrica.CreateClient(), conexion, "POS-80C");
        await using var ventana = canal.Imprimir(t.Caja.Id, cola, cada: TimeSpan.FromMilliseconds(50));
        await EsperarAsync(async () => (await t.DondeAsync()).Puede, "que la caja diga su impresora");
        Assert.Equal(DondeImprimeDto.PorCaja, (await t.DondeAsync()).Por);

        var id = Ids.Nuevo();
        var cobrado = await Leer<CobroDelCelularDto>(await t.Cobrar(id, imprimir: true));
        Assert.Equal((ImpresionDelCelularDto.Impreso, DondeImprimeDto.PorCaja), (cobrado.Impresion!.Estado, cobrado.Impresion.Por));
        // El mismo ticket, sin el pulso del cajón, una sola vez aunque la ventana siga preguntando.
        Assert.Equal(TicketEscPos.Generar(DisenoTicket.Armar(cobrado.Ticket), false), Assert.Single(impresora.Papeles));
        await Task.Delay(200);
        Assert.Single(impresora.Papeles);
        Assert.False((await t.VentaAsync(id)).TicketSinImprimir);

        // La impresora falla: el papel se queda en la cola de la caja (detenida, con su motivo) y el celular lo sabe.
        impresora.Falla = "La impresora POS-80C no tiene papel.";
        var otra = Ids.Nuevo();
        var noSalio = (await Leer<CobroDelCelularDto>(await t.Cobrar(otra, imprimir: true))).Impresion!;
        Assert.Equal((ImpresionDelCelularDto.NoImpreso, CodigosDeImpresion.NoSalio), (noSalio.Estado, noSalio.Codigo));
        Assert.Contains("La impresora POS-80C no tiene papel.", noSalio.Mensaje);
        Assert.True(cola.Detenida);
        Assert.StartsWith("Ticket ", cola.Problema!.Titulo);
        Assert.True((await t.VentaAsync(otra)).TicketSinImprimir);
        // «Reintentar» en la caja, ya con papel: sale, y la venta deja de estar sin imprimir.
        impresora.Falla = null;
        cola.Reintentar();
        await EsperarAsync(async () => !(await t.VentaAsync(otra)).TicketSinImprimir, "que la caja diga que salió al reintentar");
        Assert.Equal(2, impresora.Papeles.Count);

        // Una caja sin impresora (ve el ticket en pantalla) dice que no tiene: el celular no puede pedirlo ahí.
        await ventana.DisposeAsync();
        var sinImpresora = new CanalDelCelular(t.Servicio.Fabrica.CreateClient(), conexion);
        await using var ventanaSin = sinImpresora.Imprimir(t.Caja.Id, new ColaDeImpresion(new SinImpresora()), cada: TimeSpan.FromMilliseconds(50));
        await EsperarAsync(async () => (await t.DondeAsync()).Codigo == CodigosDeImpresion.SinImpresora, "que la caja diga que no tiene impresora");
    }

    private static async Task EsperarAsync(Func<Task<bool>> condicion, string que)
    {
        var limite = DateTime.UtcNow.AddSeconds(20);
        while (!await condicion())
        {
            Assert.True(DateTime.UtcNow < limite, $"No llegó {que}.");
            await Task.Delay(30);
        }
    }
}
