using System.Windows;
using Microsoft.Win32;
using PapeleriaPos.Interfaz;

namespace PapeleriaPos.Caja;

/// <summary>
/// «Abrir» de Windows para lo que la caja lee de un archivo (2026-10-22: el Excel de los productos que se importa en Productos ›
/// Productos en Excel), junto a «Guardar como» (<see cref="GuardarComoDeWindows"/>): encima de la ventana de la caja y en su hilo
/// (el de WPF, como pide el diálogo), empieza en la carpeta que se le dice (la del último archivo que se guardó o se abrió; la
/// primera vez, Documentos) y solo deja elegir uno que existe, del tipo que se pide. Solo pregunta: el archivo lo lee
/// <see cref="AbiertoEnLaPc"/>.
/// </summary>
/// <param name="alCerrar">
/// Al cerrarse el diálogo, el teclado de vuelta a la página de la caja (WebView2 no siempre lo recupera solo, y la caja se usa
/// con el teclado).
/// </param>
internal sealed class AbrirDeWindows(Window ventana, Action? alCerrar = null)
{
    /// <returns>Cuál abrir; vacío si se canceló.</returns>
    public Task<string?> PreguntarAsync(PreguntaAbrir pregunta) =>
        ventana.Dispatcher.InvokeAsync(() =>
        {
            var dialogo = new OpenFileDialog
            {
                Title = pregunta.Titulo,
                InitialDirectory = pregunta.Carpeta,
                Filter = pregunta.Filtro,
                CheckFileExists = true,
                Multiselect = false,
            };
            try
            {
                return dialogo.ShowDialog(ventana) == true ? dialogo.FileName : null;
            }
            finally
            {
                alCerrar?.Invoke();
            }
        }).Task;
}
