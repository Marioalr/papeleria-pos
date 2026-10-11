using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using PapeleriaPos.Interfaz;

namespace PapeleriaPos.Caja;

/// <summary>
/// La caja de prueba en Windows (Configuración › Actualización): el token de GitHub va cifrado por Windows para el
/// usuario que lo escribió, junto a los datos de la prueba (al desinstalar se borra con ellos). El instalador se abre en
/// silencio: Inno Setup pide el permiso de administrador, cierra la caja y la vuelve a abrir (instalacion/prueba.iss).
/// </summary>
internal sealed class EquipoPrueba(string carpetaDatos) : IEquipoPrueba
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
            Process.Start(new ProcessStartInfo(instalador, "/SILENT /SUPPRESSMSGBOXES /NORESTART /CLOSEAPPLICATIONS") { UseShellExecute = true });
        }
        catch (Win32Exception ex)
        {
            throw new ErrorActualizacion($"No se pudo abrir el instalador: {ex.Message}");
        }
    }
}
