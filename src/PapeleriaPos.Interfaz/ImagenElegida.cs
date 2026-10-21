using Microsoft.AspNetCore.Components.Forms;
using Microsoft.JSInterop;
using PapeleriaPos.Impresion;

namespace PapeleriaPos.Interfaz;

/// <summary>
/// La foto que se toma con el celular o se elige en la PC (entrega 13, parte 2), como la manda la página: achicada ahí mismo
/// antes de mandarla (a lo más <see cref="Lado"/> por lado, en JPEG). Una foto de 4 MB viaja como de unos 150 KB, en un momento
/// por el Wi-Fi; el servicio la deja de 240. Se ve antes de guardarla.
/// </summary>
public static class ImagenElegida
{
    /// <summary>Lo más largo de un lado de lo que se manda: el doble de lo que se guarda, para que el servicio la deje nítida.</summary>
    public const int Lado = 960;

    /// <summary>Lo más que se espera a que la página la achique (un celular lento tarda uno o dos segundos con una de 12 megapíxeles).</summary>
    private static readonly TimeSpan Espera = TimeSpan.FromSeconds(15);

    /// <summary>
    /// Sus bytes, ya achicada. Si la página no la puede achicar (un formato que el navegador no conoce, o se tardó), va como está:
    /// el servicio dice si sirve. Más de <see cref="FotoDelProducto.BytesMaximos"/> no se lee (lo dice).
    /// </summary>
    public static async Task<byte[]> LeerAsync(IBrowserFile archivo)
    {
        IBrowserFile chica;
        try
        {
            chica = await archivo.RequestImageFileAsync("image/jpeg", Lado, Lado).AsTask().WaitAsync(Espera);
        }
        catch (Exception ex) when (ex is JSException or TimeoutException or TaskCanceledException or InvalidOperationException)
        {
            chica = archivo;
        }
        try
        {
            await using var flujo = chica.OpenReadStream(maxAllowedSize: FotoDelProducto.BytesMaximos);
            using var memoria = new MemoryStream();
            await flujo.CopyToAsync(memoria);
            return memoria.ToArray();
        }
        catch (IOException)
        {
            throw new ErrorCaja($"La imagen pasa de {FotoDelProducto.BytesMaximos / 1024 / 1024} MB: usa una más chica.");
        }
    }
}
