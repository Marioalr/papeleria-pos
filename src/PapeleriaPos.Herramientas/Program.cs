using Microsoft.EntityFrameworkCore;
using System.Globalization;
using PapeleriaPos.Eleventa;
using PapeleriaPos.Datos;
using PapeleriaPos.Herramientas;
using PapeleriaPos.Herramientas.Diagnostico;
using PapeleriaPos.Herramientas.Eleventa;
using PapeleriaPos.Herramientas.Ficticios;
using PapeleriaPos.Herramientas.Importacion;
using PapeleriaPos.Herramientas.Prueba;
using PapeleriaPos.Herramientas.Simulador;
using PapeleriaPos.Herramientas.Tienda;
using PapeleriaPos.Impresion;
using PapeleriaPos.Impresiones.Datos;
using PapeleriaPos.Nucleo;
using PapeleriaPos.Nucleo.Catalogo;

CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("es-MX");

return args switch
{
    ["generar-ficticios", .. var resto] => await GenerarFicticiosAsync(resto),
    ["diagnostico", .. var resto] => await DiagnosticoAsync(resto),
    ["importar-eleventa", .. var resto] => await ImportarEleventaAsync(resto),
    ["sincronizar-eleventa", .. var resto] => await SincronizarEleventaAsync(resto),
    ["observar-eleventa", .. var resto] => await ObservarEleventaAsync(resto),
    ["probar-envio-eleventa", .. var resto] => await ProbarEnvioEleventaAsync(resto),
    ["preparar-caja", .. var resto] => await PrepararCajaAsync(resto),
    ["importar-control-print", .. var resto] => await ImportarControlPrintAsync(resto),
    ["recopilar-control-print", .. var resto] => await RecopilarControlPrintAsync(resto),
    ["aplicar-depuracion", .. var resto] => await AplicarDepuracionAsync(resto),
    ["preparar-prueba", .. var resto] => await PrepararPruebaAsync(resto),
    ["probar-impresora-tickets", .. var resto] => await ProbarImpresoraTicketsAsync(resto),
    ["probar-lector", ..] => ProbarLector(),
    ["simular-cajas", .. var resto] => await SimularCajasAsync(resto),
    _ => Ayuda(),
};

