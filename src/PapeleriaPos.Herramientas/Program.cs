using System.Globalization;
using PapeleriaPos.Datos;
using PapeleriaPos.Herramientas.Ficticios;
using PapeleriaPos.Nucleo;

CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("es-MX");

return args switch
{
    ["generar-ficticios", .. var resto] => await GenerarFicticiosAsync(resto),
    _ => Ayuda(),
};

static int Ayuda()
{
    Console.WriteLine("""
        Herramientas de Papelería POS

          generar-ficticios [--salida ficticios.db] [--semilla 2026] [--reemplazar]
              Crea una base con una papelería inventada: catálogo, un año de ventas y existencias.
        """);
    return 1;
}

static async Task<int> GenerarFicticiosAsync(string[] args)
{
    var salida = Opcion(args, "--salida") ?? "ficticios.db";
    var semilla = int.Parse(Opcion(args, "--semilla") ?? "2026", CultureInfo.InvariantCulture);
    var reemplazar = args.Contains("--reemplazar");

    if (File.Exists(salida))
    {
        if (!reemplazar)
        {
            Console.Error.WriteLine($"Ya existe {salida}. Usa --reemplazar para sobrescribirla.");
            return 1;
        }
        foreach (var archivo in new[] { salida, salida + "-wal", salida + "-shm" })
            File.Delete(archivo);
    }

    var opciones = new OpcionesFicticios { Semilla = semilla };
    Console.WriteLine($"Generando datos ficticios (semilla {semilla})…");
    var datos = GeneradorFicticios.Generar(opciones);

    // Reloj fijo: con la misma semilla, la base sale idéntica.
    var cierre = new DateTimeOffset(HoraMexico.AUtc(opciones.UltimoDia.ToDateTime(new TimeOnly(22, 0))));
    await using (var db = new PosDbContext(BaseDeDatos.Opciones($"Data Source={salida}")) { Reloj = new RelojFijo(cierre) })
    {
        await BaseDeDatos.PrepararAsync(db);
        await GuardadoFicticios.GuardarAsync(datos, db);
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
