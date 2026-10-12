namespace PapeleriaPos.Interfaz;

/// <summary>
/// El punto de color de cada valor en la búsqueda. No es el tono exacto, solo una referencia para reconocerlo de un vistazo
/// (decisión del dueño, 2026-10-11). Sale de la clave del color que lee <see cref="Nucleo.Catalogo.LectorDeNombres"/>.
/// </summary>
public static class MuestraDeColor
{
    /// <summary>El color CSS; null para el surtido, que lleva su propio punto de varios colores.</summary>
    public static string? Css(string clave)
    {
        if (clave == "SURTIDO")
            return null;
        if (Tonos.TryGetValue(clave, out var tono))
            return tono;
        // «Azul Medio» y los que no están: el color de su primera palabra.
        var primera = clave.Split(' ')[0];
        return Tonos.GetValueOrDefault(primera, "#b3bbc5");
    }

    private static readonly Dictionary<string, string> Tonos = new()
    {
        ["NEGRO"] = "#222222", ["BLANCO"] = "#ffffff", ["AZUL"] = "#1f4fd6", ["ROJO"] = "#d42a2a", ["VERDE"] = "#1e9a4b",
        ["AMARILLO"] = "#f2c81b", ["ROSA"] = "#ec5fa8", ["MORADO"] = "#7a3fc4", ["NARANJA"] = "#f07d1a", ["CAFE"] = "#7a4a23",
        ["GRIS"] = "#8a8f98", ["LILA"] = "#c3a3e6", ["VIOLETA"] = "#8f4fd8", ["TURQUESA"] = "#19b5b0", ["FUCSIA"] = "#e0218a",
        ["MAGENTA"] = "#d4198f", ["DORADO"] = "#c9a227", ["PLATA"] = "#c0c4c9", ["COBRE"] = "#b87333", ["TRANSPARENTE"] = "#eef2f6",
        ["CREMA"] = "#f3e5c0", ["HUESO"] = "#efe6d2", ["MARFIL"] = "#f6f0dc", ["BEIGE"] = "#d9c4a0", ["OCRE"] = "#c98a1b",
        ["VINO"] = "#7b1e3a", ["GUINDA"] = "#6d1a2e", ["CORAL"] = "#f2735a", ["LAVANDA"] = "#b8a4e3", ["MALVA"] = "#b784a7",
        ["CELESTE"] = "#7cc4ef", ["MARINO"] = "#1b2a6b", ["MELON"] = "#f5a76c", ["TERRACOTA"] = "#c4623b", ["CARMIN"] = "#b5122e",
        ["CIAN"] = "#1fb7d6", ["SEPIA"] = "#8a5a2b", ["PALO DE ROSA"] = "#d8a1a8",
        ["AZUL CIELO"] = "#7cc4ef", ["AZUL CELESTE"] = "#7cc4ef", ["AZUL REY"] = "#1d3fbf", ["AZUL MARINO"] = "#1b2a6b",
        ["AZUL ULTRAMAR"] = "#2a35b8", ["AZUL COBALTO"] = "#1f47a8", ["AZUL PASTEL"] = "#a9c8f0", ["AZUL CLARO"] = "#8fb8f2",
        ["AZUL ELECTRICO"] = "#1f6bff", ["AZUL TURQUESA"] = "#19b5b0", ["AZUL OBSCURO"] = "#1b2f7a",
        ["VERDE LIMON"] = "#9ccc2a", ["VERDE BANDERA"] = "#13843d", ["VERDE OBSCURO"] = "#145c32", ["VERDE CLARO"] = "#7fd18f",
        ["VERDE MANZANA"] = "#8ccf3f", ["VERDE ESMERALDA"] = "#12a06b", ["VERDE HOJA"] = "#3d8b2f", ["VERDE OLIVO"] = "#6b7a2a",
        ["VERDE MILITAR"] = "#4b5a2a", ["VERDE JADE"] = "#20a37a", ["VERDE PASTEL"] = "#a8dcb5",
        ["ROSA MEXICANO"] = "#e4007c", ["ROSA PASTEL"] = "#f6b8d2", ["ROSA FUERTE"] = "#e8368f", ["ROSA BEBE"] = "#f7c6d9",
        ["ROJO CEREZA"] = "#b3122a", ["AMARILLO CANARIO"] = "#ffe03a", ["AMARILLO HUEVO"] = "#f2b81b",
        ["AMARILLO CLARO"] = "#f7e27a", ["AMARILLO LIMON"] = "#e9e43a", ["AMARILLO PASTEL"] = "#f8e9a1",
        ["GRIS OBSCURO"] = "#4f545c", ["GRIS CLARO"] = "#c4c8ce", ["CAFE CLARO"] = "#b07a4a", ["CAFE OBSCURO"] = "#4e2e14",
        ["MORADO CLARO"] = "#b18be0", ["MORADO PASTEL"] = "#cdb6ee", ["NEGRO INTENSO"] = "#111111",
    };
}
