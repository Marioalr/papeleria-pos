namespace PapeleriaPos.Nucleo.Configuracion;

/// <summary>
/// Un botón de la fila de rápidos de la venta: vende <see cref="Cantidad"/> de esa variante. Va por la variante y no por un
/// código porque los servicios (copias, impresiones) casi nunca tienen uno. Así «CURP» vende 10 impresiones mientras
/// Control Print siga aparte (docs/propuesta-interfaz.md, «Rápidos»). Con un número escrito en el cuadro, se multiplica:
/// «20» y Alt+1 son veinte copias.
/// </summary>
public sealed record Rapido(string Nombre, Guid VarianteId, decimal Cantidad = 1)
{
    /// <summary>A lo más seis: Alt+1 a Alt+6.</summary>
    public const int Maximo = 6;

    /// <summary>Lo largo que puede ser su nombre: es lo que dice su botón en la fila de la venta.</summary>
    public const int LargoNombre = 24;

    /// <summary>Lo más que vende de una vez (el de la CURP vende 10).</summary>
    public const decimal CantidadMaxima = 9999;
}
