using Microsoft.EntityFrameworkCore;
using PapeleriaPos.Interfaz;
using PapeleriaPos.Nucleo.Contratos;
using PapeleriaPos.Nucleo.Ventas;
using PapeleriaPos.Pruebas.Servicio;

namespace PapeleriaPos.Pruebas.Interfaz;

/// <summary>
/// El cobro que no se sabe si quedó (entrega 5, parte 2, revisión): la caja principal no contestó a tiempo (puede que sí lo
/// haya guardado, con el siguiente folio de esta caja) y sin conexión tampoco se pudo cobrar (lleva algo que la copia no
/// tiene). Ese folio queda apartado, para que el siguiente cobro sin conexión no lo repita, y el ticket solo se cobra en línea
/// con el mismo Id: si ya había quedado, sale el que quedó; nunca otro distinto con el mismo Id.
/// </summary>
public sealed class CobroSinConfirmarTests : IDisposable
{
    private readonly string _baseLocal = Path.Combine(Path.GetTempPath(), $"caja-sin-confirmar-{Guid.NewGuid():N}.db");

    public void Dispose() => BasesDePrueba.Borrar(_baseLocal);

    private const string CodigoNuevo = "9900000777123";
    private const string NombreNuevo = "Separadores recién dados de alta";

    private sealed record NoSeSabe(ErrorCaja Error, SolicitudCobro Solicitud, Venta EnLaPrincipal, TurnoDto Turno, ProductoVentaDto A);

    /// <summary>
    /// Un cobro de un producto de la copia y uno recién dado de alta en otra caja (la copia no lo tiene): la caja principal lo
    /// guarda, pero su respuesta no llega antes del plazo (aquí medio segundo; en la caja de verdad, 2.5 s), y con la copia no
    /// se puede cobrar.
    /// </summary>
    private static async Task<NoSeSabe> NoSeSabeSiQuedoAsync(ServicioDePrueba servicio, VentaConCopia v)
    {
        var turno = (await v.Cliente.TurnoAsync(v.Caja.Id))!;
        var (a, _) = v.DosDeLaCopia();
        var nuevo = await v.DarDeAltaEnOtraCajaAsync(CodigoNuevo, NombreNuevo);
        await servicio.CatalogoAlDiaAsync();
        var solicitud = new SolicitudCobro(v.Caja.Id, turno.Id, v.Cajero.UsuarioId,
            [new LineaSolicitada(a.VarianteId, a.Presentacion.Id, 1000), new LineaSolicitada(nuevo.VarianteId, nuevo.Presentacion.Id, 1000)],
            [new CobroSolicitado(FormaPago.Efectivo, 10_000_000)], Guid.NewGuid());

        v.Cliente.PlazoEscritura = TimeSpan.FromMilliseconds(500);
        v.Red.CobroRetenido = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var error = await Assert.ThrowsAsync<ErrorCaja>(() => v.Cliente.CobrarAsync(solicitud));
        v.Red.CobroRetenido.SetResult();
        v.Cliente.PlazoEscritura = null;
        Assert.Equal(EstadoRed.Lenta, v.Contingencia.EstadoDeLaRed);

        // La caja principal sí lo guardó.
        Venta? enLaPrincipal = null;
        await VentaConCopia.EsperarAsync(() =>
        {
            using var db = servicio.Base();
            enLaPrincipal = db.Ventas.AsNoTracking().Include(x => x.Lineas).SingleOrDefault(x => x.Id == solicitud.VentaId);
            return enLaPrincipal is not null;
        }, "la venta en la caja principal");
        return new NoSeSabe(error, solicitud, enLaPrincipal!, turno, a);
    }

    /// <summary>Vuelve la red: tres latidos que contestan.</summary>
    private static async Task VolverAsync(VentaConCopia v)
    {
        for (var i = 0; i < 5 && !v.Contingencia.EnLinea; i++)
            await v.Contingencia.LatidoAsync();
        Assert.True(v.Contingencia.EnLinea);
    }

