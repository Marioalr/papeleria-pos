using System.Net;
using System.Net.Http.Json;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using PapeleriaPos.Datos;
using PapeleriaPos.Interfaz;
using PapeleriaPos.Nucleo;
using PapeleriaPos.Nucleo.Clientes;
using PapeleriaPos.Nucleo.Contratos;
using PapeleriaPos.Nucleo.Control;
using PapeleriaPos.Nucleo.Telegram;
using PapeleriaPos.Nucleo.Turnos;
using PapeleriaPos.Nucleo.Ventas;

namespace PapeleriaPos.Pruebas.Servicio;

/// <summary>
/// El crédito sin conexión (fase 3, entrega 2, parte 2.4): la caja de verdad fía y recibe abonos con el saldo que conoce
/// (el del servicio más lo suyo sin confirmar); al volver, el servicio acepta todo, nada se cuenta dos veces y lo que no
/// cuadró queda por revisar con su aviso.
/// </summary>
public sealed class CreditoSinConexionTests(ServicioDePrueba servicio) : IClassFixture<ServicioDePrueba>, IDisposable
{
    private const long ChatDelDueno = 9494;
    private readonly List<string> _bases = [];

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        foreach (var ruta in _bases)
            foreach (var archivo in new[] { ruta, ruta + "-wal", ruta + "-shm" })
                File.Delete(archivo);
    }

    /// <summary>Una caja de verdad (cliente, contingencia y base local propia) con su turno abierto.</summary>
    private sealed record Puesto(
        ClienteCaja Cliente, Contingencia Contingencia, AlmacenLocal Almacen, Interruptor Red, CajaDto Caja, SesionDto Cajero, SesionDto Encargado,
        SesionDto Dueno, TurnoDto Turno)
    {
        public SolicitudCobro Venta(Guid clienteId, long total, params CobroSolicitado[] cobros) => new(Caja.Id, Turno.Id, Cajero.UsuarioId,
            [new LineaSolicitada(null, null, 1000, "Mercancía", total)], cobros, Ids.Nuevo(), ClienteId: clienteId);

        public SolicitudAbono Abono(Guid clienteId, FormaPago forma, long? monto, long recibido = 0) => new(Ids.Nuevo(), Caja.Id, Turno.Id, Cajero.UsuarioId,
            clienteId, forma, monto, recibido, forma == FormaPago.Efectivo ? null : "R-1");
    }

    private HttpClient Local => servicio.Fabrica.CreateClient();

    private async Task<Puesto> PuestoAsync(string serie = "A")
    {
        var cajas = (await Local.GetFromJsonAsync<List<CajaDto>>("api/cajas"))!;
        var caja = cajas.FirstOrDefault(c => c.Serie == serie)
            ?? (await (await Local.PostAsJsonAsync("api/alta", new SolicitudAltaCaja("dueno", "3333", cajas[0].SucursalId, null, $"Caja {serie}", serie, $"PC-{serie}")))
                .Content.ReadFromJsonAsync<AltaCajaDto>())!.Caja;
        var ruta = Path.Combine(Path.GetTempPath(), $"credito-sin-conexion-{Guid.NewGuid():N}.db");
        _bases.Add(ruta);
        var red = new Interruptor(servicio.Fabrica.Server.CreateHandler());
        var http = new HttpClient(red);
        var conexion = new ConexionCaja { Servicio = new Uri("http://localhost/"), CajaId = caja.Id };
        var almacen = new AlmacenLocal(ruta);
        var contingencia = new Contingencia(almacen, http, conexion);
        var cliente = new ClienteCaja(http, conexion, contingencia);
        await contingencia.LatidoAsync();
        var cajero = await cliente.EntrarAsync("cajero", "1111");
        var turno = await cliente.TurnoAsync(caja.Id) ?? await cliente.AbrirTurnoAsync(caja.Id, cajero.UsuarioId, 50000);
        await AvisosAlDuenoAsync();
        return new Puesto(cliente, contingencia, almacen, red, caja, cajero, await cliente.EntrarAsync("encargado", "2222"),
            await cliente.EntrarAsync("dueno", "3333"), turno);
    }

    /// <summary>Un chat del dueño, para ver los avisos que le llegarían.</summary>
    private async Task AvisosAlDuenoAsync()
    {
        using var alcance = servicio.Fabrica.Services.CreateScope();
        var db = alcance.ServiceProvider.GetRequiredService<PosDbContext>();
        if (await db.ChatsTelegram.AnyAsync(c => c.ChatId == ChatDelDueno))
            return;
        var dueno = await db.Usuarios.FirstAsync(u => u.NombreUsuario == "dueno");
        db.ChatsTelegram.Add(new ChatTelegram { UsuarioId = dueno.Id, ChatId = ChatDelDueno, VinculadoUtc = DateTime.UtcNow });
        await db.SaveChangesAsync();
    }

    /// <summary>Un cliente nuevo con su límite; con <paramref name="debe"/>, el saldo de la libreta.</summary>
    private async Task<ClienteRegistradoDto> ClienteAsync(Puesto p, string nombre, long limite, long debe = 0)
    {
        var cliente = await p.Cliente.GuardarClienteAsync(new SolicitudGuardarCliente(p.Encargado.UsuarioId, p.Caja.SucursalId,
            new ClienteRegistradoDto(Guid.Empty, nombre, 0, null, true)));
        cliente = await p.Cliente.GuardarCondicionesAsync(cliente.Id, new SolicitudCreditoCliente(p.Dueno.UsuarioId, p.Caja.SucursalId, limite, 30));
        if (debe != 0)
            await p.Cliente.AjustarCreditoAsync(cliente.Id, new SolicitudAjusteCredito(Ids.Nuevo(), p.Dueno.UsuarioId, p.Caja.SucursalId, debe, "Saldo inicial"));
        return cliente;
    }

    private static async Task SinConexionAsync(Puesto p)
    {
        p.Red.Caido = true;
        await p.Contingencia.LatidoAsync();
        Assert.False(p.Contingencia.EnLinea);
    }

    /// <summary>Vuelve: sube la bandeja y, con el siguiente latido, pide los saldos.</summary>
    private static async Task VuelveAsync(Puesto p)
    {
        p.Red.Caido = false;
        await p.Contingencia.LatidoAsync();
        await p.Contingencia.LatidoAsync();
        Assert.Equal((0, 0), (p.Contingencia.Pendientes, p.Contingencia.ConError));
    }

    private async Task<T> EnLaBaseAsync<T>(Func<PosDbContext, Task<T>> consulta)
    {
        using var alcance = servicio.Fabrica.Services.CreateScope();
        return await consulta(alcance.ServiceProvider.GetRequiredService<PosDbContext>());
    }

    private Task<long> SaldoAsync(Guid clienteId) =>
        EnLaBaseAsync(db => db.MovimientosCredito.Where(m => m.ClienteId == clienteId).SumAsync(m => m.MontoCentavos));

    private static DateOnly Hoy => DateOnly.FromDateTime(HoraMexico.ALocal(DateTime.UtcNow));

    [Fact]
    public async Task Sin_conexion_se_fia_y_se_abona_con_el_saldo_conocido_y_al_volver_todo_cuadra()
    {
        var p = await PuestoAsync();
        var cliente = await ClienteAsync(p, "Sin conexión: Escuela", 100000);
        await p.Cliente.CobrarAsync(p.Venta(cliente.Id, 13745, new CobroSolicitado(FormaPago.Credito, 13745)));
        await p.Contingencia.LatidoAsync();
        Assert.Empty(p.Almacen.CuentaPropia(cliente.Id));
        var inicio = (await Local.GetFromJsonAsync<CorteDto>($"api/turnos/{p.Turno.Id}/resumen"))!;
        await SinConexionAsync(p);

        // Liquidar en efectivo, al peso: debe $137.45, se cobran $137.
        var abono = await p.Cliente.AbonarAsync(p.Abono(cliente.Id, FormaPago.Efectivo, null, 20000));
        var fiado = await p.Cliente.CobrarAsync(p.Venta(cliente.Id, 60000, new CobroSolicitado(FormaPago.Credito, 60000)));
        // Debe $600 de $1,000: fiar $500 pasa el límite por $100 y el cajero lo pide.
        var pasado = p.Venta(cliente.Id, 50000, new CobroSolicitado(FormaPago.Credito, 50000));
        var requiere = await Assert.ThrowsAsync<ErrorCaja>(() => p.Cliente.CobrarAsync(pasado));
        var autorizacion = await p.Cliente.AutorizarAsync(new SolicitudAutorizacion(AccionControlada.ExcederLimiteCredito, p.Caja.SucursalId, p.Caja.Id,
            p.Cajero.UsuarioId, "encargado", "2222", 10000, "Pasar el límite"));
        var conPermiso = await p.Cliente.CobrarAsync(pasado with { AutorizacionCreditoId = autorizacion.Id });
        var corteLocal = await p.Cliente.CorteXAsync(p.Turno.Id, p.Cajero.UsuarioId);
        await VuelveAsync(p);

        Assert.Equal((13745L, -45L, 13700L, 6300L, 13745L, 0L, true), (abono.MontoCentavos, abono.RedondeoCentavos, abono.CobradoCentavos,
            abono.CambioCentavos, abono.SaldoAnteriorCentavos, abono.SaldoCentavos, abono.Estimado));
        Assert.Equal(new CreditoTicketDto(60000, 60000, Hoy.AddDays(30), Estimado: true), fiado.Credito);
        Assert.Equal("requiere-autorizacion", requiere.Codigo);
        Assert.Equal(110000, conPermiso.Credito!.SaldoCentavos);
        // En el servicio: lo mismo, una vez, y nada por revisar (lo que pasó del límite se autorizó).
        Assert.Equal(110000, await SaldoAsync(cliente.Id));
        Assert.Equal(0, await EnLaBaseAsync(db => db.RevisionesCredito.CountAsync(r => r.ClienteId == cliente.Id)));
        Assert.Empty(p.Almacen.CuentaPropia(cliente.Id));
        Assert.Equal(110000, p.Contingencia.Credito(cliente.Id)!.SaldoCentavos);
        var cargo = await EnLaBaseAsync(db => db.MovimientosCredito.SingleAsync(m => m.Id == conPermiso.VentaId));
        Assert.Equal((110000L, true), (cargo.SaldoCentavos, cargo.EnContingencia));
        Assert.Contains(await EnLaBaseAsync(db => db.Bitacora.Where(b => b.Accion == AccionControlada.ExcederLimiteCredito && b.VentaId == conPermiso.VentaId)
            .Select(b => new { b.Detalle, b.Resultado }).ToListAsync()), b => b.Resultado == ResultadoBitacora.Autorizado && b.Detalle.Contains("según esta caja debía $600.00"));
        // La reimpresión dice lo mismo que se imprimió.
        var reimpreso = (await Local.GetFromJsonAsync<TicketDto>($"api/ventas/{fiado.VentaId}/ticket"))!;
        Assert.Equal(fiado.Credito, reimpreso.Credito);
        var comprobante = await (await Local.PostAsJsonAsync($"api/abonos/{abono.AbonoId}/reimpresion",
            new SolicitudReimpresion(p.Dueno.UsuarioId, p.Caja.Id, "Prueba"))).Content.ReadFromJsonAsync<ComprobanteAbonoDto>();
        Assert.Equal((abono.MontoCentavos, abono.SaldoAnteriorCentavos, abono.SaldoCentavos, true),
            (comprobante!.MontoCentavos, comprobante.SaldoAnteriorCentavos, comprobante.SaldoCentavos, comprobante.Estimado));
        // El corte de la caja sin conexión dice lo mismo que el del servicio.
        var resumen = (await Local.GetFromJsonAsync<CorteDto>($"api/turnos/{p.Turno.Id}/resumen"))!;
        Assert.Equal((resumen.EsperadoCentavos, resumen.CreditoCentavos, resumen.Abonos, resumen.AbonosEfectivoCentavos, resumen.RedondeoCentavos),
            (corteLocal.EsperadoCentavos, corteLocal.CreditoCentavos, corteLocal.Abonos, corteLocal.AbonosEfectivoCentavos, corteLocal.RedondeoCentavos));
        Assert.Equal((110000L, 1, 13700L), (resumen.CreditoCentavos - inicio.CreditoCentavos, resumen.Abonos - inicio.Abonos,
            resumen.AbonosEfectivoCentavos - inicio.AbonosEfectivoCentavos));
    }

    [Fact]
    public async Task Lo_fiado_sin_conexion_que_paso_el_limite_se_acepta_y_queda_por_revisar()
    {
        var b = await PuestoAsync("B");
        var a = await PuestoAsync("A");
        var cliente = await ClienteAsync(b, "Sin conexión: Arquitecto", 50000, debe: 10000);
        await b.Contingencia.LatidoAsync();
        await SinConexionAsync(b);

        // La caja A, en línea, le fía $300; la B no lo sabe: conoce $100 y le fía $350 sin pasar (según ella) del límite.
        await a.Cliente.CobrarAsync(a.Venta(cliente.Id, 30000, new CobroSolicitado(FormaPago.Credito, 30000)));
        var ticket = await b.Cliente.CobrarAsync(b.Venta(cliente.Id, 35000, new CobroSolicitado(FormaPago.Credito, 35000)));
        await VuelveAsync(b);

        Assert.Equal(new CreditoTicketDto(35000, 45000, Hoy.AddDays(30), Estimado: true), ticket.Credito);
        Assert.Equal(75000, await SaldoAsync(cliente.Id));
        Assert.Equal(45000, await EnLaBaseAsync(db => db.MovimientosCredito.Where(m => m.Id == ticket.VentaId).Select(m => m.SaldoCentavos).SingleAsync()));
        var revision = await EnLaBaseAsync(db => db.RevisionesCredito.SingleAsync(r => r.ClienteId == cliente.Id));
        Assert.Equal((MotivoRevisionCredito.PasoLimite, 25000L, ticket.VentaId!.Value), (revision.Motivo, revision.MontoCentavos, revision.MovimientoId));
        Assert.Contains("la caja sabía que debía $100.00; con lo que llegó antes ahora debe $750.00 y pasó su límite de $500.00 por $250.00", revision.Detalle);
        Assert.Equal(1, await EnLaBaseAsync(db => db.MensajesTelegram.CountAsync(m => m.ChatId == ChatDelDueno && m.Clave == $"credito-revisar:{revision.Id}")));

        // La revisa el encargado (el cajero no puede) y sale de la lista.
        var porRevisar = (await Local.GetFromJsonAsync<List<RevisionCreditoDto>>($"api/sucursales/{b.Caja.SucursalId}/creditos/por-revisar"))!;
        var delCajero = await Local.PostAsJsonAsync($"api/creditos/revisiones/{revision.Id}", new SolicitudRevision(b.Cajero.UsuarioId, "Visto"));
        var delEncargado = await Local.PostAsJsonAsync($"api/creditos/revisiones/{revision.Id}", new SolicitudRevision(b.Encargado.UsuarioId, "Le avisé"));
        var despues = (await Local.GetFromJsonAsync<List<RevisionCreditoDto>>($"api/sucursales/{b.Caja.SucursalId}/creditos/por-revisar"))!;

        Assert.Contains(porRevisar, r => r.Id == revision.Id && r.Cliente == cliente.Nombre && r.Motivo == MotivoRevisionCredito.PasoLimite);
        Assert.Equal(HttpStatusCode.Forbidden, delCajero.StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, delEncargado.StatusCode);
        Assert.DoesNotContain(despues, r => r.Id == revision.Id);
        Assert.Equal("Le avisé", await EnLaBaseAsync(db => db.RevisionesCredito.Where(r => r.Id == revision.Id).Select(r => r.Nota).SingleAsync()));
    }

    [Fact]
    public async Task Lo_fiado_sin_conexion_a_quien_ya_no_tiene_credito_queda_por_revisar_una_vez()
    {
        var p = await PuestoAsync();
        var cliente = await ClienteAsync(p, "Sin conexión: Ya sin crédito", 50000);
        await p.Contingencia.LatidoAsync();
        await SinConexionAsync(p);
        // Mientras, el dueño le quita el crédito desde otra caja.
        await Local.PutAsJsonAsync($"api/clientes/{cliente.Id}/credito", new SolicitudCreditoCliente(p.Dueno.UsuarioId, p.Caja.SucursalId, 0, 30));

        var ticket = await p.Cliente.CobrarAsync(p.Venta(cliente.Id, 10000, new CobroSolicitado(FormaPago.Credito, 10000)));
        await VuelveAsync(p);

        var revision = await EnLaBaseAsync(db => db.RevisionesCredito.SingleAsync(r => r.ClienteId == cliente.Id));
        Assert.Equal((MotivoRevisionCredito.SinCredito, 10000L, ticket.VentaId!.Value), (revision.Motivo, revision.MontoCentavos, revision.MovimientoId));
        Assert.Equal(10000, await SaldoAsync(cliente.Id));
    }

    [Fact]
    public async Task El_abono_sin_conexion_siempre_sube_aunque_lo_deje_a_favor()
    {
        var p = await PuestoAsync();
        var cliente = await ClienteAsync(p, "Sin conexión: Pagó dos veces", 100000, debe: 20000);
        await p.Contingencia.LatidoAsync();
        await SinConexionAsync(p);
        // Mientras, paga con tarjeta en otra caja (en línea).
        (await Local.PostAsJsonAsync("api/abonos", new SolicitudAbono(Ids.Nuevo(), p.Caja.Id, p.Turno.Id, p.Cajero.UsuarioId, cliente.Id,
            FormaPago.Tarjeta, 20000, Referencia: "T-1"))).EnsureSuccessStatusCode();

        var abono = await p.Cliente.AbonarAsync(p.Abono(cliente.Id, FormaPago.Efectivo, null, 20000));
        await VuelveAsync(p);

        Assert.Equal((20000L, 0L), (abono.SaldoAnteriorCentavos, abono.SaldoCentavos));
        Assert.Equal(-20000, await SaldoAsync(cliente.Id));
        var guardado = await EnLaBaseAsync(db => db.Abonos.SingleAsync(x => x.Id == abono.AbonoId));
        Assert.Equal((20000L, 20000L, 0L, true), (guardado.MontoCentavos, guardado.SaldoAnteriorCentavos, guardado.SaldoCentavos, guardado.EnContingencia));
        var revision = await EnLaBaseAsync(db => db.RevisionesCredito.SingleAsync(r => r.ClienteId == cliente.Id));
        Assert.Equal((MotivoRevisionCredito.SaldoAFavor, 20000L), (revision.Motivo, revision.MontoCentavos));
        Assert.Equal(1, await EnLaBaseAsync(db => db.AperturasCajon.CountAsync(x => x.Motivo == MotivoApertura.Abono && x.Nota!.Contains(guardado.Folio))));
    }

    [Fact]
    public async Task El_abono_sin_conexion_que_llega_despues_del_corte_Z_queda_fuera_de_ese_corte()
    {
        var p = await PuestoAsync();
        var cliente = await ClienteAsync(p, "Sin conexión: Llegó tarde", 100000, debe: 15000);
        await p.Contingencia.LatidoAsync();
        await SinConexionAsync(p);
        var abono = await p.Cliente.AbonarAsync(p.Abono(cliente.Id, FormaPago.Efectivo, 10000, 10000));
        var encargado = await p.Cliente.EntrarAsync("encargado", "2222");
        await p.Cliente.CorteZAsync(p.Turno.Id, new SolicitudCorteZ(encargado.UsuarioId, 60000));
        // Al volver, el abono no sube (falla una vez) pero el corte Z sí.
        p.Red.Rechaza = r => r.RequestUri!.AbsolutePath == "/api/bandeja/abonos";
        p.Red.Caido = false;
        await p.Contingencia.LatidoAsync();
        Assert.Equal(1, p.Contingencia.ConError);
        var corte = await EnLaBaseAsync(db => db.Cortes.AsNoTracking().SingleAsync(c => c.TurnoId == p.Turno.Id && c.Tipo == TipoCorte.Z));
        p.Red.Rechaza = null;
        await p.Contingencia.SubirAsync();

        Assert.Equal(0, p.Contingencia.ConError);
        var guardado = await EnLaBaseAsync(db => db.Abonos.SingleAsync(x => x.Id == abono.AbonoId));
        Assert.Equal((p.Turno.Id, true), (guardado.TurnoId, guardado.FueraDeCorteUtc is not null));
        var mismoCorte = await EnLaBaseAsync(db => db.Cortes.AsNoTracking().SingleAsync(c => c.Id == corte.Id));
        Assert.Equal((corte.Abonos, corte.EsperadoCentavos), (mismoCorte.Abonos, mismoCorte.EsperadoCentavos));
        Assert.Equal(5000, await SaldoAsync(cliente.Id));
        var aviso = await EnLaBaseAsync(db => db.MensajesTelegram.SingleAsync(m => m.ChatId == ChatDelDueno && m.Clave == $"fuera-de-corte:{abono.AbonoId}"));
        Assert.Contains($"El abono {abono.Folio}", aviso.Texto);
        Assert.Contains("sobrante", aviso.Texto);
        Assert.Equal(0, await EnLaBaseAsync(db => db.RevisionesCredito.CountAsync(r => r.ClienteId == cliente.Id)));
    }

    [Fact]
    public async Task Lo_propio_sin_confirmar_no_se_cuenta_dos_veces()
    {
        var p = await PuestoAsync();
        var cliente = await ClienteAsync(p, "Sin conexión: Sin doble cuenta", 100000);
        await p.Contingencia.LatidoAsync();

        var primero = await p.Cliente.CobrarAsync(p.Venta(cliente.Id, 20000, new CobroSolicitado(FormaPago.Credito, 20000)));
        // Todavía sin pedir los saldos: cuenta por lo anotado.
        Assert.Equal([primero.VentaId!.Value], p.Almacen.CuentaPropia(cliente.Id).Select(f => f.Id));
        Assert.Equal(20000, p.Contingencia.Credito(cliente.Id)!.SaldoCentavos);
        await p.Contingencia.LatidoAsync();
        // Ya está en los saldos: se borra de lo propio y no cambia.
        Assert.Empty(p.Almacen.CuentaPropia(cliente.Id));
        Assert.Equal(20000, p.Contingencia.Credito(cliente.Id)!.SaldoCentavos);

        // Otro fiado en línea y se cae antes de pedir los saldos: lo propio lo cuenta una vez.
        await p.Cliente.CobrarAsync(p.Venta(cliente.Id, 5000, new CobroSolicitado(FormaPago.Credito, 5000)));
        await SinConexionAsync(p);
        Assert.Equal(25000, p.Contingencia.Credito(cliente.Id)!.SaldoCentavos);
        var sinConexion = await p.Cliente.CobrarAsync(p.Venta(cliente.Id, 10000, new CobroSolicitado(FormaPago.Credito, 10000)));
        await VuelveAsync(p);

        Assert.Equal(35000, sinConexion.Credito!.SaldoCentavos);
        Assert.Equal(35000, await SaldoAsync(cliente.Id));
        Assert.Empty(p.Almacen.CuentaPropia(cliente.Id));
        Assert.Equal(35000, p.Contingencia.Credito(cliente.Id)!.SaldoCentavos);
    }

    [Fact]
    public async Task Un_abono_por_transferencia_sin_conexion_baja_el_saldo_pero_no_da_credito()
    {
        var p = await PuestoAsync();
        var cliente = await ClienteAsync(p, "Sin conexión: Transferencia", 50000, debe: 50000);
        await p.Contingencia.LatidoAsync();
        await SinConexionAsync(p);

        await p.Cliente.AbonarAsync(p.Abono(cliente.Id, FormaPago.Transferencia, 20000));
        var cuenta = p.Contingencia.Credito(cliente.Id)!;
        var fiar = await Assert.ThrowsAsync<ErrorCaja>(() => p.Cliente.CobrarAsync(p.Venta(cliente.Id, 10000, new CobroSolicitado(FormaPago.Credito, 10000))));

        Assert.Equal((30000L, 20000L, 0L), (cuenta.SaldoCentavos, cuenta.PorVerificarCentavos, cuenta.DisponibleCentavos));
        Assert.Equal("requiere-autorizacion", fiar.Codigo);
    }

    [Fact]
    public async Task Lo_que_sube_dos_veces_se_guarda_una()
    {
        var p = await PuestoAsync();
        var cliente = await ClienteAsync(p, "Sin conexión: Dos veces", 100000, debe: 30000);
        var paquete = (await Local.GetFromJsonAsync<PaqueteContingenciaDto>($"api/cajas/{p.Caja.Id}/paquete"))!;
        var venta = new VentaContingenciaDto(Ids.Nuevo(), p.Caja.Id, p.Turno.Id, p.Cajero.UsuarioId, paquete.UltimoFolio + 1, DateTime.UtcNow,
            [new LineaContingenciaDto(null, null, "Mercancía", 1000, 5000)], [new CobroSolicitado(FormaPago.Credito, 5000)], cliente.Id, 0,
            SaldoConocidoCentavos: 30000, VenceCredito: Hoy.AddDays(30));
        var abono = new AbonoContingenciaDto(Ids.Nuevo(), p.Caja.Id, p.Turno.Id, p.Cajero.UsuarioId, cliente.Id, FormaPago.Efectivo, 10000, 0, 10000, 0, null,
            DateTime.UtcNow, 35000);

        foreach (var _ in Enumerable.Range(0, 2))
        {
            (await Local.PostAsJsonAsync("api/bandeja/ventas", venta)).EnsureSuccessStatusCode();
            (await Local.PostAsJsonAsync("api/bandeja/abonos", abono)).EnsureSuccessStatusCode();
        }
        var aLaVez = await Task.WhenAll(Enumerable.Range(0, 3).Select(_ => Local.PostAsJsonAsync("api/bandeja/abonos", abono)));

        Assert.All(aLaVez, r => Assert.True(r.IsSuccessStatusCode));
        Assert.Equal(1, await EnLaBaseAsync(db => db.Ventas.CountAsync(v => v.Id == venta.Id)));
        Assert.Equal(1, await EnLaBaseAsync(db => db.Abonos.CountAsync(x => x.Id == abono.Id)));
        Assert.Equal(2, await EnLaBaseAsync(db => db.MovimientosCredito.CountAsync(m => m.Id == venta.Id || m.Id == abono.Id)));
        Assert.Equal(25000, await SaldoAsync(cliente.Id));
        Assert.Equal(1, await EnLaBaseAsync(db => db.AperturasCajon.CountAsync(x => x.Motivo == MotivoApertura.Abono && x.Nota!.Contains(Abono.FolioDe(abono.Id)))));
    }

    [Fact]
    public async Task Una_caja_de_antes_que_manda_lo_fiado_sin_lo_que_sabia_se_revisa_con_el_saldo_real()
    {
        var p = await PuestoAsync();
        var cliente = await ClienteAsync(p, "Sin conexión: Caja de antes", 50000, debe: 45000);
        var paquete = (await Local.GetFromJsonAsync<PaqueteContingenciaDto>($"api/cajas/{p.Caja.Id}/paquete"))!;
        var id = Ids.Nuevo();
        // El JSON de una caja de antes: sin los campos nuevos.
        var respuesta = await Local.PostAsJsonAsync("api/bandeja/ventas", new
        {
            Id = id, CajaId = p.Caja.Id, TurnoId = p.Turno.Id, CajeroId = p.Cajero.UsuarioId, Folio = paquete.UltimoFolio + 1, PagadaUtc = DateTime.UtcNow,
            Lineas = new[] { new LineaContingenciaDto(null, null, "Mercancía", 1000, 10000) }, Cobros = new[] { new CobroSolicitado(FormaPago.Credito, 10000) },
            ClienteEspecialId = cliente.Id, RedondeoCentavos = 0,
        });

        Assert.True(respuesta.IsSuccessStatusCode, await respuesta.Content.ReadAsStringAsync());
        Assert.Equal(55000, await SaldoAsync(cliente.Id));
        var revision = await EnLaBaseAsync(db => db.RevisionesCredito.SingleAsync(r => r.MovimientoId == id));
        Assert.Equal((MotivoRevisionCredito.PasoLimite, 5000L), (revision.Motivo, revision.MontoCentavos));
    }

    private static bool CobraEnLinea(HttpRequestMessage r) => r.Method == HttpMethod.Post && r.RequestUri!.AbsolutePath == "/api/ventas";

    private static bool AbonaEnLinea(HttpRequestMessage r) => r.Method == HttpMethod.Post && r.RequestUri!.AbsolutePath == "/api/abonos";

    [Fact]
    public async Task Volver_a_mandar_un_fiado_que_ya_quedo_no_lo_cuenta_dos_veces()
    {
        var p = await PuestoAsync();
        var cliente = await ClienteAsync(p, "Sin conexión: Lo mandó otra vez", 10000);
        await p.Contingencia.LatidoAsync();
        // Fiar $200 con límite de $100: lo autoriza el encargado en línea; se pierde la respuesta y sin conexión esa
        // autorización no existe: el ticket espera.
        var autorizacion = await p.Cliente.AutorizarAsync(new SolicitudAutorizacion(AccionControlada.ExcederLimiteCredito, p.Caja.SucursalId, p.Caja.Id,
            p.Cajero.UsuarioId, "encargado", "2222", 10000, "Pasar el límite"));
        var venta = p.Venta(cliente.Id, 20000, new CobroSolicitado(FormaPago.Credito, 20000)) with { AutorizacionCreditoId = autorizacion.Id };
        p.Red.PierdeRespuesta = CobraEnLinea;
        var perdido = await Assert.ThrowsAsync<ErrorCaja>(() => p.Cliente.CobrarAsync(venta));
        p.Red.PierdeRespuesta = null;
        // Vuelve: los saldos ya lo traen (se confirma) y se manda otra vez con el mismo Id.
        await p.Contingencia.LatidoAsync();
        await p.Contingencia.LatidoAsync();
        var ticket = await p.Cliente.CobrarAsync(venta, soloEnLinea: true);
        // En ese momento (si la conexión se fuera ahora) ya cuenta una vez.
        var alMandarlo = p.Contingencia.Credito(cliente.Id)!.SaldoCentavos;
        await p.Contingencia.LatidoAsync();

        Assert.Equal("fiado-sin-confirmar", perdido.Codigo);
        Assert.Equal(20000, alMandarlo);
        Assert.Equal(venta.VentaId, ticket.VentaId);
        Assert.Equal(20000, await SaldoAsync(cliente.Id));
        Assert.Empty(p.Almacen.CuentaPropia(cliente.Id));
        Assert.Equal(20000, p.Contingencia.Credito(cliente.Id)!.SaldoCentavos);
    }

    [Fact]
    public async Task Un_abono_que_se_perdio_y_no_se_pudo_hacer_sin_conexion_espera_la_conexion_y_queda_uno()
    {
        var p = await PuestoAsync();
        var cliente = await ClienteAsync(p, "Sin conexión: Abono perdido", 100000, debe: 30000);
        await p.Contingencia.LatidoAsync();
        // Otra caja le fía $100: esta caja todavía no lo sabe, así que lo que vio en línea no cuadra con lo que conoce.
        await (await PuestoAsync()).Cliente.CobrarAsync(p.Venta(cliente.Id, 10000, new CobroSolicitado(FormaPago.Credito, 10000)));
        var abono = p.Abono(cliente.Id, FormaPago.Efectivo, 10000, 10000) with { SaldoVistoCentavos = 40000 };
        p.Red.PierdeRespuesta = AbonaEnLinea;
        var perdido = await Assert.ThrowsAsync<ErrorCaja>(() => p.Cliente.AbonarAsync(abono));
        p.Red.PierdeRespuesta = null;
        await SinConexionAsync(p);
        // Sin conexión, ese abono no se vuelve a cobrar.
        var espera = await Assert.ThrowsAsync<ErrorCaja>(() => p.Cliente.AbonarAsync(abono, soloEnLinea: true));
        await VuelveAsync(p);
        var comprobante = await p.Cliente.AbonarAsync(abono, soloEnLinea: true);

        Assert.Equal(("abono-sin-confirmar", "abono-sin-confirmar"), (perdido.Codigo, espera.Codigo));
        Assert.Equal((abono.Id, 10000L), (comprobante.AbonoId, comprobante.MontoCentavos));
        Assert.Equal(1, await EnLaBaseAsync(db => db.Abonos.CountAsync(x => x.ClienteId == cliente.Id)));
        Assert.Equal(30000, await SaldoAsync(cliente.Id));
        await p.Contingencia.LatidoAsync();
        Assert.Equal(30000, p.Contingencia.Credito(cliente.Id)!.SaldoCentavos);
    }

    [Fact]
    public async Task Con_el_reloj_de_la_caja_atrasado_lo_fiado_vence_desde_la_fecha_acotada()
    {
        var p = await PuestoAsync();
        var cliente = await ClienteAsync(p, "Sin conexión: Reloj atrasado", 100000);
        var paquete = (await Local.GetFromJsonAsync<PaqueteContingenciaDto>($"api/cajas/{p.Caja.Id}/paquete"))!;
        // La caja creía que era el 1 de enero de 2020 e imprimió que vence a los 30 días.
        var venta = new VentaContingenciaDto(Ids.Nuevo(), p.Caja.Id, p.Turno.Id, p.Cajero.UsuarioId, paquete.UltimoFolio + 1,
            new DateTime(2020, 1, 1, 18, 0, 0, DateTimeKind.Utc), [new LineaContingenciaDto(null, null, "Mercancía", 1000, 5000)],
            [new CobroSolicitado(FormaPago.Credito, 5000)], cliente.Id, 0, SaldoConocidoCentavos: 0, VenceCredito: new DateOnly(2020, 1, 31));

        (await Local.PostAsJsonAsync("api/bandeja/ventas", venta)).EnsureSuccessStatusCode();

        var cargo = await EnLaBaseAsync(db => db.MovimientosCredito.SingleAsync(m => m.Id == venta.Id));
        Assert.True(cargo.FechaUtc.Year > 2020);
        Assert.Equal(CuentaCredito.Vence(cargo.FechaUtc, 30), cargo.Vence);
        Assert.Equal(0, (await new RegistroCreditoLectura(servicio).CuentaAsync(cliente.Id)).Vencido(Hoy));
    }

    /// <summary>Para leer la cuenta como la calcula el servicio.</summary>
    private sealed class RegistroCreditoLectura(ServicioDePrueba servicio)
    {
        public async Task<CreditoClienteDto> CuentaAsync(Guid clienteId)
        {
            using var alcance = servicio.Fabrica.Services.CreateScope();
            return await new RegistroCredito(alcance.ServiceProvider.GetRequiredService<PosDbContext>()).CuentaAsync(clienteId);
        }
    }

    // ── Devolver el saldo a favor (parte 3): solo en línea, y cuenta en el corte de la caja ──

    /// <summary>
    /// Si la devolución pasa el tope de siempre (la caja principal sí la guardó, pero no contestó a tiempo), tampoco se repite:
    /// queda sin confirmar, como cuando se pierde la respuesta, y otra vez con el mismo Id sale el mismo comprobante.
    /// </summary>
    [Fact]
    public async Task Si_la_devolucion_del_saldo_no_contesta_a_tiempo_tampoco_se_repite()
    {
        var p = await PuestoAsync();
        var cliente = await ClienteAsync(p, "Sin conexión: pagó de más y tardó", 0, debe: -15000);
        var solicitud = new SolicitudDevolverSaldo(Ids.Nuevo(), p.Caja.Id, p.Turno.Id, p.Encargado.UsuarioId, 15000, -15000);

        p.Red.TardaDemasiado = r => r.RequestUri!.AbsolutePath.EndsWith("/saldo-devuelto");
        var tarde = await Assert.ThrowsAsync<ErrorCaja>(() => p.Cliente.DevolverSaldoAsync(cliente.Id, solicitud, 15000));
        p.Red.TardaDemasiado = null;
        var pendiente = p.Cliente.DevolucionSinConfirmar(cliente.Id);
        var comprobante = await p.Cliente.DevolverSaldoAsync(cliente.Id, solicitud, 15000);

        Assert.Equal("saldo-sin-confirmar", tarde.Codigo);
        Assert.Equal(solicitud, pendiente?.Solicitud);
        Assert.Equal(15000, comprobante.CobradoCentavos);
        Assert.Equal(0, await SaldoAsync(cliente.Id));
    }

    [Fact]
    public async Task Lo_devuelto_de_un_saldo_a_favor_entra_al_corte_de_la_caja_y_si_se_pierde_la_respuesta_no_se_repite()
    {
        var p = await PuestoAsync();
        var cliente = await ClienteAsync(p, "Sin conexión: pagó de más", 0, debe: -15000);
        await SinConexionAsync(p);
        var antes = await p.Cliente.CorteXAsync(p.Turno.Id, p.Encargado.UsuarioId);
        var sinConexion = await Assert.ThrowsAsync<ErrorCaja>(() => p.Cliente.DevolverSaldoAsync(cliente.Id,
            new SolicitudDevolverSaldo(Ids.Nuevo(), p.Caja.Id, p.Turno.Id, p.Encargado.UsuarioId, 15000, -15000), 15000));
        p.Red.Caido = false;
        await p.Contingencia.LatidoAsync();
        var solicitud = new SolicitudDevolverSaldo(Ids.Nuevo(), p.Caja.Id, p.Turno.Id, p.Encargado.UsuarioId, 15000, -15000);

        // Se guarda, pero la respuesta no llega: la caja no sabe si quedó, y ya no cuenta con ese saldo a favor.
        p.Red.PierdeRespuesta = r => r.RequestUri!.AbsolutePath.EndsWith("/saldo-devuelto");
        var perdida = await Assert.ThrowsAsync<ErrorCaja>(() => p.Cliente.DevolverSaldoAsync(cliente.Id, solicitud, 15000));
        var sabiaSinConexion = p.Contingencia.Credito(cliente.Id);
        var pendiente = p.Cliente.DevolucionSinConfirmar(cliente.Id);
        p.Red.PierdeRespuesta = null;
        await p.Contingencia.LatidoAsync();
        var comprobante = await p.Cliente.DevolverSaldoAsync(cliente.Id, solicitud, 15000);
        var otraVez = await p.Cliente.DevolverSaldoAsync(cliente.Id, solicitud, 15000);
        await SinConexionAsync(p);
        var despues = await p.Cliente.CorteXAsync(p.Turno.Id, p.Encargado.UsuarioId);

        Assert.Equal("sin-conexion", sinConexion.Codigo);
        Assert.Equal("saldo-sin-confirmar", perdida.Codigo);
        Assert.Equal(0, sabiaSinConexion!.SaldoCentavos);
        Assert.Equal(solicitud, pendiente?.Solicitud);
        Assert.Null(p.Cliente.DevolucionSinConfirmar(cliente.Id));
        Assert.Equal((comprobante.AbonoId, 15000L), (otraVez.AbonoId, comprobante.CobradoCentavos));
        Assert.True(antes.EnContingencia && despues.EnContingencia);
        Assert.Equal((15000L, -15000L), (despues.SaldoDevueltoCentavos - antes.SaldoDevueltoCentavos, despues.EsperadoCentavos - antes.EsperadoCentavos));
        Assert.Equal(0, await SaldoAsync(cliente.Id));
        Assert.Equal(0, p.Contingencia.Credito(cliente.Id)!.SaldoCentavos);
    }

    [Fact]
    public async Task Si_la_caja_tiene_algo_del_cliente_sin_subir_no_le_devuelve_su_saldo()
    {
        var p = await PuestoAsync();
        var cliente = await ClienteAsync(p, "Sin conexión: le fiaron y pagó de más", 100000, debe: -15000);
        await p.Contingencia.LatidoAsync();
        await SinConexionAsync(p);
        await p.Cliente.CobrarAsync(p.Venta(cliente.Id, 5000, new CobroSolicitado(FormaPago.Credito, 5000)));
        // Vuelve la conexión pero la bandeja todavía no sube.
        p.Red.Caido = false;
        p.Red.Rechaza = r => r.RequestUri!.AbsolutePath.Contains("/bandeja/");
        await p.Contingencia.LatidoAsync();

        var error = await Assert.ThrowsAsync<ErrorCaja>(() => p.Cliente.DevolverSaldoAsync(cliente.Id,
            new SolicitudDevolverSaldo(Ids.Nuevo(), p.Caja.Id, p.Turno.Id, p.Encargado.UsuarioId, 15000, -15000), 15000));

        Assert.StartsWith("Esta caja todavía tiene lo fiado o abonado de este cliente sin subir", error.Message);
        Assert.Equal(-15000, await SaldoAsync(cliente.Id));
        p.Red.Rechaza = null;
        await p.Contingencia.SubirAsync();
        await VuelveAsync(p);
    }
}
