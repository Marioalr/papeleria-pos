using Microsoft.EntityFrameworkCore;
using PapeleriaPos.Datos;
using PapeleriaPos.Eleventa;
using PapeleriaPos.Herramientas.Eleventa;
using PapeleriaPos.Herramientas.Importacion;
using PapeleriaPos.Nucleo.Catalogo;
using PapeleriaPos.Pruebas.Datos;

namespace PapeleriaPos.Pruebas.Eleventa;

/// <summary>
/// La lectura del piloto contra el Firebird de prueba, con lo que desde el 2026-11-07 es de aquí: el departamento de cada
/// producto y los departamentos mismos (el dueño reparte todos los productos en departamentos nuevos), y la unidad, que sigue a
/// eleventa una vez cuando allá un producto pasa de por pieza a granel (<c>TVENTA</c> de 'U' a 'D'), nunca al revés.
/// </summary>
public sealed class LoDeAquiEnLaLecturaTests
{
    private sealed record Caja2(EleventaDePrueba Eleventa, BaseEnMemoria Pos) : IAsyncDisposable
    {
        public async Task<ResultadoSincronizacion> LeerAsync()
        {
            await using var db = Pos.Contexto();
            return await SincronizacionEleventa.AplicarAsync(db, await LectorEleventa.LeerCatalogoAsync(Eleventa.Conexion), Pos.Reloj.GetUtcNow().UtcDateTime);
        }

        public async Task<(string Departamento, UnidadBase Unidad)> ComoQuedaAsync(string codigo)
        {
            await using var db = Pos.Contexto();
            var variante = await db.Variantes.Include(v => v.Producto)
                .SingleAsync(v => v.Id == db.CodigosBarras.Where(c => c.Codigo == codigo).Select(c => c.Presentacion!.VarianteId).Single());
            return ((await db.Departamentos.SingleAsync(d => d.Id == variante.Producto!.DepartamentoId)).Nombre, variante.UnidadBase);
        }

        public async ValueTask DisposeAsync()
        {
            await Pos.DisposeAsync();
            await Eleventa.DisposeAsync();
        }
    }

    /// <summary>Eleventa con «Papelería» y «Escolar», la pluma, el folder y el listón por pieza (como en la tienda), ya importado.</summary>
    private static async Task<Caja2> ImportadaAsync()
    {
        var e = await EleventaDePrueba.CrearAsync();
        await e.EjecutarAsync("INSERT INTO DEPARTAMENTOS (ID, NOMBRE) VALUES (5, 'Papelería')");
        await e.EjecutarAsync("INSERT INTO DEPARTAMENTOS (ID, NOMBRE) VALUES (6, 'Escolar')");
        await e.ProductoAsync(10, "PLU-FINA", "Pluma punto fino", depto: 5, costo: 4, precio: 9, existencia: 40);
        await e.ProductoAsync(11, "FOLDER", "Folder carta", depto: 6, costo: 2, precio: 5, existencia: 30);
        await e.ProductoAsync(12, "LIS-22", "Liston 22.0 V/Colores X Metro", depto: 5, costo: 3, precio: 6, existencia: 25);
        await e.ProductoAsync(13, "DIA", "Diamantina a granel", depto: 5, costo: 20, precio: 40, existencia: 2.5, tipoVenta: 'D');
        var pos = await BaseEnMemoria.CrearAsync();
        await using (var db = pos.Contexto())
            await GuardadoLote.GuardarAsync(ImportadorEleventa.Importar(await LectorEleventa.LeerAsync(e.Conexion)), db);
        return new Caja2(e, pos);
    }

