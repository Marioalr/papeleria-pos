using System.Collections.Concurrent;
using System.Data.Common;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using PapeleriaPos.Datos;
using PapeleriaPos.Nucleo;
using PapeleriaPos.Nucleo.Catalogo;
using PapeleriaPos.Nucleo.Contratos;
using PapeleriaPos.Nucleo.Inventario;
using PapeleriaPos.Nucleo.Usuarios;

namespace PapeleriaPos.Pruebas.Datos;

/// <summary>
/// Lo que lee la existencia para escribirla la lee ya con el candado de escritura tomado (revisión del PR #138): ajustes, mermas,
/// el cierre de una compra, el conteo del día, abrir un paquete y la depuración. Antes la leían sin él, y la existencia no lleva
/// control de concurrencia: lo que otra caja agregaba entre esa lectura y la escritura se perdía en la tabla de existencias (el
/// kardex sí lo tenía). Aquí un «Agregar inventario» sale justo después de esa lectura y llega hasta el candado antes de que la
/// operación siga: tiene que esperar a que termine, y queda lo de los dos. No depende de lo que tarde cada uno.
/// </summary>
public sealed class ExistenciaConElCandadoTests : IAsyncLifetime
{
    private readonly string _ruta = Path.Combine(Path.GetTempPath(), $"existencia-candado-{Guid.NewGuid():N}.db");
    private readonly CatalogoMinimo _catalogo = new();
    private readonly Usuario _dueno = new() { Nombre = "Rosa", NombreUsuario = "rosa", Rol = Rol.Dueno };
    private readonly Proveedor _proveedor = new() { Nombre = "Papelera del centro" };

    private string Cadena => $"Data Source={_ruta};Pooling=False";

    private PosDbContext Contexto(params IInterceptor[] interceptores) =>
        new(new DbContextOptionsBuilder<PosDbContext>().UseSqlite(Cadena).AddInterceptors(interceptores).Options);

    private Guid Sucursal => _catalogo.Matriz.Id;

    public async Task InitializeAsync()
    {
        _dueno.EstablecerPin("3333");
        await using var db = Contexto();
        await BaseDeDatos.PrepararAsync(db);
        _catalogo.Agregar(db);
        db.AddRange(_dueno, _proveedor);
        await db.SaveChangesAsync();
        await MigracionPermisos.AplicarAsync(db);
        // 10 piezas a $10.00.
        await new RegistroInventario(db).RegistrarAsync(Kardex.Entrada(Sucursal, _catalogo.Azul, TipoMovimiento.InventarioInicial, Cantidad.Piezas(10),
            10_000, DateTime.UtcNow));
    }

    public Task DisposeAsync()
    {
        ArchivosDePrueba.BorrarBase(_ruta);
        return Task.CompletedTask;
    }

    /// <summary>Avisa cuando su contexto va a tomar el candado de escritura (su BEGIN).</summary>
    private sealed class AlCandado(TaskCompletionSource llego) : DbTransactionInterceptor
    {
        public override ValueTask<InterceptionResult<DbTransaction>> TransactionStartingAsync(DbConnection connection, TransactionStartingEventData eventData,
            InterceptionResult<DbTransaction> result, CancellationToken cancellationToken = default)
        {
            llego.TrySetResult();
            return ValueTask.FromResult(result);
        }
    }

    /// <summary>
    /// Justo después de que la operación lee la existencia (la primera vez), otra caja agrega 5 piezas a $10.00, desde otra
    /// conexión; la operación sigue cuando ese agregado ya llegó al candado (o falló antes).
    /// </summary>
    private sealed class AgregadoALaVez(Func<TaskCompletionSource, Task> agregar) : DbCommandInterceptor
    {
        public Task? Agregado { get; private set; }

        public override async ValueTask<DbDataReader> ReaderExecutedAsync(DbCommand command, CommandExecutedEventData eventData, DbDataReader result,
            CancellationToken cancellationToken = default)
        {
            if (Agregado is null && command.CommandText.Contains("FROM \"Existencias\"", StringComparison.Ordinal))
            {
                var enElCandado = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                Agregado = Task.Run(() => agregar(enElCandado));
                await Task.WhenAny(enElCandado.Task, Agregado);
            }
            return result;
        }
    }

