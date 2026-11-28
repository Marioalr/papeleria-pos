using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using PapeleriaPos.Interfaz;

namespace PapeleriaPos.Caja;

/// <summary>
/// La caja en Windows (Configuración › Actualización): el token de GitHub va cifrado por Windows para el usuario que lo escribió,
/// junto a los datos de la caja. El instalador de prueba se abre en silencio: Inno Setup pide el permiso de administrador, cierra
/// la caja y la vuelve a abrir (instalacion/prueba.iss). El de la tienda (<paramref name="enSilencio"/> en false) se abre con sus
/// páginas, con las respuestas de esta PC ya puestas, para que quien actualiza vea qué va a quedar (instalacion/papeleria-pos.iss).
/// </summary>
internal sealed class EquipoPrueba(string carpetaDatos, bool enSilencio = true) : IEquipoPrueba
{
    private static readonly byte[] Proposito = Encoding.UTF8.GetBytes("PapeleriaPos-Prueba/GitHub");

    private string Archivo => Path.Combine(carpetaDatos, "github-token.dat");

    public string? LeerToken()
    {
        if (!File.Exists(Archivo))
            return null;
        try
        {
            return Encoding.UTF8.GetString(ProtectedData.Unprotect(File.ReadAllBytes(Archivo), Proposito, DataProtectionScope.CurrentUser));
        }
        catch (CryptographicException)
        {
            // Lo guardó otro usuario de Windows: este escribe el suyo.
            return null;
        }
    }

    public void GuardarToken(string? token)
    {
        if (token is null)
        {
            File.Delete(Archivo);
            return;
        }
        Directory.CreateDirectory(carpetaDatos);
        File.WriteAllBytes(Archivo, ProtectedData.Protect(Encoding.UTF8.GetBytes(token), Proposito, DataProtectionScope.CurrentUser));
    }

    public string CarpetaDescargas => Path.Combine(Path.GetTempPath(), "PapeleriaPos-Prueba");

    public void Instalar(string instalador)
    {
        try
        {
            Process.Start(new ProcessStartInfo(instalador, enSilencio ? "/SILENT /SUPPRESSMSGBOXES /NORESTART /CLOSEAPPLICATIONS" : "")
                { UseShellExecute = true });
            // El de la tienda no sigue hasta que la caja se cierre («Listo para instalar» lo pide): se cierra como siempre (con sus avisos,
            // si queda algo por mandar o imprimir). Si después no se da el permiso o se cancela, se vuelve a abrir: nada cambió.
            if (!enSilencio)
                System.Windows.Application.Current?.Dispatcher.BeginInvoke(() => System.Windows.Application.Current.MainWindow?.Close());
        }
        catch (Win32Exception ex)
        {
            throw new ErrorActualizacion($"No se pudo abrir el instalador: {ex.Message}");
        }
    }
}
