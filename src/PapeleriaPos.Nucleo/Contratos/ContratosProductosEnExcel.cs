using PapeleriaPos.Nucleo.Catalogo;

namespace PapeleriaPos.Nucleo.Contratos;

// ── Productos en Excel: importar (Productos › Productos en Excel, 2026-10-22) ───────
// Como «Importar» del catálogo de eleventa, pero siempre con una vista previa: el Excel que se exportó, ya cambiado, se manda a
// la caja principal, que dice qué cambia producto por producto, qué es nuevo y qué se ignora (y por qué); nada cambia hasta
// aplicarlo. Lo hace quien entró con «Productos, precios y etiquetas»; el costo y el margen solo los ve quien tiene «Ver
// costos, utilidad y margen». En el piloto con eleventa no se importa.

/// <summary>Ver qué cambia con un Excel, o aplicarlo: el mismo archivo, para que lo que se aplica sea lo que se vio.</summary>
/// <param name="UsuarioId">Quien entró a la caja: con «Productos, precios y etiquetas» en «Puede».</param>
/// <param name="Archivo">Cómo se llama el archivo, para decirlo y para la bitácora.</param>
/// <param name="ExcelBase64">El .xlsx tal cual, hasta <see cref="ProductosEnExcel.BytesMaximos"/>.</param>
/// <param name="Huella">Al aplicar, la de la vista que se vio: si algo cambió desde entonces, no se aplica.</param>
/// <param name="Id">
/// Al aplicar, el de esta vez (lo pone la caja con la vista): si la respuesta se pierde y se vuelve a pedir, no se aplica dos
/// veces.
/// </param>
/// <param name="ConLosQueCambiaron">
/// «Aplicarlos de todos modos»: también lo de los productos que cambiaron después de exportar el Excel (el Excel trae lo de
/// antes). Sin esto, esos se enseñan aparte y no se aplican.
/// </param>
public sealed record SolicitudImportarProductos(
    Guid UsuarioId, Guid SucursalId, string Archivo, string ExcelBase64, string? Huella = null, Guid? Id = null, bool ConLosQueCambiaron = false);

/// <summary>Un dato que cambia: «Precio: $12.50 → $13.00».</summary>
/// <param name="Dato">«Nombre», «Precio», «Mayoreo desde 12», «Códigos», «Caja 12: precio»…</param>
/// <param name="Antes">Como está («—» si no tenía).</param>
/// <param name="Despues">Como queda («—» si se quita).</param>
/// <param name="Revisar">
/// Si conviene verlo antes de aplicar (va resaltado): un precio que baja o que sube a más del doble, un código que se quita…
/// </param>
public sealed record CambioDeDatoDto(string Dato, string Antes, string Despues, PorQueSeRevisa? Revisar = null);

/// <summary>Un producto (una de sus variantes) que cambia, con lo que le cambia.</summary>
/// <param name="Hoja">De dónde salió: «Productos», o «Presentaciones» si solo cambian sus presentaciones.</param>
/// <param name="Renglon">Su renglón en Excel.</param>
/// <param name="Producto">Como se llama ahora (con su variante).</param>
/// <param name="MargenAntesCentesimas">
/// Si cambia su menudeo: el margen de ahora contra su costo promedio. Solo para quien ve la utilidad, y si se sabe su costo.
/// </param>
/// <param name="MargenMinimoCentesimas">El de su departamento o el general: si el que queda es menor, se avisa.</param>
/// <param name="CambioDespues">
/// Si cambió después de exportar el Excel: qué cambió («Precio: $25.00 → $32.00», «sus códigos»…). El Excel trae lo de antes, así
/// que aplicarlo lo regresaría.
/// </param>
public sealed record ProductoQueCambiaDto(
    Guid ProductoId, Guid VarianteId, string Hoja, int Renglon, string? Codigo, string Producto, IReadOnlyList<CambioDeDatoDto> Cambios,
    int? MargenAntesCentesimas = null, int? MargenDespuesCentesimas = null, int? MargenMinimoCentesimas = null,
    IReadOnlyList<string>? CambioDespues = null)
{
    /// <summary>Si conviene verlo antes de aplicar: cambió después de exportar, o algo de lo que cambia va resaltado.</summary>
    public bool Revisar => CambioDespues is not null || Cambios.Any(c => c.Revisar is not null);
}

/// <summary>Un producto nuevo: un código que no tenía ningún producto.</summary>
/// <param name="Detalle">Lo demás que lleva: «mayoreo desde 12 a $5.00 · Caja 12 · mínimo 5».</param>
/// <param name="Aviso">Si ya hay uno que se llama igual: quizá se le cambió el código a uno que ya existía.</param>
public sealed record ProductoNuevoDeExcelDto(
    int Renglon, string Codigo, string Producto, string Departamento, long PrecioCentavos, string? Detalle, string? Aviso);