static int Ayuda()
{
    Console.WriteLine("""
        Herramientas de Papelería POS

          generar-ficticios [--salida ficticios.db] [--semilla 2026] [--reemplazar]
              Crea una base con una papelería inventada: catálogo, un año de ventas y existencias.

          diagnostico [--base ficticios.db] [--salida diagnostico.xlsx] [--desde 2025-10-01]
              Analiza una base y deja el resultado en Excel, una hoja por análisis. Con --desde, solo las
              ventas a partir de ese día (las existencias siempre son las actuales).

          importar-eleventa --fdb PDVDATA.FDB [--salida eleventa.db] [--reemplazar]
                            [--firebird32 <carpeta>] [--firebird64 <carpeta>]
              Importa una copia de la base de eleventa (catálogo, ventas y existencias). La original no se abre.
              Por omisión busca Firebird en firebird25-32\embed y firebird25, junto a la carpeta de la base.

          importar-eleventa --fdb PDVDATA.FDB --base pos.db [--simular] [--firebird32 <carpeta>] [--firebird64 <carpeta>]
              La importación final, al dejar eleventa: trae a la base de la tienda (la que se armó con importar-eleventa y
              donde corrió el piloto) el historial de ventas de eleventa, sin las ventas que mandó la caja 2 (ya están ahí
              como ventas, con sus devoluciones). Solo el historial: el catálogo ya está y las existencias salen del conteo.
              Se puede correr más de una vez: lo que ya estaba no se duplica y lo que cambió en eleventa se actualiza.
              Antes deja una copia de la base junto a ella. Dice qué hizo, qué no trajo y por qué, y lo que hay que
              revisar a mano. Con --simular dice qué haría, sin guardar nada.

          observar-eleventa --servidor <PC> --ruta <C:\...\PDVDATA.FDB> [--puerto 3050] --usuario <usuario>
                            [--carpeta <carpeta>]
              Para entender qué escribe eleventa (el piloto, paso 2): toma una foto de toda su base, solo leyendo;
              después de hacer algo en eleventa (cobrar, cancelar, un corte) toma otra y dice qué renglones aparecieron
              o cambiaron y qué contadores avanzaron. Si fue una cancelación, una devolución o un cierre de turno, dice
              también si eleventa hizo lo mismo que escribe la caja 2. Cada comparación queda en un archivo de --carpeta
              (por omisión, AppData\Local\PapeleriaPos\observaciones-eleventa, que no se sincroniza con OneDrive):
              son datos reales, no van al repositorio. scripts\observar-en-vivo.cmd lo abre ya configurado.

          probar-envio-eleventa --ruta <copia.fdb> [--puerto 3050] --usuario <usuario> --caja <ID> --cajero <ID>
                                --articulo <ID de eleventa>:<cantidad> [--comun <nombre>:<importe>] [--pago <pesos>]
              Manda una venta de prueba a una copia de eleventa como la mandará la caja 2 del piloto (paso 3) y dice
              qué cambió en la base, como observar-eleventa. Solo contra un Firebird en esta misma PC, nunca contra la
              caja principal. --articulo y --comun se pueden repetir; el artículo va al precio de eleventa.

          sincronizar-eleventa --base pos.db [--simular]
                               (--fdb PDVDATA.FDB [--firebird32 <carpeta>] [--firebird64 <carpeta>]
                                | --servidor <PC> --ruta <C:\...\PDVDATA.FDB> [--puerto 3050] --usuario <usuario>)
              Pone al día una base ya importada con lo que dice eleventa ahora (altas, precios, promociones,
              existencias), como lo hará la caja 2 del piloto cada pocos minutos. Con --fdb lee una copia; con
              --servidor, el Firebird de la caja principal por la red, solo leyendo. La clave se pide al correrlo
              (o va en la variable ELEVENTA_CLAVE). Con --simular dice qué cambiaría, sin guardar nada.

          importar-control-print --base pos.db [--control-print controlprint.db]
              Copia la configuración de costos de Control Print (tinta, papel, márgenes, calibración) a todas las
              sucursales, y la IP de su impresora si hay una sola sucursal. Sus trabajos no se importan. Por
              omisión lee la base de Control Print de esta PC.

          recopilar-control-print --servidor <IP o nombre> [--puerto 47815] [--salida <carpeta>] [--desde AAAA-MM-DD]
                                  [--miniaturas] [--huella <SHA-256 del certificado>]
              Junta los datos de Control Print para seguir con el módulo de impresión, SOLO LEYENDO por su red (no cobra, no
              cambia ni borra nada): los trabajos con todos sus campos, las copias, el resumen y las caras de cada día, las
              cotizaciones, las lecturas de la impresora, la configuración (costos y ajustes) y los usuarios (solo sus
              nombres); con --miniaturas, también la de cada trabajo en PNG. Pide el código de conexión (Control Print →
              Ajustes → Red, en la PC servidora) sin que se vea, o lo toma de la variable CONTROLPRINT_CODIGO; nunca lo
              escribe en pantalla ni en archivos. Deja una carpeta con la fecha en --salida (por omisión
              ~/.cache/control-print-datos, o Documentos\TPV\control-print-datos en Windows) con control-print.db y un
              README.md. Ctrl+C lo detiene: al volver a correrlo ese día, sigue donde se quedó.

          aplicar-depuracion --desde pos-anterior.db --base eleventa-nueva.db
              Repite en una importación nueva de eleventa lo que se decidió al depurar el catálogo en otra base (dar
              de baja, juntar, cambiar la unidad), buscando cada producto por su ID de eleventa.

          preparar-caja --base ficticios.db
              Da de alta la Caja 1 y usuarios de prueba (ver README) en una base de prueba.

          preparar-prueba [--salida prueba.db] [--ejemplos Ejemplos] [--reemplazar]
              La base de la prueba con una persona real: la papelería ficticia con la caja y los usuarios de prueba,
              un chat de Telegram simulado, impresiones por cobrar, y una factura XML y una nota PDF de ejemplo.

          probar-impresora-tickets --impresora <IP>[:puerto]
              Para el fin de semana en la tienda: qué hace la impresora de tickets que comparten las dos cajas cuando le
              llegan dos tickets a la vez. Manda dos tickets de prueba como los manda la caja (al puerto 9100), cada uno
              por su conexión: primero a la vez y después casi juntos. Dice cuándo terminó cada uno o qué error dio; en el
              papel se ve si salieron completos. No toca eleventa ni ninguna base. scripts\probar-impresora-tickets.cmd
              lo abre.

          probar-lector
              Para el fin de semana en la tienda: qué manda el lector y a qué ritmo. Con cada código escaneado dice qué
              llegó y el tiempo entre tecla y tecla, y si en el cobro la caja lo reconoce como el lector (caja.js,
              RAFAGA_MS). scripts\probar-lector.cmd lo abre.

          simular-cajas (--servicio <url> | --iniciar-servicio <PapeleriaPos.Servicio.exe o .dll> [--base prueba.db])
                        [--cajas 2] [--tickets 120] [--minutos 10] [--caida 30s@50%] [--caida-caja <n>] [--salida informe.md]
                        [--semilla 2026] [--donde "PC de desarrollo"] [--pid <proceso del servicio>] [--nucleos <n>] [--catalogo-por <n>]
                        [--dueno usuario:PIN] [--cajero usuario:PIN] [--encargado usuario:PIN]
              El día pico con varias cajas (docs/prueba-sucursales-linux.md): da de alta las cajas con el PIN del dueño (o usa
              las que ya había: «Caja simulada 1», «2»…) y cada una vende sus --tickets como la caja de verdad, sin la pantalla:
              con su copia del catálogo, su contingencia y su base local en una carpeta temporal, en --minutos (0: sin esperas).
              Con --caida, la red de una caja (la última, o --caida-caja) se cae a media jornada: «30s@50%», «2m@40%» o «5t@50%»
              (5 tickets de esa caja). Mide cada operación (la mitad, 19 de 20, 99 % y la peor) contra las metas, la memoria del
              servicio y la copia; hace el corte de cada turno y cuadra todo contra la caja principal. El informe sale en la
              consola y, con --salida, en un archivo.
              Con --iniciar-servicio arranca su propio servicio en esta PC, con una copia de --base (sin ella, arma la base de
              prueba como preparar-prueba), y lo detiene al terminar; con --nucleos, ese servicio usa solo esos núcleos (para
              parecerse a la caja principal en una PC con muchos), y con --catalogo-por 4 su catálogo es del tamaño del de la
              tienda (cada producto 4 veces). Con --servicio, solo uno de prueba (su api/estado lo dice):
              nunca el de la tienda ni uno en el piloto con eleventa. Los usuarios, por omisión, los de la base de prueba.
              Sale con 0 si cuadra, 1 si no cuadra o algo falló, 2 si no pudo empezar. scripts\simular-dia-pico.cmd lo abre.
        """);
    return 1;
}

