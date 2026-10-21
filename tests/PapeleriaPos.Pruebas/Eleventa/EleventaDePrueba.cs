using FirebirdSql.Data.FirebirdClient;
using PapeleriaPos.Eleventa;

namespace PapeleriaPos.Pruebas.Eleventa;

/// <summary>
/// Una prueba contra un Firebird de verdad. Solo corre donde hay uno de prueba en esta misma PC (nunca el de la tienda),
/// dicho en la variable <c>PRUEBAS_FIREBIRD</c> como «servidor:puerto» (por ejemplo «127.0.0.1:3055»); la clave va en
/// <c>PRUEBAS_FIREBIRD_CLAVE</c> o es la de omisión de Firebird. En el CI no hay: ahí se salta.
/// <para>
/// Las bases de prueba las crea el servidor, en su propio disco: en la carpeta temporal de esta PC, o en la que diga
/// <c>PRUEBAS_FIREBIRD_CARPETA</c> si el Firebird corre en un contenedor (por ejemplo «/firebird/data» con
/// jacobalberty/firebird:2.5-ss, publicado solo en 127.0.0.1).
/// </para>
/// </summary>
public sealed class FactConFirebirdAttribute : FactAttribute
{
    public FactConFirebirdAttribute()
    {
        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("PRUEBAS_FIREBIRD")))
            Skip = "Sin Firebird de prueba (variable PRUEBAS_FIREBIRD).";
    }
}

/// <summary>
/// Una base nueva y vacía con las tablas de eleventa (esquema 216) que toca una venta, con sus mismos tipos, contadores
/// y triggers. Los productos, departamentos y cajas los pone cada prueba; se borra al terminar.
/// </summary>
public sealed class EleventaDePrueba : IAsyncDisposable
{
    private readonly string _cadena;

    public ServidorEleventa Servidor { get; }
    public FbConnection Conexion { get; }

    private EleventaDePrueba(ServidorEleventa servidor, FbConnection conexion)
    {
        Servidor = servidor;
        Conexion = conexion;
        _cadena = servidor.Cadena();
    }

    public static async Task<EleventaDePrueba> CrearAsync()
    {
        var variable = Environment.GetEnvironmentVariable("PRUEBAS_FIREBIRD")!.Split(':');
        var nombre = $"eleventa-prueba-{Guid.NewGuid():N}.fdb";
        var archivo = Environment.GetEnvironmentVariable("PRUEBAS_FIREBIRD_CARPETA") is { Length: > 0 } carpeta
            ? $"{carpeta.TrimEnd('/', '\\')}/{nombre}"
            : Path.Combine(Path.GetTempPath(), nombre);
        var servidor = new ServidorEleventa(variable[0], archivo, "SYSDBA",
            Environment.GetEnvironmentVariable("PRUEBAS_FIREBIRD_CLAVE") is { Length: > 0 } clave ? clave : "masterkey",
            variable.Length > 1 ? int.Parse(variable[1]) : 3050);
        await FbConnection.CreateDatabaseAsync(servidor.Cadena(), pageSize: 4096, forcedWrites: false);
        var conexion = new FbConnection(servidor.Cadena());
        await conexion.OpenAsync();
        foreach (var sentencia in Esquema)
            await EjecutarAsync(conexion, sentencia);
        return new EleventaDePrueba(servidor, conexion);
    }

    public Task EjecutarAsync(string sql, params (string Nombre, object? Valor)[] parametros) => EjecutarAsync(Conexion, sql, parametros);

    public async Task<object?> ValorAsync(string sql, params (string Nombre, object? Valor)[] parametros)
    {
        await using var comando = Comando(Conexion, sql, parametros);
        return await comando.ExecuteScalarAsync() is { } valor and not DBNull ? valor : null;
    }

    public async Task<List<Dictionary<string, object?>>> FilasAsync(string sql, params (string Nombre, object? Valor)[] parametros)
    {
        await using var comando = Comando(Conexion, sql, parametros);
        await using var lector = await comando.ExecuteReaderAsync();
        var filas = new List<Dictionary<string, object?>>();
        while (await lector.ReadAsync())
        {
            var fila = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
            for (var i = 0; i < lector.FieldCount; i++)
                fila[lector.GetName(i)] = lector.IsDBNull(i) ? null : lector.GetValue(i);
            filas.Add(fila);
        }
        return filas;
    }

