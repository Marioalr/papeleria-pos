using System.Text;

namespace PapeleriaPos.Instalacion;

/// <summary>
/// Lo que se pasan el asistente del instalador (Inno Setup), instalar-pc.ps1 y este programa: un dato por renglón, «clave=valor», en
/// UTF-8 con BOM (así lo leen y lo escriben Inno y Windows PowerShell). El primer renglón es un comentario («#»): los que empiezan con
/// «#» no cuentan. Las claves no distinguen mayúsculas; si una se repite, vale la última. Nunca lleva la clave de eleventa ni el código
/// de Control Print: esos no pasan por aquí.
/// </summary>
public sealed class ArchivoDeClaves
{
    private readonly List<(string Clave, string Valor)> _datos = [];

    public IReadOnlyList<(string Clave, string Valor)> Datos => _datos;

    /// <summary>El valor de esa clave (el último, si se repite); vacío si no está.</summary>
    public string? this[string clave]
    {
        get
        {
            for (var i = _datos.Count - 1; i >= 0; i--)
                if (string.Equals(_datos[i].Clave, clave, StringComparison.OrdinalIgnoreCase))
                    return _datos[i].Valor;
            return null;
        }
    }

    /// <summary>Agrega un dato. Un valor va en un solo renglón: sus saltos de línea se vuelven espacios.</summary>
    public ArchivoDeClaves Poner(string clave, string? valor)
    {
        _datos.Add((clave, (valor ?? "").Replace("\r\n", " ").Replace('\r', ' ').Replace('\n', ' ')));
        return this;
    }

    public ArchivoDeClaves Poner(string clave, bool valor) => Poner(clave, valor ? "si" : "no");

    /// <summary>«si» o «no» (también «sí», «true», «1»…); vacío si no está o no es ninguno de los dos.</summary>
    public bool? SiNo(string clave) => this[clave]?.Trim().ToLowerInvariant() switch
    {
        "si" or "sí" or "true" or "1" => true,
        "no" or "false" or "0" => false,
        _ => null,
    };

    public static ArchivoDeClaves Leer(string ruta) => Desde(File.ReadAllLines(ruta, Encoding.UTF8));

    public static ArchivoDeClaves Desde(IEnumerable<string> renglones)
    {
        var archivo = new ArchivoDeClaves();
        foreach (var crudo in renglones)
        {
            var renglon = crudo.TrimStart('﻿');
            if (renglon.Length == 0 || renglon.StartsWith('#'))
                continue;
            var igual = renglon.IndexOf('=');
            if (igual <= 0)
                continue;
            archivo._datos.Add((renglon[..igual].Trim(), renglon[(igual + 1)..]));
        }
        return archivo;
    }

    public void Guardar(string ruta)
    {
        var texto = new StringBuilder("# Papelería POS: lo que se pasan el instalador y su ayudante, un dato por renglón\r\n");
        foreach (var (clave, valor) in _datos)
            texto.Append(clave).Append('=').Append(valor).Append("\r\n");
        if (Path.GetDirectoryName(Path.GetFullPath(ruta)) is { Length: > 0 } carpeta)
            Directory.CreateDirectory(carpeta);
        File.WriteAllText(ruta, texto.ToString(), new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));
    }
}
