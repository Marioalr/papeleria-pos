using Microsoft.EntityFrameworkCore;
using PapeleriaPos.Datos;
using PapeleriaPos.Eleventa;
using PapeleriaPos.Herramientas.Eleventa;
using PapeleriaPos.Herramientas.Importacion;
using PapeleriaPos.Nucleo;
using PapeleriaPos.Nucleo.Catalogo;
using PapeleriaPos.Nucleo.Inventario;
using PapeleriaPos.Nucleo.Turnos;
using PapeleriaPos.Nucleo.Usuarios;
using PapeleriaPos.Nucleo.Ventas;
using PapeleriaPos.Pruebas.Datos;

namespace PapeleriaPos.Pruebas.Herramientas;

/// <summary>La caja 2 del piloto se pone al día con eleventa cada pocos minutos: lo de allá manda (docs/plan.md, paso 1).</summary>
public sealed class SincronizacionEleventaTests : IAsyncDisposable
{
    private BaseEnMemoria? _base;

    public async ValueTask DisposeAsync()
    {
        if (_base is not null)
            await _base.DisposeAsync();
    }

    private static FilaProducto Producto(int id, string codigo, string descripcion, double precio = 15, double costo = 10, double mayoreo = 0,
        int? departamento = 2, bool eliminado = false, char tipo = 'U') =>
        new(id, codigo, descripcion, tipo, 1, costo, precio, mayoreo, departamento, eliminado);

    private static DatosEleventa Datos(IEnumerable<FilaProducto> productos, IEnumerable<FilaExistencia>? existencias = null,
        IEnumerable<FilaPromocion>? promociones = null, IEnumerable<FilaDepartamento>? departamentos = null) => new()
    {
        Departamentos = departamentos?.ToList() ?? [new(2, "Papelería", true), new(5, "Escolar", true)],
        Productos = productos.ToList(),
        Existencias = existencias?.ToList() ?? [],
        Promociones = promociones?.ToList() ?? [],
    };

    /// <summary>La caja 2 como queda con importar-eleventa: el punto de partida de cada lectura.</summary>
    private async Task<BaseEnMemoria> ImportadaAsync(DatosEleventa inicial)
    {
        _base = await BaseEnMemoria.CrearAsync();
        await using var db = _base.Contexto();
        await GuardadoLote.GuardarAsync(ImportadorEleventa.Importar(inicial), db);
        return _base;
    }

    private async Task<ResultadoSincronizacion> SincronizarAsync(DatosEleventa e, EnviosEnCurso.Vigilancia? mandados = null)
    {
        await using var db = _base!.Contexto();
        return await SincronizacionEleventa.AplicarAsync(db, e, _base.Reloj.GetUtcNow().UtcDateTime, mandados);
    }

    private static async Task<Variante> VarianteAsync(PosDbContext db, string codigo) =>
        await db.Variantes.Include(v => v.Producto).SingleAsync(v => v.Id == db.CodigosBarras.Where(c => c.Codigo == codigo).Select(c => c.Presentacion!.VarianteId).Single());

    private static async Task<long> VersionAsync(PosDbContext db) =>
        await db.Secuencias.Where(s => s.Nombre == Secuencia.Catalogo).Select(s => s.Valor).SingleOrDefaultAsync();

    private static Task<List<Precio>> EscalasAsync(PosDbContext db, Guid variante) =>
        db.Precios.Where(p => p.VarianteId == variante && p.PresentacionId == null && p.SucursalId == null).OrderBy(p => p.CantidadMinima).ToListAsync();

    [Fact]
    public async Task Con_lo_mismo_que_se_importo_no_cambia_nada()
    {
        var datos = Datos([Producto(1, "7501035102446", "Cinta Janel"), Producto(2, "LAP", "Lápiz del 2", precio: 6)],
            existencias: [new(1, 10), new(2, 3)], promociones: [new("LAP", 12, 5)]);
        await ImportadaAsync(datos);
        long version;
        await using (var db = _base!.Contexto())
            version = await VersionAsync(db);

        var primera = await SincronizarAsync(datos);
        var segunda = await SincronizarAsync(datos);

        Assert.False(primera.HuboCambios, primera.ToString());
        Assert.False(segunda.HuboCambios, segunda.ToString());
        Assert.Empty(primera.Avisos);
        await using var lectura = _base.Contexto();
        Assert.Equal(version, await VersionAsync(lectura));
        Assert.DoesNotContain(await lectura.MovimientosInventario.ToListAsync(), m => m.DocumentoTipo == SincronizacionEleventa.Documento);
    }

    [Fact]
    public async Task Un_producto_nuevo_en_eleventa_llega_con_su_codigo_precio_costo_existencia_y_mayoreo_por_revisar()
    {
        await ImportadaAsync(Datos([Producto(1, "A1", "Goma")]));

        var r = await SincronizarAsync(Datos([Producto(1, "A1", "Goma"), Producto(7, "7509876543210", "Folder carta", precio: 4.5, costo: 2.4, mayoreo: 3.8, departamento: 5)],
            existencias: [new(7, 20)]));

        Assert.Equal((1, 1), (r.Nuevos, r.Existencias));
        Assert.Contains(SincronizacionEleventa.AvisoMayoreoNuevo, r.Avisos.Keys);
        await using var db = _base!.Contexto();
        var folder = await VarianteAsync(db, "7509876543210");
        Assert.Equal(("Folder carta", "Escolar"), (folder.Producto!.Nombre, (await db.Departamentos.SingleAsync(d => d.Id == folder.Producto.DepartamentoId)).Nombre));
        Assert.Equal([450L], (await EscalasAsync(db, folder.Id)).Select(p => p.PrecioCentavos));
        Assert.Equal(240, (await db.ProveedoresVariantes.SingleAsync(pv => pv.VarianteId == folder.Id)).UltimoCostoCentavos);
        var existencia = await db.Existencias.SingleAsync(x => x.VarianteId == folder.Id);
        Assert.Equal((Cantidad.Piezas(20), 4800L), (existencia.Cantidad, existencia.CostoCentavos));
        Assert.Equal(TipoMovimiento.InventarioInicial, (await db.MovimientosInventario.SingleAsync(m => m.VarianteId == folder.Id)).Tipo);
        Assert.Equal(380, (await db.MayoreoPorRevisar.SingleAsync(m => m.VarianteId == folder.Id)).PrecioMayoreoCentavos);
        // La siguiente lectura ya lo conoce.
        Assert.False((await SincronizarAsync(Datos([Producto(1, "A1", "Goma"), Producto(7, "7509876543210", "Folder carta", precio: 4.5, costo: 2.4,
            mayoreo: 3.8, departamento: 5)], existencias: [new(7, 20)]))).HuboCambios);
    }