    public async Task ProductoAsync(int id, string codigo, string nombre, int depto, double costo, double precio, double existencia, char tipoVenta = 'U')
    {
        await EjecutarAsync("""
            INSERT INTO PRODUCTOS (ID, CODIGO, DESCRIPCION, TVENTA, PCOSTO, PVENTA, DEPT, PFINAL, USA_INVENTARIO)
            VALUES (@id, @codigo, @nombre, @tventa, @costo, @precio, @depto, @precio, 't')
            """, ("id", id), ("codigo", codigo), ("nombre", nombre), ("tventa", tipoVenta.ToString()), ("costo", costo), ("precio", precio), ("depto", depto));
        await EjecutarAsync("INSERT INTO INVENTARIO_BALANCES (PRODUCTO_ID, CANTIDAD_ACTUAL, ALMACEN_ID) VALUES (@id, @c, 1)", ("id", id), ("c", existencia));
    }

    /// <summary>
    /// Eleventa abre la caja (docs/migracion-eleventa.md, «Al abrir la caja»): la hora en <c>CAJAS</c>, el renglón del día
    /// en ceros si no existe, el turno con su fondo y su primer ticket abierto. En el piloto el turno lo abre eleventa y la
    /// caja nueva escribe en él (CorteEnElPiloto). Regresa el <c>TURNOS.ID</c>.
    /// </summary>
    public async Task<int> AbrirCajaAsync(int caja, int cajero, DateTime inicio, decimal fondo)
    {
        await EjecutarAsync("UPDATE CAJAS SET ULTIMO_INGRESO = @inicio WHERE ID = @caja", ("inicio", inicio), ("caja", caja));
        if (await ValorAsync("SELECT ID FROM CORTE_OPERACIONES WHERE DIA = @dia AND ID_CAJA = @caja", ("dia", inicio.Date), ("caja", caja)) is null)
            await EjecutarAsync($"INSERT INTO CORTE_OPERACIONES (DIA, ID_CAJA, {string.Join(", ", AcumuladosDelDia)}) VALUES (@dia, @caja, {Ceros(AcumuladosDelDia)})",
                ("dia", inicio.Date), ("caja", caja));
        var dia = Convert.ToInt32(await ValorAsync("SELECT ID FROM CORTE_OPERACIONES WHERE DIA = @dia AND ID_CAJA = @caja", ("dia", inicio.Date), ("caja", caja)));
        await EjecutarAsync($"""
            INSERT INTO TURNOS (ID_CAJA, ID_OPERACION_INICIO, ID_CAJERO, INICIO_EN, DINERO_INICIAL, SOSPECHOSO, {string.Join(", ", AcumuladosDelTurno)})
            VALUES (@caja, @dia, @cajero, @inicio, @fondo, 'F', {Ceros(AcumuladosDelTurno)})
            """, ("caja", caja), ("dia", dia), ("cajero", cajero), ("inicio", inicio), ("fondo", fondo));
        var turno = Convert.ToInt32(await ValorAsync("SELECT ID FROM TURNOS WHERE ID_CAJA = @caja AND TERMINO_EN IS NULL", ("caja", caja)));
        await EjecutarAsync("INSERT INTO VENTATICKETS (CAJA_ID, CAJERO_ID, NOMBRE, CREADO_EN, OPERACION_ID, TURNO_ID) VALUES (@caja, @cajero, 'Ticket 1', @inicio, -1, @turno)",
            ("caja", caja), ("cajero", cajero), ("inicio", inicio), ("turno", turno));
        return turno;
    }

    /// <summary>El corte en eleventa, lo que importa aquí: el turno termina y se borra su ticket abierto.</summary>
    public async Task CortarAsync(int turno, DateTime termino)
    {
        await EjecutarAsync("UPDATE TURNOS SET TERMINO_EN = @termino WHERE ID = @turno", ("termino", termino), ("turno", turno));
        await EjecutarAsync("DELETE FROM VENTATICKETS WHERE TURNO_ID = @turno AND ESTA_ABIERTO = 't'", ("turno", turno));
    }

