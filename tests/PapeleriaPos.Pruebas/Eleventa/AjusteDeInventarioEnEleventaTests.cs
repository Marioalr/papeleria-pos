using FirebirdSql.Data.FirebirdClient;
using PapeleriaPos.Eleventa;
using PapeleriaPos.Nucleo.Ventas;

namespace PapeleriaPos.Pruebas.Eleventa;

/// <summary>
/// El ajuste de inventario que el puente le escribe a eleventa (docs/migracion-eleventa.md, «Cómo ajusta eleventa el
/// inventario»): campo por campo como uno hecho a mano en Inventario › Ajustes, con la diferencia y nunca el total, sin
/// repetirse y sin quedar a medias. Los datos son inventados.
/// </summary>
public class AjusteDeInventarioEnEleventaTests
{
    private static readonly CajaEnEleventa Caja = new(CajaId: 4, CajeroId: 7, Ip: "10.0.0.20");
    private static readonly TurnoParaEleventa Turno = new(new DateTime(2026, 3, 9, 8, 0, 0), DineroInicial: 500);
    private static readonly DateTime Contado = new(2026, 3, 9, 11, 30, 0);
    private static readonly DateTime Marca = new(2026, 3, 9, 12, 0, 5);

    private static async Task<EleventaDePrueba> TiendaAsync()
    {
        var e = await EleventaDePrueba.CrearAsync();
        await e.EjecutarAsync("INSERT INTO CAJAS (ID, NOMBRE, ULTIMA_IP) VALUES (4, 'Caja 2', '10.0.0.19')");
        await e.EjecutarAsync("INSERT INTO USUARIOS (ID, NOMBRE_COMPLETO, USUARIO) VALUES (7, 'Ana Ruiz', 'cajera2')");
        foreach (var (id, nombre) in new[] { (5, "Papelería"), (9, EnvioEleventa.DepartamentoComunes) })
            await e.EjecutarAsync("INSERT INTO DEPARTAMENTOS (ID, NOMBRE) VALUES (@id, @nombre)", ("id", id), ("nombre", nombre));
        await e.ProductoAsync(10, "CUAD-PRO", "Cuaderno profesional", depto: 5, costo: 18.4, precio: 30, existencia: 12);
        await e.ProductoAsync(11, "LIST-MT", "Listón por metro", depto: 5, costo: 2.5, precio: 6, existencia: 1.1, tipoVenta: 'D');
        // Lo que ya traía la base: el contador de eleventa no empieza en cero.
        await e.EjecutarAsync("SET GENERATOR GEN_INVENTARIO_AJUSTES_ID TO 411");
        await e.EjecutarAsync("SET GENERATOR INVENTARIO_AJUSTE_FOLIO TO 1000000");
        return e;
    }

    private static double Doble(object? valor) => Convert.ToDouble(valor);

    private static async Task<double> ExistenciaAsync(EleventaDePrueba e, int producto) =>
        Doble(await e.ValorAsync("SELECT CANTIDAD_ACTUAL FROM INVENTARIO_BALANCES WHERE PRODUCTO_ID = @p", ("p", producto)));

    private static async Task<(int Ajustes, int Kardex)> CuantosAsync(EleventaDePrueba e) =>
        (Convert.ToInt32(await e.ValorAsync("SELECT COUNT(*) FROM INVENTARIO_AJUSTES")),
         Convert.ToInt32(await e.ValorAsync("SELECT COUNT(*) FROM INVENTARIO_HISTORIAL WHERE AJUSTE_ID IS NOT NULL")));

    /// <summary>
    /// La cadena del kardex del producto: cada renglón empieza donde terminó el anterior (una venta resta su cantidad; un
    /// ajuste suma la suya, con su signo) y la existencia es donde termina el último.
    /// </summary>
    private static async Task CadenaAsync(EleventaDePrueba e, int producto, double inicial)
    {
        var hay = inicial;
        foreach (var h in await e.FilasAsync("SELECT * FROM INVENTARIO_HISTORIAL WHERE PRODUCTO_ID = @p ORDER BY ID", ("p", producto)))
        {
            Assert.Equal(hay, Doble(h["CANTIDAD_ANTERIOR"]), 4);
            hay += h["AJUSTE_ID"] is null ? -Doble(h["CANTIDAD"]) : Doble(h["CANTIDAD"]);
        }
        Assert.Equal(hay, await ExistenciaAsync(e, producto), 4);
    }

