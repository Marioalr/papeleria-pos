using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using PapeleriaPos.Datos;
using PapeleriaPos.Eleventa;
using PapeleriaPos.Herramientas.Eleventa;
using PapeleriaPos.Herramientas.Importacion;
using PapeleriaPos.Nucleo.Contratos;
using PapeleriaPos.Pruebas.Datos;
using PapeleriaPos.Pruebas.Servicio;
using static PapeleriaPos.Pruebas.Servicio.PilotoDePrueba;

namespace PapeleriaPos.Pruebas.Eleventa;

/// <summary>
/// El mínimo y el máximo de «Productos bajos en inventario» contra el Firebird de prueba, con las columnas de la base de la
/// tienda (<c>PRODUCTOS.DINVMINIMO</c> y <c>DINVMAXIMO</c>, DOUBLE PRECISION; la tienda los usa, iguales, base del 2026-10-24):
/// la importación los trae, con 0 y vacío como «sin» y a granel en su unidad; la lectura del piloto trae también un cambio
/// solo del mínimo (eleventa no anota cuándo cambió un producto: se compara todo), y en la caja 2 la ficha no los cambia.
/// </summary>
public sealed class MinimosDeEleventaTests
{
    /// <summary>
    /// Eleventa con la pluma (10 y 10), la goma (0 y 0), el lápiz (vacíos), el listón por metro (2.5 y 2.5) y el cuaderno con
    /// media pieza (no se toma).
    /// </summary>
    private static async Task<EleventaDePrueba> ConMinimosAsync()
    {
        var e = await EleventaDePrueba.CrearAsync();
        await e.EjecutarAsync("INSERT INTO DEPARTAMENTOS (ID, NOMBRE) VALUES (5, 'Papelería')");
        await e.ProductoAsync(10, "PLU-FINA", "Pluma punto fino", depto: 5, costo: 4, precio: 9, existencia: 4, minimo: 10, maximo: 10);
        await e.ProductoAsync(11, "GOMA", "Goma blanca", depto: 5, costo: 2, precio: 5, existencia: 20, minimo: 0, maximo: 0);
        await e.ProductoAsync(12, "LAPIZ", "Lapiz del 2", depto: 5, costo: 1, precio: 3, existencia: 3);
        await e.ProductoAsync(13, "LIS-MT", "Liston X Metro", depto: 5, costo: 2, precio: 4, existencia: 7.5, tipoVenta: 'D', minimo: 2.5, maximo: 2.5);
        await e.ProductoAsync(14, "CUAD", "Cuaderno", depto: 5, costo: 20, precio: 35, existencia: 6, minimo: 1.5, maximo: 1.5);
        return e;
    }

    /// <summary>Cada código con «mínimo/máximo» de su variante, en su unidad («—», sin), por código.</summary>
    private static async Task<List<(string Codigo, string MinimoYMaximo)>> MinimosAsync(PosDbContext db)
    {
        var variantes = await db.Variantes.ToDictionaryAsync(v => v.Id);
        var codigos = await db.CodigosBarras.Select(c => new { c.Codigo, c.Presentacion!.VarianteId }).ToListAsync();
        return codigos.OrderBy(c => c.Codigo, StringComparer.Ordinal)
            .Select(c => (c.Codigo, $"{variantes[c.VarianteId].InventarioMinimo?.ToString() ?? "—"}/{variantes[c.VarianteId].InventarioMaximo?.ToString() ?? "—"}"))
            .ToList();
    }

    [FactConFirebird]
    public async Task La_importacion_trae_el_minimo_y_el_maximo_de_eleventa_en_su_unidad()
    {
        await using var e = await ConMinimosAsync();
        await using var pos = await BaseEnMemoria.CrearAsync();

        var r = ImportadorEleventa.Importar(await LectorEleventa.LeerAsync(e.Conexion));
        await using (var db = pos.Contexto())
            await GuardadoLote.GuardarAsync(r, db);

        await using var lectura = pos.Contexto();
        Assert.Equal([("CUAD", "—/—"), ("GOMA", "—/—"), ("LAPIZ", "—/—"), ("LIS-MT", "2.5/2.5"), ("PLU-FINA", "10/10")], await MinimosAsync(lectura));
        Assert.Equal(1, r.Avisos[ReglasEleventa.AvisoMinimoConFracciones]);
    }