    [Fact]
    public async Task Llegan_los_cambios_de_precio_nombre_codigo_departamento_y_costo()
    {
        await ImportadaAsync(Datos([Producto(1, "A1", "Goma blanca", precio: 8, costo: 4)]));
        long version;
        await using (var db = _base!.Contexto())
            version = await VersionAsync(db);

        var r = await SincronizarAsync(Datos([Producto(1, "A1-N", "Goma blanca Pelikan", precio: 9.5, costo: 4.4, departamento: 5)]));

        Assert.Equal((1, 1, 1), (r.Cambiados, r.Precios, r.Costos));
        await using var lectura = _base.Contexto();
        var goma = await VarianteAsync(lectura, "A1-N");
        Assert.False(await lectura.CodigosBarras.AnyAsync(c => c.Codigo == "A1"));
        Assert.Equal("Goma blanca Pelikan", goma.Producto!.Nombre);
        Assert.Equal("Escolar", (await lectura.Departamentos.SingleAsync(d => d.Id == goma.Producto.DepartamentoId)).Nombre);
        Assert.Equal([950L], (await EscalasAsync(lectura, goma.Id)).Select(p => p.PrecioCentavos));
        Assert.Contains(await lectura.PreciosHistorial.ToListAsync(), h => h.VarianteId == goma.Id && h.PrecioAnteriorCentavos == 800 && h.PrecioNuevoCentavos == 950);
        Assert.True(await VersionAsync(lectura) > version);
    }

    [Fact]
    public async Task Lo_borrado_en_eleventa_queda_inactivo_y_lo_dado_de_alta_aqui_no_se_toca()
    {
        await ImportadaAsync(Datos([Producto(1, "A1", "Goma"), Producto(2, "B2", "Sacapuntas"), Producto(3, "C3", "Regla")]));
        Guid propia;
        await using (var db = _base!.Contexto())
        {
            var departamento = await db.Departamentos.FirstAsync();
            var producto = new Producto { Nombre = "Engargolado", DepartamentoId = departamento.Id };
            var variante = new Variante { ProductoId = producto.Id };
            db.AddRange(producto, variante, new Presentacion { VarianteId = variante.Id, Nombre = "Pieza", EsBase = true });
            await db.SaveChangesAsync();
            propia = variante.Id;
        }

        // La 2 se marcó como borrada; la 3 ya ni viene.
        var r = await SincronizarAsync(Datos([Producto(1, "A1", "Goma"), Producto(2, "B2", "Sacapuntas", eliminado: true)]));

        Assert.Equal(2, r.Desactivados);
        Assert.Contains(SincronizacionEleventa.AvisoDesaparecido, r.Avisos.Keys);
        await using var lectura = _base.Contexto();
        Assert.True((await VarianteAsync(lectura, "A1")).Activa);
        Assert.False((await VarianteAsync(lectura, "B2")).Activa);
        Assert.False((await VarianteAsync(lectura, "C3")).Producto!.Activo);
        Assert.True((await lectura.Variantes.SingleAsync(v => v.Id == propia)).Activa);
        // Si en eleventa lo vuelven a activar, regresa.
        Assert.Equal(1, (await SincronizarAsync(Datos([Producto(1, "A1", "Goma"), Producto(2, "B2", "Sacapuntas")]))).Cambiados);
    }

    [Fact]
    public async Task Las_promociones_se_agregan_cambian_y_se_quitan_sin_tocar_las_escalas_capturadas_aqui()
    {
        await ImportadaAsync(Datos([Producto(1, "LAP", "Lápiz del 2", precio: 6)], promociones: [new("LAP", 12, 5)]));
        Guid lapiz;
        await using (var db = _base!.Contexto())
        {
            lapiz = (await VarianteAsync(db, "LAP")).Id;
            // Una escala que puso el encargado en F3.
            db.Precios.Add(new Precio { VarianteId = lapiz, CantidadMinima = Cantidad.Piezas(100), PrecioCentavos = 400 });
            await db.SaveChangesAsync();
        }

        var cambio = await SincronizarAsync(Datos([Producto(1, "LAP", "Lápiz del 2", precio: 6)], promociones: [new("LAP", 12, 4.8), new("LAP", 24, 4.5)]));
        long version;
        await using (var db = _base.Contexto())
        {
            Assert.Equal([(1L, 600L), (12L, 480L), (24L, 450L), (100L, 400L)],
                (await EscalasAsync(db, lapiz)).Select(p => ((long)p.CantidadMinima.EnUnidades, p.PrecioCentavos)));
            version = await VersionAsync(db);
        }
        var quitadas = await SincronizarAsync(Datos([Producto(1, "LAP", "Lápiz del 2", precio: 6)]));

        Assert.Equal((2, 2), (cambio.Precios, quitadas.Precios));
        await using var lectura = _base.Contexto();
        Assert.Equal([(1L, 600L), (100L, 400L)], (await EscalasAsync(lectura, lapiz)).Select(p => ((long)p.CantidadMinima.EnUnidades, p.PrecioCentavos)));
        // Quitar también avisa a las cajas.
        Assert.True(await VersionAsync(lectura) > version);
    }

    [Fact]
    public async Task Las_existencias_quedan_como_en_eleventa_con_un_ajuste_en_el_kardex()
    {
        await ImportadaAsync(Datos([Producto(1, "A1", "Goma", costo: 2), Producto(2, "B2", "Sacapuntas")], existencias: [new(1, 10), new(2, 4)]));

        // En la caja 1 se vendieron 3 gomas; los sacapuntas tienen una fracción (está mal en eleventa).
        var r = await SincronizarAsync(Datos([Producto(1, "A1", "Goma", costo: 2), Producto(2, "B2", "Sacapuntas")], existencias: [new(1, 7), new(2, 4.5)]));

        Assert.Equal(1, r.Existencias);
        Assert.Contains(SincronizacionEleventa.AvisoFraccionEnPieza, r.Avisos.Keys);
        await using var db = _base!.Contexto();
        var goma = await VarianteAsync(db, "A1");
        Assert.Equal((Cantidad.Piezas(7), 1400L), await db.Existencias.Where(x => x.VarianteId == goma.Id).Select(x => ValueTuple.Create(x.Cantidad, x.CostoCentavos)).SingleAsync());
        var ajuste = await db.MovimientosInventario.SingleAsync(m => m.VarianteId == goma.Id && m.DocumentoTipo == SincronizacionEleventa.Documento);
        Assert.Equal((TipoMovimiento.Conteo, -Cantidad.Piezas(3), -600L), (ajuste.Tipo, ajuste.Cantidad, ajuste.CostoCentavos));
        Assert.Empty(await new RegistroInventario(db).VerificarExistenciasAsync());
    }

