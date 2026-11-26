using Microsoft.EntityFrameworkCore;
using PapeleriaPos.Datos;
using PapeleriaPos.Nucleo;
using PapeleriaPos.Nucleo.Catalogo;
using PapeleriaPos.Nucleo.Inventario;
using PapeleriaPos.Nucleo.Turnos;
using PapeleriaPos.Nucleo.Usuarios;
using PapeleriaPos.Nucleo.Ventas;

namespace PapeleriaPos.Pruebas.Datos;

/// <summary>
/// Los folios sin letra (2026-11-22, regla del dueño; <see cref="Folios"/>): lo que registra en línea la caja principal sale sin letra, de un
/// solo consecutivo de la sucursal, cobre la caja que cobre; una caja secundaria solo usa su letra en lo que cobra sin conexión; lo de antes
/// se queda como salió y se busca con y sin letra.
/// </summary>
public class FoliosSinLetraTests : IAsyncLifetime
{
    private BaseEnMemoria _base = null!;
    private readonly CatalogoMinimo _catalogo = new();
    private readonly Usuario _cajera = new() { Nombre = "Ana", NombreUsuario = "ana" };
    private readonly Usuario _otra = new() { Nombre = "Bety", NombreUsuario = "bety" };
    private Caja _principal = null!;
    private Caja _secundaria = null!;
    private readonly Precio _precio;

    public FoliosSinLetraTests() =>
        _precio = new Precio { VarianteId = _catalogo.Azul.Id, CantidadMinima = Cantidad.Piezas(1), PrecioCentavos = 1000 };

    public async Task InitializeAsync()
    {
        _base = await BaseEnMemoria.CrearAsync();
        _principal = new Caja { SucursalId = _catalogo.Matriz.Id, Nombre = "Caja principal", Serie = Folios.SinLetra, EsPrincipal = true };
        _secundaria = new Caja { SucursalId = _catalogo.Matriz.Id, Nombre = "Caja 2", Serie = "B" };
        _cajera.EstablecerPin("1111");
        _otra.EstablecerPin("2222");
        await using var db = _base.Contexto();
        _catalogo.Agregar(db);
        db.AddRange(_principal, _secundaria, _cajera, _otra, _precio);
        await db.SaveChangesAsync();
        await MigracionPermisos.AplicarAsync(db);
        await new RegistroInventario(db).RegistrarAsync(Kardex.Entrada(_catalogo.Matriz.Id, _catalogo.Azul, TipoMovimiento.Compra,
            Cantidad.Piezas(100), 4000, _base.Reloj.Ahora.UtcDateTime));
    }

    public async Task DisposeAsync() => await _base.DisposeAsync();

    private Venta VentaDe(Turno turno, Caja caja)
    {
        var venta = new Venta { SucursalId = turno.SucursalId, CajaId = turno.CajaId, TurnoId = turno.Id, CajeroId = turno.CajeroId, Serie = caja.Serie };
        var producto = new ProductoEnVenta(_catalogo.Azul.Id, "Pluma punto fino Azul", Impuesto.Iva16, UnidadBase.Pieza, true);
        venta.AgregarProducto(producto, _catalogo.Pieza, Cantidad.Piezas(1), [_precio]);
        return venta;
    }

    private static readonly CobroSolicitado[] Diez = [new(FormaPago.Efectivo, 1000)];

    [Fact]
    public async Task En_linea_las_dos_cajas_toman_del_mismo_consecutivo_sin_letra()
    {
        await using var db = _base.Contexto();
        var registro = new RegistroCaja(db);
        var dePrincipal = await registro.AbrirTurnoAsync(_principal.Id, _cajera.Id, 0);
        var deLaSegunda = await registro.AbrirTurnoAsync(_secundaria.Id, _otra.Id, 0);

        var uno = await registro.CobrarAsync(VentaDe(dePrincipal, _principal), Diez);
        var dos = await registro.CobrarAsync(VentaDe(deLaSegunda, _secundaria), Diez);
        var tres = await registro.CobrarAsync(VentaDe(dePrincipal, _principal), Diez);

        Assert.Equal(["1", "2", "3"], new[] { uno, dos, tres }.Select(v => v.FolioTexto));
        Assert.All(new[] { uno, dos, tres }, v => Assert.Equal(Folios.SinLetra, v.Serie));
        // La letra de la segunda sigue sin usarse: es para lo que cobre sin conexión.
        Assert.Equal(0, await registro.UltimoFolioAsync(_catalogo.Matriz.Id, "B"));
        Assert.Equal(3, await registro.UltimoFolioAsync(_catalogo.Matriz.Id, Folios.SinLetra));
    }