static async Task<int> ImportarEleventaAsync(string[] args)
{
    if (Opcion(args, "--fdb") is not { } fdb || !File.Exists(fdb))
    {
        Console.Error.WriteLine("Indica la copia de la base de eleventa con --fdb (por ejemplo PDVDATA.FDB).");
        return 1;
    }
    var final = Opcion(args, "--base");
    if (final is not null && !File.Exists(final))
    {
        Console.Error.WriteLine($"No existe la base {final}. La importación final va a la base de la tienda (la que se armó con importar-eleventa).");
        return 1;
    }
    var salida = Opcion(args, "--salida") ?? "eleventa.db";
    if (final is null && !PrepararSalida(salida, args.Contains("--reemplazar")))
        return 1;

    var carpeta = Path.GetDirectoryName(Path.GetFullPath(fdb))!;
    var firebird = new FirebirdLocal(
        Opcion(args, "--firebird32") ?? Path.Combine(carpeta, "firebird25-32", "embed"),
        Opcion(args, "--firebird64") ?? Path.Combine(carpeta, "firebird25"));

    var eleventa = await LectorEleventa.LeerAsync(fdb, firebird, Console.WriteLine);
    if (final is not null)
        return await ImportacionFinalAsync(final, eleventa, args.Contains("--simular"));
    Console.WriteLine("Pasando al modelo del POS…");
    var datos = ImportadorEleventa.Importar(eleventa);

    await using (var db = new PosDbContext(BaseDeDatos.Opciones($"Data Source={salida}")))
    {
        await BaseDeDatos.PrepararAsync(db);
        await GuardadoLote.GuardarAsync(datos, db);
    }

    var lineas = datos.Ventas.Sum(v => v.Lineas.Count);
    var primera = datos.Ventas.Min(v => HoraMexico.ALocal(v.FechaUtc));
    var ultima = datos.Ventas.Max(v => HoraMexico.ALocal(v.FechaUtc));
    Console.WriteLine($"""

        Base creada: {Path.GetFullPath(salida)}

        Catálogo
          Departamentos   {datos.Departamentos.Count,8:N0}
          Productos       {datos.Productos.Count,8:N0}  ({datos.Productos.Count(p => p.Activo):N0} activos)
          Códigos         {datos.Codigos.Count,8:N0}
          Precios         {datos.Precios.Count,8:N0}

        Ventas ({primera:d} a {ultima:d})
          Tickets         {datos.Ventas.Count,8:N0}  ({datos.Ventas.Count(v => v.Cancelada):N0} cancelados)
          Líneas          {lineas,8:N0}
          Total           {datos.VentasCentavos / 100m,14:C0}

        Inventario
          Con existencia  {datos.Movimientos.Count,8:N0}
          Valor a costo   {datos.ValorInventarioCentavos / 100m,14:C0}
          Con mínimo      {datos.Variantes.Count(v => v.InventarioMinimo is not null || v.InventarioMaximo is not null),8:N0}  (o máximo: «Bajos en inventario»)
        """);
    if (datos.Avisos.Count > 0)
    {
        Console.WriteLine("\nPara revisar");
        foreach (var (aviso, veces) in datos.Avisos)
            Console.WriteLine($"  {veces,6:N0}  {aviso}");
    }
    return 0;
}

/// <summary>
/// La importación final en la base de la tienda (docs/migracion-eleventa.md, «La importación final»): todo en una transacción,
/// después de dejar una copia de la base junto a ella; con --simular se deshace al final.
/// </summary>
static async Task<int> ImportacionFinalAsync(string ruta, DatosEleventa eleventa, bool simular)
{
    if (!simular)
        Console.WriteLine($"Copia de la base antes de importar: {CopiarAntesDeImportar(ruta)}");
    Console.WriteLine("Comparando con lo que ya tiene la base…");
    await using var db = new PosDbContext(BaseDeDatos.Opciones($"Data Source={ruta}"));
    await BaseDeDatos.PrepararAsync(db);
    await using var transaccion = await db.Database.BeginTransactionAsync();
    ResultadoImportacionFinal resultado;
    try
    {
        resultado = await ImportacionFinal.AplicarAsync(db, eleventa);
    }
    catch (InvalidOperationException ex)
    {
        Console.Error.WriteLine(ex.Message);
        return 1;
    }
    if (simular)
        await transaccion.RollbackAsync();
    else
        await transaccion.CommitAsync();

    Console.WriteLine();
    Console.WriteLine($"{(simular ? "Simulación de la importación final" : "Importación final")} en {Path.GetFullPath(ruta)}");
    Console.WriteLine();
    foreach (var linea in ImportacionFinal.Describir(resultado))
        Console.WriteLine(linea);
    if (simular)
        Console.WriteLine("\nSimulación: no se guardó nada.");
    return 0;
}

/// <summary>
/// Antes de la importación final, una copia de la base junto a ella (con la copia en línea de SQLite, como los respaldos del
/// servicio): «pos.antes-de-importar-AAAA-MM-DD-HHmmss.db».
/// </summary>
static string CopiarAntesDeImportar(string ruta)
{
    var completa = Path.GetFullPath(ruta);
    var destino = Path.Combine(Path.GetDirectoryName(completa)!,
        $"{Path.GetFileNameWithoutExtension(completa)}.antes-de-importar-{DateTime.Now:yyyy-MM-dd-HHmmss}{Path.GetExtension(completa)}");
    static string Cadena(string archivo) => new Microsoft.Data.Sqlite.SqliteConnectionStringBuilder { DataSource = archivo, Pooling = false }.ToString();
    using (var origen = new Microsoft.Data.Sqlite.SqliteConnection(Cadena(completa)))
    using (var copia = new Microsoft.Data.Sqlite.SqliteConnection(Cadena(destino)))
    {
        origen.Open();
        copia.Open();
        BaseDeDatos.Ajustar(origen);
        BaseDeDatos.Ajustar(copia);
        origen.BackupDatabase(copia);
    }
    return destino;
}