    [Fact]
    public async Task Un_codigo_que_aqui_ya_tiene_otro_producto_no_se_mueve()
    {
        await ImportadaAsync(Datos([Producto(1, "A1", "Goma"), Producto(2, "B2", "Sacapuntas")]));

        var r = await SincronizarAsync(Datos([Producto(1, "A1", "Goma"), Producto(2, "A1", "Sacapuntas")]));

        Assert.False(r.HuboCambios);
        await using var db = _base!.Contexto();
        Assert.Equal("Goma", (await VarianteAsync(db, "A1")).Producto!.Nombre);
        Assert.Equal("Sacapuntas", (await VarianteAsync(db, "B2")).Producto!.Nombre);
    }

    /// <summary>
    /// El mayoreo (F11) de un producto que ya estaba también llega (2026-10-24; en el piloto los precios se ponen en eleventa): el
    /// pegamento tiene su escala desde 12 (deducida de sus ventas con F11) y la escala toma el precio nuevo; el clip (por revisar)
    /// y el lápiz (sin mayoreo hasta hoy) quedan por revisar en F3 con el precio nuevo y un aviso. Si eleventa se lo quita, se
    /// quita la escala. Con lo mismo, la lectura siguiente no cambia nada.
    /// </summary>
    [Fact]
    public async Task El_mayoreo_que_cambia_en_eleventa_llega_a_su_escala_o_queda_por_revisar()
    {
        var vendidos = Enumerable.Range(0, 3).Select(i => new FilaTicket(500 + i, 500 + i, new DateTime(2026, 3, 5 + i, 12, 0, 0), 216, false)).ToList();
        var conF11 = vendidos.Select(t => new FilaLinea(t.Id, "PEG", "Pegamento", 12, 216, 18, 6, 2, UsaMayoreo: true)).ToList();
        DatosEleventa Con(double pegamento, double clip, double lapiz) => new()
        {
            Departamentos = [new(2, "Papelería", true)],
            Productos =
            [
                Producto(1, "PEG", "Pegamento 35g", precio: 22, costo: 12, mayoreo: pegamento),
                Producto(2, "CLI", "Clip", precio: 5, costo: 2, mayoreo: clip),
                Producto(3, "LAP", "Lápiz", precio: 6, costo: 3, mayoreo: lapiz),
            ],
            Tickets = vendidos, Lineas = conF11,
        };
        await ImportadaAsync(Con(18, 4, 0));
        // Las escalas del pegamento y lo que está por revisar en F3: «2200 1800 | Clip 400».
        async Task<string> ComoQuedaAsync()
        {
            await using var db = _base!.Contexto();
            var pegamento = await VarianteAsync(db, "PEG");
            var porRevisar = await new RegistroDepuracion(db).MayoreoPorRevisarAsync();
            return string.Join(" ", (await EscalasAsync(db, pegamento.Id)).Select(p => p.PrecioCentavos)) + " | "
                + string.Join(", ", porRevisar.OrderBy(m => m.Producto).Select(m => $"{m.Producto} {m.PrecioMayoreoCentavos}"));
        }
        Assert.Equal("2200 1800 | Clip 400", await ComoQuedaAsync());
        Assert.False((await SincronizarAsync(Con(18, 4, 0))).HuboCambios);

        var cambio = await SincronizarAsync(Con(17, 3.5, 5));
        Assert.Equal((3, 2), (cambio.Precios, cambio.Avisos[SincronizacionEleventa.AvisoMayoreoCambiado]));
        Assert.Equal("2200 1700 | Clip 350, Lápiz 500", await ComoQuedaAsync());
        Assert.False((await SincronizarAsync(Con(17, 3.5, 5))).HuboCambios);

        // Eleventa se los quita (o el mayoreo ya no es más barato que el menudeo): la escala se va y no queda nada por revisar.
        var quitados = await SincronizarAsync(Con(0, 0, 6));
        Assert.Equal((3, 1), (quitados.Precios, quitados.Avisos[SincronizacionEleventa.AvisoMayoreoQuitado]));
        Assert.Equal("2200 | ", await ComoQuedaAsync());
        Assert.False((await SincronizarAsync(Con(0, 0, 6))).HuboCambios);
    }

    /// <summary>Las escalas de un producto: «1:2200 12:1800» (desde cuántas milésimas, a cuántos centavos).</summary>
    private async Task<string> EscalasDeAsync(string codigo)
    {
        await using var db = _base!.Contexto();
        var variante = await VarianteAsync(db, codigo);
        return string.Join(" ", (await EscalasAsync(db, variante.Id)).Select(p => $"{p.CantidadMinima.Milesimas / 1000}:{p.PrecioCentavos}"));
    }

    /// <summary>El pegamento: $22 a menudeo, vendido tres veces de a 12 con F11 (su escala del F11 sale desde 12).</summary>
    private static DatosEleventa Pegamento(double menudeo, double mayoreo, params FilaPromocion[] promociones)
    {
        var vendidos = Enumerable.Range(0, 3).Select(i => new FilaTicket(500 + i, 500 + i, new DateTime(2026, 3, 5 + i, 12, 0, 0), 216, false)).ToList();
        return new DatosEleventa
        {
            Departamentos = [new(2, "Papelería", true)],
            Productos = [Producto(1, "PEG", "Pegamento 35g", precio: menudeo, costo: 12, mayoreo: mayoreo)],
            Tickets = vendidos, Lineas = vendidos.Select(t => new FilaLinea(t.Id, "PEG", "Pegamento", 12, 216, 18, 6, 2, UsaMayoreo: true)).ToList(),
            Promociones = [.. promociones],
        };
    }

