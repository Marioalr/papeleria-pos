using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using PapeleriaPos.Datos;
using PapeleriaPos.Nucleo.Configuracion;
using PapeleriaPos.Nucleo.Contratos;
using PapeleriaPos.Nucleo.Control;
using PapeleriaPos.Nucleo.Turnos;
using PapeleriaPos.Nucleo.Usuarios;

namespace PapeleriaPos.Pruebas.Datos;

/// <summary>
/// La configuración de la tienda (la Configuración nueva, entrega 7): la lee cualquier caja y la cambia quien entró con
/// «Configuración de la tienda», sin volver a escribir su PIN; cada cambio queda en la bitácora con lo que cambió.
/// </summary>
public class RegistroConfiguracionTests : IAsyncLifetime
{
    private BaseEnMemoria _base = null!;
    private readonly CatalogoMinimo _catalogo = new();
    private readonly Usuario _ana = new() { Nombre = "Ana", NombreUsuario = "ana" };
    private readonly Usuario _luis = new() { Nombre = "Luis", NombreUsuario = "luis", Rol = Rol.Encargado };
    private readonly Usuario _mario = new() { Nombre = "Mario", NombreUsuario = "mario", Rol = Rol.Dueno };
    private readonly Caja _caja = new() { Nombre = "Caja 1", Serie = "A" };

    public async Task InitializeAsync()
    {
        _base = await BaseEnMemoria.CrearAsync();
        _caja.SucursalId = _catalogo.Matriz.Id;
        _ana.SucursalId = _luis.SucursalId = _catalogo.Matriz.Id;
        _ana.EstablecerPin("1111");
        _luis.EstablecerPin("2222");
        _mario.EstablecerPin("3333");
        await using var db = _base.Contexto();
        _catalogo.Agregar(db);
        db.AddRange(_caja, _ana, _luis, _mario);
        await db.SaveChangesAsync();
        // Los usuarios de antes (con su rol) reciben sus permisos, como al arrancar el servicio: «Configuración de la tienda»,
        // solo el dueño.
        await MigracionPermisos.AplicarAsync(db);
    }

    public async Task DisposeAsync() => await _base.DisposeAsync();

    private async Task<T> ConAsync<T>(Func<RegistroConfiguracion, Task<T>> hacer)
    {
        await using var db = _base.Contexto();
        return await hacer(new RegistroConfiguracion(db));
    }

    private Task<ConfiguracionSucursalDto> LeerAsync() => ConAsync(r => r.LeerAsync(_catalogo.Matriz.Id));

    private async Task<ConfiguracionSucursalDto> CambiarAsync(Guid quien, Func<ConfiguracionSucursalDto, ConfiguracionSucursalDto> cambio)
    {
        var actual = await LeerAsync();
        return await ConAsync(r => r.GuardarAsync(_caja.Id, new SolicitudConfiguracionSucursal(quien, cambio(actual))));
    }

    private Task<List<Rapido>> RapidosAsync(Guid quien, params Rapido[] rapidos) =>
        ConAsync(r => r.GuardarRapidosAsync(_caja.Id, new SolicitudRapidos(quien, rapidos)));

    private async Task<List<(string Detalle, ResultadoBitacora Resultado, Guid? UsuarioId, Guid? CajaId)>> BitacoraAsync()
    {
        await using var db = _base.Contexto();
        return (await db.Bitacora.AsNoTracking().Where(b => b.Accion == AccionControlada.ConfigurarTienda).OrderBy(b => b.FechaUtc).ToListAsync())
            .Select(b => (b.Detalle, b.Resultado, b.UsuarioId, b.CajaId)).ToList();
    }

    [Fact]
    public async Task Sin_cambiar_nada_son_tres_fichas_sin_fondo_sugerido_y_el_nombre_de_la_sucursal()
    {
        var configuracion = await LeerAsync();

        Assert.Equal(ConfiguracionSucursal.FichasEnEsperaPorOmision, configuracion.FichasEnEspera);
        Assert.Equal(3, configuracion.FichasEnEspera);
        Assert.Null(configuracion.FondoSugeridoCentavos);
        Assert.Null(configuracion.NombreTienda);
        Assert.Null(configuracion.LimiteEfectivoCentavos);
        Assert.Equal((new TimeOnly(8, 0), new TimeOnly(21, 0), 5000L), (configuracion.HoraApertura, configuracion.HoraCierre, configuracion.UmbralCorteCentavos));
    }