    [Fact]
    public async Task Su_folio_queda_apartado_y_el_siguiente_cobro_sin_conexion_sube()
    {
        await using var servicio = await ServicioDePrueba.CrearAsync();
        await using var v = await VentaConCopia.AbrirAsync(servicio, _baseLocal);
        var p = await NoSeSabeSiQuedoAsync(servicio, v);

        // El cajero lee que no se sabe, no lo que le faltó a la copia.
        Assert.Equal("cobro-sin-confirmar", p.Error.Codigo);
        Assert.Contains("no se sabe si el cobro quedó", p.Error.Message);

        // El siguiente cliente, con la red lenta (con la copia): otro folio, no el que quizá ya usó la caja principal, y sube.
        var dos = await v.Cliente.CobrarAsync(new SolicitudCobro(v.Caja.Id, p.Turno.Id, v.Cajero.UsuarioId,
            [new LineaSolicitada(p.A.VarianteId, p.A.Presentacion.Id, 1000)], [new CobroSolicitado(FormaPago.Efectivo, 10_000_000)], Guid.NewGuid()));
        Assert.NotEqual(p.EnLaPrincipal.FolioTexto, dos.Folio);
        await v.Contingencia.LatidoAsync();

        Assert.Equal((0, 0), (v.Contingencia.Pendientes, v.Contingencia.ConError));
        await using var db = servicio.Base();
        Assert.True(await db.Ventas.AnyAsync(x => x.Id == dos.VentaId));
    }

    [Fact]
    public async Task Solo_se_cobra_en_linea_con_el_mismo_Id_y_sale_el_que_quedo()
    {
        await using var servicio = await ServicioDePrueba.CrearAsync();
        await using var v = await VentaConCopia.AbrirAsync(servicio, _baseLocal);
        var p = await NoSeSabeSiQuedoAsync(servicio, v);

        // El cajero quita lo que la copia no tiene y cobra otra vez el mismo ticket con la red lenta: no se cobra con la copia
        // (sería otra venta con el mismo Id, y al subir se quedaría la primera).
        var cambiado = p.Solicitud with { Lineas = [p.Solicitud.Lineas[0]] };
        var otraVez = await Assert.ThrowsAsync<ErrorCaja>(() => v.Cliente.CobrarAsync(cambiado));
        Assert.Equal("cobro-sin-confirmar", otraVez.Codigo);
        Assert.Equal((0, 0), (v.Contingencia.Pendientes, v.Contingencia.ConError));

        // Vuelve la red: en línea sale el que ya había quedado, como se cobró la primera vez; una sola venta.
        await VolverAsync(v);
        var ticket = await v.Cliente.CobrarAsync(cambiado);

        Assert.Equal(p.EnLaPrincipal.FolioTexto, ticket.Folio);
        Assert.Equal(p.EnLaPrincipal.TotalCentavos, ticket.TotalCentavos);
        Assert.Equal(2, ticket.Lineas.Count);
        Assert.True(ticket.YaCobrada);
        Assert.False(v.Contingencia.CobroSinConfirmar(p.Solicitud.VentaId!.Value));
        Assert.True(v.Contingencia.UltimoFolio >= p.EnLaPrincipal.Folio);
        await using var db = servicio.Base();
        Assert.Equal(1, await db.Ventas.CountAsync(x => x.Id == p.Solicitud.VentaId));
    }