    /// <summary>
    /// Con lo de antes (la principal era la A y llegó a A-004812; la B, sin conexión, a B-000300), el primer folio sin letra es el 004813:
    /// después del más alto de la sucursal. Lo de antes no se renumera.
    /// </summary>
    [Fact]
    public async Task El_consecutivo_sin_letra_empieza_despues_de_lo_que_ya_habia()
    {
        Guid antes;
        await using (var db = _base.Contexto())
        {
            var registro = new RegistroCaja(db);
            var turno = await registro.AbrirTurnoAsync(_principal.Id, _cajera.Id, 0);
            // Como la dejó la versión de antes: una venta de la serie A (se sube como sin conexión para fijar su folio).
            var vieja = VentaDe(turno, _principal);
            vieja.Serie = "A";
            antes = (await registro.CobroDeContingenciaAsync(vieja, Diez, 4812, _base.Reloj.Ahora.UtcDateTime)).Id;
            // La B avisó que usó hasta el 300 sin conexión.
            await registro.FolioUsadoAsync(_secundaria.Id, 300);
        }

        await using (var db = _base.Contexto())
        {
            var registro = new RegistroCaja(db);
            Assert.Equal(4812, await registro.UltimoFolioAsync(_catalogo.Matriz.Id, Folios.SinLetra));
            var turno = await registro.TurnoAbiertoAsync(_principal.Id);
            var nueva = await registro.CobrarAsync(VentaDe(turno!, _principal), Diez);
            Assert.Equal("4813", nueva.FolioTexto);
        }

        await using var lectura = _base.Contexto();
        Assert.Equal("A-4812", (await lectura.Ventas.SingleAsync(v => v.Id == antes)).FolioTexto);
        Assert.Equal(300, await new RegistroCaja(lectura).UltimoFolioAsync(_catalogo.Matriz.Id, "B"));
    }

    /// <summary>
    /// La caja secundaria sin conexión cobra con su letra y su propio consecutivo, que lo de en línea no mueve; al subir se queda con
    /// su folio. La caja principal sin conexión sigue el consecutivo sin letra; si ese folio ya salió en línea, toma uno más adelante y
    /// su nota dice con cuál salió el ticket (como con letra).
    /// </summary>
    [Fact]
    public async Task Sin_conexion_la_secundaria_lleva_su_letra_y_la_principal_sigue_sin_letra()
    {
        await using var db = _base.Contexto();
        var registro = new RegistroCaja(db);
        var dePrincipal = await registro.AbrirTurnoAsync(_principal.Id, _cajera.Id, 0);
        var deLaSegunda = await registro.AbrirTurnoAsync(_secundaria.Id, _otra.Id, 0);
        var ahora = _base.Reloj.Ahora.UtcDateTime;

        var enLinea = await registro.CobrarAsync(VentaDe(deLaSegunda, _secundaria), Diez);
        var b1 = await registro.CobroDeContingenciaAsync(VentaDe(deLaSegunda, _secundaria), Diez, 1, ahora);
        var b2 = await registro.CobroDeContingenciaAsync(VentaDe(deLaSegunda, _secundaria), Diez, 2, ahora);
        Assert.Equal(("1", "B-1", "B-2"), (enLinea.FolioTexto, b1.FolioTexto, b2.FolioTexto));
        Assert.Equal(2, await registro.UltimoFolioAsync(_catalogo.Matriz.Id, "B"));
        Assert.Equal(1, await registro.UltimoFolioAsync(_catalogo.Matriz.Id, Folios.SinLetra));

        // La principal sin conexión siguió del 1 (el 2); otro ticket suyo dice el 1, que ya salió en línea: brinca, con su nota.
        var p2 = await registro.CobroDeContingenciaAsync(VentaDe(dePrincipal, _principal), Diez, 2, ahora);
        var repetido = await registro.CobroDeContingenciaAsync(VentaDe(dePrincipal, _principal), Diez, 1, ahora);
        Assert.Equal("2", p2.FolioTexto);
        Assert.Equal(Folios.SinLetra, repetido.Serie);
        Assert.Equal(1002, repetido.Folio);
        Assert.StartsWith("Su ticket salió con el folio 1.", repetido.Nota);
        Assert.True(repetido.EnContingencia);
        // Lo que sigue en línea, después del salto.
        Assert.Equal("1003", (await registro.CobrarAsync(VentaDe(dePrincipal, _principal), Diez)).FolioTexto);
    }

