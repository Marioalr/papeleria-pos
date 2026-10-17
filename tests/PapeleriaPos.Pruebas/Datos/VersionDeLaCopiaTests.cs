using Microsoft.EntityFrameworkCore;
using PapeleriaPos.Datos;
using PapeleriaPos.Nucleo;
using PapeleriaPos.Nucleo.Catalogo;
using PapeleriaPos.Nucleo.Configuracion;
using PapeleriaPos.Nucleo.Inventario;
using PapeleriaPos.Nucleo.Turnos;
using PapeleriaPos.Nucleo.Usuarios;

namespace PapeleriaPos.Pruebas.Datos;

/// <summary>
/// La versión del catálogo sube solo con lo que lleva la copia de las cajas (entrega 5, parte 2): un PIN mal tecleado, abrir
/// y cerrar un turno o el último costo de un proveedor ya no obligan a cada caja a bajar su copia. Lo quitado queda anotado
/// con su variante, y las existencias llevan su propia versión: una venta sube esa y no la del catálogo.
/// </summary>
public class VersionDeLaCopiaTests : IAsyncLifetime
{
    private BaseEnMemoria _base = null!;
    private readonly CatalogoMinimo _catalogo = new();
    private readonly Usuario _cajera = new() { Nombre = "Ana", NombreUsuario = "ana" };
    private readonly Usuario _dueno = new() { Nombre = "Don", NombreUsuario = "don", Rol = Rol.Dueno };
    private Caja _caja = null!;

    public async Task InitializeAsync()
    {
        _base = await BaseEnMemoria.CrearAsync();
        _caja = new Caja { SucursalId = _catalogo.Matriz.Id, Nombre = "Caja 1", Serie = "A", EsPrincipal = true };
        _cajera.EstablecerPin("1111");
        _dueno.EstablecerPin("3333");
        await using var db = _base.Contexto();
        _catalogo.Agregar(db);
        db.AddRange(_caja, _cajera, _dueno, new Precio { VarianteId = _catalogo.Azul.Id, PrecioCentavos = 1500 },
            new ConfiguracionSucursal { SucursalId = _catalogo.Matriz.Id });
        await db.SaveChangesAsync();
        await MigracionPermisos.AplicarAsync(db);
    }

    public async Task DisposeAsync() => await _base.DisposeAsync();

    private async Task<long> VersionAsync(string secuencia = Secuencia.Catalogo)
    {
        await using var db = _base.Contexto();
        return await db.Secuencias.Where(s => s.Nombre == secuencia).Select(s => s.Valor).FirstOrDefaultAsync();
    }

    private async Task CambiarAsync(Action<PosDbContext> cambio)
    {
        await using var db = _base.Contexto();
        cambio(db);
        await db.SaveChangesAsync();
    }

    [Fact]
    public async Task Un_PIN_mal_tecleado_no_sube_la_version_y_cambiar_el_PIN_si()
    {
        var antes = await VersionAsync();

        await using (var db = _base.Contexto())
        {
            var ana = await db.Usuarios.SingleAsync(u => u.Id == _cajera.Id);
            Assert.Equal(ResultadoPin.Incorrecto, ana.VerificarPin("9999", _base.Reloj.Ahora.UtcDateTime));
            await db.SaveChangesAsync();
            // Y entrar bien después (se borran los intentos) tampoco.
            Assert.Equal(ResultadoPin.Correcto, ana.VerificarPin("1111", _base.Reloj.Ahora.UtcDateTime));
            await db.SaveChangesAsync();
        }
        Assert.Equal(antes, await VersionAsync());

        await using (var db = _base.Contexto())
        {
            (await db.Usuarios.SingleAsync(u => u.Id == _cajera.Id)).EstablecerPin("2468");
            await db.SaveChangesAsync();
        }
        Assert.Equal(antes + 1, await VersionAsync());
        await using (var lectura = _base.Contexto())
            Assert.Equal(antes + 1, (await lectura.Usuarios.SingleAsync(u => u.Id == _cajera.Id)).Version);
    }

    [Fact]
    public async Task Abrir_y_cerrar_un_turno_no_sube_la_version()
    {
        var antes = await VersionAsync();

        Turno turno;
        await using (var db = _base.Contexto())
            turno = await new RegistroCaja(db).AbrirTurnoAsync(_caja.Id, _cajera.Id, 50000);
        await using (var db = _base.Contexto())
            await new RegistroCaja(db).CorteZAsync(turno.Id, _dueno.Id, 50000);

        Assert.Equal(antes, await VersionAsync());
    }

