using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace PapeleriaPos.Impresiones.Servicio;

/// <summary>Lo que el módulo de impresión pone en el servicio de la caja principal.</summary>
public static class ServiciosImpresiones
{
    /// <summary>
    /// La impresora de cada sucursal (su IP está en la configuración de impresión): quién la lee por la red (las pruebas
    /// ponen el suyo) y la vigilancia (niveles, contador, hojas que salieron), que corre sola salvo con
    /// <c>Impresora:Vigilar=false</c> (las pruebas piden cada lectura). Las rutas se montan aparte, sobre el grupo «/api»
    /// del servicio (<see cref="ApiImpresiones.MapearApiImpresiones"/>).
    /// </summary>
    public static IServiceCollection AgregarImpresiones(this IServiceCollection servicios, IConfiguration configuracion)
    {
        servicios.TryAddSingleton(TimeProvider.System);
        servicios.TryAddSingleton<ILectorImpresora, LectorImpresora>();
        servicios.AddSingleton<VigilanciaImpresora>();
        if (configuracion.GetValue("Impresora:Vigilar", true))
            servicios.AddHostedService(proveedor => proveedor.GetRequiredService<VigilanciaImpresora>());
        return servicios;
    }
}
