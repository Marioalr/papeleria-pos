using System.Windows;
using Microsoft.Win32;
using PapeleriaPos.Interfaz;

namespace PapeleriaPos.Caja;

/// <summary>
/// «Guardar como» de Windows para lo que guarda la caja (entrega 9, parte 2, 2026-10-22: el Excel de los reportes), encima de la
/// ventana de la caja y en su hilo (el de WPF, como pide el diálogo): empieza en la carpeta que se le dice (la primera vez,
/// Documentos) con el nombre que propone la caja principal («Ventas 2026-10.xlsx»), y si el archivo ya existe pregunta si se
/// reemplaza. Solo pregunta: el archivo lo escribe <see cref="GuardadoEnLaPc"/>.
/// </summary>
/// <param name="alCerrar">
/// Al cerrarse el diálogo, el teclado de vuelta a la página de la caja (WebView2 no siempre lo recupera solo, y la caja se usa
/// con el teclado).
/// </param>
internal sealed class GuardarComoDeWindows(Window ventana, Action? alCerrar = null)
{
    /// <returns>Dónde guardarlo; vacío si se canceló.</returns>
    public Task<string?> PreguntarAsync(PreguntaGuardarComo pregunta) =>
        ventana.Dispatcher.InvokeAsync(() =>
        {
            var dialogo = new SaveFileDialog
            {
                Title = "Guardar el archivo",
                InitialDirectory = pregunta.Carpeta,
                FileName = pregunta.Nombre,
                // Sin el punto, como lo pide el diálogo de Windows («xlsx»).
                DefaultExt = pregunta.Extension.TrimStart('.'),
                AddExtension = true,
                Filter = pregunta.Filtro,
                OverwritePrompt = true,
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