    [Fact]
    public async Task El_dueno_cambia_cada_ajuste_y_en_la_bitacora_queda_solo_lo_que_cambio()
    {
        var guardada = await CambiarAsync(_mario.Id, c => c with
        {
            NombreTienda = "  Papelería   La Esquina ",
            Direccion = "Calle 5 #12, Centro",
            Telefono = "55 1234 5678",
            HoraApertura = new TimeOnly(9, 0),
            HoraCierre = new TimeOnly(20, 30),
            FichasEnEspera = 5,
            LimiteEfectivoCentavos = 300000,
            FondoSugeridoCentavos = 50000,
            UmbralCorteCentavos = 10000,
        });
        // Lo mismo otra vez: no se guarda ni se anota nada.
        await CambiarAsync(_mario.Id, c => c);
        // Solo el fondo: solo él en la bitácora. Vacío, ya no hay fondo sugerido.
        var sinFondo = await CambiarAsync(_mario.Id, c => c with { FondoSugeridoCentavos = null, NombreTienda = "" });

        Assert.Equal(new ConfiguracionSucursalDto("Papelería La Esquina", "Calle 5 #12, Centro", "55 1234 5678", new TimeOnly(9, 0), new TimeOnly(20, 30), 300000,
            50000, 10000, 5), guardada);
        Assert.Equal(guardada with { FondoSugeridoCentavos = null, NombreTienda = null }, sinFondo);
        Assert.Equal(sinFondo, await LeerAsync());
        var bitacora = await BitacoraAsync();
        Assert.Equal(
            [
                "Configuración de la tienda: Nombre de la tienda «Papelería La Esquina»; Dirección «Calle 5 #12, Centro»; Teléfono «55 1234 5678»; "
                + "Horario de 09:00 a 20:30; Tickets en espera a la vista 5; Límite de efectivo en caja $3,000.00; Fondo sugerido al abrir turno $500.00; "
                + "Avisar si un corte no cuadra por más de $100.00",
                "Configuración de la tienda: Nombre de la tienda vacío (el de la sucursal); Fondo sugerido al abrir turno sin fondo sugerido",
            ],
            bitacora.Select(b => b.Detalle));
        Assert.All(bitacora, b => Assert.Equal((ResultadoBitacora.Hecho, (Guid?)_mario.Id, (Guid?)_caja.Id), (b.Resultado, b.UsuarioId, b.CajaId)));
        // Lo de siempre lo toma quien lo usa: el límite de efectivo y el horario.
        await using var db = _base.Contexto();
        var configuracion = await db.ConfiguracionesSucursal.AsNoTracking().SingleAsync();
        Assert.True(configuracion.FueraDeHorario(new DateTime(2026, 10, 15, 20, 45, 0)));
        Assert.Equal(300000, configuracion.LimiteEfectivoCentavos);
    }

    [Fact]
    public async Task Con_como_estaba_en_la_pantalla_solo_se_toma_lo_que_cambio_quien_guarda()
    {
        // Una caja abrió la Configuración; mientras tanto, otra cambió el horario.
        var enLaPantalla = await LeerAsync();
        await CambiarAsync(_mario.Id, c => c with { HoraApertura = new TimeOnly(7, 0) });

        // La primera cambia el teléfono, con como lo tenía: el horario de la otra se queda.
        var guardada = await ConAsync(r => r.GuardarAsync(_caja.Id,
            new SolicitudConfiguracionSucursal(_mario.Id, enLaPantalla with { Telefono = "55 1111 2222" }, enLaPantalla)));

        Assert.Equal((new TimeOnly(7, 0), "55 1111 2222"), (guardada.HoraApertura, guardada.Telefono));
        Assert.Equal(guardada, await LeerAsync());
        Assert.Equal("Configuración de la tienda: Teléfono «55 1111 2222»", (await BitacoraAsync())[^1].Detalle);
    }