    /// <summary>
    /// Un producto que ya estaba no se mueve al departamento de eleventa (ni cuando aquí se pasó a uno nuevo), y un departamento
    /// que eleventa renombra o desactiva se queda como está aquí.
    /// </summary>
    [FactConFirebird]
    public async Task El_departamento_de_aqui_no_se_mueve_ni_se_renombra_desde_eleventa()
    {
        await using var c = await ImportadaAsync();
        Assert.False((await c.LeerAsync()).HuboCambios);
        // El reparto del dueño: la pluma pasa a «Escritura», un departamento de aquí.
        await using (var db = c.Pos.Contexto())
        {
            var escritura = new Departamento { Nombre = "Escritura" };
            db.Departamentos.Add(escritura);
            var pluma = await db.Productos.SingleAsync(p => p.Nombre == "Pluma punto fino");
            pluma.DepartamentoId = escritura.Id;
            await db.SaveChangesAsync();
        }

        await c.Eleventa.EjecutarAsync("UPDATE PRODUCTOS SET DEPT = 6 WHERE ID IN (10, 12)");
        await c.Eleventa.EjecutarAsync("UPDATE DEPARTAMENTOS SET NOMBRE = 'Útiles escolares' WHERE ID = 6");
        await c.Eleventa.EjecutarAsync("UPDATE DEPARTAMENTOS SET ACTIVO = 'f' WHERE ID = 5");
        var r = await c.LeerAsync();

        Assert.False(r.HuboCambios, r.ToString());
        Assert.Equal("Escritura", (await c.ComoQuedaAsync("PLU-FINA")).Departamento);
        Assert.Equal("Papelería", (await c.ComoQuedaAsync("LIS-22")).Departamento);
        Assert.Equal("Escolar", (await c.ComoQuedaAsync("FOLDER")).Departamento);
        await using var lectura = c.Pos.Contexto();
        Assert.True((await lectura.Departamentos.SingleAsync(d => d.Nombre == "Papelería")).Activo);
        Assert.False(await lectura.Departamentos.AnyAsync(d => d.Nombre == "Útiles escolares"));
    }

    /// <summary>Un departamento nuevo en eleventa se crea aquí con el producto nuevo que llega en él.</summary>
    [FactConFirebird]
    public async Task Un_departamento_nuevo_en_eleventa_se_crea_aqui_con_su_producto()
    {
        await using var c = await ImportadaAsync();
        await c.Eleventa.EjecutarAsync("INSERT INTO DEPARTAMENTOS (ID, NOMBRE) VALUES (8, 'Regalos')");
        await c.Eleventa.EjecutarAsync("INSERT INTO DEPARTAMENTOS (ID, NOMBRE) VALUES (9, 'Temporada')");
        await c.Eleventa.ProductoAsync(20, "BOLSA-R", "Bolsa de regalo mediana", depto: 8, costo: 6, precio: 15, existencia: 12);

        var r = await c.LeerAsync();

        Assert.Equal((1, 1), (r.Nuevos, r.Departamentos));
        Assert.Equal("Regalos", (await c.ComoQuedaAsync("BOLSA-R")).Departamento);
        await using var lectura = c.Pos.Contexto();
        Assert.False(await lectura.Departamentos.AnyAsync(d => d.Nombre == "Temporada"));
        Assert.False((await c.LeerAsync()).HuboCambios);
    }

    /// <summary>
    /// El listón pasa a granel en eleventa (de 'U' a 'D'): aquí toma el metro (por su nombre), una vez y con un aviso. La diamantina,
    /// que eleventa regresa de 'D' a 'U', se queda a granel: al revés no cambia nada.
    /// </summary>
    [FactConFirebird]
    public async Task De_por_pieza_a_granel_cambia_la_unidad_y_al_reves_no()
    {
        await using var c = await ImportadaAsync();
        Assert.Equal(UnidadBase.Pieza, (await c.ComoQuedaAsync("LIS-22")).Unidad);
        Assert.Equal(UnidadBase.PiezaAGranel, (await c.ComoQuedaAsync("DIA")).Unidad);
        Assert.False((await c.LeerAsync()).HuboCambios);

        await c.Eleventa.EjecutarAsync("UPDATE PRODUCTOS SET TVENTA = 'D' WHERE ID = 12");
        await c.Eleventa.EjecutarAsync("UPDATE PRODUCTOS SET TVENTA = 'U' WHERE ID = 13");
        var r = await c.LeerAsync();

        Assert.Equal((1, 1), (r.Cambiados, r.Avisos[SincronizacionEleventa.AvisoUnidadCambiada]));
        Assert.Equal(UnidadBase.Metro, (await c.ComoQuedaAsync("LIS-22")).Unidad);
        Assert.Equal(UnidadBase.PiezaAGranel, (await c.ComoQuedaAsync("DIA")).Unidad);
        var otra = await c.LeerAsync();
        Assert.False(otra.HuboCambios, otra.ToString());
        Assert.DoesNotContain(SincronizacionEleventa.AvisoUnidadCambiada, otra.Avisos.Keys);
    }
}