    /// <summary>Un ajuste hecho a mano en eleventa (Inventario › Ajustes): lo deja en <paramref name="nueva"/>.</summary>
    private static async Task<int> AjusteAManoAsync(EleventaDePrueba e, int producto, double nueva, DateTime cuando, string? motivo = null)
    {
        var habia = await ExistenciaAsync(e, producto);
        var id = Convert.ToInt32(await e.ValorAsync("SELECT GEN_ID(GEN_INVENTARIO_AJUSTES_ID, 1) FROM RDB$DATABASE"));
        var p = (await e.FilasAsync("SELECT DESCRIPCION, PCOSTO FROM PRODUCTOS WHERE ID = @p", ("p", producto))).Single();
        var texto = motivo is null ? $"Ajuste de inventario #{id}" : $"Ajuste: {motivo}";
        await e.EjecutarAsync("""
            INSERT INTO INVENTARIO_AJUSTES (ID, FOLIO, CUANDO_FUE, PRODUCTO_ID, CANTIDAD, COSTO_UNITARIO, DESCRIPCION, MOTIVO, CAJA_ID, USUARIO_ID, ALMACEN_ID)
            VALUES (@id, @id, @cuando, @p, @c, @costo, @nombre, @motivo, 1, 1, 1)
            """, ("id", id), ("cuando", cuando), ("p", producto), ("c", nueva - habia), ("costo", p["PCOSTO"]), ("nombre", p["DESCRIPCION"]), ("motivo", texto));
        await e.EjecutarAsync("""
            INSERT INTO INVENTARIO_HISTORIAL (PRODUCTO_ID, CUANDO_FUE, CANTIDAD_ANTERIOR, CANTIDAD, DESCRIPCION, COSTO_UNITARIO, COSTO_DESPUES, AJUSTE_ID,
                CAJA_ID, VENTA_POR_KIT, USUARIO_ID, ALMACEN_ID)
            VALUES (@p, @cuando, @habia, @c, @motivo, @costo, @costo, @id, 1, 'f', 1, 1)
            """, ("p", producto), ("cuando", cuando), ("habia", habia), ("c", nueva - habia), ("motivo", texto), ("costo", p["PCOSTO"]), ("id", id));
        await e.EjecutarAsync("UPDATE INVENTARIO_BALANCES SET CANTIDAD_ACTUAL = @n WHERE PRODUCTO_ID = @p", ("n", nueva), ("p", producto));
        return id;
    }

    [FactConFirebird]
    public async Task Un_ajuste_hacia_abajo_queda_campo_por_campo_como_el_de_eleventa()
    {
        await using var e = await TiendaAsync();

        // Había 12 y se contaron 9. La hora llega con milésimas y se guarda al segundo.
        var r = await EnvioEleventa.AjustarInventarioAsync(e.Conexion, Caja, new AjusteParaEleventa(10, -3, Marca.AddMilliseconds(430), Contado));

        Assert.Equal(new AjusteEnEleventa(412, YaEstaba: false, Habia: 12, Quedo: 9), r);
        var a = (await e.FilasAsync("SELECT * FROM INVENTARIO_AJUSTES")).Single();
        Assert.Equal((412, 412, Marca, 10, 4, 7, 1), (Convert.ToInt32(a["ID"]), Convert.ToInt32(a["FOLIO"]), (DateTime)a["CUANDO_FUE"]!,
            Convert.ToInt32(a["PRODUCTO_ID"]), Convert.ToInt32(a["CAJA_ID"]), Convert.ToInt32(a["USUARIO_ID"]), Convert.ToInt32(a["ALMACEN_ID"])));
        Assert.Equal(-3, Doble(a["CANTIDAD"]), 4);
        // El costo y el nombre, los del producto en eleventa (PCOSTO es FLOAT: 18.4, no 18.3999996185303).
        Assert.Equal(18.4f, Convert.ToSingle(a["COSTO_UNITARIO"]));
        Assert.Equal(("Cuaderno profesional", "Ajuste de inventario #412"), ((string)a["DESCRIPCION"]!, (string)a["MOTIVO"]!));

        var h = (await e.FilasAsync("SELECT * FROM INVENTARIO_HISTORIAL")).Single();
        Assert.Equal((412, 10, Marca, "Ajuste de inventario #412", 4, 7, "f", 1), (Convert.ToInt32(h["AJUSTE_ID"]), Convert.ToInt32(h["PRODUCTO_ID"]),
            (DateTime)h["CUANDO_FUE"]!, (string)h["DESCRIPCION"]!, Convert.ToInt32(h["CAJA_ID"]), Convert.ToInt32(h["USUARIO_ID"]),
            (string)h["VENTA_POR_KIT"]!, Convert.ToInt32(h["ALMACEN_ID"])));
        Assert.Equal((12, -3), (Doble(h["CANTIDAD_ANTERIOR"]), Doble(h["CANTIDAD"])));
        Assert.Equal((18.4f, 18.4f), (Convert.ToSingle(h["COSTO_UNITARIO"]), Convert.ToSingle(h["COSTO_DESPUES"])));
        Assert.Equal((null, null, null), (h["VENTA_ID"], h["RECIBO_INVENTARIO_ID"], h["TRANSFERENCIA_ID"]));

        // La existencia: lo anterior MÁS la diferencia (12 + −3). Con la aritmética de una venta habría quedado en 15.
        Assert.Equal(9, await ExistenciaAsync(e, 10));
        // No toca nada más: ni turnos, ni tickets, ni el producto, ni el contador de folios que eleventa no usa.
        Assert.Equal((0, 0), (Convert.ToInt32(await e.ValorAsync("SELECT COUNT(*) FROM TURNOS")), Convert.ToInt32(await e.ValorAsync("SELECT COUNT(*) FROM VENTATICKETS"))));
        Assert.Null(await e.ValorAsync("SELECT DINVENTARIO FROM PRODUCTOS WHERE ID = 10"));
        Assert.Equal(1000000L, Convert.ToInt64(await e.ValorAsync("SELECT GEN_ID(INVENTARIO_AJUSTE_FOLIO, 0) FROM RDB$DATABASE")));
    }