    private AgregadoALaVez AgregarALaVez() => new(async enElCandado =>
    {
        await using var otra = Contexto(new AlCandado(enElCandado));
        await new RegistroAgregarInventario(otra).AgregarAsync(new SolicitudAgregarInventario(Ids.Nuevo(), _dueno.Id, Sucursal, _catalogo.Azul.Id,
            Cantidad.Piezas(5).Milesimas, 1000));
    });

    /// <summary>La existencia, y que cuadre con el kardex.</summary>
    private async Task<(Cantidad Cantidad, long Costo)> ExistenciaAsync()
    {
        await using var db = Contexto();
        Assert.Empty(await new RegistroInventario(db).VerificarExistenciasAsync());
        var existencia = await db.Existencias.AsNoTracking().SingleAsync(e => e.VarianteId == _catalogo.Azul.Id);
        return (existencia.Cantidad, existencia.CostoCentavos);
    }

    [Fact]
    public async Task Lo_agregado_mientras_se_ajusta_no_se_pierde()
    {
        var aLaVez = AgregarALaVez();

        await using (var db = Contexto(aLaVez))
            await new RegistroAjustes(db).AjustarAsync(new SolicitudAjuste(_dueno.Id, Sucursal, _catalogo.Azul.Id, Cantidad.Piezas(8).Milesimas, "Se contaron 8"));
        await aLaVez.Agregado!;

        // El ajuste vio las 10 (de 10 a 8) y el agregado esperó a que terminara: 8 + 5.
        Assert.Equal((Cantidad.Piezas(13), 13_000L), await ExistenciaAsync());
        await using var lectura = Contexto();
        var bitacora = await lectura.Bitacora.Select(b => b.Detalle).ToListAsync();
        Assert.Contains(bitacora, d => d.StartsWith("Ajustó «Pluma punto fino Bic Azul»: de 10 a 8 (-2)", StringComparison.Ordinal));
        Assert.Contains(bitacora, d => d.StartsWith("Agregó 5 «Pluma punto fino Bic Azul» (había 8, quedan 13)", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Lo_agregado_mientras_se_registra_una_merma_no_se_pierde()
    {
        var aLaVez = AgregarALaVez();

        await using (var db = Contexto(aLaVez))
            await new RegistroAjustes(db).MermaAsync(new SolicitudMerma(_dueno.Id, Sucursal, _catalogo.Azul.Id, Cantidad.Piezas(2).Milesimas, CausaMerma.Danado, null));
        await aLaVez.Agregado!;

        Assert.Equal((Cantidad.Piezas(13), 13_000L), await ExistenciaAsync());
    }

    [Fact]
    public async Task Lo_agregado_mientras_se_cierra_una_compra_no_se_pierde()
    {
        Guid compraId;
        await using (var db = Contexto())
        {
            var compras = new RegistroCompras(db);
            compraId = (await compras.AbrirAsync(new SolicitudAbrirCompra(_dueno.Id, Sucursal, _proveedor.Id, "N-1"))).Id;
            await compras.ContarAsync(compraId, new SolicitudConteo(_dueno.Id, _catalogo.Azul.Id, _catalogo.Pieza.Id, Cantidad.Piezas(4).Milesimas));
            await compras.TerminarConteoAsync(compraId, _dueno.Id);
            await compras.RenglonAsync(compraId, new SolicitudRenglonCompra(_dueno.Id, null, _catalogo.Azul.Id, _catalogo.Pieza.Id, Cantidad.Piezas(4).Milesimas, 4_800));
        }
        var aLaVez = AgregarALaVez();

        await using (var db = Contexto(aLaVez))
            await new RegistroCompras(db).CerrarAsync(compraId, _dueno.Id);
        await aLaVez.Agregado!;

        // 10 + 4 de la compra (a $12.00) + 5 agregadas.
        Assert.Equal((Cantidad.Piezas(19), 19_800L), await ExistenciaAsync());
    }

    /// <summary>Cada lectura que manda el contexto y si iba dentro de una transacción.</summary>
    private sealed class Espia : DbCommandInterceptor
    {
        public ConcurrentQueue<(string Sql, bool EnTransaccion)> Comandos { get; } = new();

        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(DbCommand command, CommandEventData eventData,
            InterceptionResult<DbDataReader> result, CancellationToken cancellationToken = default)
        {
            Comandos.Enqueue((command.CommandText, command.Transaction is not null));
            return ValueTask.FromResult(result);
        }
    }

    /// <summary>Que <paramref name="operacion"/> lea la existencia, y siempre con su transacción ya abierta.</summary>
    private async Task LeeLaExistenciaConElCandadoAsync(string que, Func<PosDbContext, Task> operacion)
    {
        var espia = new Espia();
        await using (var db = Contexto(espia))
            await operacion(db);
        var lecturas = espia.Comandos.Where(c => c.Sql.Contains("FROM \"Existencias\"", StringComparison.Ordinal)).ToList();
        Assert.True(lecturas.Count > 0, $"{que}: no leyó la existencia.");
        Assert.All(lecturas, l => Assert.True(l.EnTransaccion, $"{que} leyó la existencia sin el candado:\n{l.Sql}"));
    }

    /// <summary>Una variante más de la pluma, con su pieza y con existencia (para abrir un paquete y para la depuración).</summary>
    private async Task<Variante> OtraVarianteAsync(string descripcion, int piezas)
    {
        await using var db = Contexto();
        var variante = new Variante { ProductoId = _catalogo.Pluma.Id, Descripcion = descripcion };
        db.AddRange(variante, new Presentacion { VarianteId = variante.Id, Nombre = "Pieza", EsBase = true });
        await db.SaveChangesAsync();
        if (piezas > 0)
            await new RegistroInventario(db).RegistrarAsync(Kardex.Entrada(Sucursal, variante, TipoMovimiento.InventarioInicial, Cantidad.Piezas(piezas),
                piezas * 500L, DateTime.UtcNow));
        return variante;
    }

    /// <summary>
    /// Lo demás que lee la existencia para escribirla, también con el candado: el conteo del día, abrir un paquete y la depuración
    /// (dar de baja, juntar y cambiar la unidad).
    /// </summary>
    [Fact]
    public async Task El_conteo_del_dia_abrir_un_paquete_y_la_depuracion_leen_la_existencia_con_el_candado()
    {
        var conteo = new ConteoCiclico { SucursalId = Sucursal, Fecha = DateOnly.FromDateTime(HoraMexico.ALocal(DateTime.UtcNow)), CreadoUtc = DateTime.UtcNow };
        conteo.Renglones.Add(new ConteoCiclicoRenglon { ConteoId = conteo.Id, Orden = 1, VarianteId = _catalogo.Azul.Id });
        await using (var db = Contexto())
        {
            db.ConteosCiclicos.Add(conteo);
            await db.SaveChangesAsync();
        }
        var suelta = await OtraVarianteAsync("Suelta", 0);
        var roja = await OtraVarianteAsync("Roja", 3);
        var verde = await OtraVarianteAsync("Verde", 2);
        var negra = await OtraVarianteAsync("Negra", 1);
        var morada = await OtraVarianteAsync("Morada", 4);

        await LeeLaExistenciaConElCandadoAsync("El conteo del día", db =>
            new RegistroConteoCiclico(db).ContarAsync(conteo.Id, new SolicitudContar(_dueno.Id, _catalogo.Azul.Id, Cantidad.Piezas(9).Milesimas)));
        await LeeLaExistenciaConElCandadoAsync("Abrir un paquete", db =>
            new RegistroAperturas(db).AbrirAsync(new SolicitudAbrirPaquete(_dueno.Id, Sucursal, _catalogo.Azul.Id, Cantidad.Piezas(1).Milesimas, suelta.Id,
                Cantidad.Piezas(12).Milesimas)));
        await LeeLaExistenciaConElCandadoAsync("Dar de baja", db => new RegistroDepuracion(db).DarDeBajaAsync(_dueno.Id, Sucursal, roja.Id));
        await LeeLaExistenciaConElCandadoAsync("Juntar", db => new RegistroDepuracion(db).JuntarAsync(_dueno.Id, Sucursal, verde.Id, negra.Id));
        await LeeLaExistenciaConElCandadoAsync("Cambiar la unidad", db =>
            new RegistroDepuracion(db).CambiarUnidadAsync(_dueno.Id, Sucursal, morada.Id, UnidadBase.Metro, Cantidad.Piezas(2).Milesimas, 300));

        await using var lectura = Contexto();
        Assert.Empty(await new RegistroInventario(lectura).VerificarExistenciasAsync());
        Assert.Equal(Cantidad.Piezas(3), (await lectura.Existencias.AsNoTracking().SingleAsync(e => e.VarianteId == negra.Id)).Cantidad);
    }
}
