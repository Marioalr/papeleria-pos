using Microsoft.EntityFrameworkCore;
using PapeleriaPos.Nucleo.Catalogo;
using PapeleriaPos.Nucleo.Contratos;
using PapeleriaPos.Nucleo.Control;

namespace PapeleriaPos.Datos;

/// <summary>
/// Las fotos de los productos (entrega 13, parte 2; docs/propuesta-interfaz.md, «Lo que se señala», punto 7): una por
/// variante, la que sale en Ctrl+M, en la lista de la búsqueda y en el verificador. Las sube o las quita quien entró con
/// «Productos, precios y etiquetas» (desde el celular, en la página del conteo, o desde la PC, en Productos), sin volver a
/// escribir su PIN; queda en la bitácora. Llegan ya chicas (las achica el servicio antes de guardarlas): la original nunca se
/// guarda. Viven en la misma base: el respaldo diario y el de cada corte Z las llevan.
/// <para>
/// En el piloto con eleventa también se suben aquí: los productos son de eleventa, pero sus fotos solo las tiene el POS.
/// </para>
/// </summary>
public sealed class RegistroFotos(PosDbContext db)
{
    private const AccionControlada Permiso = AccionControlada.EditarCatalogo;

    private const string SinPermiso = "Las fotos de los productos las pone y las quita quien tiene «Productos, precios y etiquetas».";

    /// <summary>
    /// De qué producto es cada foto y su huella, en el orden de las variantes (el de .NET, el mismo en cada caja): como van en
    /// la copia de las cajas. Sin las fotos: esas se bajan aparte.
    /// </summary>
    public static async Task<List<FotoDeProductoDto>> ReferenciasAsync(PosDbContext db, CancellationToken ct = default) =>
        (await db.FotosProducto.AsNoTracking().Select(f => new { f.VarianteId, f.Huella }).ToListAsync(ct))
        .OrderBy(f => f.VarianteId)
        .Select(f => new FotoDeProductoDto(f.VarianteId, f.Huella))
        .ToList();

    /// <summary>Todas (de qué producto y su huella), con la versión del catálogo con que se leyeron: para la caja sin copia.</summary>
    public async Task<FotosDto> LeerAsync(CancellationToken ct = default)
    {
        // La versión antes que la lista: si algo cambia entre las dos, la lista es más nueva que su versión, nunca más vieja.
        var version = await db.Secuencias.AsNoTracking().Where(s => s.Nombre == Secuencia.Catalogo).Select(s => s.Valor).FirstOrDefaultAsync(ct);
        return new FotosDto(version, await ReferenciasAsync(db, ct));
    }

    /// <summary>La foto de un producto, como la bajan las cajas, con su huella; vacío si no tiene.</summary>
    public async Task<(byte[] Datos, string Huella)?> FotoAsync(Guid varianteId, CancellationToken ct = default) =>
        await db.FotosProducto.AsNoTracking().Where(f => f.VarianteId == varianteId).Select(f => new { f.Datos, f.Huella }).SingleOrDefaultAsync(ct)
            is { } foto
            ? (foto.Datos, foto.Huella)
            : null;

    /// <summary>
    /// Antes de leer la foto que llegó (es lo que más cuesta): que quien la sube pueda y que el producto exista. Así nadie sin
    /// el permiso pone a trabajar al servicio con una imagen.
    /// </summary>
    public async Task RevisarAsync(Guid varianteId, Guid usuarioId, Guid sucursalId, CancellationToken ct = default)
    {
        await new ServicioControl(db).ExigirAsync(usuarioId, sucursalId, Permiso, SinPermiso, ct);
        await NombreAsync(varianteId, ct);
    }

    /// <summary>
    /// La foto ya chica (<paramref name="jpeg"/>) de ese producto: nueva, o en lugar de la que tenía. Si es la misma, no cambia
    /// nada (ni la versión: las cajas no bajan nada). Regresa cómo quedó, con la foto, para enseñarla enseguida.
    /// </summary>
    public async Task<FotoGuardadaDto> GuardarAsync(Guid varianteId, SolicitudFoto s, byte[] jpeg, int ancho, int alto, CancellationToken ct = default)
    {
        var control = new ServicioControl(db);
        await control.ExigirAsync(s.UsuarioId, s.SucursalId, Permiso, SinPermiso, ct);
        var nombre = await NombreAsync(varianteId, ct);
        var huella = FotoProducto.HuellaDe(jpeg);
        var foto = await db.FotosProducto.SingleOrDefaultAsync(f => f.VarianteId == varianteId, ct);
        if (foto?.Huella != huella)
        {
            if (foto is null)
                db.FotosProducto.Add(new FotoProducto { VarianteId = varianteId, Datos = jpeg, Huella = huella, Ancho = ancho, Alto = alto });
            else
                (foto.Datos, foto.Huella, foto.Ancho, foto.Alto) = (jpeg, huella, ancho, alto);
            control.Registrar(s.SucursalId, null, s.UsuarioId, Permiso,
                $"{(foto is null ? "Puso" : "Cambió")} la foto de «{nombre}»{(string.IsNullOrWhiteSpace(s.Desde) ? "" : $" (desde {s.Desde.Trim()})")}");
            try
            {
                await db.SaveChangesAsync(ct);
            }
            catch (DbUpdateException)
            {
                // Dos a la vez con el mismo producto nuevo: la otra ya quedó.
                throw new InvalidOperationException($"Alguien más acaba de guardar la foto de «{nombre}»: vuelve a intentarlo.");
            }
        }
        return new FotoGuardadaDto(varianteId, huella, ancho, alto, jpeg.Length, Convert.ToBase64String(jpeg));
    }

    /// <summary>Quita la foto de un producto: en las cajas vuelven a salir sus iniciales. Si no tenía, no pasa nada.</summary>
    public async Task QuitarAsync(Guid varianteId, SolicitudQuitarFoto s, CancellationToken ct = default)
    {
        var control = new ServicioControl(db);
        await control.ExigirAsync(s.UsuarioId, s.SucursalId, Permiso, SinPermiso, ct);
        if (await db.FotosProducto.SingleOrDefaultAsync(f => f.VarianteId == varianteId, ct) is not { } foto)
            return;
        db.FotosProducto.Remove(foto);
        control.Registrar(s.SucursalId, null, s.UsuarioId, Permiso, $"Quitó la foto de «{await NombreAsync(varianteId, ct)}»");
        await db.SaveChangesAsync(ct);
    }

    /// <summary>El nombre del producto, como se dice en la bitácora; si ya no está, por qué no se puede.</summary>
    private async Task<string> NombreAsync(Guid varianteId, CancellationToken ct)
    {
        var variante = await db.Variantes.AsNoTracking().Where(v => v.Id == varianteId)
            .Select(v => new { Producto = v.Producto!.Nombre, v.Descripcion })
            .SingleOrDefaultAsync(ct) ?? throw new ArgumentException("Ese producto ya no está en el catálogo.");
        return string.IsNullOrWhiteSpace(variante.Descripcion) ? variante.Producto : $"{variante.Producto} {variante.Descripcion}";
    }
}
