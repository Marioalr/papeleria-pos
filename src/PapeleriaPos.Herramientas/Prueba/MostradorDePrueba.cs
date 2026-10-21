using Microsoft.EntityFrameworkCore;
using PapeleriaPos.Datos;
using PapeleriaPos.Nucleo;
using PapeleriaPos.Nucleo.Catalogo;

namespace PapeleriaPos.Herramientas.Prueba;

/// <summary>
/// Lo que se señala en la base de la prueba con una persona real (entrega 13): el mostrador con productos de la papelería
/// ficticia en sus dos repisas (lo que se pregunta y se señala: calculadoras, el compás de precisión, la memoria USB…) y la
/// mesa de cada temporada con lo suyo. Así Ctrl+M y el chip de la temporada que corra el día de la prueba tienen qué tocar
/// sin acomodar antes. Solo si no hay nada acomodado: lo que se acomode en la prueba se queda.
/// </summary>
public static class MostradorDePrueba
{
    /// <summary>Lo de cada lugar: el exhibidor (o la temporada), el lugar y los productos (su nombre, su marca, su variante).</summary>
    private static readonly (string Exhibidor, string Lugar, (string Producto, string? Marca, string Variante)[] Productos)[] Acomodo =
    [
        ("Mostrador", "Repisa de arriba",
            [("Calculadora", "Casio", "Básica"), ("Calculadora", "Casio", "Científica"), ("Compás", "Maped", "De precisión"), ("Memoria USB", "Kingston", "32 GB")]),
        ("Mostrador", "Repisa de abajo",
            [("Acuarelas", "Pelikan", "12 colores"), ("Marcatextos", "Sharpie", "Pastel"), ("Agenda", null, "2027")]),
        ("15 de septiembre", "Mesa de la entrada",
            [("Estampas", null, "Banderas"), ("Papel china", null, "Verde"), ("Papel china", null, "Blanco"), ("Papel china", null, "Rojo")]),
        ("Todos Santos", "Mesa de la entrada",
            [("Estampas", null, "Día de muertos"), ("Estampas", null, "Calaveras"), ("Estampas", null, "Halloween"), ("Papel china", null, "Naranja"),
             ("Papel china", null, "Morado")]),
        ("Navidad", "Mesa de la entrada",
            [("Estampas", null, "Navidad"), ("Papel para envolver", null, "Navidad"), ("Libro para colorear", null, "Navidad"), ("Agenda", null, "2027")]),
    ];

    public static async Task<List<string>> AcomodarAsync(PosDbContext db, CancellationToken ct = default)
    {
        if (await db.ProductosExhibidos.AnyAsync(ct))
            return [];
        var lugares = await db.LugaresExhibidor.Include(l => l.Exhibidor).Where(l => l.Exhibidor!.Activo).ToListAsync(ct);
        var variantes = await db.Variantes.AsNoTracking()
            .Where(v => v.Activa && v.Producto!.Activo)
            .OrderBy(v => v.Id)
            .Select(v => new { v.Id, Producto = v.Producto!.Nombre, Marca = v.Producto.Marca == null ? null : v.Producto.Marca.Nombre, v.Descripcion })
            .ToListAsync(ct);
        var puestos = 0;
        foreach (var (exhibidor, nombreLugar, productos) in Acomodo)
        {
            if (lugares.FirstOrDefault(l => l.Exhibidor!.Nombre == exhibidor && l.Nombre == nombreLugar) is not { } lugar)
                continue;
            var posicion = 0;
            foreach (var (producto, marca, variante) in productos)
            {
                var deEse = variantes.Where(v => v.Producto == producto && Texto.Normalizar(v.Descripcion).Contains(Texto.Normalizar(variante), StringComparison.Ordinal)).ToList();
                if ((deEse.FirstOrDefault(v => v.Marca == marca) ?? deEse.FirstOrDefault()) is not { } elegida)
                    continue;
                db.ProductosExhibidos.Add(new ProductoExhibido { LugarId = lugar.Id, VarianteId = elegida.Id, Posicion = ++posicion });
                puestos++;
            }
        }
        if (puestos == 0)
            return [];
        await db.SaveChangesAsync(ct);
        return [$"Mostrador y temporadas con {puestos} productos acomodados (Ctrl+M)"];
    }
}