    /// <summary>
    /// Revisión del PR #139: en eleventa bajan el menudeo del pegamento a $17 un rato, abajo de su mayoreo de $18, que no tocan, y
    /// luego lo regresan a $22. La escala del F11 se queda (la caja no la cobra mientras es más cara que el menudeo) y sin el aviso
    /// de «le quitó el mayoreo»: eleventa no se lo quitó. Solo cuando lo quitan de verdad (el precio de mayoreo igual al menudeo,
    /// como lo deja eleventa) se quita la escala.
    /// </summary>
    [Fact]
    public async Task R139_Bajar_el_menudeo_un_rato_no_le_quita_al_producto_su_escala_del_F11()
    {
        await ImportadaAsync(Pegamento(22, 18));
        Assert.Equal("1:2200 12:1800", await EscalasDeAsync("PEG"));

        var bajo = await SincronizarAsync(Pegamento(17, 18));
        Assert.Equal("1:1700 12:1800", await EscalasDeAsync("PEG"));
        var regreso = await SincronizarAsync(Pegamento(22, 18));

        Assert.False(bajo.Avisos.ContainsKey(SincronizacionEleventa.AvisoMayoreoQuitado) || regreso.Avisos.ContainsKey(SincronizacionEleventa.AvisoMayoreoQuitado));
        Assert.Equal("1:2200 12:1800", await EscalasDeAsync("PEG"));
        await using (var db = _base!.Contexto())
            Assert.Empty(await new RegistroDepuracion(db).MayoreoPorRevisarAsync());
        // Ahora sí se lo quitan: eleventa deja el precio de mayoreo igual al menudeo.
        var quitado = await SincronizarAsync(Pegamento(22, 22));
        Assert.Equal(1, quitado.Avisos[SincronizacionEleventa.AvisoMayoreoQuitado]);
        Assert.Equal("1:2200", await EscalasDeAsync("PEG"));
        Assert.False((await SincronizarAsync(Pegamento(22, 22))).HuboCambios);
    }

    /// <summary>
    /// Revisión del PR #139: en la base de la tienda, casi dos de cada tres productos con precio de mayoreo lo tienen igual al
    /// menudeo (así deja eleventa uno «sin mayoreo»), y eleventa no lo mueve al cambiar el precio. Si suben el folder de $10 a $12,
    /// los $10 que se quedaron no son un mayoreo nuevo: nada queda por revisar, ni ahora ni en las lecturas siguientes, tampoco si
    /// lo vuelven a subir. Un mayoreo que sí ponen después llega por revisar, como siempre.
    /// </summary>
    [Fact]
    public async Task R139_Subir_el_precio_de_un_producto_sin_mayoreo_no_inventa_un_mayoreo_por_revisar()
    {
        DatosEleventa Folder(double precio, double mayoreo) => Datos([Producto(1, "FOL", "Folder", precio: precio, costo: 6, mayoreo: mayoreo)]);
        await ImportadaAsync(Folder(10, 10));
        async Task<int> PorRevisarAsync()
        {
            await using var db = _base!.Contexto();
            return (await new RegistroDepuracion(db).MayoreoPorRevisarAsync()).Count;
        }

        var subio = await SincronizarAsync(Folder(12, 10));
        Assert.Equal((0, false), (await PorRevisarAsync(), subio.Avisos.ContainsKey(SincronizacionEleventa.AvisoMayoreoCambiado)));
        Assert.False((await SincronizarAsync(Folder(12, 10))).HuboCambios);
        var otraVez = await SincronizarAsync(Folder(14, 10));
        Assert.Equal((0, false), (await PorRevisarAsync(), otraVez.Avisos.ContainsKey(SincronizacionEleventa.AvisoMayoreoCambiado)));

        // Un mayoreo de verdad: queda por revisar en F3, con su aviso.
        var conMayoreo = await SincronizarAsync(Folder(14, 11));
        Assert.Equal((1, true), (await PorRevisarAsync(), conMayoreo.Avisos.ContainsKey(SincronizacionEleventa.AvisoMayoreoCambiado)));
    }

    /// <summary>
    /// Revisión del PR #139: el pegamento tiene su escala del F11 desde 12; eleventa le pone una promoción por cantidad desde 12, y
    /// la lectura la toma en ese mismo renglón. Si después eleventa le quita el F11 y la promoción sigue, la promoción se queda (antes
    /// el mayoreo borraba el renglón, que era también el de la promoción). Y al revés: si quitan la promoción y el F11 sigue, el
    /// renglón vuelve a ser la escala del F11, a su precio, en vez de borrarse.
    /// </summary>
    [Fact]
    public async Task R139_Quitar_el_F11_no_se_lleva_la_promocion_de_eleventa_de_la_misma_escala()
    {
        await ImportadaAsync(Pegamento(22, 18));
        await SincronizarAsync(Pegamento(22, 18, new FilaPromocion("PEG", 12, 17)));
        Assert.Equal("1:2200 12:1700", await EscalasDeAsync("PEG"));

        var sinF11 = await SincronizarAsync(Pegamento(22, 0, new FilaPromocion("PEG", 12, 17)));
        Assert.Equal("1:2200 12:1700", await EscalasDeAsync("PEG"));
        Assert.False(sinF11.Avisos.ContainsKey(SincronizacionEleventa.AvisoMayoreoQuitado));

        // Al revés, en otra base: la promoción se quita y el F11 sigue: el renglón regresa a su escala, a $18.
        await _base!.DisposeAsync();
        await ImportadaAsync(Pegamento(22, 18));
        await SincronizarAsync(Pegamento(22, 18, new FilaPromocion("PEG", 12, 17)));
        var sinPromocion = await SincronizarAsync(Pegamento(22, 18));
        Assert.Equal("1:2200 12:1800", await EscalasDeAsync("PEG"));
        Assert.Equal(1, sinPromocion.Precios);
        Assert.False((await SincronizarAsync(Pegamento(22, 18))).HuboCambios);
    }

    /// <summary>
    /// Lo que una importación de antes puso en kilo pasa a la pieza a granel en la siguiente lectura, una vez, con su presentación
    /// base: lo a granel con la medida PIEZA, como las impresiones (el ticket decía «0.2 kg»), y lo que tiene la medida KG, que en la
    /// tienda está desactivada (revisión del PR #139). Lo que eleventa tiene en litros no se toca, aunque aquí esté en kilo.
    /// </summary>
    [Fact]
    public async Task Lo_que_la_importacion_de_antes_puso_en_kilo_sin_ser_kilo_pasa_a_pieza_a_granel()
    {
        var datos = Datos([Producto(1, "IMP", "Impresion b/n", precio: 1, costo: 0.3, tipo: 'D'),
            Producto(2, "DIA", "Diamantina a granel", precio: 200, costo: 120, tipo: 'D') with { MedidaId = 4 },
            Producto(3, "ALC", "Alcohol", precio: 40, costo: 25, tipo: 'D') with { MedidaId = 5 }]);
        await ImportadaAsync(datos);
        long version;
        await using (var db = _base!.Contexto())
        {
            // Como las dejaba la importación de antes: en kilo, y su presentación base, «Kilogramo». El alcohol, en kilo a mano.
            foreach (var codigo in new[] { "IMP", "DIA", "ALC" })
            {
                var variante = await VarianteAsync(db, codigo);
                Assert.NotEqual(UnidadBase.Kilogramo, variante.UnidadBase);
                variante.UnidadBase = UnidadBase.Kilogramo;
                (await db.Presentaciones.SingleAsync(p => p.VarianteId == variante.Id && p.EsBase)).Nombre = "Kilogramo";
            }
            await db.SaveChangesAsync();
            version = await VersionAsync(db);
        }

        var r = await SincronizarAsync(datos);
        var otra = await SincronizarAsync(datos);

        Assert.Equal(2, r.Cambiados);
        Assert.Equal((SincronizacionEleventa.AvisoUnidadCorregida, 2), (Assert.Single(r.Avisos).Key, r.Avisos[SincronizacionEleventa.AvisoUnidadCorregida]));
        Assert.False(otra.HuboCambios, otra.ToString());
        await using var lectura = _base.Contexto();
        foreach (var codigo in new[] { "IMP", "DIA" })
        {
            var corregida = await VarianteAsync(lectura, codigo);
            Assert.Equal((UnidadBase.PiezaAGranel, "Pieza"),
                (corregida.UnidadBase, (await lectura.Presentaciones.SingleAsync(p => p.VarianteId == corregida.Id && p.EsBase)).Nombre));
        }
        Assert.Equal(UnidadBase.Kilogramo, (await VarianteAsync(lectura, "ALC")).UnidadBase);
        Assert.True(await VersionAsync(lectura) > version);
    }