static async Task<int> SincronizarEleventaAsync(string[] args)
{
    if (Opcion(args, "--base") is not { } ruta || !File.Exists(ruta))
    {
        Console.Error.WriteLine("Indica con --base la base ya importada de eleventa (importar-eleventa).");
        return 1;
    }
    DatosEleventa eleventa;
    if (Opcion(args, "--fdb") is { } fdb)
    {
        var carpeta = Path.GetDirectoryName(Path.GetFullPath(fdb))!;
        eleventa = await LectorEleventa.LeerAsync(fdb, new FirebirdLocal(
            Opcion(args, "--firebird32") ?? Path.Combine(carpeta, "firebird25-32", "embed"),
            Opcion(args, "--firebird64") ?? Path.Combine(carpeta, "firebird25")), Console.WriteLine);
    }
    else if (Opcion(args, "--servidor") is { } servidor && Opcion(args, "--ruta") is { } rutaEleventa && Opcion(args, "--usuario") is { } usuario)
    {
        // La clave no va en la línea de comandos (se queda en el historial y en la lista de procesos): la variable
        // ELEVENTA_CLAVE, o se pide aquí sin mostrarla.
        if ((Environment.GetEnvironmentVariable("ELEVENTA_CLAVE") is { Length: > 0 } variable ? variable : PedirClave()) is not { } clave)
        {
            Console.Error.WriteLine("Falta la clave del Firebird de eleventa (escríbela, o ponla en la variable ELEVENTA_CLAVE).");
            return 1;
        }
        var conexion = new ServidorEleventa(servidor, rutaEleventa, usuario, clave, int.Parse(Opcion(args, "--puerto") ?? "3050", CultureInfo.InvariantCulture));
        Console.WriteLine($"Leyendo {conexion} (solo lectura)…");
        var reloj = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            eleventa = await LectorEleventa.LeerCatalogoAsync(conexion);
        }
        catch (System.Data.Common.DbException ex)
        {
            Console.Error.WriteLine(LectorEleventa.Explicar(ex, conexion));
            return 1;
        }
        Console.WriteLine($"Leído en {reloj.Elapsed.TotalSeconds:N1} s.");
    }
    else
    {
        Console.Error.WriteLine("Indica de dónde leer: --fdb (una copia) o --servidor, --ruta y --usuario (la caja principal).");
        return 1;
    }
    Console.WriteLine($"Eleventa: {eleventa.Productos.Count:N0} productos, {eleventa.Existencias.Count:N0} existencias, {eleventa.Promociones.Count:N0} promociones.");

    var simular = args.Contains("--simular");
    await using var db = new PosDbContext(BaseDeDatos.Opciones($"Data Source={ruta}"));
    await BaseDeDatos.PrepararAsync(db);
    await using var transaccion = await db.Database.BeginTransactionAsync();
    var resultado = await SincronizacionEleventa.AplicarAsync(db, eleventa, DateTime.UtcNow);
    if (simular)
        await transaccion.RollbackAsync();
    else
        await transaccion.CommitAsync();
    Console.WriteLine($"{(simular ? "Cambiaría" : "Cambió")}: {resultado}.");
    foreach (var (aviso, veces) in resultado.Avisos)
        Console.WriteLine($"  {veces,6:N0}  {aviso}");
    return 0;
}

static async Task<int> ObservarEleventaAsync(string[] args)
{
    if (Opcion(args, "--servidor") is not { } servidor || Opcion(args, "--ruta") is not { } ruta || Opcion(args, "--usuario") is not { } usuario)
    {
        Console.Error.WriteLine("Indica --servidor, --ruta y --usuario del Firebird de eleventa.");
        return 1;
    }
    if ((Environment.GetEnvironmentVariable("ELEVENTA_CLAVE") is { Length: > 0 } variable ? variable : PedirClave()) is not { } clave)
    {
        Console.Error.WriteLine("Falta la clave del Firebird de eleventa.");
        return 1;
    }
    var conexion = new ServidorEleventa(servidor, ruta, usuario, clave, int.Parse(Opcion(args, "--puerto") ?? "3050", CultureInfo.InvariantCulture));
    var carpeta = Opcion(args, "--carpeta")
        ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "PapeleriaPos", "observaciones-eleventa");
    Directory.CreateDirectory(carpeta);

    async Task<FotoEleventa?> FotoAsync()
    {
        var reloj = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            var foto = await FotoEleventa.TomarAsync(conexion);
            Console.WriteLine($"Foto tomada ({foto.Tablas.Count} tablas, {reloj.Elapsed.TotalSeconds:N1} s).");
            return foto;
        }
        catch (System.Data.Common.DbException ex)
        {
            Console.Error.WriteLine(LectorEleventa.Explicar(ex, conexion));
            return null;
        }
    }

    Console.WriteLine($"Observando {conexion} (solo lectura). Las comparaciones van a {carpeta}.");
    if (await FotoAsync() is not { } anterior)
        return 1;
    for (var numero = 1; ; numero++)
    {
        Console.WriteLine();
        Console.WriteLine("Haz algo en eleventa, vuelve aquí y escribe qué hiciste (Enter: otra foto sin nota; «fin»: terminar).");
        Console.Write("> ");
        var nota = Console.ReadLine()?.Trim();
        if (nota is null || nota.Equals("fin", StringComparison.OrdinalIgnoreCase))
            return 0;
        if (await FotoAsync() is not { } actual)
            continue;
        var diferencia = ComparacionEleventa.Comparar(anterior, actual);
        // Si fue una cancelación, una devolución o un cierre de turno, ¿hizo eleventa lo mismo que escribe la caja 2?
        if (RevisionEnVivo.Revisar(anterior, actual) is { Count: > 0 } revision)
            diferencia += $"\nComparado con lo que escribe la caja 2 (✓ igual, ✗ distinto, · no se ve):\n{string.Join('\n', revision)}\n";
        Console.WriteLine(diferencia);
        var nombre = $"{HoraMexico.ALocal(actual.TomadaUtc):yyyy-MM-dd HH-mm-ss} {numero:00}{(nota.Length > 0 ? " " + string.Concat(nota.Where(c => !Path.GetInvalidFileNameChars().Contains(c))) : "")}.txt";
        await File.WriteAllTextAsync(Path.Combine(carpeta, nombre), $"{nota}\n{HoraMexico.ALocal(anterior.TomadaUtc):HH:mm:ss} → {HoraMexico.ALocal(actual.TomadaUtc):HH:mm:ss}\n\n{diferencia}");
        anterior = actual;
    }
}

