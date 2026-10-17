using System.IO;
using Microsoft.AspNetCore.Components.WebView.Wpf;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Primitives;
using PapeleriaPos.Interfaz;

namespace PapeleriaPos.Caja;

/// <summary>
/// La página de la caja (BlazorWebView) con las fotos de los productos (entrega 13, parte 2): lo que pide la pantalla como
/// <c>api/fotos/{variante}-{huella}.jpg</c> (el <c>src</c> de su imagen) se lo da esta misma caja, de lo que ya bajó a su base
/// (<see cref="FotosDeLaCaja"/>), sin la red: Ctrl+M, la lista de la búsqueda y el verificador las enseñan también sin conexión.
/// Lo demás (la página, sus estilos y sus scripts) sale de donde siempre.
/// </summary>
public sealed class VistaDeLaCaja : BlazorWebView
{
    public override IFileProvider CreateFileProvider(string contentRootDir) =>
        Services?.GetService<Contingencia>()?.Fotos is { } fotos
            ? new CompositeFileProvider(new ArchivosDeFotos(fotos), base.CreateFileProvider(contentRootDir))
            : base.CreateFileProvider(contentRootDir);

    /// <summary>Las fotos de esta caja como si fueran archivos de la página; lo que no es una foto que tenga, no está.</summary>
    private sealed class ArchivosDeFotos(FotosDeLaCaja fotos) : IFileProvider
    {
        public IFileInfo GetFileInfo(string subpath) =>
            fotos.Archivo(subpath) is { } datos ? new Foto(Path.GetFileName(subpath), datos) : new NotFoundFileInfo(subpath);

        public IDirectoryContents GetDirectoryContents(string subpath) => NotFoundDirectoryContents.Singleton;

        public IChangeToken Watch(string filter) => NullChangeToken.Singleton;
    }

    private sealed class Foto(string nombre, byte[] datos) : IFileInfo
    {
        public bool Exists => true;
        public long Length => datos.Length;
        public string? PhysicalPath => null;
        public string Name => nombre;
        public DateTimeOffset LastModified { get; } = DateTimeOffset.UtcNow;
        public bool IsDirectory => false;
        public Stream CreateReadStream() => new MemoryStream(datos, writable: false);
    }
}