    /// <summary>
    /// La lectura del piloto trae los mínimos de eleventa cada vez y deja la marca de que ya se trajeron (una sola): en una base que
    /// se armó antes de traerlos, después del piloto, la importación final no los vuelve a poner (revisión del PR #139).
    /// </summary>
    [Fact]
    public async Task La_lectura_deja_la_marca_de_que_los_minimos_de_eleventa_ya_se_trajeron()
    {
        var datos = Datos([Producto(1, "A1", "Goma") with { InventarioMinimo = 5, InventarioMaximo = 5 }]);
        await ImportadaAsync(datos);
        async Task<int> MarcasAsync()
        {
            await using var db = _base!.Contexto();
            return await db.MapeosImportacion.CountAsync(m => m.Entidad == ReglasEleventa.EntidadMinimosTraidos);
        }
        await using (var db = _base!.Contexto())
        {
            db.MapeosImportacion.Remove(await db.MapeosImportacion.SingleAsync(m => m.Entidad == ReglasEleventa.EntidadMinimosTraidos));
            await db.SaveChangesAsync();
        }

        await SincronizarAsync(datos);
        await SincronizarAsync(datos);

        Assert.Equal(1, await MarcasAsync());
        await using var final = _base.Contexto();
        Assert.True((await ImportacionFinal.AplicarAsync(final, datos)).MinimosYaTraidos);
    }

    /// <summary>«mínimo/máximo» de una variante, en su unidad; «—», sin.</summary>
    private static string MinimoYMaximo(Variante v) => $"{v.InventarioMinimo?.ToString() ?? "—"}/{v.InventarioMaximo?.ToString() ?? "—"}";

    /// <summary>
    /// El mínimo y el máximo de inventario llegan de eleventa (2026-10-24: en el piloto el inventario se lleva allá, y eleventa
    /// manda): la lectura compara todo el catálogo, así que un cambio solo del mínimo también llega, sin tocar lo demás, y sube
    /// la versión del catálogo. A granel, en metros; lo que la ficha no aceptaría (media pieza) no se toma y se avisa, y lo de
    /// antes se queda; si allá se lo quitan, aquí también. Con lo mismo, la lectura siguiente no cambia nada.
    /// </summary>
    [Fact]
    public async Task El_minimo_y_el_maximo_llegan_de_eleventa_tambien_un_cambio_solo_del_minimo()
    {
        static FilaProducto Pluma(double minimo, double maximo) => Producto(1, "PLU", "Pluma") with { InventarioMinimo = minimo, InventarioMaximo = maximo };
        static FilaProducto Liston(double minimo, double maximo) =>
            Producto(2, "LIS", "Listón X Metro", tipo: 'D') with { InventarioMinimo = minimo, InventarioMaximo = maximo };
        static DatosEleventa Con(FilaProducto pluma, FilaProducto liston) => Datos([pluma, liston], existencias: [new(1, 4), new(2, 7.5)]);
        async Task<(string Pluma, string Liston)> ComoQuedanAsync()
        {
            await using var db = _base!.Contexto();
            return (MinimoYMaximo(await VarianteAsync(db, "PLU")), MinimoYMaximo(await VarianteAsync(db, "LIS")));
        }
        await ImportadaAsync(Con(Pluma(10, 10), Liston(2.5, 2.5)));
        Assert.Equal(("10/10", "2.5/2.5"), await ComoQuedanAsync());
        Assert.False((await SincronizarAsync(Con(Pluma(10, 10), Liston(2.5, 2.5)))).HuboCambios);
        long version;
        await using (var db = _base!.Contexto())
            version = await VersionAsync(db);

        // En eleventa solo cambió el mínimo de la pluma.
        var soloMinimo = await SincronizarAsync(Con(Pluma(6, 10), Liston(2.5, 2.5)));
        Assert.Equal("1 mínimos y máximos", soloMinimo.ToString());
        Assert.Equal(("6/10", "2.5/2.5"), await ComoQuedanAsync());
        await using (var db = _base.Contexto())
            Assert.True(await VersionAsync(db) > version);
        Assert.False((await SincronizarAsync(Con(Pluma(6, 10), Liston(2.5, 2.5)))).HuboCambios);

        // A granel, en su unidad; media pieza no se toma (se avisa) y la pluma se queda con lo de antes.
        var otros = await SincronizarAsync(Con(Pluma(2.5, 2.5), Liston(1.25, 3.5)));
        Assert.Equal(1, otros.MinimosYMaximos);
        Assert.Equal(1, otros.Avisos[ReglasEleventa.AvisoMinimoConFracciones]);
        Assert.Equal(("6/10", "1.25/3.5"), await ComoQuedanAsync());

        // Allá se los quitaron (0 o vacío): aquí también.
        Assert.Equal(2, (await SincronizarAsync(Con(Pluma(0, 0), Liston(0, 0)))).MinimosYMaximos);
        Assert.Equal(("—/—", "—/—"), await ComoQuedanAsync());
        Assert.False((await SincronizarAsync(Con(Pluma(0, 0), Liston(0, 0)))).HuboCambios);
    }