    [FactConFirebird]
    public async Task Hacia_arriba_con_fraccion_y_hasta_cero_la_cadena_del_kardex_y_la_existencia_cuadran()
    {
        await using var e = await TiendaAsync();

        // Sobra uno; después se venden 2; faltan 4; y al final no hay ninguno.
        var arriba = await EnvioEleventa.AjustarInventarioAsync(e.Conexion, Caja, new AjusteParaEleventa(10, 1, Marca));
        await EnvioEleventa.MandarAsync(e.Conexion, Caja, new VentaParaEleventa(Marca.AddMinutes(1), Marca.AddMinutes(2), 100, Turno, [new(10, "Cuaderno", 2, 60)]));
        var abajo = await EnvioEleventa.AjustarInventarioAsync(e.Conexion, Caja, new AjusteParaEleventa(10, -4, Marca.AddMinutes(3)));
        var aCero = await EnvioEleventa.AjustarInventarioAsync(e.Conexion, Caja, new AjusteParaEleventa(10, -7, Marca.AddMinutes(4)));

        Assert.Equal(((12m, 13m), (11m, 7m), (7m, 0m)), ((arriba.Habia, arriba.Quedo), (abajo.Habia, abajo.Quedo), (aCero.Habia, aCero.Quedo)));
        Assert.Equal(0, await ExistenciaAsync(e, 10));
        await CadenaAsync(e, 10, inicial: 12);

        // Lo de a granel: 1.1 − 0.7 queda en 0.4 limpio (el FLOAT de 0.4), no en 0.40000004; + 0.25; y hasta cero.
        var fraccion = await EnvioEleventa.AjustarInventarioAsync(e.Conexion, Caja, new AjusteParaEleventa(11, -0.7m, Marca));
        Assert.Equal((1.1m, 0.4m), (fraccion.Habia, fraccion.Quedo));
        Assert.Equal(0.4f, Convert.ToSingle(await e.ValorAsync("SELECT CANTIDAD_ACTUAL FROM INVENTARIO_BALANCES WHERE PRODUCTO_ID = 11")));
        await EnvioEleventa.AjustarInventarioAsync(e.Conexion, Caja, new AjusteParaEleventa(11, 0.25m, Marca.AddMinutes(1)));
        await EnvioEleventa.AjustarInventarioAsync(e.Conexion, Caja, new AjusteParaEleventa(11, -0.65m, Marca.AddMinutes(2)));
        Assert.Equal(0f, Convert.ToSingle(await e.ValorAsync("SELECT CANTIDAD_ACTUAL FROM INVENTARIO_BALANCES WHERE PRODUCTO_ID = 11")));
        await CadenaAsync(e, 11, inicial: 1.1);

        // Eleventa admite existencias negativas: un ajuste también puede dejarla abajo de cero.
        var negativo = await EnvioEleventa.AjustarInventarioAsync(e.Conexion, Caja, new AjusteParaEleventa(10, -2, Marca.AddMinutes(5)));
        Assert.Equal((0m, -2m, -2d), (negativo.Habia, negativo.Quedo, await ExistenciaAsync(e, 10)));

        // Los folios son los ID, seguidos, y cada motivo lleva el suyo.
        var ajustes = await e.FilasAsync("SELECT ID, FOLIO, MOTIVO FROM INVENTARIO_AJUSTES ORDER BY ID");
        Assert.Equal(Enumerable.Range(412, 7), ajustes.Select(a => Convert.ToInt32(a["ID"])));
        Assert.All(ajustes, a => Assert.Equal((a["ID"], $"Ajuste de inventario #{a["ID"]}"), (a["FOLIO"], (string)a["MOTIVO"]!)));
    }