static async Task<int> ProbarEnvioEleventaAsync(string[] args)
{
    if (Opcion(args, "--ruta") is not { } ruta || Opcion(args, "--usuario") is not { } usuario
        || !int.TryParse(Opcion(args, "--caja"), out var cajaId) || !int.TryParse(Opcion(args, "--cajero"), out var cajeroId))
    {
        Console.Error.WriteLine("Indica --ruta de la copia, --usuario, --caja y --cajero (los ID de eleventa).");
        return 1;
    }
    // Escribe: mientras eleventa sea el oficial, solo en una copia servida en esta PC.
    if (Opcion(args, "--servidor") is { } otro && otro is not ("localhost" or "127.0.0.1" or "::1"))
    {
        Console.Error.WriteLine("Esta prueba escribe: solo va contra un Firebird en esta misma PC con una copia, nunca contra la caja principal.");
        return 1;
    }
    if ((Environment.GetEnvironmentVariable("ELEVENTA_CLAVE") is { Length: > 0 } variable ? variable : PedirClave()) is not { } clave)
    {
        Console.Error.WriteLine("Falta la clave del Firebird de prueba.");
        return 1;
    }
    var servidor = new ServidorEleventa("127.0.0.1", ruta, usuario, clave, int.Parse(Opcion(args, "--puerto") ?? "3050", CultureInfo.InvariantCulture));

    var articulos = new List<ArticuloParaEleventa>();
    await using (var c = new FirebirdSql.Data.FirebirdClient.FbConnection(servidor.Cadena()))
    {
        await c.OpenAsync();
        for (var i = 0; i < args.Length - 1; i++)
        {
            if (args[i] == "--articulo" && args[i + 1].Split(':') is [var id, var cantidad])
            {
                await using var precio = new FirebirdSql.Data.FirebirdClient.FbCommand("SELECT DESCRIPCION, PFINAL FROM PRODUCTOS WHERE ID = @id", c);
                precio.Parameters.AddWithValue("id", int.Parse(id, CultureInfo.InvariantCulture));
                await using var lector = await precio.ExecuteReaderAsync();
                if (!await lector.ReadAsync())
                {
                    Console.Error.WriteLine($"En la copia no hay producto con ID {id}.");
                    return 1;
                }
                var piezas = decimal.Parse(cantidad, CultureInfo.InvariantCulture);
                articulos.Add(new ArticuloParaEleventa(int.Parse(id, CultureInfo.InvariantCulture), lector.GetString(0).Trim(), piezas,
                    Math.Round((decimal)Convert.ToDouble(lector.GetValue(1)) * piezas, 2)));
            }
            else if (args[i] == "--comun" && args[i + 1].Split(':') is [var nombre, var importe])
                articulos.Add(new ArticuloParaEleventa(null, nombre, 1, decimal.Parse(importe, CultureInfo.InvariantCulture)));
        }
    }
    if (articulos.Count == 0)
    {
        Console.Error.WriteLine("Indica al menos un --articulo <ID>:<cantidad> o un --comun <nombre>:<importe>.");
        return 1;
    }

    var ahora = DateTime.Now;
    var total = articulos.Sum(a => a.Importe);
    var pago = Opcion(args, "--pago") is { } p ? decimal.Parse(p, CultureInfo.InvariantCulture) : Math.Ceiling(total);
    var venta = new VentaParaEleventa(ahora.AddMinutes(-1), ahora, pago, new TurnoParaEleventa(ahora.Date.AddHours(8), 0), articulos);

    var antes = await FotoEleventa.TomarAsync(servidor);
    var reloj = System.Diagnostics.Stopwatch.StartNew();
    var resultado = await EnvioEleventa.MandarAsync(servidor, new CajaEnEleventa(cajaId, cajeroId), venta);
    Console.WriteLine($"Mandada en {reloj.Elapsed.TotalMilliseconds:N0} ms: ticket {resultado.TicketId}, folio {resultado.Folio}, total {total:C}" +
        (resultado.FolioRepetido ? " (¡folio repetido!)" : "") + ".");
    Console.WriteLine();
    Console.WriteLine(ComparacionEleventa.Comparar(antes, await FotoEleventa.TomarAsync(servidor)));
    return 0;
}

/// <summary>La clave, tecleada sin que se vea (asteriscos). Sin teclado (la entrada redirigida), null.</summary>
static string? PedirClave() => PedirOculto("Clave del Firebird de eleventa: ");

/// <summary>Algo que no se debe ver (una clave, el código de Control Print), tecleado con asteriscos. Sin teclado, null.</summary>
static string? PedirOculto(string pregunta)
{
    if (Console.IsInputRedirected)
        return null;
    Console.Write(pregunta);
    var clave = new System.Text.StringBuilder();
    while (Console.ReadKey(intercept: true) is var tecla && tecla.Key != ConsoleKey.Enter)
    {
        if (tecla.Key == ConsoleKey.Backspace)
        {
            if (clave.Length > 0)
            {
                clave.Length--;
                Console.Write("\b \b");
            }
        }
        else if (!char.IsControl(tecla.KeyChar))
        {
            clave.Append(tecla.KeyChar);
            Console.Write('*');
        }
    }
    Console.WriteLine();
    return clave.Length > 0 ? clave.ToString() : null;
}

static async Task<int> PrepararPruebaAsync(string[] args)
{
    var salida = Opcion(args, "--salida") ?? "prueba.db";
    var ejemplos = Opcion(args, "--ejemplos") ?? "Ejemplos";
    Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(salida))!);
    var resultado = await GenerarFicticiosAsync(["--salida", salida, .. args.Contains("--reemplazar") ? new[] { "--reemplazar" } : []]);
    if (resultado != 0)
        return resultado;
    await using (var db = new PosDbContext(BaseDeDatos.Opciones($"Data Source={salida}")))
    {
        await BaseDeDatos.PrepararAsync(db);
        foreach (var hecho in await PreparacionPrueba.PrepararAsync(db, ejemplos))
            Console.WriteLine($"Creado: {hecho}");
        // Un solo archivo, sin el diario WAL aparte: el instalador lo copia tal cual.
        await db.Database.ExecuteSqlRawAsync("PRAGMA journal_mode=DELETE;");
    }
    Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
    return 0;
}

static async Task<int> PrepararCajaAsync(string[] args)
{
    var ruta = Opcion(args, "--base") ?? "ficticios.db";
    if (!File.Exists(ruta))
    {
        Console.Error.WriteLine($"No existe la base {ruta}.");
        return 1;
    }
    await using var db = new PosDbContext(BaseDeDatos.Opciones($"Data Source={ruta}"));
    await BaseDeDatos.PrepararAsync(db);
    var hecho = await PreparacionCaja.PrepararAsync(db);
    Console.WriteLine(hecho.Count == 0 ? "La base ya tenía caja y usuarios." : string.Join(Environment.NewLine, hecho.Select(h => $"Creado: {h}")));
    return 0;
}

