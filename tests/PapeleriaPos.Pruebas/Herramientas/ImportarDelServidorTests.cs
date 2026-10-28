using Microsoft.EntityFrameworkCore;
using PapeleriaPos.Eleventa;
using PapeleriaPos.Herramientas.Eleventa;
using PapeleriaPos.Herramientas.Importacion;
using PapeleriaPos.Herramientas.Usuarios;
using PapeleriaPos.Nucleo.Usuarios;
using PapeleriaPos.Pruebas.Datos;
using PapeleriaPos.Pruebas.Eleventa;
using static PapeleriaPos.Pruebas.Servicio.PilotoDePrueba;

namespace PapeleriaPos.Pruebas.Herramientas;

/// <summary>
/// <c>importar-eleventa --servidor</c> (revisión del PR #144): en la tienda, la base de la caja 2 se arma desde la caja 2, leyendo
/// por la red la copia del ensayo (PDVDATA-ENSAYO.FDB) en el Firebird de la caja principal; la caja 2 no tiene el Firebird
/// embebido. Lee todo en una sola transacción de solo lectura, sin escribirle nada, y nunca acepta la base en uso.
/// </summary>
public sealed class ImportarDelServidorTests
{
    [Theory]
    [InlineData(@"C:\Program Files (x86)\AbarrotesPDV\db\PDVDATA.FDB", true)]
    [InlineData(@"c:\abarrotespdv\db\pdvdata.fdb", true)]
    [InlineData("/firebird/data/PDVDATA.FDB", true)]
    [InlineData("PDVDATA", true)]
    [InlineData(@"C:\Program Files (x86)\AbarrotesPDV\db\PDVDATA-ENSAYO.FDB", false)]
    [InlineData(@"D:\copias\PDVDATA.FDB.ANTES", false)]
    [InlineData("ENSAYO", false)]
    public void Nunca_la_base_con_la_que_vende_eleventa(string ruta, bool enUso) => Assert.Equal(enUso, LectorEleventa.EsLaBaseEnUso(ruta));

    /// <summary>
    /// Por la red, como habla el Firebird 2.0.1 de la tienda (el protocolo 10): el catálogo, el historial y los usuarios llegan, la copia
    /// queda igual que antes de leerla, y la base del POS se arma con el dueño elegido (con su PIN) y los demás sin PIN.
    /// </summary>
    [FactConFirebird]
    public async Task Lee_la_copia_por_la_red_sin_escribirle_nada_y_arma_la_base_con_el_duenio()
    {
        var (e, _) = await EleventaAsync(abierta: false);
        await using var eleventa = e;
        await e.EjecutarAsync("INSERT INTO USUARIOS (ID, NOMBRE_COMPLETO, USUARIO, ACTIVO) VALUES (3, 'Luis Pérez', 'luis', '1')");
        var vendido = HoraLocal(30);
        await e.EjecutarAsync("""
            INSERT INTO VENTATICKETS (FOLIO, CAJA_ID, CAJERO_ID, NOMBRE, CREADO_EN, SUBTOTAL, TOTAL, GANANCIA, ESTA_ABIERTO, VENDIDO_EN, PAGO_CON,
                NUMERO_ARTICULOS, PAGADO_EN, OPERACION_ID, FORMA_PAGO, TOTAL_FACTURABLE)
            VALUES (77, 1, 7, 'Ticket 1', @vendido, 27, 27, 15, 'f', @vendido, 30, 3, @vendido, -1, 'e', 27)
            """, ("vendido", vendido));
        var ticket = Convert.ToInt32(await e.ValorAsync("SELECT ID FROM VENTATICKETS WHERE FOLIO = 77"));
        await e.EjecutarAsync("""
            INSERT INTO VENTATICKETS_ARTICULOS (TICKET_ID, PRODUCTO_CODIGO, PRODUCTO_NOMBRE, CANTIDAD, GANANCIA, DEPARTAMENTO_ID, PAGADO_EN, PRECIO_USADO,
                PRECIO_FINAL, TOTAL_ARTICULO)
            VALUES (@ticket, 'PLU-FINA', 'Pluma punto fino', 3, 5, 5, @vendido, 9, 9, 27)
            """, ("ticket", ticket), ("vendido", vendido));
        using var proxy = new ProxyFirebird(e.Servidor.Servidor, e.Servidor.Puerto) { SoloProtocolo10 = true };
        var antes = await FotoEleventa.TomarAsync(e.Conexion);

        var datos = await LectorEleventa.LeerAsync(e.Servidor with { Servidor = "127.0.0.1", Puerto = proxy.Puerto });

        Assert.Equal("Sin cambios.", ComparacionEleventa.Comparar(antes, await FotoEleventa.TomarAsync(e.Conexion)));
        Assert.Equal(["PLU-FINA"], datos.Productos.Select(p => p.Codigo));
        Assert.Equal([3, 7], datos.Usuarios!.Select(u => u.Id).Order());
        var importados = ImportadorEleventa.Importar(datos);
        var venta = Assert.Single(importados.Ventas, v => v.FolioOrigen == "77");
        Assert.Equal(2700, venta.TotalCentavos);

        // La base del POS, como la arma importar-eleventa --duenio luis.
        await using var pos = await BaseEnMemoria.CrearAsync();
        await using (var db = pos.Contexto())
            await GuardadoLote.GuardarAsync(importados, db);
        var (duenio, _) = UsuariosDeLaTienda.Elegir(datos.Usuarios!, "luis");
        await using (var db = pos.Contexto())
            await UsuariosDeLaTienda.ImportarAsync(db, importados.Matriz.Id, datos.Usuarios!, duenio, "4821");
        await using var leida = pos.Contexto();
        var usuarios = await leida.Usuarios.AsNoTracking().OrderBy(u => u.EleventaId).ToListAsync();
        Assert.Equal([("Luis Pérez", true, true), ("Ana Ruiz", false, false)], usuarios.Select(u => (u.Nombre, u.TienePin, u.PrimeraCuenta)));
        Assert.Equal(ResultadoPin.Correcto, usuarios[0].VerificarPin("4821", DateTime.UtcNow));
    }
}