    [FactConFirebird]
    public async Task Repetir_el_mismo_ajuste_no_lo_duplica()
    {
        await using var e = await TiendaAsync();
        var ajuste = new AjusteParaEleventa(10, -3, Marca, Contado);
        var granel = new AjusteParaEleventa(11, 0.3m, Marca, Contado);

        var primero = await EnvioEleventa.AjustarInventarioAsync(e.Conexion, Caja, ajuste);
        var fraccion = await EnvioEleventa.AjustarInventarioAsync(e.Conexion, Caja, granel);
        // Se cortó la red al confirmar y aquí no se supo: se manda otra vez, igual. También con la hora con milésimas.
        var otraVez = await EnvioEleventa.AjustarInventarioAsync(e.Conexion, Caja, ajuste with { MarcaLocal = Marca.AddMilliseconds(900) });
        var fraccionOtraVez = await EnvioEleventa.AjustarInventarioAsync(e.Conexion, Caja, granel);

        Assert.Equal(new AjusteEnEleventa(primero.AjusteId, YaEstaba: true, Habia: 12, Quedo: 9), otraVez);
        Assert.Equal(new AjusteEnEleventa(fraccion.AjusteId, YaEstaba: true, Habia: 1.1m, Quedo: 1.4m), fraccionOtraVez);
        Assert.Equal((2, 2), await CuantosAsync(e));
        Assert.Equal((9, 1.4), (await ExistenciaAsync(e, 10), Math.Round(await ExistenciaAsync(e, 11), 4)));
        // El contador tampoco avanzó: no se tomó ningún número de más.
        Assert.Equal(413L, Convert.ToInt64(await e.ValorAsync("SELECT GEN_ID(GEN_INVENTARIO_AJUSTES_ID, 0) FROM RDB$DATABASE")));

        // Buscarlo sin escribir: está por su marca; con otra hora, otro producto u otra diferencia, no.
        Assert.Equal(primero.AjusteId, await EnvioEleventa.BuscarAjusteAsync(e.Servidor, 10, Marca, -3));
        Assert.Equal(fraccion.AjusteId, await EnvioEleventa.BuscarAjusteAsync(e.Servidor, 11, Marca, 0.3m));
        Assert.Null(await EnvioEleventa.BuscarAjusteAsync(e.Servidor, 10, Marca.AddSeconds(1), -3));
        Assert.Null(await EnvioEleventa.BuscarAjusteAsync(e.Servidor, 11, Marca, -3));
        Assert.Null(await EnvioEleventa.BuscarAjusteAsync(e.Servidor, 10, Marca, 3));

        // Otro ajuste del mismo producto lleva su propia marca (otra hora): ese sí entra.
        var otro = await EnvioEleventa.AjustarInventarioAsync(e.Conexion, Caja, ajuste with { MarcaLocal = Marca.AddSeconds(1), AjustesDelPos = [primero.AjusteId] });
        Assert.Equal((false, 6d), (otro.YaEstaba, await ExistenciaAsync(e, 10)));
        await CadenaAsync(e, 10, inicial: 12);
    }

    [FactConFirebird]
    public async Task Si_falla_a_media_escritura_no_queda_nada_a_medias()
    {
        await using var e = await TiendaAsync();
        var ajuste = new AjusteParaEleventa(10, -3, Marca, Contado);

        // Ya escribió el ajuste, su kardex y la existencia, y justo antes de confirmar se deja de esperar: se deshace todo.
        await Assert.ThrowsAsync<EscrituraDejada>(() => EnvioEleventa.AjustarInventarioAsync(e.Conexion, Caja with { SigueEsperando = () => false }, ajuste));
        Assert.Equal(((0, 0), 12d), (await CuantosAsync(e), await ExistenciaAsync(e, 10)));

        // Una caja de eleventa tiene apartada esa existencia (cobra en ese instante y no ha confirmado): el ajuste ya había insertado
        // sus dos renglones cuando Firebird rechaza el cambio de la existencia. No se espera el candado: se reintenta y, si sigue,
        // sale el error y no queda nada.
        await using (var otra = new FbConnection(e.Servidor.Cadena()))
        {
            await otra.OpenAsync();
            await using var cobrando = await otra.BeginTransactionAsync();
            await using (var resta = new FbCommand("UPDATE INVENTARIO_BALANCES SET CANTIDAD_ACTUAL = CANTIDAD_ACTUAL - 1 WHERE PRODUCTO_ID = 10", otra, cobrando))
                await resta.ExecuteNonQueryAsync();
            var choque = await Assert.ThrowsAsync<FbException>(() => EnvioEleventa.AjustarInventarioAsync(e.Conexion, Caja, ajuste));
            Assert.Contains(choque.Errors, error => error.Number is 335544345 or 335544336 or 335544451);
            await cobrando.RollbackAsync();
        }
        Assert.Equal(((0, 0), 12d), (await CuantosAsync(e), await ExistenciaAsync(e, 10)));

        // Al siguiente intento, con la misma marca, entra una sola vez.
        var r = await EnvioEleventa.AjustarInventarioAsync(e.Conexion, Caja, ajuste);
        Assert.Equal((false, (1, 1), 9d), (r.YaEstaba, await CuantosAsync(e), await ExistenciaAsync(e, 10)));
        Assert.Equal($"Ajuste de inventario #{r.AjusteId}", (string)(await e.ValorAsync("SELECT MOTIVO FROM INVENTARIO_AJUSTES"))!);
    }