    [Fact]
    public async Task Sin_configuracion_de_la_tienda_no_se_cambia_nada_y_el_intento_queda()
    {
        var cajero = await Assert.ThrowsAsync<PermisoRequerido>(() => CambiarAsync(_ana.Id, c => c with { FichasEnEspera = 1 }));
        var encargado = await Assert.ThrowsAsync<PermisoRequerido>(() => CambiarAsync(_luis.Id, c => c with { FondoSugeridoCentavos = 10000 }));
        var rapidos = await Assert.ThrowsAsync<PermisoRequerido>(() => RapidosAsync(_luis.Id));

        Assert.Equal("Esto lo hace quien tiene el permiso «Configuración de la tienda»: Mario.", cajero.Message);
        Assert.Equal(cajero.Message, encargado.Message);
        Assert.Equal(cajero.Message, rapidos.Message);
        Assert.Equal(NivelPermiso.NoPermitido, cajero.Nivel);
        Assert.Equal(RegistroConfiguracion.Dto(new ConfiguracionSucursal()), await LeerAsync());
        var bitacora = await BitacoraAsync();
        Assert.Equal(3, bitacora.Count);
        Assert.All(bitacora, b => Assert.Equal(("Intento sin permiso", ResultadoBitacora.Negado), (b.Detalle, b.Resultado)));
        Assert.Equal([_ana.Id, _luis.Id, _luis.Id], bitacora.Select(b => b.UsuarioId!.Value));
    }

    [Fact]
    public async Task Con_el_permiso_otro_usuario_tambien_la_cambia()
    {
        await using (var db = _base.Contexto())
        {
            var permiso = await db.PermisosUsuario.SingleAsync(p => p.UsuarioId == _luis.Id && p.Accion == AccionControlada.ConfigurarTienda);
            permiso.Nivel = NivelPermiso.Permitido;
            await db.SaveChangesAsync();
        }

        var guardada = await CambiarAsync(_luis.Id, c => c with { FichasEnEspera = 0 });

        Assert.Equal(0, guardada.FichasEnEspera);
    }

    [Theory]
    [InlineData("fichas", "Se ven de 0 a 6 fichas; los demás tickets en espera van en «Más».")]
    [InlineData("horario", "La hora de cierre va después de la de apertura.")]
    [InlineData("limite", "El límite de efectivo debe ser mayor a cero (o vacío para no tener límite).")]
    [InlineData("fondo", "El fondo sugerido no puede ser negativo.")]
    [InlineData("corte", "El aviso de un corte que no cuadra no puede ser negativo.")]
    [InlineData("nombre", "El nombre de la tienda va hasta 60 caracteres.")]
    [InlineData("telefono", "El teléfono va hasta 30 caracteres.")]
    public async Task Lo_que_no_sirve_se_dice_y_no_se_guarda(string ajuste, string mensaje)
    {
        var antes = await LeerAsync();

        var error = await Assert.ThrowsAsync<ArgumentException>(() => CambiarAsync(_mario.Id, c => ajuste switch
        {
            "fichas" => c with { FichasEnEspera = 7 },
            "horario" => c with { HoraCierre = c.HoraApertura },
            "limite" => c with { LimiteEfectivoCentavos = 0 },
            "fondo" => c with { FondoSugeridoCentavos = -100 },
            "corte" => c with { UmbralCorteCentavos = -1 },
            "nombre" => c with { NombreTienda = new string('x', 61) },
            _ => c with { Telefono = new string('5', 31) },
        }));

        Assert.Equal(mensaje, error.Message);
        Assert.Equal(antes, await LeerAsync());
        Assert.Empty(await BitacoraAsync());
    }

    [Fact]
    public async Task Los_rapidos_se_guardan_en_su_orden_y_vacios_quedan_vacios()
    {
        var guardados = await RapidosAsync(_mario.Id, new Rapido(" Docena ", _catalogo.Azul.Id, 12), new Rapido("Pluma", _catalogo.Azul.Id));
        var otraVez = await RapidosAsync(_mario.Id, guardados[1], guardados[0]);
        var ninguno = await RapidosAsync(_mario.Id);

        Assert.Equal([new Rapido("Docena", _catalogo.Azul.Id, 12), new Rapido("Pluma", _catalogo.Azul.Id)], guardados);
        Assert.Equal(["Pluma", "Docena"], otraVez.Select(r => r.Nombre));
        Assert.Empty(ninguno);
        await using var db = _base.Contexto();
        // Vacíos (no «sin configurar»): preparar-caja no vuelve a poner los de la prueba.
        Assert.Equal([], (await db.ConfiguracionesSucursal.AsNoTracking().SingleAsync()).Rapidos);
        Assert.Equal(
            ["Rápidos: Alt+1 Docena ×12; Alt+2 Pluma", "Rápidos: Alt+1 Pluma; Alt+2 Docena ×12", "Rápidos: ninguno"],
            (await BitacoraAsync()).Select(b => b.Detalle));
    }