    /// <summary>
    /// Lo dado de alta aquí se queda con el mínimo de su ficha y un servicio no lleva inventario (no toma el de eleventa); un
    /// producto nuevo de eleventa llega con el suyo, como al importar (cuenta como nuevo).
    /// </summary>
    [Fact]
    public async Task Lo_dado_de_alta_aqui_y_los_servicios_no_toman_el_minimo_de_eleventa_y_lo_nuevo_llega_con_el_suyo()
    {
        await ImportadaAsync(Datos([Producto(1, "PLU", "Pluma"), Producto(2, "ENG", "Engargolado")]));
        Guid propia;
        await using (var db = _base!.Contexto())
        {
            var departamento = await db.Departamentos.FirstAsync();
            var producto = new Producto { Nombre = "Bolsa de regalo", DepartamentoId = departamento.Id };
            var variante = new Variante { ProductoId = producto.Id, InventarioMinimo = Cantidad.Piezas(2) };
            db.AddRange(producto, variante, new Presentacion { VarianteId = variante.Id, Nombre = "Pieza", EsBase = true });
            (await VarianteAsync(db, "ENG")).Producto!.Tipo = TipoProducto.Servicio;
            await db.SaveChangesAsync();
            propia = variante.Id;
        }

        var r = await SincronizarAsync(Datos(
        [
            Producto(1, "PLU", "Pluma") with { InventarioMinimo = 5, InventarioMaximo = 5 },
            Producto(2, "ENG", "Engargolado") with { InventarioMinimo = 3, InventarioMaximo = 3 },
            Producto(3, "FOL", "Folder") with { InventarioMinimo = 20, InventarioMaximo = 20 },
        ]));

        Assert.Equal((1, 1), (r.Nuevos, r.MinimosYMaximos));
        await using var lectura = _base.Contexto();
        Assert.Equal("5/5", MinimoYMaximo(await VarianteAsync(lectura, "PLU")));
        Assert.Equal("—/—", MinimoYMaximo(await VarianteAsync(lectura, "ENG")));
        Assert.Equal("20/20", MinimoYMaximo(await VarianteAsync(lectura, "FOL")));
        Assert.Equal("2/—", MinimoYMaximo(await lectura.Variantes.SingleAsync(v => v.Id == propia)));
    }

    // El piloto: la caja 2 vende y lo manda a eleventa por la bandeja. La lectura no deshace lo que eleventa todavía no tiene.

    private sealed record Mostrador(Caja Caja, Usuario Cajera, Usuario Encargado, Turno Turno);

    /// <summary>La caja 2 con su turno abierto, sobre la base importada.</summary>
    private async Task<Mostrador> AbrirCajaAsync()
    {
        await using var db = _base!.Contexto();
        var caja = new Caja { Nombre = "Caja 2", Serie = "A", SucursalId = (await db.Sucursales.SingleAsync()).Id };
        var cajera = new Usuario { Nombre = "Ana", NombreUsuario = "ana" };
        var encargado = new Usuario { Nombre = "Luis", NombreUsuario = "luis", Rol = Rol.Encargado };
        cajera.EstablecerPin("1111");
        encargado.EstablecerPin("2222");
        db.AddRange(caja, cajera, encargado);
        await db.SaveChangesAsync();
        // Los usuarios de antes (con su rol) reciben sus permisos, como al arrancar el servicio.
        await MigracionPermisos.AplicarAsync(db);
        return new Mostrador(caja, cajera, encargado, await new RegistroCaja(db).AbrirTurnoAsync(caja.Id, cajera.Id, 50000));
    }

    /// <summary>Una venta en efectivo que entra a la bandeja hacia eleventa junto con el cobro, como en la caja 2.</summary>
    private async Task<(Venta Venta, Guid Envio)> VenderAsync(Mostrador m, string codigo, int piezas)
    {
        await using var db = _base!.Contexto();
        var variante = await VarianteAsync(db, codigo);
        var venta = new Venta { SucursalId = m.Caja.SucursalId, CajaId = m.Caja.Id, TurnoId = m.Turno.Id, CajeroId = m.Cajera.Id, Serie = "A" };
        venta.AgregarProducto(new ProductoEnVenta(variante.Id, variante.Producto!.Nombre, Impuesto.Iva16, UnidadBase.Pieza, true),
            await db.Presentaciones.SingleAsync(p => p.VarianteId == variante.Id && p.EsBase), Cantidad.Piezas(piezas),
            await db.Precios.Where(p => p.VarianteId == variante.Id).ToListAsync());
        var envio = new EnvioAEleventa { Tipo = TipoEnvioEleventa.Venta, VentaId = venta.Id, CreadoUtc = _base.Reloj.Ahora.UtcDateTime };
        db.EnviosAEleventa.Add(envio);
        await new RegistroCaja(db).CobrarAsync(venta, [new CobroSolicitado(FormaPago.Efectivo, venta.TotalCentavos)]);
        return (venta, envio.Id);
    }

    /// <summary>Lo que hace la bandeja (o quien la revisa) con un envío.</summary>
    private async Task MarcarAsync(Guid envio, EstadoEnvioEleventa estado, int intentos = 0)
    {
        await using var db = _base!.Contexto();
        var x = await db.EnviosAEleventa.SingleAsync(e => e.Id == envio);
        (x.Estado, x.Intentos) = (estado, intentos);
        await db.SaveChangesAsync();
    }

    private async Task<Guid> EnvioDeAsync(Guid devolucionId)
    {
        await using var db = _base!.Contexto();
        return await db.EnviosAEleventa.Where(e => e.DevolucionId == devolucionId).Select(e => e.Id).SingleAsync();
    }

    private async Task<Cantidad> ExistenciaAsync(string codigo)
    {
        await using var db = _base!.Contexto();
        var variante = await VarianteAsync(db, codigo);
        return (await new RegistroInventario(db).ExistenciaAsync((await db.Sucursales.SingleAsync()).Id, variante.Id)).Cantidad;
    }

    private async Task<List<long>> AjustesAsync(string codigo)
    {
        await using var db = _base!.Contexto();
        var variante = await VarianteAsync(db, codigo);
        return await db.MovimientosInventario.Where(m => m.VarianteId == variante.Id && m.DocumentoTipo == SincronizacionEleventa.Documento)
            .OrderBy(m => m.FechaUtc).Select(m => m.Cantidad.Milesimas).ToListAsync();
    }

    private static DatosEleventa Gomas(double existencia) => Datos([Producto(1, "A1", "Goma", costo: 2)], existencias: [new(1, existencia)]);