    /// <summary>Los acumulados que eleventa deja en ceros al abrir un turno (los mismos que el puente, EnvioEleventa).</summary>
    private static readonly string[] AcumuladosDelTurno =
    [
        "ACUMULADO_VENTAS", "ACUMULADO_ENTRADAS", "ACUMULADO_SALIDAS", "ACUMULADO_GANANCIA", "VENTAS_EFECTIVO", "VENTAS_TARJETA",
        "VENTAS_VALES", "VENTAS_CREDITO", "EFECTIVO_AL_CIERRE", "ABONOS_EFECTIVO", "DEVOLUCIONES_VENTAS_EFECTIVO",
        "DEVOLUCIONES_VENTAS_CREDITO", "DEVOLUCIONES_ABONOS_EFECTIVO", "NUMERO_VENTAS", "DEVOLUCIONES_VENTAS_VALES", "ABONOS_TARJETA",
        "ABONOS_VALES", "COMISIONES_TARJETA", "DEVOLUCIONES_VENTAS_TARJETA", "VENTAS_TRANSFERENCIA", "ABONOS_TRANSFERENCIA",
        "VENTAS_CHEQUE", "ABONOS_CHEQUE", "DEVOLUCIONES_VENTAS_TRANS", "DEVOLUCIONES_VENTAS_CHEQUE",
    ];

    /// <summary>Los del día y la caja: los mismos, menos el efectivo al cierre.</summary>
    private static readonly string[] AcumuladosDelDia = [.. AcumuladosDelTurno.Where(c => c != "EFECTIVO_AL_CIERRE")];

    private static string Ceros(string[] columnas) => string.Join(", ", columnas.Select(_ => "0"));

    /// <summary>
    /// Cierra la conexión y borra la base. Antes de borrarla se sueltan las conexiones guardadas de esta base, nunca las de
    /// todas (<c>FbConnection.ClearAllPools</c>), como en las de SQLite (<see cref="ArchivosDePrueba"/>). Hoy ninguna se
    /// guarda (<see cref="ServidorEleventa.Cadena"/> va sin pool), así que no le movía nada a las demás pruebas; esto solo
    /// evita que lo haga si un día se guardan.
    /// </summary>
    public async ValueTask DisposeAsync()
    {
        await Conexion.DisposeAsync();
        FbConnection.ClearPool(_cadena);
        await FbConnection.DropDatabaseAsync(_cadena);
    }

    private static async Task EjecutarAsync(FbConnection c, string sql, params (string Nombre, object? Valor)[] parametros)
    {
        await using var comando = Comando(c, sql, parametros);
        await comando.ExecuteNonQueryAsync();
    }

    private static FbCommand Comando(FbConnection c, string sql, (string Nombre, object? Valor)[] parametros)
    {
        var comando = new FbCommand(sql, c);
        foreach (var (nombre, valor) in parametros)
            comando.Parameters.AddWithValue(nombre, valor ?? DBNull.Value);
        return comando;
    }

    private static readonly string[] Generadores =
    [
        "GEN_VENTATICKETS_ID", "GEN_VENTATICKETS_ARTICULOS_ID", "GEN_INVENTARIO_HISTORIAL_ID", "GEN_TURNOS_ID",
        "GEN_CORTE_OPERACIONES_ID", "GEN_CORTE_VENTAS_POR_DEPTO_ID", "GEN_CORTE_VENTAS_DE_ID", "GEN_DEVOLUCIONES_ID",
        "GEN_DEVOLUCIONES_ARTICULOS_ID", "GEN_CORTE_MOVIMIENTOS_ID",
    ];

    private static readonly (string Tabla, string Generador)[] Triggers =
    [
        ("VENTATICKETS", "GEN_VENTATICKETS_ID"), ("VENTATICKETS_ARTICULOS", "GEN_VENTATICKETS_ARTICULOS_ID"),
        ("INVENTARIO_HISTORIAL", "GEN_INVENTARIO_HISTORIAL_ID"), ("TURNOS", "GEN_TURNOS_ID"),
        ("CORTE_OPERACIONES", "GEN_CORTE_OPERACIONES_ID"), ("CORTE_VENTAS_POR_DEPTO", "GEN_CORTE_VENTAS_POR_DEPTO_ID"),
        ("CORTE_VENTAS_DEPTO_OPERACIONES", "GEN_CORTE_VENTAS_DE_ID"), ("DEVOLUCIONES", "GEN_DEVOLUCIONES_ID"),
        ("DEVOLUCIONES_ARTICULOS", "GEN_DEVOLUCIONES_ARTICULOS_ID"), ("CORTE_MOVIMIENTOS", "GEN_CORTE_MOVIMIENTOS_ID"),
    ];

