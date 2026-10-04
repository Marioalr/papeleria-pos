using Microsoft.EntityFrameworkCore;
using System.Globalization;
using PapeleriaPos.Datos;
using PapeleriaPos.Herramientas;
using PapeleriaPos.Herramientas.Diagnostico;
using PapeleriaPos.Herramientas.Eleventa;
using PapeleriaPos.Herramientas.Ficticios;
using PapeleriaPos.Herramientas.Importacion;
using PapeleriaPos.Nucleo;

CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("es-MX");

return args switch
{
    ["generar-ficticios", .. var resto] => await GenerarFicticiosAsync(resto),
    ["diagnostico", .. var resto] => await DiagnosticoAsync(resto),
    ["importar-eleventa", .. var resto] => await ImportarEleventaAsync(resto),
    ["preparar-caja", .. var resto] => await PrepararCajaAsync(resto),
    ["importar-control-print", .. var resto] => await ImportarControlPrintAsync(resto),
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

          importar-control-print --base pos.db [--control-print controlprint.db]
              Copia la configuración de costos de Control Print (tinta, papel, márgenes, calibración) a todas las
              sucursales, y la IP de su impresora si hay una sola sucursal. Sus trabajos no se importan. Por
              omisión lee la base de Control Print de esta PC.

          preparar-caja --base ficticios.db
              Da de alta la Caja 1 y usuarios de prueba (ver README) en una base de prueba.
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
    var salida = Opcion(args, "--salida") ?? "eleventa.db";
    if (!PrepararSalida(salida, args.Contains("--reemplazar")))
        return 1;

    var carpeta = Path.GetDirectoryName(Path.GetFullPath(fdb))!;
    var firebird = new FirebirdLocal(
        Opcion(args, "--firebird32") ?? Path.Combine(carpeta, "firebird25-32", "embed"),
        Opcion(args, "--firebird64") ?? Path.Combine(carpeta, "firebird25"));

    var eleventa = await LectorEleventa.LeerAsync(fdb, firebird, Console.WriteLine);
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
        """);
    if (datos.Avisos.Count > 0)
    {
        Console.WriteLine("\nPara revisar");
        foreach (var (aviso, veces) in datos.Avisos)
            Console.WriteLine($"  {veces,6:N0}  {aviso}");
    }
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
    var registro = new RegistroImpresiones(db);
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
            var configuracion = await db.ConfiguracionesImpresion.SingleAsync();
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