    [FactConFirebird]
    public async Task Si_eleventa_vende_mientras_se_escribe_el_ajuste_se_vuelve_a_leer_y_no_pisa_la_venta()
    {
        await using var e = await TiendaAsync();
        // Como en la tienda (Firebird 2.0.1, protocolo 10). El intermediario retiene lo que contesta Firebird a media escritura.
        using var proxy = new ProxyFirebird(e.Servidor.Servidor, e.Servidor.Puerto) { SoloProtocolo10 = true, Retener = true };
        await using var lenta = new FbConnection((e.Servidor with { Servidor = "127.0.0.1", Puerto = proxy.Puerto }).Cadena());
        await lenta.OpenAsync();

        // El ajuste ya leyó que había 12 y va a escribir; en eso, eleventa cobra 2 y lo confirma.
        proxy.Disparador = "INSERT INTO INVENTARIO_AJUSTES";
        var ajustando = EnvioEleventa.AjustarInventarioAsync(lenta, Caja, new AjusteParaEleventa(10, -3, Marca, Contado));
        for (var i = 0; i < 300 && proxy.Disparador is not null; i++)
            await Task.Delay(100);
        Assert.Null(proxy.Disparador);
        await EnvioEleventa.MandarAsync(e.Conexion, Caja, new VentaParaEleventa(Marca.AddMinutes(-1), Marca, 100, Turno, [new(10, "Cuaderno", 2, 60)]));
        proxy.Soltar();
        Assert.Same(ajustando, await Task.WhenAny(ajustando, Task.Delay(TimeSpan.FromSeconds(60))));
        var r = await ajustando;

        // Firebird rechazó la primera escritura (la existencia ya era otra) y la segunda partió de 10: quedan 7, no 9.
        Assert.Equal((false, 10m, 7m), (r.YaEstaba, r.Habia, r.Quedo));
        Assert.Equal(((1, 1), 7d), (await CuantosAsync(e), await ExistenciaAsync(e, 10)));
        await CadenaAsync(e, 10, inicial: 12);
    }

    [FactConFirebird]
    public async Task Un_producto_que_eleventa_ya_borro_o_no_tiene_queda_por_revisar_y_no_se_escribe_nada()
    {
        await using var e = await TiendaAsync();
        await e.EjecutarAsync("UPDATE PRODUCTOS SET ELIMINADO_EN = @cuando WHERE ID = 10", ("cuando", Contado.AddMinutes(5)));

        var borrado = await Assert.ThrowsAsync<ChoqueConEleventa>(() => EnvioEleventa.AjustarInventarioAsync(e.Conexion, Caja, new AjusteParaEleventa(10, -3, Marca, Contado)));
        var sinProducto = await Assert.ThrowsAsync<ChoqueConEleventa>(() => EnvioEleventa.AjustarInventarioAsync(e.Conexion, Caja, new AjusteParaEleventa(99, 1, Marca, Contado)));

        Assert.Contains("Eleventa ya no tiene el producto 10", borrado.Message);
        Assert.Contains("Eleventa ya no tiene el producto 99", sinProducto.Message);
        Assert.Equal(((0, 0), 12d), (await CuantosAsync(e), await ExistenciaAsync(e, 10)));
        Assert.Null(await e.ValorAsync("SELECT CANTIDAD_ACTUAL FROM INVENTARIO_BALANCES WHERE PRODUCTO_ID = 99"));
        // El número que se hubiera tomado no se tomó: el contador sigue donde estaba.
        Assert.Equal(411L, Convert.ToInt64(await e.ValorAsync("SELECT GEN_ID(GEN_INVENTARIO_AJUSTES_ID, 0) FROM RDB$DATABASE")));
    }

