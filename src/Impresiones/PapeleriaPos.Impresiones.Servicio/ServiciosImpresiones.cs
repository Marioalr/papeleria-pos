using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Configuration.CommandLine;
using Microsoft.Extensions.Configuration.EnvironmentVariables;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;

namespace PapeleriaPos.Impresiones.Servicio;

/// <summary>Lo que el módulo de impresión pone en el servicio de la caja principal.</summary>
public static class ServiciosImpresiones
{
    /// <summary>
    /// La impresora de cada sucursal (su IP está en la configuración de impresión): quién la lee por la red (las pruebas
    /// ponen el suyo) y la vigilancia (niveles, contador, hojas que salieron), que corre sola salvo con
    /// <c>Impresora:Vigilar=false</c> (las pruebas piden cada lectura). Y la cola de Control Print (solo leyendo), si el servicio
    /// tiene la sección «ControlPrint» (la caja 2, 2026-10-25). Las rutas se montan aparte, sobre el grupo «/api» del servicio
    /// (<see cref="ApiImpresiones.MapearApiImpresiones"/>).
    /// </summary>
    /// <param name="rutaDeLaBase">
    /// La base del POS: junto a ella se guarda la huella del certificado de Control Print que se fijó en el primer saludo
    /// («pos.controlprint»). Vacía, no se guarda (se vuelve a fijar en cada arranque).
    /// </param>
    public static IServiceCollection AgregarImpresiones(this IServiceCollection servicios, IConfiguration configuracion, string? rutaDeLaBase = null)
    {
        servicios.TryAddSingleton(TimeProvider.System);
        servicios.TryAddSingleton<ILectorImpresora, LectorImpresora>();
        servicios.AddSingleton<VigilanciaImpresora>();
        if (configuracion.GetValue("Impresora:Vigilar", true))
            servicios.AddHostedService(proveedor => proveedor.GetRequiredService<VigilanciaImpresora>());

        var controlPrint = configuracion.GetSection("ControlPrint").Get<OpcionesControlPrint>();
        // El código, solo de la variable de entorno (2026-10-29): uno en servicio.json (o en otro lado) se ignora y se avisa.
        var (codigo, avisoCodigo) = CodigoDelEntorno(configuracion);
        if (controlPrint is not null)
            controlPrint.Codigo = codigo;
        servicios.AddSingleton(proveedor => new ControlPrintEnElServicio(controlPrint,
            string.IsNullOrEmpty(rutaDeLaBase) ? null : ArchivoHuella(rutaDeLaBase), proveedor.GetRequiredService<IServiceScopeFactory>(),
            proveedor.GetRequiredService<TimeProvider>(), proveedor.GetRequiredService<ILogger<ControlPrintEnElServicio>>(), avisoCodigo));
        if (controlPrint?.Servidor is { Length: > 0 })
            servicios.AddHostedService(proveedor => proveedor.GetRequiredService<ControlPrintEnElServicio>());
        return servicios;
    }

    /// <summary>La variable de entorno del código de Control Print (la pone poner-codigo-controlprint.ps1).</summary>
    public const string VariableCodigo = "ControlPrint__Codigo";

    /// <summary>
    /// El código de conexión de Control Print, solo de la variable de entorno <see cref="VariableCodigo"/> (2026-10-29): uno en
    /// servicio.json, appsettings.json, la línea de órdenes o cualquier otro lado se ignora, y el aviso dice dónde está y dónde va,
    /// sin repetirlo. Vacío el código si no está en la variable; vacío el aviso si no hay otro.
    /// </summary>
    public static (string? Codigo, string? Aviso) CodigoDelEntorno(IConfiguration configuracion)
    {
        const string clave = "ControlPrint:Codigo";
        if (configuracion is not IConfigurationRoot raiz)
            return (null, configuracion[clave] is { Length: > 0 } ? AvisoCodigo(["la configuración"]) : null);
        string? codigo = null;
        var otros = new List<string>();
        foreach (var proveedor in raiz.Providers)
        {
            if (!proveedor.TryGet(clave, out var valor) || string.IsNullOrWhiteSpace(valor))
                continue;
            if (proveedor is EnvironmentVariablesConfigurationProvider)
                codigo = valor;
            else
                otros.Add(proveedor switch
                {
                    FileConfigurationProvider archivo => Path.GetFileName(archivo.Source.Path) ?? "un archivo",
                    CommandLineConfigurationProvider => "la línea de órdenes",
                    _ => "la configuración",
                });
        }
        return (codigo, otros.Count == 0 ? null : AvisoCodigo(otros.Distinct().ToList()));
    }

    private static string AvisoCodigo(IReadOnlyList<string> donde) =>
        $"El código de conexión de Control Print está en {string.Join(" y ", donde)} y ahí se ignora: va solo en la variable de entorno del "
        + $"servicio {VariableCodigo} (con instalacion\\poner-codigo-controlprint.ps1). Quítalo de ahí.";

    /// <summary>Junto a la base, como su llave («pos.controlprint»): la huella de Control Print fijada en el primer saludo.</summary>
    public static string ArchivoHuella(string rutaDeLaBase) => Path.ChangeExtension(Path.GetFullPath(rutaDeLaBase), ".controlprint");
}