static bool PrepararSalida(string salida, bool reemplazar)
{
    if (!File.Exists(salida))
        return true;
    if (!reemplazar)
    {
        Console.Error.WriteLine($"Ya existe {salida}. Usa --reemplazar para sobrescribirla.");
        return false;
    }
    foreach (var archivo in new[] { salida, salida + "-wal", salida + "-shm" })
        File.Delete(archivo);
    return true;
}

static async Task<int> DiagnosticoAsync(string[] args)
{
    var ruta = Opcion(args, "--base") ?? "ficticios.db";
    var salida = Opcion(args, "--salida") ?? "diagnostico.xlsx";
    if (!File.Exists(ruta))
    {
        Console.Error.WriteLine($"No existe la base {ruta}.");
        return 1;
    }

    Console.WriteLine($"Analizando {ruta}…");
    DatosDiagnostico datos;
    await using (var db = new PosDbContext(BaseDeDatos.Opciones($"Data Source={ruta}")))
    {
        await BaseDeDatos.PrepararAsync(db);
        datos = await DatosDiagnostico.CargarAsync(db);
    }
    if (Opcion(args, "--desde") is { } desde)
        datos = datos.Desde(DateOnly.Parse(desde, CultureInfo.InvariantCulture));
    if (datos.Ventas.Count == 0)
    {
        Console.Error.WriteLine("La base no tiene ventas que analizar.");
        return 1;
    }

    var analisis = new Analisis(datos);
    LibroDiagnostico.Guardar(analisis, salida, Path.GetFileName(ruta));

    Console.WriteLine();
    foreach (var fila in analisis.Resumen().Where(f => f.Concepto.Length > 0))
    {
        var valor = fila.Valor switch
        {
            decimal d when fila.Formato == Formato.Dinero => d.ToString("C0"),
            decimal d when fila.Formato == Formato.Porcentaje => d.ToString("P1"),
            decimal d => d.ToString("N1"),
            int i => i.ToString("N0"),
            DateTime f => f.ToString("dd/MM/yyyy"),
            var v => v?.ToString() ?? "",
        };
        Console.WriteLine($"  {fila.Concepto,-42} {valor,16}");
    }
    Console.WriteLine($"\nDiagnóstico: {Path.GetFullPath(salida)}");
    return 0;
}

static async Task<int> GenerarFicticiosAsync(string[] args)
{
    var salida = Opcion(args, "--salida") ?? "ficticios.db";
    var semilla = int.Parse(Opcion(args, "--semilla") ?? "2026", CultureInfo.InvariantCulture);
    if (!PrepararSalida(salida, args.Contains("--reemplazar")))
        return 1;

    var opciones = new OpcionesFicticios { Semilla = semilla };
    Console.WriteLine($"Generando datos ficticios (semilla {semilla})…");
    var datos = GeneradorFicticios.Generar(opciones);

    // Reloj fijo: con la misma semilla, la base sale idéntica.
    var cierre = new DateTimeOffset(HoraMexico.AUtc(opciones.UltimoDia.ToDateTime(new TimeOnly(22, 0))));
    await using (var db = new PosDbContext(BaseDeDatos.Opciones($"Data Source={salida}")) { Reloj = new RelojFijo(cierre) })
    {
        await BaseDeDatos.PrepararAsync(db);
        await GuardadoLote.GuardarAsync(datos, db);
    }

    var lineas = datos.Ventas.Sum(v => v.Lineas.Count);
    var dias = datos.Ventas.Select(v => HoraMexico.ALocal(v.FechaUtc).Date).Distinct().Count();
    Console.WriteLine($"""

        Base creada: {Path.GetFullPath(salida)}

        Catálogo
          Productos       {datos.Productos.Count,8:N0}
          Variantes       {datos.Variantes.Count,8:N0}
          Presentaciones  {datos.Presentaciones.Count,8:N0}
          Códigos         {datos.Codigos.Count,8:N0}
          Precios         {datos.Precios.Count,8:N0}
          Marcas          {datos.Marcas.Count,8:N0}
          Proveedores     {datos.Proveedores.Count,8:N0}

        Ventas ({opciones.UltimoDia.AddDays(1 - opciones.Dias):d} a {opciones.UltimoDia:d})
          Tickets         {datos.Ventas.Count,8:N0}  ({(double)datos.Ventas.Count / dias:N0} por día abierto)
          Líneas          {lineas,8:N0}
          Total           {datos.VentasCentavos / 100m,14:C0}

        Inventario al cierre
          Variantes       {datos.Movimientos.Count,8:N0}
          Valor a costo   {datos.ValorInventarioCentavos / 100m,14:C0}

        Puesto a propósito para el diagnóstico
          Sin movimiento  {datos.Resumen.SinMovimiento.Count,8:N0}
          Sin código      {datos.Resumen.SinCodigo.Count,8:N0}
          Precio < costo  {datos.Resumen.PrecioBajoCosto.Count,8:N0}
          Duplicados      {datos.Resumen.Duplicados.Count,8:N0}
        """);
    return 0;
}

static async Task<int> ProbarImpresoraTicketsAsync(string[] args)
{
    var texto = Opcion(args, "--impresora");
    if (texto is null || !Uri.TryCreate(texto.Contains("://") ? texto : $"tcp://{texto}", UriKind.Absolute, out var uri) || uri.Host.Length == 0)
    {
        Console.Error.WriteLine("Indica la impresora de tickets con --impresora: su IP (por ejemplo 192.168.1.100) o IP:puerto.");
        return 1;
    }
    var puerto = uri.Port > 0 ? uri.Port : 9100;
    var pc = Environment.MachineName;
    Console.WriteLine($"""
        La impresora de tickets en {uri.Host}:{puerto}, desde {pc}. En cada vuelta salen dos tickets de prueba, cada uno
        por su conexión, como los manda la caja. Para probar desde las dos cajas, abre esto también en la otra y den Enter
        al mismo tiempo.
        """);
    foreach (var vuelta in PruebaImpresoraTickets.Vueltas)
    {
        Console.WriteLine();
        var separacion = vuelta.Separacion > TimeSpan.Zero ? $" ({vuelta.Separacion.TotalMilliseconds:N0} ms entre uno y otro)" : "";
        Console.Write($"Enter para mandarlos {vuelta.Nombre}{separacion}; «fin» para terminar: ");
        if (Console.ReadLine()?.Trim() is not { } respuesta || respuesta.Equals("fin", StringComparison.OrdinalIgnoreCase))
            return 0;
        var resultados = await PruebaImpresoraTickets.ProbarAsync(() => new ImpresoraRed(uri.Host, puerto), pc, vuelta, 2, TimeProvider.System);
        foreach (var linea in PruebaImpresoraTickets.Describir(resultados))
            Console.WriteLine(linea);
    }
    Console.WriteLine();
    Console.WriteLine("Listo. Anota en la guía qué salió en cada vuelta.");
    return 0;
}

