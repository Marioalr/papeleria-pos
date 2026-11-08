using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using PapeleriaPos.Datos;
using PapeleriaPos.Eleventa;
using PapeleriaPos.Herramientas.Eleventa;
using PapeleriaPos.Herramientas.Importacion;
using PapeleriaPos.Nucleo.Catalogo;
using PapeleriaPos.Nucleo.Contratos;
using PapeleriaPos.Pruebas.Datos;
using PapeleriaPos.Pruebas.Servicio;
using static PapeleriaPos.Pruebas.Servicio.PilotoDePrueba;

namespace PapeleriaPos.Pruebas.Eleventa;

/// <summary>
/// El mínimo y el máximo de «Productos bajos en inventario» contra el Firebird de prueba, con las columnas de la base de la
/// tienda (<c>PRODUCTOS.DINVMINIMO</c> y <c>DINVMAXIMO</c>, DOUBLE PRECISION; la tienda los usa, iguales, base del 2026-10-24):
/// la importación los trae, con 0 y vacío como «sin» y a granel en su unidad. Desde el 2026-11-07 son de aquí: la lectura del
/// piloto ya no los cambia (solo un producto nuevo trae los suyos, una vez) y en la caja 2 la ficha sí los cambia.
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

    /// <summary>
    /// En la lectura del piloto los mínimos son de aquí (2026-11-07): un cambio en eleventa (solo del mínimo, o que se los pongan o
    /// se los quiten) no mueve los que ya están aquí; un producto nuevo de eleventa llega con los suyos, una vez, y desde ahí
    /// tampoco cambian con eleventa.
    /// </summary>
    [FactConFirebird]
    public async Task La_lectura_no_cambia_los_minimos_de_aqui_y_un_producto_nuevo_trae_los_suyos_una_vez()
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
        List<(string, string)> importados = [("CUAD", "—/—"), ("GOMA", "—/—"), ("LAPIZ", "—/—"), ("LIS-MT", "2.5/2.5"), ("PLU-FINA", "10/10")];
        Assert.False((await LeerAsync()).HuboCambios);

        // En eleventa cambia solo el mínimo de la pluma, a la goma y al lápiz se los ponen, el listón cambia y a la pluma se los quitan.
        await e.EjecutarAsync("UPDATE PRODUCTOS SET DINVMINIMO = 6 WHERE ID = 10");
        Assert.False((await LeerAsync()).HuboCambios);
        await e.EjecutarAsync("UPDATE PRODUCTOS SET DINVMINIMO = 5, DINVMAXIMO = 5 WHERE ID IN (11, 12)");
        await e.EjecutarAsync("UPDATE PRODUCTOS SET DINVMINIMO = 1.25, DINVMAXIMO = 3.5 WHERE ID = 13");
        await e.EjecutarAsync("UPDATE PRODUCTOS SET DINVMINIMO = NULL, DINVMAXIMO = NULL WHERE ID = 10");
        Assert.False((await LeerAsync()).HuboCambios);
        Assert.Equal(importados, await ComoQuedanAsync());

        // Un producto nuevo en eleventa llega con los suyos; si allá se los cambian después, aquí se quedan.
        await e.ProductoAsync(15, "FOLDER", "Folder carta", depto: 5, costo: 2, precio: 5, existencia: 30, minimo: 12, maximo: 24);
        Assert.Equal(1, (await LeerAsync()).Nuevos);
        await e.EjecutarAsync("UPDATE PRODUCTOS SET DINVMINIMO = 3, DINVMAXIMO = 3 WHERE ID = 15");
        Assert.False((await LeerAsync()).HuboCambios);
        Assert.Equal([("CUAD", "—/—"), ("FOLDER", "12/24"), ("GOMA", "—/—"), ("LAPIZ", "—/—"), ("LIS-MT", "2.5/2.5"), ("PLU-FINA", "10/10")],
            await ComoQuedanAsync());
    }

    /// <summary>
    /// En la caja 2 del piloto, con su lectura: la ficha (F3) cambia el mínimo, el máximo, el departamento y la unidad de la pluma
    /// de eleventa (2026-11-07: la base de aquí manda en ellos), y la siguiente lectura los deja como quedaron aunque en eleventa
    /// cambien. Lo que pone la lectura (si la variante está activa) sigue sin cambiarse aquí.
    /// </summary>
    [FactConFirebird]
    public async Task En_el_piloto_la_ficha_cambia_el_minimo_el_departamento_y_la_unidad_y_la_lectura_los_deja()
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
        var nuevo = (await Leer<DepartamentosDto>(await c.Http.PostAsJsonAsync("api/catalogo/departamentos",
            new SolicitudDepartamento(c.Encargado.UsuarioId, c.Datos.SucursalId, "Plumas y lápices")))).Departamentos.Single(d => d.Nombre == "Plumas y lápices");

        var (antes, pluma) = await FichaAsync();
        Assert.True(pluma.DeEleventa);
        var cambiada = antes with
        {
            DepartamentoId = nuevo.Id,
            Variantes = antes.Variantes.Select(v => v.Id == c.Variante
                ? v with { Unidad = UnidadBase.PiezaAGranel, MinimoYMaximo = new MinimoYMaximoDto(3_500, 20_000) }
                : v).ToList(),
        };
        await Leer<FichaProductoDto>(await c.Http.PostAsJsonAsync("api/catalogo/productos", new SolicitudGuardarProducto(c.Encargado.UsuarioId, c.Datos.SucursalId, cambiada)));
        var guardada = (await FichaAsync()).Ficha;
        var inactiva = guardada with { Variantes = guardada.Variantes.Select(v => v.Id == c.Variante ? v with { Activa = false } : v).ToList() };
        Assert.Equal(TrastiendaEnElPiloto.ProductosEnEleventa, await RechazoAsync(await c.Http.PostAsJsonAsync("api/catalogo/productos",
            new SolicitudGuardarProducto(c.Encargado.UsuarioId, c.Datos.SucursalId, inactiva))));

        // En eleventa le cambian el mínimo y el departamento, y el departamento se renombra.
        await e.EjecutarAsync("INSERT INTO DEPARTAMENTOS (ID, NOMBRE) VALUES (12, 'Nuevo de eleventa sin productos nuevos')");
        await e.EjecutarAsync("UPDATE PRODUCTOS SET DINVMINIMO = 4, DEPT = 12 WHERE ID = 10");
        await e.EjecutarAsync("UPDATE DEPARTAMENTOS SET NOMBRE = 'Papelería y oficina' WHERE ID = 5");
        await LeerAsync(c);

        var (despues, comoQuedo) = await FichaAsync();
        Assert.Equal((nuevo.Id, UnidadBase.PiezaAGranel, new MinimoYMaximoDto(3_500, 20_000)), (despues.DepartamentoId, comoQuedo.Unidad, comoQuedo.MinimoYMaximo));
        var departamentos = (await c.Http.GetFromJsonAsync<DepartamentosDto>("api/catalogo/departamentos"))!.Departamentos.Select(d => d.Nombre).ToList();
        Assert.DoesNotContain("Papelería y oficina", departamentos);
        // Un departamento nuevo de eleventa sin productos nuevos no se crea aquí.
        Assert.DoesNotContain("Nuevo de eleventa sin productos nuevos", departamentos);
    }
}