    [Fact]
    public async Task Lo_que_la_copia_no_lleva_no_sube_la_version()
    {
        var antes = await VersionAsync();
        var proveedor = new Proveedor { Nombre = "Papelera del Centro" };

        await CambiarAsync(db => db.AddRange(proveedor, new ProveedorVariante { ProveedorId = proveedor.Id, VarianteId = _catalogo.Azul.Id, UltimoCostoCentavos = 700 }));
        await CambiarAsync(db => db.Departamentos.Single().MargenMinimoCentesimas = 3500);
        await CambiarAsync(db => db.Productos.Single().PorCompletar = true);
        await CambiarAsync(db => db.ConfiguracionesSucursal.Single().UmbralSalidaCentavos = 70000);
        await CambiarAsync(db => db.ConfiguracionesSucursal.Single().Rapidos = [new Rapido("Copia", _catalogo.Azul.Id, 1)]);
        await CambiarAsync(db => db.CodigosBarras.Single().Origen = OrigenCodigo.Interno);
        await CambiarAsync(db => db.Cajas.Single().DarDeAlta("CAJA-2", _base.Reloj.Ahora.UtcDateTime));

        // El equipo dado de alta sí va en la copia (el nombre de la PC de esa caja), la llave no: subió una vez.
        Assert.Equal(antes + 1, await VersionAsync());
        await using var lectura = _base.Contexto();
        Assert.Equal(0, (await lectura.ProveedoresVariantes.SingleAsync()).Version);
        // Su hora sí queda (el sugerido ordena los proveedores con ella).
        Assert.Equal(_base.Reloj.Ahora.UtcDateTime, (await lectura.ProveedoresVariantes.SingleAsync()).ModificadoUtc);
    }

    [Fact]
    public async Task Lo_que_la_copia_lleva_sube_la_version_una_vez_por_guardado()
    {
        var antes = await VersionAsync();

        await CambiarAsync(db =>
        {
            db.Productos.Single().Nombre = "Pluma punto mediano";
            db.Precios.Single().PrecioCentavos = 1600;
        });
        await CambiarAsync(db => db.ConfiguracionesSucursal.Single().NombreTienda = "Papelería La Esquina");
        await CambiarAsync(db => db.Marcas.Single().Nombre = "BIC");

        Assert.Equal(antes + 3, await VersionAsync());
        await using var lectura = _base.Contexto();
        Assert.Equal(antes + 1, (await lectura.Productos.SingleAsync()).Version);
        Assert.Equal(antes + 1, (await lectura.Precios.SingleAsync()).Version);
        Assert.Equal(antes + 1, (await lectura.PreciosHistorial.SingleAsync(h => h.PrecioNuevoCentavos == 1600)).VersionCatalogo);
    }

    [Fact]
    public async Task Lo_quitado_queda_anotado_con_su_variante_y_su_version()
    {
        await CambiarAsync(db => db.CodigosBarras.Add(new CodigoBarras { Codigo = "7501234567891", PresentacionId = _catalogo.Pieza.Id }));
        var antes = await VersionAsync();

        // Un código, desde otro contexto que no tiene la presentación en memoria (se lee de la base), y una escala de precio.
        await CambiarAsync(db => db.CodigosBarras.Remove(db.CodigosBarras.Single(c => c.Codigo == "7501234567891")));
        await CambiarAsync(db => db.Precios.Remove(db.Precios.Single()));

        await using var lectura = _base.Contexto();
        var borrados = await lectura.Borrados.OrderBy(b => b.Version).ToListAsync();
        Assert.Equal([(nameof(CodigoBarras), _catalogo.Azul.Id, antes + 1), (nameof(Precio), _catalogo.Azul.Id, antes + 2)],
            borrados.Select(b => (b.Entidad, b.VarianteId!.Value, b.Version)));
        Assert.NotNull(borrados[0].EntidadId);
    }

    [Fact]
    public async Task Un_codigo_que_pasa_a_otra_presentacion_deja_anotada_la_variante_de_antes()
    {
        var roja = new Variante { ProductoId = _catalogo.Pluma.Id, Descripcion = "Roja" };
        var piezaRoja = new Presentacion { VarianteId = roja.Id, Nombre = "Pieza", EsBase = true };
        await CambiarAsync(db => db.AddRange(roja, piezaRoja));

        await CambiarAsync(db => db.CodigosBarras.Single().PresentacionId = piezaRoja.Id);

        await using var lectura = _base.Contexto();
        var borrado = await lectura.Borrados.SingleAsync();
        Assert.Equal((nameof(CodigoBarras), _catalogo.Azul.Id), (borrado.Entidad, borrado.VarianteId!.Value));
        Assert.Equal(await VersionAsync(), (await lectura.CodigosBarras.SingleAsync()).Version);
    }

