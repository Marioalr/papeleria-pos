using System.ComponentModel;
using System.Diagnostics;

namespace PapeleriaPos.Pruebas.Interfaz;

/// <summary>Se salta si en esta PC no está node (en el CI sí está: lo traen las de GitHub).</summary>
public sealed class FactConNodeAttribute : FactAttribute
{
    public FactConNodeAttribute()
    {
        if (!Node.Esta)
            Skip = "Sin node: caja.js se prueba con node.";
    }
}

/// <summary>Correr node con un archivo y sus argumentos.</summary>
internal static class Node
{
    private static readonly Lazy<bool> _esta = new(() =>
    {
        try
        {
            return Correr("--version").Codigo == 0;
        }
        catch (Win32Exception)
        {
            return false;
        }
    });

    public static bool Esta => _esta.Value;

    public static (int Codigo, string Salida) Correr(params string[] argumentos)
    {
        var info = new ProcessStartInfo("node") { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        foreach (var argumento in argumentos)
            info.ArgumentList.Add(argumento);
        using var proceso = Process.Start(info)!;
        var salida = proceso.StandardOutput.ReadToEndAsync();
        var errores = proceso.StandardError.ReadToEndAsync();
        if (!proceso.WaitForExit(TimeSpan.FromSeconds(60)))
        {
            proceso.Kill(entireProcessTree: true);
            throw new TimeoutException("node no terminó.");
        }
        return (proceso.ExitCode, salida.Result + errores.Result);
    }
}

/// <summary>
/// caja.js en node, con un DOM mínimo (CajaJs/prueba-caja.mjs; revisión del PR #133): lo que manda a la pantalla con cada tecla.
/// F4 en el monto del abono es Inventario (la nota es del cobro de la venta, sin ventana); con «Varios», F11 manda lo escrito
/// (la pantalla lo regresa al cuadro si el panel no se deja); el Enter del lector es el código, y lo tecleado a mano no.
/// </summary>
public sealed class CajaJsTests
{
    [FactConNode]
    public void Las_teclas_de_caja_js_llegan_a_la_pantalla_como_se_espera()
    {
        var raiz = RaizDelRepositorio();
        // Como módulo (.mjs): node no tiene que adivinar qué es caja.js.
        var copia = Path.Combine(Path.GetTempPath(), $"caja-{Guid.NewGuid():N}.mjs");
        File.Copy(Path.Combine(raiz, "src", "PapeleriaPos.Interfaz", "wwwroot", "caja.js"), copia);
        try
        {
            var (codigo, salida) = Node.Correr(Path.Combine(raiz, "tests", "PapeleriaPos.Pruebas", "Interfaz", "CajaJs", "prueba-caja.mjs"), copia);

            Assert.True(codigo == 0, salida);
            Assert.Contains("todo ok", salida);
        }
        finally
        {
            File.Delete(copia);
        }
    }

    private static string RaizDelRepositorio()
    {
        for (var carpeta = new DirectoryInfo(AppContext.BaseDirectory); carpeta is not null; carpeta = carpeta.Parent)
            if (File.Exists(Path.Combine(carpeta.FullName, "src", "PapeleriaPos.Interfaz", "wwwroot", "caja.js")))
                return carpeta.FullName;
        throw new DirectoryNotFoundException("No encontré la raíz del repositorio.");
    }
}
