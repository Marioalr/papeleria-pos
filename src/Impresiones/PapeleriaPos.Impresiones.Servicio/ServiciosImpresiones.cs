using Microsoft.Extensions.Configuration;
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
        servicios.AddSingleton(proveedor => new ControlPrintEnElServicio(controlPrint,
            string.IsNullOrEmpty(rutaDeLaBase) ? null : ArchivoHuella(rutaDeLaBase), proveedor.GetRequiredService<IServiceScopeFactory>(),
            proveedor.GetRequiredService<TimeProvider>(), proveedor.GetRequiredService<ILogger<ControlPrintEnElServicio>>()));
        if (controlPrint?.Servidor is { Length: > 0 })
            servicios.AddHostedService(proveedor => proveedor.GetRequiredService<ControlPrintEnElServicio>());
        return servicios;
    }

    /// <summary>Junto a la base, como su llave («pos.controlprint»): la huella de Control Print fijada en el primer saludo.</summary>
    public static string ArchivoHuella(string rutaDeLaBase) => Path.ChangeExtension(Path.GetFullPath(rutaDeLaBase), ".controlprint");
}