    [FactConFirebird]
    public async Task Si_alguien_lo_ajusto_a_mano_en_eleventa_despues_de_contarlo_no_se_aplica()
    {
        await using var e = await TiendaAsync();
        // Antes de contarlo no estorba: es parte de lo que se contó.
        await AjusteAManoAsync(e, 10, nueva: 14, Contado.AddHours(-1));
        // Después de contarlo, alguien lo dejó en 8 en eleventa.
        var aMano = await AjusteAManoAsync(e, 10, nueva: 8, Contado.AddMinutes(10), motivo: "se recontó");

        var choque = await Assert.ThrowsAsync<ChoqueConEleventa>(() => EnvioEleventa.AjustarInventarioAsync(e.Conexion, Caja, new AjusteParaEleventa(10, -3, Marca, Contado)));

        Assert.Equal(MotivoPorRevisar.EleventaYaLoTenia, choque.Motivo);
        Assert.Contains("«Cuaderno profesional» se ajustó en eleventa el 09/03 a las 11:40, después de contarlo (09/03 11:30)", choque.Message);
        Assert.Equal(((2, 2), 8d), (await CuantosAsync(e), await ExistenciaAsync(e, 10)));

        // Lo vendido después de contar no es un ajuste: no lo detiene (la diferencia ya lo trae descontado). El otro producto, tampoco.
        await EnvioEleventa.MandarAsync(e.Conexion, Caja, new VentaParaEleventa(Contado.AddMinutes(19), Contado.AddMinutes(20), 100, Turno, [new(11, "Listón", 0.5m, 3)]));
        var otro = await EnvioEleventa.AjustarInventarioAsync(e.Conexion, Caja, new AjusteParaEleventa(11, 0.4m, Marca, Contado));
        Assert.Equal((0.6m, 1m), (otro.Habia, otro.Quedo));

        // Un ajuste que escribió el propio POS (otro conteo del mismo producto, aceptado antes) no es «a mano»: quien manda dice cuáles son.
        var delPos = await EnvioEleventa.AjustarInventarioAsync(e.Conexion, Caja, new AjusteParaEleventa(10, 1, Marca, Contado, AjustesDelPos: [aMano]));
        Assert.Equal((8m, 9m), (delPos.Habia, delPos.Quedo));
        // Sin la hora en que se contó (una herramienta contra una copia) no se revisa.
        var sinRevisar = await EnvioEleventa.AjustarInventarioAsync(e.Conexion, Caja, new AjusteParaEleventa(10, 1, Marca.AddSeconds(1)));
        Assert.Equal(10m, sinRevisar.Quedo);
        await CadenaAsync(e, 10, inicial: 12);
    }

    [FactConFirebird]
    public async Task Con_eleventa_restaurada_a_un_respaldo_anterior_no_se_escribe()
    {
        await using var e = await TiendaAsync();
        var primero = await EnvioEleventa.AjustarInventarioAsync(e.Conexion, Caja, new AjusteParaEleventa(10, -3, Marca, Contado));
        // Lo último que el puente sabe que escribió: el contador de los ajustes le dio el 412.
        var huella = new HuellaDelPuente([], [], new Dictionary<string, int> { ["GEN_INVENTARIO_AJUSTES_ID"] = primero.AjusteId });
        var conHuella = Caja with { Huella = huella };

        // Todo sigue ahí: escribe.
        var segundo = await EnvioEleventa.AjustarInventarioAsync(e.Conexion, conHuella, new AjusteParaEleventa(11, 0.5m, Marca, Contado));
        Assert.False(segundo.YaEstaba);

        // Eleventa regresa a un respaldo de antes de los dos: ya no están y su contador regresó.
        await e.EjecutarAsync("DELETE FROM INVENTARIO_HISTORIAL");
        await e.EjecutarAsync("DELETE FROM INVENTARIO_AJUSTES");
        await e.EjecutarAsync("UPDATE INVENTARIO_BALANCES SET CANTIDAD_ACTUAL = 12 WHERE PRODUCTO_ID = 10");
        await e.EjecutarAsync("SET GENERATOR GEN_INVENTARIO_AJUSTES_ID TO 411");

        var restaurada = await Assert.ThrowsAsync<EleventaRestaurada>(
            () => EnvioEleventa.AjustarInventarioAsync(e.Conexion, conHuella, new AjusteParaEleventa(10, -1, Marca.AddMinutes(1), Contado)));

        Assert.Equal(["los números de los ajustes de inventario regresaron: eleventa va en el 411 y ya le había dado el 412 a la caja 2"], restaurada.Restauracion.Pruebas);
        Assert.Equal(((0, 0), 12d), (await CuantosAsync(e), await ExistenciaAsync(e, 10)));
    }

    [FactConFirebird]
    public async Task Sin_diferencia_no_hay_ajuste_y_sin_renglon_de_existencia_lo_crea_desde_cero()
    {
        await using var e = await TiendaAsync();

        await Assert.ThrowsAsync<ArgumentException>(() => EnvioEleventa.AjustarInventarioAsync(e.Conexion, Caja, new AjusteParaEleventa(10, 0, Marca)));
        Assert.Equal((0, 0), await CuantosAsync(e));

        // Un producto sin renglón en INVENTARIO_BALANCES (en la tienda todos lo tienen): parte de cero, como una venta.
        await e.EjecutarAsync("DELETE FROM INVENTARIO_BALANCES WHERE PRODUCTO_ID = 11");
        var r = await EnvioEleventa.AjustarInventarioAsync(e.Servidor, Caja, new AjusteParaEleventa(11, 2.5m, Marca));
        Assert.Equal((0m, 2.5m, 2.5d), (r.Habia, r.Quedo, await ExistenciaAsync(e, 11)));
        Assert.Equal(1, Convert.ToInt32(await e.ValorAsync("SELECT ALMACEN_ID FROM INVENTARIO_BALANCES WHERE PRODUCTO_ID = 11")));
    }