    [Fact]
    public async Task Una_venta_sube_la_version_de_las_existencias_y_no_la_del_catalogo()
    {
        var (catalogo, existencias) = (await VersionAsync(), await VersionAsync(Secuencia.Existencias));

        await using (var db = _base.Contexto())
        {
            var inventario = new RegistroInventario(db);
            await inventario.AgregarAsync(Kardex.Entrada(_catalogo.Matriz.Id, _catalogo.Azul, TipoMovimiento.Compra, Cantidad.Piezas(10), 7000,
                _base.Reloj.Ahora.UtcDateTime));
            await db.SaveChangesAsync();
        }
        await using (var db = _base.Contexto())
        {
            var inventario = new RegistroInventario(db);
            var existencia = await inventario.ExistenciaAsync(_catalogo.Matriz.Id, _catalogo.Azul.Id);
            await inventario.AgregarAsync(Kardex.Salida(_catalogo.Matriz.Id, _catalogo.Azul, TipoMovimiento.Venta, Cantidad.Piezas(3), existencia, 700,
                _base.Reloj.Ahora.UtcDateTime));
            await db.SaveChangesAsync();
        }

        Assert.Equal(catalogo, await VersionAsync());
        Assert.Equal(existencias + 2, await VersionAsync(Secuencia.Existencias));
        await using var lectura = _base.Contexto();
        var azul = await lectura.Existencias.SingleAsync(e => e.VarianteId == _catalogo.Azul.Id);
        Assert.Equal((Cantidad.Piezas(7), existencias + 2), (azul.Cantidad, azul.Version));
    }

    [Fact]
    public void Cada_propiedad_que_no_lleva_la_copia_existe_en_el_modelo()
    {
        using var db = new PosDbContext(BaseDeDatos.Opciones("Data Source=:memory:"));
        foreach (var (tipo, propiedades) in LoQueLlevaLaCopia.QueNoLleva)
        {
            var entidad = db.Model.FindEntityType(tipo);
            Assert.NotNull(entidad);
            Assert.True(typeof(IVersionado).IsAssignableFrom(tipo), tipo.Name);
            Assert.All(propiedades, p => Assert.True(entidad.FindProperty(p) is not null, $"{tipo.Name}.{p} no está en el modelo."));
        }
        Assert.All(LoQueLlevaLaCopia.QueNoVan, t => Assert.True(typeof(IVersionado).IsAssignableFrom(t), t.Name));
    }

    /// <summary>
    /// Dos guardados a la vez, en dos conexiones a la misma base, nunca quedan con la misma versión: la versión sube en la base
    /// dentro de la transacción de cada uno. Antes se leía antes y dos podían quedar con la misma; con los cambios de la copia,
    /// una caja que ya había pedido esa versión no habría recibido el segundo.
    /// </summary>
    [Fact]
    public async Task Dos_guardados_a_la_vez_no_quedan_con_la_misma_version()
    {
        var ruta = Path.Combine(Path.GetTempPath(), $"version-copia-{Guid.NewGuid():N}.db");
        var cadena = $"Data Source={ruta}";
        try
        {
            var catalogo = new CatalogoMinimo();
            await using (var db = new PosDbContext(BaseDeDatos.Opciones(cadena)))
            {
                await BaseDeDatos.PrepararAsync(db);
                catalogo.Agregar(db);
                await db.SaveChangesAsync();
            }

            async Task<List<long>> GuardarAsync(int cuantos)
            {
                var versiones = new List<long>();
                for (var i = 0; i < cuantos; i++)
                {
                    await using var db = new PosDbContext(BaseDeDatos.Opciones(cadena));
                    var precio = new Precio { VarianteId = catalogo.Azul.Id, CantidadMinima = Cantidad.Piezas(10 + i), PrecioCentavos = 100 + i };
                    db.Precios.Add(precio);
                    await db.SaveChangesAsync();
                    versiones.Add(precio.Version);
                }
                return versiones;
            }

            var resultados = await Task.WhenAll(Task.Run(() => GuardarAsync(20)), Task.Run(async () =>
            {
                var versiones = new List<long>();
                for (var i = 0; i < 20; i++)
                {
                    await using var db = new PosDbContext(BaseDeDatos.Opciones(cadena));
                    var producto = await db.Productos.SingleAsync();
                    producto.Nombre = $"Pluma {i}";
                    await db.SaveChangesAsync();
                    versiones.Add(producto.Version);
                }
                return versiones;
            }));

            var todas = resultados.SelectMany(v => v).ToList();
            Assert.Equal(todas.Count, todas.Distinct().Count());
            await using var lectura = new PosDbContext(BaseDeDatos.Opciones(cadena));
            Assert.Equal(todas.Max(), await lectura.Secuencias.Where(s => s.Nombre == Secuencia.Catalogo).Select(s => s.Valor).SingleAsync());
        }
        finally
        {
            BasesDePrueba.Borrar(ruta, cadena);
        }
    }
}