    [FactConFirebird]
    public async Task La_lectura_trae_un_cambio_solo_del_minimo_y_lo_que_se_pone_o_se_quita_en_eleventa()
    {
        await using var e = await ConMinimosAsync();
        await using var pos = await BaseEnMemoria.CrearAsync();
        await using (var db = pos.Contexto())
            await GuardadoLote.GuardarAsync(ImportadorEleventa.Importar(await LectorEleventa.LeerAsync(e.Conexion)), db);
        async Task<ResultadoSincronizacion> LeerAsync()
        {
            await using var db = pos.Contexto();
            return await SincronizacionEleventa.AplicarAsync(db, await LectorEleventa.LeerCatalogoAsync(e.Conexion), pos.Reloj.GetUtcNow().UtcDateTime);
        }
        async Task<List<(string, string)>> ComoQuedanAsync()
        {
            await using var db = pos.Contexto();
            return await MinimosAsync(db);
        }
        Assert.False((await LeerAsync()).HuboCambios);

        // En eleventa solo cambia el mínimo de la pluma: llega, y nada más.
        await e.EjecutarAsync("UPDATE PRODUCTOS SET DINVMINIMO = 6 WHERE ID = 10");
        Assert.Equal("1 mínimos y máximos", (await LeerAsync()).ToString());
        Assert.Equal([("CUAD", "—/—"), ("GOMA", "—/—"), ("LAPIZ", "—/—"), ("LIS-MT", "2.5/2.5"), ("PLU-FINA", "6/10")], await ComoQuedanAsync());

        // A la goma (0) y al lápiz (vacío) se los ponen, el listón cambia (en metros) y a la pluma se los quitan.
        await e.EjecutarAsync("UPDATE PRODUCTOS SET DINVMINIMO = 5, DINVMAXIMO = 5 WHERE ID IN (11, 12)");
        await e.EjecutarAsync("UPDATE PRODUCTOS SET DINVMINIMO = 1.25, DINVMAXIMO = 3.5 WHERE ID = 13");
        await e.EjecutarAsync("UPDATE PRODUCTOS SET DINVMINIMO = NULL, DINVMAXIMO = NULL WHERE ID = 10");
        var otros = await LeerAsync();
        Assert.Equal((4, "4 mínimos y máximos"), (otros.MinimosYMaximos, otros.ToString()));
        Assert.Equal([("CUAD", "—/—"), ("GOMA", "5/5"), ("LAPIZ", "5/5"), ("LIS-MT", "1.25/3.5"), ("PLU-FINA", "—/—")], await ComoQuedanAsync());
        // La media pieza del cuaderno se sigue avisando, y con lo mismo no cambia nada.
        var igual = await LeerAsync();
        Assert.False(igual.HuboCambios);
        Assert.Equal(1, igual.Avisos[ReglasEleventa.AvisoMinimoConFracciones]);
    }

    /// <summary>
    /// En la caja 2 del piloto, con su lectura: la pluma toma el mínimo y el máximo de eleventa, la ficha (F3) los enseña como de
    /// eleventa y no deja cambiarlos, y un cambio solo del mínimo en eleventa llega con la siguiente lectura.
    /// </summary>
    [FactConFirebird]
    public async Task En_el_piloto_la_lectura_pone_el_minimo_de_eleventa_y_aqui_no_se_cambia()
    {
        var (e, _) = await EleventaAsync();
        await using var eleventa = e;
        await e.EjecutarAsync("UPDATE PRODUCTOS SET DINVMINIMO = 10, DINVMAXIMO = 10 WHERE ID = 10");
        await using var servicio = await ServicioDePrueba.CrearAsync(Ajustes(e.Servidor, leer: true));
        var c = await AbrirAsync(servicio, await MapearAsync(servicio));
        await EsperarPrimeraLecturaAsync(c);
        Guid producto;
        await using (var db = servicio.Base())
            producto = await db.Variantes.Where(v => v.Id == c.Variante).Select(v => v.ProductoId).SingleAsync();
        async Task<(FichaProductoDto Ficha, VarianteFichaDto Pluma)> FichaAsync()
        {
            var ficha = (await c.Http.GetFromJsonAsync<FichaProductoDto>(
                $"api/catalogo/productos/{producto}?sucursalId={c.Datos.SucursalId}&usuarioId={c.Encargado.UsuarioId}"))!;
            return (ficha, ficha.Variantes.Single(v => v.Id == c.Variante));
        }

        var (antes, pluma) = await FichaAsync();
        Assert.Equal((true, new MinimoYMaximoDto(10_000, 10_000)), (pluma.DeEleventa, pluma.MinimoYMaximo));
        var otro = antes with { Variantes = antes.Variantes.Select(v => v.Id == c.Variante ? v with { MinimoYMaximo = new MinimoYMaximoDto(3_000, 10_000) } : v).ToList() };
        Assert.Equal(TrastiendaEnElPiloto.ProductosEnEleventa, await RechazoAsync(await c.Http.PostAsJsonAsync("api/catalogo/productos",
            new SolicitudGuardarProducto(c.Encargado.UsuarioId, c.Datos.SucursalId, otro))));

        await e.EjecutarAsync("UPDATE PRODUCTOS SET DINVMINIMO = 4 WHERE ID = 10");
        await LeerAsync(c);

        Assert.Equal(new MinimoYMaximoDto(4_000, 10_000), (await FichaAsync()).Pluma.MinimoYMaximo);
        Assert.Contains("1 mínimos y máximos", (await c.Http.GetFromJsonAsync<EstadoEleventaDto>("api/eleventa"))!.Resultado);
    }
}