    [FactConFirebird]
    public async Task Lo_que_escribe_el_puente_pasa_la_revision_de_observar_eleventa_igual_que_uno_hecho_a_mano()
    {
        await using var e = await TiendaAsync();
        var antes = await FotoEleventa.TomarAsync(e.Conexion);
        await EnvioEleventa.AjustarInventarioAsync(e.Conexion, Caja, new AjusteParaEleventa(10, -3, Marca, Contado));
        await EnvioEleventa.AjustarInventarioAsync(e.Conexion, Caja, new AjusteParaEleventa(11, 0.4m, Marca, Contado));
        var delPuente = await FotoEleventa.TomarAsync(e.Conexion);
        await AjusteAManoAsync(e, 10, nueva: 0, Marca.AddMinutes(5));
        var aMano = await FotoEleventa.TomarAsync(e.Conexion);

        var revision = RevisionEnVivo.Revisar(antes, delPuente);
        var revisionAMano = RevisionEnVivo.Revisar(delPuente, aMano);

        Assert.Equal("Ajuste de inventario #412 (INVENTARIO_AJUSTES 412, producto 10, − 3):", revision[0]);
        Assert.Contains("Ajuste de inventario #413 (INVENTARIO_AJUSTES 413, producto 11, + 0.4):", revision);
        Assert.Contains("  ✓ el motivo es «Ajuste de inventario #412»", revision);
        Assert.Contains("  ✓ empieza en lo que había (12)", revision);
        Assert.Contains("  ✓ la existencia quedó en lo anterior más la diferencia: 12 − 3 = 9", revision);
        Assert.Contains("  ✓ la existencia quedó en lo anterior más la diferencia: 1.1 + 0.4 = 1.5", revision);
        Assert.Contains("  ✓ GEN_INVENTARIO_AJUSTES_ID avanzó 2, uno por ajuste", revision);
        Assert.Contains("  ✓ INVENTARIO_AJUSTE_FOLIO no se movió: el folio es el ID", revision);
        Assert.Contains("  ✓ la existencia quedó en lo anterior más la diferencia: 9 − 9 = 0", revisionAMano);
        foreach (var lineas in new[] { revision, revisionAMano })
        {
            Assert.DoesNotContain(lineas, l => l.Contains('✗'));
            Assert.DoesNotContain(lineas, l => l.Contains('·'));
        }
        // Sin ajustes no hay nada que revisar.
        Assert.Empty(RevisionEnVivo.Revisar(aMano, await FotoEleventa.TomarAsync(e.Conexion)));
    }

    // Sin Firebird: fotos armadas a mano, con lo que eleventa haría distinto.

    private static TablaEnFoto Tabla(string nombre, string[] columnas, params string[][] renglones) =>
        new(nombre, columnas, [columnas[0]], renglones.Length, true, renglones.ToDictionary(r => r[0], r => r));

    private static FotoEleventa Foto(long contador, params TablaEnFoto[] tablas) =>
        new(DateTime.UtcNow, new Dictionary<string, long> { ["GEN_INVENTARIO_AJUSTES_ID"] = contador, ["INVENTARIO_AJUSTE_FOLIO"] = 1000000 },
            tablas.ToDictionary(t => t.Nombre, t => t));

    private static readonly string[] Ajustes = ["ID", "FOLIO", "CUANDO_FUE", "PRODUCTO_ID", "CANTIDAD", "COSTO_UNITARIO", "DESCRIPCION", "MOTIVO", "CAJA_ID", "USUARIO_ID", "ALMACEN_ID"];
    private static readonly string[] Kardex = ["ID", "PRODUCTO_ID", "CUANDO_FUE", "CANTIDAD_ANTERIOR", "CANTIDAD", "DESCRIPCION", "COSTO_UNITARIO", "COSTO_DESPUES",
        "AJUSTE_ID", "RECIBO_INVENTARIO_ID", "VENTA_ID", "TRANSFERENCIA_ID", "CAJA_ID", "VENTA_POR_KIT", "USUARIO_ID", "ALMACEN_ID"];
    private static readonly string[] Balances = ["PRODUCTO_ID", "CANTIDAD_ACTUAL", "ALMACEN_ID"];
    private static readonly string[] Productos = ["CODIGO", "ID", "DESCRIPCION", "PCOSTO", "PORCENTAJE_GANANCIA"];
    private const string Hora = "2026-03-09 12:00:05.000";

    private static FotoEleventa Antes(params TablaEnFoto[] otras) => Foto(411,
        [Tabla("INVENTARIO_AJUSTES", Ajustes), Tabla("INVENTARIO_HISTORIAL", Kardex), Tabla("INVENTARIO_BALANCES", Balances, ["10", "12", "1"]),
         Tabla("PRODUCTOS", Productos, ["CUAD-PRO", "10", "Cuaderno profesional", "18.4", "20"]), .. otras]);