    // Los tipos son los dominios de eleventa: montos y cantidades en FLOAT, acumulados del corte en NUMERIC(18,4).
    private static readonly string[] Esquema =
    [
        "CREATE DOMAIN TLLAVE AS INTEGER",
        "CREATE DOMAIN TCANTIDAD AS FLOAT",
        "CREATE DOMAIN TMONEDA AS FLOAT",
        "CREATE DOMAIN TCURRENCY AS NUMERIC(18, 4)",
        "CREATE DOMAIN TBOOLEANO AS CHAR(1)",
        "CREATE DOMAIN TFECHAHORA AS TIMESTAMP",
        "CREATE DOMAIN TCADENAMEDIANA AS VARCHAR(50)",
        "CREATE DOMAIN TCADENALARGA AS VARCHAR(255)",
        "CREATE DOMAIN TCODIGOPRODUCTO AS VARCHAR(20)",
        "CREATE DOMAIN TNOTA AS BLOB SUB_TYPE 0 SEGMENT SIZE 80",
        "CREATE DOMAIN TFORMAPAGO AS CHAR(10)",
        "CREATE DOMAIN TINTEGER AS INTEGER",
        "CREATE DOMAIN TFOLIO AS INTEGER",
        "CREATE DOMAIN TMONEDA_4_DECIMALES AS INTEGER",
        "CREATE DOMAIN TCODIGOMONEDA AS VARCHAR(3)",
        "CREATE DOMAIN TCANTIDADINV AS DOUBLE PRECISION",
        "CREATE DOMAIN TENUMERADO AS CHAR(1)",
        // De los usuarios, solo lo que lee la caja nueva: el nombre con que eleventa anota quién devolvió.
        "CREATE TABLE USUARIOS (ID TLLAVE NOT NULL, NOMBRE_COMPLETO TCADENALARGA, USUARIO TCADENAMEDIANA NOT NULL, CONSTRAINT PK_USUARIOS PRIMARY KEY (ID))",
        "CREATE TABLE INVENTARIO_APARTADO (PRODUCTO_ID TLLAVE NOT NULL, CANTIDAD_APARTADA TCANTIDAD NOT NULL, TICKET_ID TLLAVE, CAJA_ID TLLAVE)",
        """
        CREATE TABLE DEVOLUCIONES (ID TLLAVE NOT NULL, TURNO_ID TLLAVE, TICKET_ID TLLAVE, TIPO_DEVOLUCION TBOOLEANO, PAGADO_EN TFECHAHORA,
            DEVUELTO_EN TFECHAHORA, CAJERO TCADENAMEDIANA, CAJA TCADENAMEDIANA, TOTAL_DEVUELTO TMONEDA, SINCRONIZADO_EN TFECHAHORA,
            CONSTRAINT PK_DEVOLUCIONES PRIMARY KEY (ID))
        """,
        """
        CREATE TABLE DEVOLUCIONES_ARTICULOS (ID TLLAVE NOT NULL, DEVOLUCION_ID TLLAVE NOT NULL, TICKET_ID TLLAVE, CODIGO_PRODUCTO TCODIGOPRODUCTO,
            DESCRIPCION_PRODUCTO VARCHAR(255), CANTIDAD_DEVUELTA TCANTIDADINV, DINERO_DEVUELTO TMONEDA, INVENTARIO_POST_DEVOLUCION TCANTIDADINV,
            CONSTRAINT PK_DEVOLUCIONES_ARTICULOS PRIMARY KEY (ID))
        """,
        """
        CREATE TABLE CORTE_MOVIMIENTOS (ID TLLAVE NOT NULL, BORRADO_EL TFECHAHORA, CUANDO_FUE TFECHAHORA, MONTO TCURRENCY NOT NULL,
            DESCRIPCION TCADENALARGA NOT NULL, TIPO TCADENAMEDIANA, ID_TURNO TLLAVE, FORMA_PAGO TENUMERADO, RAZON_DEVOLUCION TENUMERADO, ID_CAJA TLLAVE,
            CONSTRAINT PK_CORTE_MOVIMIENTOS PRIMARY KEY (ID))
        """,
        """
        CREATE TABLE CAJAS (ID TLLAVE NOT NULL, NOMBRE TCADENAMEDIANA, ULTIMA_IP TCADENAMEDIANA NOT NULL, ULTIMO_INGRESO TFECHAHORA,
            NOMBRE_PC VARCHAR(255), ELIMINADA_EN TFECHAHORA, ES_PRINCIPAL TBOOLEANO DEFAULT 'F', TERMINAL_ID VARCHAR(40),
            CONSTRAINT PK_CAJAS PRIMARY KEY (ID))
        """,
        """
        CREATE TABLE DEPARTAMENTOS (ID TLLAVE NOT NULL, NOMBRE VARCHAR(255) NOT NULL, PORCENTAJE_IMPUESTO SMALLINT DEFAULT 0,
            ACTIVO TBOOLEANO DEFAULT 1, CONSTRAINT PK_DEPARTAMENTOS PRIMARY KEY (ID))
        """,
        """
        CREATE TABLE PRODUCTOS (ID TLLAVE NOT NULL, CODIGO TCADENAMEDIANA NOT NULL, DESCRIPCION VARCHAR(255), TVENTA CHAR(1) NOT NULL,
            PCOSTO TMONEDA, PVENTA TMONEDA, DEPT TLLAVE, MAYOREO TMONEDA, MEDIDA_ID TLLAVE, PFINAL TMONEDA DEFAULT 0,
            PMAYOREOFINAL TMONEDA DEFAULT 0, ES_KIT TBOOLEANO DEFAULT 'f', USA_INVENTARIO TBOOLEANO DEFAULT 'f', ELIMINADO_EN TFECHAHORA,
            CONSTRAINT PK_PRODUCTOS PRIMARY KEY (CODIGO))
        """,
        "CREATE TABLE INVENTARIO_BALANCES (PRODUCTO_ID TLLAVE NOT NULL, CANTIDAD_ACTUAL TCANTIDAD, ALMACEN_ID TLLAVE, UNIQUE (PRODUCTO_ID))",
        // De las promociones por cantidad, solo lo que lee la lectura de cada pocos minutos (LectorEleventa).
        """
        CREATE TABLE PROMOCIONES_POR_CANTIDAD (ID TLLAVE NOT NULL, PRODUCTO_CODIGO TCODIGOPRODUCTO NOT NULL, DESDE TCANTIDAD,
            PRECIO_PROMOCION_CON_IMPUESTOS TMONEDA, CONSTRAINT PK_PROMOCIONES_POR_CANTIDAD PRIMARY KEY (ID))
        """,
        """
        CREATE TABLE INVENTARIO_HISTORIAL (ID TLLAVE NOT NULL, PRODUCTO_ID TLLAVE NOT NULL, CUANDO_FUE TFECHAHORA NOT NULL,
            CANTIDAD_ANTERIOR TCANTIDAD NOT NULL, CANTIDAD TCANTIDAD NOT NULL, DESCRIPCION TCADENALARGA, COSTO_UNITARIO TMONEDA,
            COSTO_DESPUES TMONEDA, AJUSTE_ID TLLAVE, RECIBO_INVENTARIO_ID TLLAVE, VENTA_ID TLLAVE, TRANSFERENCIA_ID TLLAVE, CAJA_ID TLLAVE,
            VENTA_POR_KIT TBOOLEANO, USUARIO_ID TLLAVE, ALMACEN_ID TLLAVE, PRIMARY KEY (ID))
        """,
        """
        CREATE TABLE VENTATICKETS (ID TLLAVE NOT NULL, FOLIO TFOLIO, CAJA_ID TLLAVE NOT NULL, CAJERO_ID TLLAVE NOT NULL,
            NOMBRE TCADENAMEDIANA, CREADO_EN TFECHAHORA, SUBTOTAL TMONEDA DEFAULT 0, IMPUESTOS TMONEDA DEFAULT 0, TOTAL TMONEDA DEFAULT 0,
            GANANCIA TMONEDA DEFAULT 0, ESTA_ABIERTO TBOOLEANO DEFAULT 't', CLIENTE_ID TLLAVE, VENDIDO_EN TFECHAHORA,
            ES_MODIFICABLE TBOOLEANO DEFAULT 't', PAGO_CON TMONEDA, MONEDA TCODIGOMONEDA, NUMERO_ARTICULOS TINTEGER DEFAULT 0,
            PAGADO_EN TFECHAHORA, ESTA_CANCELADO TBOOLEANO DEFAULT 'f', OPERACION_ID TLLAVE NOT NULL, OLD_TICKET_ID TLLAVE, NOTAS TNOTA,
            IMPRIMIR_NOTA TBOOLEANO DEFAULT 't', FORMA_PAGO TFORMAPAGO, REFERENCIA TCADENALARGA, FACTURA_ID TLLAVE,
            TOTAL_DEVUELTO TMONEDA DEFAULT 0, TOTAL_AHORRADO TMONEDA DEFAULT 0 NOT NULL, TURNO_ID TLLAVE, TIPO_DE_CAMBIO TMONEDA DEFAULT 1,
            TOTAL_CREDITO TMONEDA DEFAULT 0, NOTAS_AL_PIE TNOTA DEFAULT NULL, REFRESCAR_TICKET TBOOLEANO DEFAULT 'F',
            TOTAL_FACTURABLE TMONEDA DEFAULT 0 NOT NULL, CLIENTESV2_ID INTEGER DEFAULT 1 NOT NULL, CLIENTESV2_CREDITO_ID INTEGER,
            ACTIVO CHAR(1) DEFAULT 't', IMPRIMIR_DATOS_CLIENTE CHAR(1), SALDO_CREDITO TMONEDA DEFAULT 0 NOT NULL,
            IMPUESTOS_RETENIDOS TMONEDA DEFAULT 0 NOT NULL, CONSTRAINT PK_VENTATICKETS PRIMARY KEY (ID))
        """,
        "CREATE DESCENDING INDEX IX_FOLIO ON VENTATICKETS (FOLIO)",
        """
        CREATE TABLE VENTATICKETS_ARTICULOS (ID TLLAVE NOT NULL, TICKET_ID TLLAVE NOT NULL, PRODUCTO_CODIGO TCODIGOPRODUCTO NOT NULL,
            PRODUCTO_NOMBRE TCADENALARGA NOT NULL, CANTIDAD TCANTIDAD NOT NULL, GANANCIA TMONEDA, DEPARTAMENTO_ID TLLAVE,
            PAGADO_EN TFECHAHORA, USA_MAYOREO TBOOLEANO DEFAULT 'f', PORCENTAJE_DESCUENTO TCANTIDAD, COMPONENTES TCADENALARGA,
            IMPUESTOS_USADOS TCADENALARGA, IMPUESTO_UNITARIO TMONEDA, PRECIO_USADO TMONEDA, CANTIDAD_DEVUELTA TCANTIDAD DEFAULT 0,
            FUE_DEVUELTO TBOOLEANO DEFAULT 'f', PORCENTAJE_PAGADO TMONEDA_4_DECIMALES DEFAULT 0, PRECIO_FINAL TMONEDA DEFAULT 0,
            AGREGADO_EN TFECHAHORA, TOTAL_ARTICULO TMONEDA DEFAULT NULL, CONSTRAINT PK_VENTATICKETS_ARTICULOS PRIMARY KEY (ID))
        """,
        """
        CREATE TABLE CORTE_OPERACIONES (ID TLLAVE NOT NULL, DIA TFECHAHORA NOT NULL, ID_CAJA TLLAVE, ACUMULADO_VENTAS TCURRENCY,
            ACUMULADO_ENTRADAS TCURRENCY, ACUMULADO_SALIDAS TCURRENCY, ACUMULADO_GANANCIA TCURRENCY, VENTAS_EFECTIVO TCURRENCY,
            VENTAS_TARJETA TCURRENCY, VENTAS_VALES TCURRENCY, VENTAS_CREDITO TCURRENCY, ABONOS_EFECTIVO TCURRENCY,
            DEVOLUCIONES_VENTAS_EFECTIVO TCURRENCY, DEVOLUCIONES_VENTAS_CREDITO TCURRENCY, DEVOLUCIONES_ABONOS_EFECTIVO TCURRENCY,
            NUMERO_VENTAS TINTEGER DEFAULT 0, DEVOLUCIONES_VENTAS_VALES TCURRENCY, ABONOS_TARJETA TCURRENCY DEFAULT 0,
            ABONOS_VALES TCURRENCY DEFAULT 0, COMISIONES_TARJETA TMONEDA DEFAULT 0,
            DEVOLUCIONES_VENTAS_TARJETA NUMERIC(10, 4) DEFAULT 0.0000 NOT NULL, VENTAS_TRANSFERENCIA NUMERIC(18, 4) DEFAULT 0,
            ABONOS_TRANSFERENCIA NUMERIC(18, 4) DEFAULT 0, VENTAS_CHEQUE NUMERIC(18, 4) DEFAULT 0, ABONOS_CHEQUE NUMERIC(18, 4) DEFAULT 0,
            DEVOLUCIONES_VENTAS_TRANS NUMERIC(18, 4) DEFAULT 0, DEVOLUCIONES_VENTAS_CHEQUE NUMERIC(18, 4) DEFAULT 0,
            CONSTRAINT PK_CORTE_OPERACIONES PRIMARY KEY (ID), CONSTRAINT UNQ1_CORTE_OPERACIONES UNIQUE (DIA, ID_CAJA))
        """,
        """
        CREATE TABLE TURNOS (ID TLLAVE NOT NULL, ID_CAJA TLLAVE NOT NULL, ID_OPERACION_INICIO TLLAVE NOT NULL, ID_OPERACION_FIN TLLAVE,
            ID_CAJERO TLLAVE, INICIO_EN TFECHAHORA NOT NULL, TERMINO_EN TFECHAHORA, DINERO_INICIAL TCURRENCY NOT NULL,
            SOSPECHOSO CHAR(1) NOT NULL, ACUMULADO_VENTAS TCURRENCY NOT NULL, ACUMULADO_ENTRADAS TCURRENCY,
            ACUMULADO_SALIDAS TCURRENCY DEFAULT 0, ACUMULADO_GANANCIA TCURRENCY, VENTAS_EFECTIVO TCURRENCY, VENTAS_TARJETA TCURRENCY,
            VENTAS_VALES TCURRENCY, VENTAS_CREDITO TCURRENCY, EFECTIVO_AL_CIERRE TCURRENCY, ABONOS_EFECTIVO TCURRENCY,
            DEVOLUCIONES_VENTAS_EFECTIVO TCURRENCY, DEVOLUCIONES_VENTAS_CREDITO TCURRENCY, DEVOLUCIONES_ABONOS_EFECTIVO TCURRENCY,
            NUMERO_VENTAS TINTEGER, DEVOLUCIONES_VENTAS_VALES TCURRENCY, ABONOS_TARJETA TCURRENCY DEFAULT 0, ABONOS_VALES TCURRENCY DEFAULT 0,
            COMISIONES_TARJETA TMONEDA DEFAULT 0, SINCRONIZADO_EN TFECHAHORA, DEVOLUCIONES_VENTAS_TARJETA NUMERIC(10, 4) DEFAULT 0.0000 NOT NULL,
            VENTAS_TRANSFERENCIA NUMERIC(18, 4) DEFAULT 0, ABONOS_TRANSFERENCIA NUMERIC(18, 4) DEFAULT 0, VENTAS_CHEQUE NUMERIC(18, 4) DEFAULT 0,
            ABONOS_CHEQUE NUMERIC(18, 4) DEFAULT 0, DEVOLUCIONES_VENTAS_TRANS NUMERIC(18, 4) DEFAULT 0,
            DEVOLUCIONES_VENTAS_CHEQUE NUMERIC(18, 4) DEFAULT 0,
            CONSTRAINT PK_TURNOS PRIMARY KEY (ID), CONSTRAINT UNQ_TURNO_ABIERTO_CAJA UNIQUE (ID_CAJA, TERMINO_EN))
        """,
        """
        CREATE TABLE CORTE_VENTAS_POR_DEPTO (ID TLLAVE NOT NULL, ID_DEPARTAMENTO TLLAVE, ACUMULADO_VENTAS TMONEDA, ID_TURNO TLLAVE,
            ACUMULADO_GANANCIAS TCURRENCY DEFAULT 0, CONSTRAINT PK_CORTE_VENTAS_POR_DEPTO PRIMARY KEY (ID))
        """,
        """
        CREATE TABLE CORTE_VENTAS_DEPTO_OPERACIONES (ID TLLAVE NOT NULL, ID_DEPARTAMENTO TLLAVE, ACUMULADO_VENTAS TMONEDA, ID_OPERACION TLLAVE,
            ACUMULADO_GANANCIAS TCURRENCY DEFAULT 0, CONSTRAINT PK_CORTE_VENTAS_DEPTO_OPERACION PRIMARY KEY (ID))
        """,
        .. Generadores.Select(g => $"CREATE GENERATOR {g}"),
        .. Triggers.Select(t => $"""
            CREATE TRIGGER {t.Generador[4..^3]}_BI FOR {t.Tabla} ACTIVE BEFORE INSERT POSITION 0
            AS BEGIN IF (NEW.ID IS NULL) THEN NEW.ID = GEN_ID({t.Generador}, 1); END
            """),
    ];
}