    /// <summary>
    /// En la pantalla: el cajero escanea lo de la copia y lo recién dado de alta (la caja principal se lo dice), cobra, y la
    /// respuesta no llega a tiempo. El panel dice que no se sabe si quedó; con la red lenta tampoco se cobra otra vez; al volver
    /// la red, el mismo ticket sale como ya había quedado, y el panel lo dice.
    /// </summary>
    [Fact]
    public async Task La_pantalla_dice_que_no_se_sabe_y_al_volver_la_red_que_ya_habia_quedado()
    {
        await using var servicio = await ServicioDePrueba.CrearAsync();
        await using var v = await VentaConCopia.AbrirAsync(servicio, _baseLocal);
        var (a, _) = v.DosDeLaCopia();
        await v.DarDeAltaEnOtraCajaAsync(CodigoNuevo, NombreNuevo);
        await servicio.CatalogoAlDiaAsync();
        await v.EscanearAsync(a.Codigo!);
        await v.EscanearAsync(CodigoNuevo);
        await VentaConCopia.EsperarAsync(() => VentaConCopia.RenglonesAsync(v.Pantalla).Result.Count(r => r.Contains(NombreNuevo)) == 1,
            "lo recién dado de alta en el ticket");

        v.Cliente.PlazoEscritura = TimeSpan.FromMilliseconds(500);
        v.Red.CobroRetenido = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await v.Pantalla.OprimirAsync(await v.Pantalla.BotonAsync("Cobrar exacto"));
        await v.Pantalla.EsperarAsync(t => t.Contains("no se sabe si el cobro quedó"), "lo que dice el panel");
        v.Red.CobroRetenido.SetResult();
        v.Cliente.PlazoEscritura = null;
        Assert.DoesNotContain("Cobrado", await v.Pantalla.TextoAsync());

        // Con la red lenta, otra vez: lo mismo, y nada a la bandeja.
        await v.Pantalla.OprimirAsync(await v.Pantalla.BotonAsync("Cobrar exacto"));
        Assert.Contains("no se sabe si el cobro quedó", await v.Pantalla.TextoAsync());
        Assert.Equal(0, v.Contingencia.Pendientes);

        await VolverAsync(v);
        await v.Pantalla.OprimirAsync(await v.Pantalla.BotonAsync("Cobrar exacto"));
        await v.Pantalla.EsperarAsync(t => t.Contains("ya había quedado cobrado"), "el ticket que ya había quedado");

        Assert.Contains("Cobrado", await v.Pantalla.TextoAsync());
        await using var db = servicio.Base();
        Assert.Equal(1, await db.Ventas.CountAsync(x => x.CajaId == v.Caja.Id && x.Lineas.Any(l => l.Descripcion.Contains(NombreNuevo))));
        Assert.Empty(v.Pantalla.Errores);
    }

    /// <summary>
    /// Si ni se pudo conectar (la caja principal apagada), el cobro no llegó: no se aparta nada, y el cajero puede quitar lo que
    /// la copia no tiene y cobrar el resto sin conexión, con el siguiente folio.
    /// </summary>
    [Fact]
    public async Task Si_ni_se_pudo_conectar_no_se_aparta_nada()
    {
        await using var servicio = await ServicioDePrueba.CrearAsync();
        await using var v = await VentaConCopia.AbrirAsync(servicio, _baseLocal);
        var turno = (await v.Cliente.TurnoAsync(v.Caja.Id))!;
        var (a, _) = v.DosDeLaCopia();
        var nuevo = await v.DarDeAltaEnOtraCajaAsync(CodigoNuevo, NombreNuevo);
        var folio = v.Contingencia.UltimoFolio;
        var solicitud = new SolicitudCobro(v.Caja.Id, turno.Id, v.Cajero.UsuarioId,
            [new LineaSolicitada(a.VarianteId, a.Presentacion.Id, 1000), new LineaSolicitada(nuevo.VarianteId, nuevo.Presentacion.Id, 1000)],
            [new CobroSolicitado(FormaPago.Efectivo, 10_000_000)], Guid.NewGuid());
        v.Red.Caida = true;

        var error = await Assert.ThrowsAsync<ErrorCaja>(() => v.Cliente.CobrarAsync(solicitud));
        Assert.Contains("no está en la copia", error.Message);
        Assert.Equal(EstadoRed.Caida, v.Contingencia.EstadoDeLaRed);
        var ticket = await v.Cliente.CobrarAsync(solicitud with { Lineas = [solicitud.Lineas[0]] });

        Assert.EndsWith($"-{folio + 1:000000}", ticket.Folio);
        Assert.Equal(1, v.Contingencia.Pendientes);
    }
}