    [Fact]
    public async Task Una_venta_que_no_ha_llegado_a_eleventa_no_se_deshace_ni_se_repite_al_llegar()
    {
        await ImportadaAsync(Gomas(10));
        var caja = await AbrirCajaAsync();
        var (_, envio) = await VenderAsync(caja, "A1", 2);

        // Eleventa todavía no la tiene: la lectura no regresa las 2 gomas.
        var antes = await SincronizarAsync(Gomas(10));
        // Mientras, en la caja 1 de eleventa se vendió una: eso sí llega.
        var otraCaja = await SincronizarAsync(Gomas(9));
        Assert.Equal(Cantidad.Piezas(7), await ExistenciaAsync("A1"));
        // Llega a eleventa: ya la descuenta allá, y aquí no se descuenta otra vez.
        await MarcarAsync(envio, EstadoEnvioEleventa.Enviada, intentos: 1);
        var despues = await SincronizarAsync(Gomas(7));

        Assert.Equal((0, 1, 0), (antes.Existencias, otraCaja.Existencias, despues.Existencias));
        Assert.Empty(antes.Avisos);
        Assert.Equal(Cantidad.Piezas(7), await ExistenciaAsync("A1"));
        // Solo el ajuste de lo que vendió la caja 1: sin ida y vuelta en el kardex.
        Assert.Equal([-1000L], await AjustesAsync("A1"));
        await using var db = _base!.Contexto();
        Assert.Empty(await new RegistroInventario(db).VerificarExistenciasAsync());
    }

    [Fact]
    public async Task Lo_que_quedo_por_revisar_se_sigue_descontando_hasta_que_se_anota_a_mano_en_eleventa()
    {
        await ImportadaAsync(Gomas(10));
        var caja = await AbrirCajaAsync();
        var (_, envio) = await VenderAsync(caja, "A1", 2);
        // Eleventa no la entendería (o se dejó para revisar): no va sola.
        await MarcarAsync(envio, EstadoEnvioEleventa.PorRevisar);

        var primera = await SincronizarAsync(Gomas(10));
        var segunda = await SincronizarAsync(Gomas(10));
        // Alguien la registra a mano en eleventa y la anota en la bandeja.
        await MarcarAsync(envio, EstadoEnvioEleventa.AMano);
        var aMano = await SincronizarAsync(Gomas(8));

        Assert.Equal((0, 0, 0), (primera.Existencias, segunda.Existencias, aMano.Existencias));
        Assert.Equal(Cantidad.Piezas(8), await ExistenciaAsync("A1"));
        Assert.Empty(await AjustesAsync("A1"));
    }

    [Fact]
    public async Task Las_devoluciones_que_no_han_llegado_cuentan_y_lo_danado_no_regresa_en_ningun_lado()
    {
        await ImportadaAsync(Gomas(10));
        var caja = await AbrirCajaAsync();
        var (venta, envio) = await VenderAsync(caja, "A1", 3);
        await MarcarAsync(envio, EstadoEnvioEleventa.Enviada, intentos: 1);
        Guid buena, danada;
        await using (var db = _base!.Contexto())
        {
            var linea = venta.Lineas.Single().Id;
            var devoluciones = new RegistroDevoluciones(db);
            buena = (await devoluciones.DevolverAsync(venta.Id, [new LineaPorDevolver(linea, Cantidad.Piezas(1))], caja.Cajera.Id, caja.Caja.Id, "No era", null)).Id;
            danada = (await devoluciones.DevolverAsync(venta.Id, [new LineaPorDevolver(linea, Cantidad.Piezas(1), RegresaAlInventario: false)],
                caja.Cajera.Id, caja.Caja.Id, "Llegó rota", null)).Id;
        }
        Assert.Equal(Cantidad.Piezas(8), await ExistenciaAsync("A1"));

        // Eleventa tiene la venta, todavía no las devoluciones.
        var sinDevoluciones = await SincronizarAsync(Gomas(7));
        // Llegan: la buena regresa al inventario de eleventa y la dañada no.
        await MarcarAsync(await EnvioDeAsync(buena), EstadoEnvioEleventa.Enviada, intentos: 1);
        await MarcarAsync(await EnvioDeAsync(danada), EstadoEnvioEleventa.Enviada, intentos: 1);
        var conDevoluciones = await SincronizarAsync(Gomas(8));

        Assert.Equal((0, 0), (sinDevoluciones.Existencias, conDevoluciones.Existencias));
        Assert.Equal(Cantidad.Piezas(8), await ExistenciaAsync("A1"));
        Assert.Empty(await AjustesAsync("A1"));
    }

    [Fact]
    public async Task Lo_cancelado_antes_de_llegar_a_eleventa_no_mueve_la_existencia()
    {
        await ImportadaAsync(Gomas(10));
        var caja = await AbrirCajaAsync();
        var (venta, envio) = await VenderAsync(caja, "A1", 2);
        Guid cancelacion;
        await using (var db = _base!.Contexto())
            cancelacion = await EnvioDeAsync((await new RegistroDevoluciones(db).CancelarAsync(venta.Id, caja.Encargado.Id, caja.Caja.Id, "Se arrepintió", null)).Id);

        // La bandeja descarta la venta y enseguida su cancelación: entre una y otra, y después, nada cambia.
        await MarcarAsync(envio, EstadoEnvioEleventa.Descartada);
        var entreLasDos = await SincronizarAsync(Gomas(10));
        await MarcarAsync(cancelacion, EstadoEnvioEleventa.Descartada);
        var descartadas = await SincronizarAsync(Gomas(10));

        Assert.Equal((0, 0), (entreLasDos.Existencias, descartadas.Existencias));
        Assert.Equal(Cantidad.Piezas(10), await ExistenciaAsync("A1"));
        Assert.Empty(await AjustesAsync("A1"));
    }

    [Fact]
    public void La_lectura_junta_lo_que_se_estaba_mandando_al_empezar_y_lo_que_se_mando_despues()
    {
        var enCurso = new EnviosEnCurso();
        Guid antes = Guid.NewGuid(), aMedias = Guid.NewGuid(), despues = Guid.NewGuid(), yaNo = Guid.NewGuid();
        // Lo que terminó de mandarse (y quedó anotado) antes de empezar a leer, no.
        using (enCurso.Mandando(antes))
        {
        }
        var mandando = enCurso.Mandando(aMedias);
        var vigilancia = enCurso.Vigilar();
        mandando.Dispose();
        using (enCurso.Mandando(despues))
        {
        }
        var mandados = vigilancia.Mandados();
        vigilancia.Dispose();
        using (enCurso.Mandando(yaNo))
        {
        }

        Assert.Equal(new[] { aMedias, despues }.Order(), mandados.Order());
    }