/// <summary>Un renglón que no se importa, y por qué.</summary>
/// <param name="Hoja">«Productos» o «Presentaciones».</param>
/// <param name="Detalle">Lo que se le dice a quien importa: qué está mal y cómo corregirlo.</param>
public sealed record RenglonIgnoradoDto(string Hoja, int Renglon, string? Codigo, string? Producto, PorQueSeIgnora PorQue, string Detalle);

/// <summary>Lo que pasaría con ese Excel, sin cambiar nada. Las listas van completas (la caja las enseña por páginas).</summary>
/// <param name="Renglones">Los renglones con datos que se leyeron, de las dos hojas.</param>
/// <param name="Cambian">
/// Los que cambian al aplicar: primero los que conviene revisar (<see cref="ProductoQueCambiaDto.Revisar"/>), luego los demás,
/// cada grupo en el orden del archivo.
/// </param>
/// <param name="Nuevos">Los productos nuevos, en el orden del archivo.</param>
/// <param name="Ignorados">Los renglones que no se importan, con su porqué.</param>
/// <param name="SinCambios">Los renglones que ya estaban así.</param>
/// <param name="DepartamentosNuevos">Los que se dan de alta al aplicar (un nombre que no tenía ningún departamento).</param>
/// <param name="MarcasNuevas">Las marcas que se dan de alta al aplicar.</param>
/// <param name="Avisos">Lo demás que conviene saber: las columnas que no se usan, que la existencia y el costo no se importan.</param>
/// <param name="ConMargen">Si trae el margen (quien ve la utilidad).</param>
/// <param name="Huella">Lo que se vio: al aplicar tiene que ser igual.</param>
/// <param name="CambiaronDespues">
/// Los productos que cambiaron después de exportar el Excel (o todos los que ya existían, si el Excel no dice cuándo se exportó):
/// lo que el Excel les cambiaría no se aplica, salvo con <see cref="SolicitudImportarProductos.ConLosQueCambiaron"/> (entonces van
/// en <paramref name="Cambian"/>, primero).
/// </param>
/// <param name="ConLosQueCambiaron">Si se pidió aplicarlos de todos modos.</param>
public sealed record VistaImportacionDto(
    string Archivo, int Renglones, IReadOnlyList<ProductoQueCambiaDto> Cambian, int TotalCambian, IReadOnlyList<ProductoNuevoDeExcelDto> Nuevos,
    int TotalNuevos, IReadOnlyList<RenglonIgnoradoDto> Ignorados, int TotalIgnorados, int SinCambios, IReadOnlyList<string> DepartamentosNuevos,
    IReadOnlyList<string> MarcasNuevas, IReadOnlyList<string> Avisos, bool ConMargen, string Huella,
    IReadOnlyList<ProductoQueCambiaDto>? CambiaronDespues = null, bool ConLosQueCambiaron = false)
{
    /// <summary>Si hay algo que aplicar.</summary>
    public bool HayQueAplicar => TotalCambian + TotalNuevos > 0;
}

/// <summary>Lo que se aplicó: cuántos cambiaron, cuántos se dieron de alta, y el renglón de la bitácora.</summary>
/// <param name="YaSeHabiaAplicado">Ya se había aplicado con ese Id (la respuesta se perdió): no se hizo otra vez.</param>
public sealed record ImportacionAplicadaDto(int Cambiaron, int Nuevos, int DepartamentosNuevos, string Bitacora, bool YaSeHabiaAplicado = false);

/// <summary>
/// Lo que aplicó una importación, completo (va con su renglón de la bitácora, que solo dice cuántos): cada producto con lo de
/// antes y lo que quedó, los nuevos, y los departamentos y marcas que se dieron de alta. Se ve en Configuración › Bitácora, en
/// Excel.
/// </summary>
/// <param name="VersionDelExcel">La versión del catálogo con que se exportó el Excel; vacía si no la traía.</param>
/// <param name="Ignorados">Cuántos renglones no se importaron.</param>
/// <param name="SinAplicar">Cuántos productos que cambiaron después de exportar se quedaron como estaban.</param>
public sealed record DetalleDeImportacionDto(
    string Archivo, long? VersionDelExcel, IReadOnlyList<ProductoQueCambiaDto> Cambiaron, IReadOnlyList<ProductoNuevoDeExcelDto> Nuevos,
    IReadOnlyList<string> DepartamentosNuevos, IReadOnlyList<string> MarcasNuevas, int Ignorados, int SinAplicar);