static int ProbarLector()
{
    if (Console.IsInputRedirected)
    {
        Console.Error.WriteLine("probar-lector necesita la consola: ábrelo con scripts\\probar-lector.cmd.");
        return 1;
    }
    Console.WriteLine($"""
        Escanea con el lector códigos de barras, uno por uno, también uno de 11 cifras. Para comparar, teclea un código a
        mano y Enter. Aquí no se vende nada. Esc para terminar.

        """);
    var reloj = System.Diagnostics.Stopwatch.StartNew();
    var codigos = new List<PruebaLector.Codigo>();
    var teclas = new List<PruebaLector.Tecla>();
    while (Console.ReadKey(intercept: true) is var tecla && tecla.Key != ConsoleKey.Escape)
    {
        var hora = reloj.Elapsed;
        if (tecla.Key == ConsoleKey.Enter)
        {
            if (teclas.Count == 0)
                continue;
            var codigo = PruebaLector.Medir(teclas, hora);
            teclas.Clear();
            codigos.Add(codigo);
            foreach (var linea in PruebaLector.Describir(codigo))
                Console.WriteLine(linea);
            Console.WriteLine();
        }
        else if (tecla.KeyChar != '\0')
        {
            teclas.Add(new PruebaLector.Tecla(tecla.KeyChar, hora));
        }
    }
    foreach (var linea in PruebaLector.Resumen(codigos))
        Console.WriteLine(linea);
    return 0;
}

static async Task<int> SimularCajasAsync(string[] args)
{
    if (OpcionesSimulador.Leer(args, out var error) is not { } opciones)
    {
        Console.Error.WriteLine(error);
        return 2;
    }
    var url = Opcion(args, "--servicio");
    var ejecutable = Opcion(args, "--iniciar-servicio");
    if ((url is null) == (ejecutable is null))
    {
        Console.Error.WriteLine("Indica --servicio <url> (un servicio de prueba que ya corre) o --iniciar-servicio <ruta del servicio> (arranca uno "
            + "propio, con una copia de la base de prueba).");
        return 2;
    }
    Uri? servicio = null;
    if (url is not null && !Uri.TryCreate(url.EndsWith('/') ? url : url + "/", UriKind.Absolute, out servicio))
    {
        Console.Error.WriteLine($"No entiendo la dirección del servicio «{url}» (por ejemplo, http://192.168.1.10:5180).");
        return 2;
    }
    var carpeta = Path.Combine(Path.GetTempPath(), $"simulador-cajas-{Guid.NewGuid():N}");
    Directory.CreateDirectory(carpeta);
    ServicioLocal? propio = null;
    try
    {
        string? baseDeDatos = null, descripcion = null;
        if (ejecutable is not null)
        {
            baseDeDatos = Path.Combine(carpeta, "pos.db");
            var catalogoPor = 1;
            if (Opcion(args, "--catalogo-por") is { } por && (!int.TryParse(por, out catalogoPor) || catalogoPor is < 1 or > 10))
            {
                Console.Error.WriteLine("--catalogo-por va de 1 a 10 (4 deja la papelería ficticia del tamaño del catálogo de la tienda).");
                return 2;
            }
            descripcion = await BaseDePrueba.PrepararAsync(baseDeDatos, Opcion(args, "--base"), carpeta, Console.Out, catalogoPor);
            int? nucleos = null;
            if (Opcion(args, "--nucleos") is { } texto)
            {
                if (!int.TryParse(texto, out var n) || n < 1 || n > Environment.ProcessorCount)
                {
                    Console.Error.WriteLine($"--nucleos va de 1 a {Environment.ProcessorCount}.");
                    return 2;
                }
                nucleos = n;
                descripcion += $"; el servicio, con {n} {(n == 1 ? "núcleo" : "núcleos")} de esta PC (su afinidad y DOTNET_PROCESSOR_COUNT), para parecerse a la caja principal";
            }
            Console.WriteLine("Arrancando el servicio…");
            propio = await ServicioLocal.IniciarAsync(ejecutable, baseDeDatos, carpeta, nucleos);
            servicio = propio.Url;
        }
        System.Diagnostics.Process? proceso = propio?.Proceso;
        if (proceso is null && int.TryParse(Opcion(args, "--pid"), out var pid))
        {
            try
            {
                proceso = System.Diagnostics.Process.GetProcessById(pid);
            }
            catch (ArgumentException)
            {
                Console.Error.WriteLine($"No hay un proceso {pid}: la memoria del servicio no se mide.");
            }
        }
        var simulador = new SimuladorCajas(opciones, servicio!, fondo => PapeleriaPos.Interfaz.RedCaja.Manejador(TimeSpan.FromSeconds(fondo ? 3 : 2)))
        {
            Salida = Console.Out, Proceso = proceso, ServicioPropio = propio is not null,
            DescripcionDeLaBase = descripcion ?? "la del servicio de prueba",
            Base = baseDeDatos is null ? null : () => new PosDbContext(BaseDeDatos.Opciones($"Data Source={baseDeDatos};Pooling=False")),
            Carpeta = Path.Combine(carpeta, "cajas"),
        };
        var resultado = await simulador.CorrerAsync();
        // Los PIN no van al informe (aunque sean los de la base de prueba).
        var comando = "simular-cajas " + string.Join(' ', args.Select((a, i) =>
            i > 0 && args[i - 1] is "--dueno" or "--cajero" or "--encargado" ? a.Split(':')[0] + ":****" : a.Contains(' ') ? $"\"{a}\"" : a));
        var informe = Informe.Markdown(resultado, comando);
        Console.WriteLine();
        Console.WriteLine(informe);
        if (Opcion(args, "--salida") is { } salida)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(salida))!);
            await File.WriteAllTextAsync(salida, informe);
            Console.WriteLine($"El informe quedó en {Path.GetFullPath(salida)}");
        }
        return resultado.Paso ? 0 : 1;
    }
    catch (InvalidOperationException ex)
    {
        Console.Error.WriteLine(ex.Message);
        return 2;
    }
    finally
    {
        if (propio is not null)
            await propio.DisposeAsync();
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try
        {
            Directory.Delete(carpeta, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Console.Error.WriteLine($"No se pudo borrar la carpeta temporal {carpeta}: {ex.Message}");
        }
    }
}