    [Fact]
    public async Task Los_rapidos_se_revisan()
    {
        var azul = _catalogo.Azul.Id;
        async Task<string> ErrorAsync(params Rapido[] rapidos) => (await Assert.ThrowsAsync<ArgumentException>(() => RapidosAsync(_mario.Id, rapidos))).Message;

        Assert.Equal("Van hasta 6 rápidos: Alt+1 a Alt+6.", await ErrorAsync([.. Enumerable.Range(1, 7).Select(i => new Rapido($"R{i}", azul))]));
        Assert.Equal("Cada rápido lleva su nombre: es lo que dice su botón.", await ErrorAsync(new Rapido("  ", azul)));
        Assert.Equal("«Pluma punto fino azul de gel» no cabe en el botón: el nombre de un rápido va hasta 24 caracteres.",
            await ErrorAsync(new Rapido("Pluma punto fino azul de gel", azul)));
        Assert.Equal("Ya hay un rápido que se llama «PLUMA»: ponle otro nombre.", await ErrorAsync(new Rapido("Pluma", azul), new Rapido("PLUMA", azul, 2)));
        Assert.Equal("La cantidad de «Pluma» va de 0.001 a 9,999.", await ErrorAsync(new Rapido("Pluma", azul, 0)));
        Assert.Equal("La cantidad de «Pluma» va de 0.001 a 9,999.", await ErrorAsync(new Rapido("Pluma", azul, 0.0005m)));
        Assert.Equal("El producto de «Otro» ya no está en el catálogo: elige otro.", await ErrorAsync(new Rapido("Otro", Guid.NewGuid())));

        // Una variante dada de baja tampoco.
        await using (var db = _base.Contexto())
        {
            (await db.Variantes.SingleAsync(v => v.Id == azul)).Activa = false;
            await db.SaveChangesAsync();
        }
        Assert.Equal("El producto de «Pluma» ya no está en el catálogo: elige otro.", await ErrorAsync(new Rapido("Pluma", azul)));
        Assert.Empty(await BitacoraAsync());
    }
}

/// <summary>La migración de la Configuración nueva: lo que ya estaba guardado sigue con las 3 fichas y sin fondo sugerido.</summary>
public class MigracionConfiguracionTests
{
    private const string Anterior = "20261015145722_PermisosPorUsuario";

    [Fact]
    public async Task Una_sucursal_que_ya_tenia_configuracion_sigue_con_tres_fichas_y_sin_fondo_sugerido()
    {
        await using var conexion = new SqliteConnection("Data Source=:memory:");
        await conexion.OpenAsync();
        await using var db = new PosDbContext(new DbContextOptionsBuilder<PosDbContext>().UseSqlite(conexion).Options);
        var migrador = db.GetService<IMigrator>();
        await migrador.MigrateAsync(Anterior);
        var sucursal = Guid.NewGuid().ToString().ToUpperInvariant();
        await db.Database.ExecuteSqlRawAsync(
            "INSERT INTO Sucursales (Id, Clave, Nombre, Activa, Version, ModificadoUtc) VALUES ({0}, 'M', 'Matriz', 1, 1, '2026-10-01 12:00:00')", sucursal);
        await db.Database.ExecuteSqlRawAsync(
            "INSERT INTO ConfiguracionesSucursal (SucursalId, LimiteEfectivoCentavos, AlertasApagadas, EtiquetasAjusteXDecimas, EtiquetasAjusteYDecimas, "
            + "Version, ModificadoUtc) VALUES ({0}, 300000, '', 0, 0, 1, '2026-10-01 12:00:00')", sucursal);

        await migrador.MigrateAsync();

        var configuracion = await new RegistroConfiguracion(db).LeerAsync(Guid.Parse(sucursal));
        Assert.Equal((3, (long?)null, (string?)null, (long?)300000), (configuracion.FichasEnEspera, configuracion.FondoSugeridoCentavos,
            configuracion.NombreTienda, configuracion.LimiteEfectivoCentavos));
    }
}