    [Fact]
    public void Un_ajuste_a_mano_con_motivo_escrito_y_la_ganancia_recalculada_no_son_diferencias()
    {
        // Como se vio en la tienda (2026-11-27): con motivo escrito eleventa guarda «Ajuste: » más lo escrito, y al dejar un
        // producto en cero su ventana «Ajustar» recalculó el porcentaje de ganancia.
        var despues = Foto(412,
            Tabla("INVENTARIO_AJUSTES", Ajustes, ["412", "412", Hora, "10", "-12", "18.4", "Cuaderno profesional", "Ajuste: se mojaron", "1", "2", "1"]),
            Tabla("INVENTARIO_HISTORIAL", Kardex, ["900", "10", Hora, "12", "-12", "Ajuste: se mojaron", "18.4", "18.4", "412", "∅", "∅", "∅", "1", "f", "2", "1"]),
            Tabla("INVENTARIO_BALANCES", Balances, ["10", "0", "1"]),
            Tabla("PRODUCTOS", Productos, ["CUAD-PRO", "10", "Cuaderno profesional", "18.4", "63.04"]));

        var lineas = RevisionEnVivo.Revisar(Antes(), despues);

        Assert.Equal("Ajuste de inventario #412 (INVENTARIO_AJUSTES 412, producto 10, − 12):", lineas[0]);
        Assert.Contains("  · lleva un motivo escrito a mano («Ajuste: » más lo escrito); el POS siempre escribe «Ajuste de inventario #folio»", lineas);
        Assert.Contains("  ✓ la existencia quedó en lo anterior más la diferencia: 12 − 12 = 0", lineas);
        Assert.Contains("· Eleventa recalculó PORCENTAJE_GANANCIA del producto 10: lo hace a veces su ventana «Ajustar»; es esperado y el POS no lo escribe.", lineas);
        Assert.DoesNotContain(lineas, l => l.Contains('✗'));
    }

    [Fact]
    public void Lo_que_eleventa_haga_distinto_en_un_ajuste_se_marca()
    {
        // El signo al revés (como una venta), otro costo después, la hora con milésimas, el folio de su contador, y además cambió
        // el costo del producto y anotó algo en una tabla que el POS no toca.
        var antes = Antes(Tabla("BITACORA", ["ID", "TEXTO"]), Tabla("VENTATICKETS", ["ID", "FOLIO"]));
        var despues = Foto(413,
            Tabla("INVENTARIO_AJUSTES", Ajustes, ["412", "1000001", "2026-03-09 12:00:05.430", "10", "-3", "18.4", "Cuaderno", "Conteo", "1", "2", "1"]),
            Tabla("INVENTARIO_HISTORIAL", Kardex, ["900", "10", Hora, "12", "3", "Conteo", "18.4", "19", "412", "∅", "∅", "∅", "1", "f", "2", "1"]),
            Tabla("INVENTARIO_BALANCES", Balances, ["10", "15", "1"]),
            Tabla("PRODUCTOS", Productos, ["CUAD-PRO", "10", "Cuaderno profesional", "19", "20"]),
            Tabla("BITACORA", ["ID", "TEXTO"], ["1", "Ajustó"]),
            Tabla("VENTATICKETS", ["ID", "FOLIO"], ["70", "4522"]));

        var lineas = RevisionEnVivo.Revisar(antes, despues);

        Assert.Contains("  ✗ el folio es 1000001 y el ID 412 (el POS pone el ID)", lineas);
        Assert.Contains("  ✗ la hora lleva milésimas (2026-03-09 12:00:05.430; el POS la escribe al segundo)", lineas);
        Assert.Contains("  ✗ el motivo es «Conteo» (el POS escribe «Ajuste de inventario #1000001»)", lineas);
        Assert.Contains("  ✗ la descripción es «Cuaderno» y el producto se llama «Cuaderno profesional»", lineas);
        Assert.Contains("  ✓ el costo es el del producto (PCOSTO)", lineas);
        Assert.Contains(lineas, l => l.StartsWith("  ✗ su renglón del kardex (INVENTARIO_HISTORIAL 900) difiere en la cantidad (3, el POS pondría la del ajuste con su signo, -3); "
            + "COSTO_DESPUES (19, el POS pone el mismo costo, 18.4); la hora ("));
        Assert.Contains("  ✗ la existencia quedó en 15 y lo anterior más la diferencia da 9 (el POS deja eso)", lineas);
        Assert.Contains("  · GEN_INVENTARIO_AJUSTES_ID avanzó 2 con 1 ajustes: algún número se quedó sin usar", lineas);
        // Las tablas de una venta pueden ser de otra caja en ese rato; las demás, no.
        Assert.Contains("· En el mismo rato también cambió VENTATICKETS (de una venta, una devolución o un corte): si no hubo ninguno, lo tocó el ajuste y hay que ver qué es.", lineas);
        Assert.Contains("✗ Eleventa también tocó PRODUCTOS, BITACORA, que el POS no escribe: hay que ver qué es.", lineas);
    }
}