    /// <summary>
    /// Buscar para devolver (<see cref="RegistroDevoluciones.PorFolioAsync"/>): «4812», «004812» y «-004812» dan con el sin letra; «A-004812»
    /// y «a4812», con el de antes; solo el número, si no hay sin letra, con el de la serie de esa caja.
    /// </summary>
    [Fact]
    public async Task Se_busca_con_y_sin_letra()
    {
        Guid deAntes, sinLetra, deLaB;
        await using (var db = _base.Contexto())
        {
            var registro = new RegistroCaja(db);
            var turno = await registro.AbrirTurnoAsync(_principal.Id, _cajera.Id, 0);
            var deLaSegunda = await registro.AbrirTurnoAsync(_secundaria.Id, _otra.Id, 0);
            var vieja = VentaDe(turno, _principal);
            vieja.Serie = "A";
            deAntes = (await registro.CobroDeContingenciaAsync(vieja, Diez, 4812, _base.Reloj.Ahora.UtcDateTime)).Id;
            deLaB = (await registro.CobroDeContingenciaAsync(VentaDe(deLaSegunda, _secundaria), Diez, 7, _base.Reloj.Ahora.UtcDateTime)).Id;
            // El sin letra empieza en el 4813: para buscar «4813» sin letra.
            sinLetra = (await registro.CobrarAsync(VentaDe(turno, _principal), Diez)).Id;
        }

        await using var lectura = _base.Contexto();
        var devoluciones = new RegistroDevoluciones(lectura);
        async Task<Guid?> Busca(string folio, Guid? caja = null) => (await devoluciones.PorFolioAsync(_catalogo.Matriz.Id, folio, cajaId: caja))?.Id;

        Assert.Equal(sinLetra, await Busca("4813"));
        Assert.Equal(sinLetra, await Busca("004813"));
        Assert.Equal(sinLetra, await Busca("-004813"));
        Assert.Equal(sinLetra, await Busca(" 004813 ", _secundaria.Id));
        Assert.Equal(deAntes, await Busca("A-004812"));
        Assert.Equal(deAntes, await Busca("a4812"));
        // Solo el número, sin un sin letra con él: el más reciente con ese número (el de antes).
        Assert.Equal(deAntes, await Busca("4812"));
        Assert.Null(await Busca("-004812"));
        Assert.Equal(deLaB, await Busca("B-000007"));
        Assert.Equal(deLaB, await Busca("7", _secundaria.Id));
        // En la principal (sin letra) no hay otra serie que probar: el más reciente con ese número.
        Assert.Equal(deLaB, await Busca("7", _principal.Id));
        Assert.Null(await Busca("folio"));

        // Ventas de otros días: con el número, con ceros, con letra y con guion.
        var otrosDias = new VentasDeOtrosDias(lectura);
        async Task<List<string>> Folio(string folio) =>
            (await otrosDias.PorFolioAsync(_principal.Id, _cajera.Id, folio)).Ventas.Select(v => v.Folio).ToList();
        Assert.Equal(["4813"], await Folio("004813"));
        Assert.Equal(["4813"], await Folio("-4813"));
        Assert.Equal(["A-4812"], await Folio("A-004812"));
        Assert.Equal(["A-4812"], await Folio("4812"));
    }

    /// <summary>
    /// Al arrancar: la caja dada de alta desde la PC del servicio (la de la sucursal 2, «Caja nueva» con la serie A) es la principal y se
    /// queda sin letra; una segunda vez no hace nada, y si ya hay una sin letra en su sucursal, tampoco.
    /// </summary>
    [Fact]
    public async Task La_caja_de_la_pc_del_servicio_se_queda_sin_letra_al_arrancar()
    {
        await using (var db = _base.Contexto())
        {
            var principal = await db.Cajas.SingleAsync(c => c.Id == _principal.Id);
            principal.Serie = "A";
            principal.EsPrincipal = false;
            principal.DarDeAlta("PC-SUCURSAL-2", _base.Reloj.Ahora.UtcDateTime);
            (await db.Cajas.SingleAsync(c => c.Id == _secundaria.Id)).DarDeAlta("CAJA-2", _base.Reloj.Ahora.UtcDateTime);
            await db.SaveChangesAsync();
        }

        await using (var db = _base.Contexto())
        {
            Assert.Null(await PrincipalSinLetra.AplicarAsync(db, "OTRA-PC"));
            var cambio = await PrincipalSinLetra.AplicarAsync(db, "pc-sucursal-2");
            Assert.Equal((_principal.Id, "A"), (cambio!.Value.Caja.Id, cambio.Value.LetraDeAntes));
            Assert.Null(await PrincipalSinLetra.AplicarAsync(db, "PC-SUCURSAL-2"));
            // La de la otra PC no se queda sin letra: ya hay una sin letra en la sucursal.
            Assert.Null(await PrincipalSinLetra.AplicarAsync(db, "CAJA-2"));
        }

        await using var lectura = _base.Contexto();
        var cajas = await lectura.Cajas.AsNoTracking().ToDictionaryAsync(c => c.Id, c => (c.Serie, c.EsPrincipal));
        Assert.Equal(("", true), cajas[_principal.Id]);
        Assert.Equal(("B", false), cajas[_secundaria.Id]);
    }
}