static string? Opcion(string[] args, string nombre)
{
    var i = Array.IndexOf(args, nombre);
    return i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
}

static async Task<int> ImportarControlPrintAsync(string[] args)
{
    var ruta = Opcion(args, "--base") ?? "pos.db";
    var origen = Opcion(args, "--control-print") ?? ImportacionControlPrint.RutaPorOmision;
    if (!File.Exists(ruta) || !File.Exists(origen))
    {
        Console.Error.WriteLine($"No existe {(File.Exists(ruta) ? origen : ruta)}.");
        return 1;
    }
    var costos = ImportacionControlPrint.LeerCostos(origen);
    await using var db = new PosDbContext(BaseDeDatos.Opciones($"Data Source={ruta}"));
    await BaseDeDatos.PrepararAsync(db);
    var registro = ImpresionesDelPos.Registro(db);
    var sucursales = await db.Sucursales.ToListAsync();
    foreach (var sucursal in sucursales)
    {
        await registro.GuardarCostosAsync(sucursal.Id, costos);
        Console.WriteLine($"Costos de impresión copiados a {sucursal.Nombre}.");
    }
    // La impresora es de una sucursal: con varias, su IP se escribe en Configuración → Impresora.
    if (ImportacionControlPrint.LeerImpresora(origen) is { } host)
    {
        if (sucursales.Count == 1)
        {
            var configuracion = await ImpresionesDelPos.Impresora(db).ConfiguracionAsync(sucursales[0].Id);
            configuracion.ImpresoraHost = host;
            await db.SaveChangesAsync();
            Console.WriteLine($"La caja principal leerá la impresora de {sucursales[0].Nombre} (la misma que lee Control Print).");
        }
        else
        {
            Console.WriteLine("Hay varias sucursales: escribe la IP de la impresora en Configuración → Impresora de la que la tiene.");
        }
    }
    return 0;
}

/// <summary>
/// recopilar-control-print (docs/impresion.md, «Recopilar los datos de Control Print»): todo lo hace RecopilacionControlPrint, del
/// módulo; aquí solo el teclado. El código sale de la variable CONTROLPRINT_CODIGO o se teclea sin que se vea: nunca va en la
/// línea de comandos (se quedaría en el historial y en la lista de procesos). Ctrl+C detiene con orden la primera vez (lo
/// recopilado se queda y se puede seguir); la segunda, o mientras se escribe el código, sale ya.
/// </summary>
static async Task<int> RecopilarControlPrintAsync(string[] args)
{
    if (OpcionesRecopilacion.Leer(args, out var error) is not { } opciones)
    {
        Console.Error.WriteLine(error);
        return 2;
    }
    var escribiendoCodigo = false;
    var codigo = Environment.GetEnvironmentVariable(OpcionesRecopilacion.VariableCodigo) is { Length: > 0 } variable
        ? new CodigoDeConexion(() => variable, false, $"de la variable {OpcionesRecopilacion.VariableCodigo}")
        : new CodigoDeConexion(() =>
        {
            escribiendoCodigo = true;
            try
            {
                return PedirOculto("Código de conexión: ");
            }
            finally
            {
                escribiendoCodigo = false;
            }
        }, !Console.IsInputRedirected);
    using var detener = new CancellationTokenSource();
    void AlOprimirCtrlC(object? _, ConsoleCancelEventArgs e)
    {
        if (escribiendoCodigo || detener.IsCancellationRequested)
            return;
        e.Cancel = true;
        detener.Cancel();
        Console.Error.WriteLine();
        Console.Error.WriteLine("Deteniendo… (otra vez Ctrl+C para salir ya)");
    }
    Console.CancelKeyPress += AlOprimirCtrlC;
    try
    {
        return await RecopilacionControlPrint.EjecutarAsync(opciones, codigo, Console.Out, detener.Token);
    }
    finally
    {
        Console.CancelKeyPress -= AlOprimirCtrlC;
    }
}

static async Task<int> AplicarDepuracionAsync(string[] args)
{
    if (Opcion(args, "--desde") is not { } desde || Opcion(args, "--base") is not { } destino || !File.Exists(desde) || !File.Exists(destino))
    {
        Console.Error.WriteLine("Indica la base donde se depuró (--desde) y la importación nueva (--base); las dos deben existir.");
        return 1;
    }
    List<DecisionDepuracion> decisiones;
    await using (var anterior = new PosDbContext(BaseDeDatos.Opciones($"Data Source={desde};Mode=ReadOnly")))
        decisiones = await anterior.DecisionesDepuracion.AsNoTracking().ToListAsync();
    await using var db = new PosDbContext(BaseDeDatos.Opciones($"Data Source={destino}"));
    await BaseDeDatos.PrepararAsync(db);
    var (aplicadas, omitidas) = await new RegistroDepuracion(db).RepetirAsync(decisiones);
    Console.WriteLine($"Decisiones aplicadas: {aplicadas} de {decisiones.Count}.");
    foreach (var o in omitidas)
        Console.WriteLine($"  Omitida: {o}");
    return 0;
}