    [Fact]
    public void Lo_que_eleventa_rechazo_vale_hasta_el_siguiente_intento_y_lo_anotado_a_mano_tambien_se_junta()
    {
        var enCurso = new EnviosEnCurso();
        Guid rechazado = Guid.NewGuid(), deRed = Guid.NewGuid(), aMano = Guid.NewGuid();
        using (enCurso.Mandando(rechazado))
            enCurso.Rechazado(rechazado);
        using (enCurso.Mandando(deRed))
        {
        }
        var vigilancia = enCurso.Vigilar();
        var antes = vigilancia.Rechazados();
        enCurso.AnotadoAMano(aMano);
        // Otro intento: hasta que termine, no se sabe cómo le fue.
        using (enCurso.Mandando(rechazado))
        {
        }
        var despues = vigilancia.Rechazados();
        var mandados = vigilancia.Mandados();
        vigilancia.Dispose();

        Assert.Equal([rechazado], antes);
        Assert.Empty(despues);
        Assert.Equal(new[] { rechazado, aMano }.Order(), mandados.Order());
    }

    [Fact]
    public async Task Lo_que_se_mando_mientras_se_leia_eleventa_o_no_se_sabe_si_entro_se_ajusta_en_otra_lectura()
    {
        await ImportadaAsync(Datos([Producto(1, "A1", "Goma", costo: 2), Producto(2, "B2", "Sacapuntas")], existencias: [new(1, 10), new(2, 4)]));
        var caja = await AbrirCajaAsync();
        var (_, envio) = await VenderAsync(caja, "A1", 2);
        DatosEleventa Eleventa(double gomas) =>
            Datos([Producto(1, "A1", "Goma", costo: 2), Producto(2, "B2", "Sacapuntas")], existencias: [new(1, gomas), new(2, 3)]);

        // Se lee eleventa (todavía sin la venta) y, antes de aplicarlo, la venta entra allá y aquí se anota enviada.
        var enCurso = new EnviosEnCurso();
        ResultadoSincronizacion mientras;
        using (var vigilancia = enCurso.Vigilar())
        {
            using (enCurso.Mandando(envio))
                await MarcarAsync(envio, EstadoEnvioEleventa.Enviada, intentos: 1);
            mientras = await SincronizarAsync(Eleventa(10), vigilancia);
        }
        var siguiente = await SincronizarAsync(Eleventa(8));

        // Las gomas no se tocan esa vez (hubieran subido a 10 y bajado otra vez); los sacapuntas sí.
        Assert.Equal(1, mientras.Existencias);
        Assert.Contains(SincronizacionEleventa.AvisoEnCamino, mientras.Avisos.Keys);
        Assert.Equal(0, siguiente.Existencias);
        Assert.Empty(siguiente.Avisos);
        Assert.Equal(Cantidad.Piezas(8), await ExistenciaAsync("A1"));
        Assert.Empty(await AjustesAsync("A1"));
        Assert.Equal(Cantidad.Piezas(3), await ExistenciaAsync("B2"));

        // Pendiente con un intento que falló: pudo haber entrado (la red se cortó justo al confirmar) o no.
        var (_, otraVenta) = await VenderAsync(caja, "A1", 1);
        await MarcarAsync(otraVenta, EstadoEnvioEleventa.Pendiente, intentos: 1);
        var sinSaber = await SincronizarAsync(Eleventa(7));
        var sinSaberTampoco = await SincronizarAsync(Eleventa(8));
        Assert.Equal((0, 0), (sinSaber.Existencias, sinSaberTampoco.Existencias));
        Assert.Contains(SincronizacionEleventa.AvisoEnCamino, sinSaber.Avisos.Keys);
        Assert.Equal(Cantidad.Piezas(7), await ExistenciaAsync("A1"));
    }

    [Fact]
    public async Task Lo_que_eleventa_rechazo_se_descuenta_aunque_la_bandeja_siga_detenida_en_eso()
    {
        await ImportadaAsync(Gomas(10));
        var caja = await AbrirCajaAsync();
        var (_, envio) = await VenderAsync(caja, "A1", 2);
        // La bandeja se detuvo en esta venta: ya lleva varios intentos.
        await MarcarAsync(envio, EstadoEnvioEleventa.Pendiente, intentos: 7);
        var enCurso = new EnviosEnCurso();

        async Task<ResultadoSincronizacion> LeerAsync(double gomas)
        {
            using var vigilancia = enCurso.Vigilar();
            return await SincronizarAsync(Gomas(gomas), vigilancia);
        }

        // En la caja 1 entraron 10 de compra. Recién arrancado el servicio no se sabe cómo fallaron sus intentos: esa vez
        // las gomas no se ajustan.
        var alArrancar = await LeerAsync(20);
        // Se vuelve a intentar y eleventa la rechaza (no es la red): allá no está, y la compra sí llega.
        using (enCurso.Mandando(envio))
        {
            enCurso.Rechazado(envio);
            await MarcarAsync(envio, EstadoEnvioEleventa.Pendiente, intentos: 8);
        }
        var rechazada = await LeerAsync(20);
        var otraVez = await LeerAsync(20);

        Assert.Equal((0, 1, 0), (alArrancar.Existencias, rechazada.Existencias, otraVez.Existencias));
        Assert.Contains(SincronizacionEleventa.AvisoEnCamino, alArrancar.Avisos.Keys);
        Assert.Empty(rechazada.Avisos);
        Assert.Equal(Cantidad.Piezas(18), await ExistenciaAsync("A1"));
        Assert.Equal([10000L], await AjustesAsync("A1"));
    }

    [Fact]
    public async Task Lo_que_se_anota_a_mano_mientras_se_lee_eleventa_se_ajusta_en_otra_lectura()
    {
        await ImportadaAsync(Gomas(10));
        var caja = await AbrirCajaAsync();
        var (_, envio) = await VenderAsync(caja, "A1", 2);
        await MarcarAsync(envio, EstadoEnvioEleventa.PorRevisar);

        // Se lee eleventa (todavía sin la venta) y, antes de aplicarlo, alguien la registra allá y la anota a mano.
        var enCurso = new EnviosEnCurso();
        ResultadoSincronizacion mientras;
        using (var vigilancia = enCurso.Vigilar())
        {
            enCurso.AnotadoAMano(envio);
            await MarcarAsync(envio, EstadoEnvioEleventa.AMano);
            mientras = await SincronizarAsync(Gomas(10), vigilancia);
        }
        var siguiente = await SincronizarAsync(Gomas(8));

        // Sin ida y vuelta: las gomas no suben a 10 para volver a bajar.
        Assert.Equal((0, 0), (mientras.Existencias, siguiente.Existencias));
        Assert.Contains(SincronizacionEleventa.AvisoEnCamino, mientras.Avisos.Keys);
        Assert.Equal(Cantidad.Piezas(8), await ExistenciaAsync("A1"));
        Assert.Empty(await AjustesAsync("A1"));
    }
}
