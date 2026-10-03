# Modelo de datos — fase 0

> **Propuesta para revisar.** Cubre solo lo que pide la fase 0 (catálogo, precios, proveedores, kardex e histórico
> importado). Ventas, turnos, usuarios, pedidos y recetas se agregan en su fase, sin cambiar lo de aquí.

## Diagrama

```mermaid
erDiagram
    Sucursal ||--o{ MovimientoInventario : "registra"
    Departamento ||--o{ Producto : "agrupa"
    Producto ||--|{ Variante : "tiene"
    Variante ||--|{ Presentacion : "se vende en"
    Presentacion ||--o{ CodigoBarras : "se identifica con"
    Presentacion ||--o{ Precio : "cuesta"
    Sucursal |o--o{ Precio : "precio propio (opcional)"
    Proveedor ||--o{ ProveedorVariante : "surte"
    Variante ||--o{ ProveedorVariante : ""
    Variante ||--o{ MovimientoInventario : ""
    Variante ||--o{ Existencia : ""
    Sucursal ||--o{ Existencia : ""
    VentaHistorica ||--|{ VentaHistoricaLinea : ""
    Variante ||--o{ VentaHistoricaLinea : ""
```

## Reglas generales

| Regla | Detalle |
|---|---|
| IDs | UUIDv7 (`Guid.CreateVersion7()`), generados donde nace el registro |
| Dinero | Centavos enteros (`long`). Precios al público **con IVA incluido** |
| Cantidades | Enteros en la **unidad base** de la variante (pieza, hoja, cm…). Ver [pregunta 1](#preguntas) |
| Fechas | UTC; se muestran en hora de Ciudad de México |
| Enumeraciones | Se guardan como texto (`"Compra"`, no `2`): la base se lee sin el código |
| Catálogo | Nunca se borra, se desactiva (`Activo = false`): el kardex y el histórico lo siguen apuntando |
| Sincronización | Las tablas del catálogo llevan `Version` y `ModificadoUtc` desde ya, para bajar cambios por versión después |
| Sucursal | Todo lo que pertenece a una sucursal lleva `SucursalId`, aunque hoy solo exista la matriz |

## Catálogo

**Sucursal**: `Id`, `Clave` (corta, para folios: `M`, `S2`), `Nombre`, `Activa`.

**Departamento**: `Id`, `Nombre`, `Activo`. Plano, como en eleventa.

**Producto** es el modelo que se ve en la búsqueda ("Pluma Bic punto fino"):

| Campo | Notas |
|---|---|
| `Nombre`, `Marca`, `DepartamentoId` | |
| `Tipo` | `Mercancia`, `Servicio`, `Kit`, `CobroTerceros` (recargas y pagos de servicios: solo la comisión es ingreso) |
| `ClaveSat`, `ClaveUnidadSat`, `Impuesto` | Fiscales. `Impuesto`: `Iva16`, `Iva0` o `Exento` |
| `Activo` | |

Solo `Mercancia` lleva inventario. Servicios y kits existen desde ya para que la importación los distinga; sus recetas
y componentes llegan en las fases 3 y 4.

**Variante** es lo que se cuenta y se vende ("Azul"). Un producto sin variantes tiene exactamente una.

| Campo | Notas |
|---|---|
| `ProductoId`, `Descripcion` | `Descripcion` en texto libre ("Azul", "Carta · 75 g"). Vacía si es la única |
| `UnidadBase` | `Pieza`, `Hoja`, `Centimetro`… |
| `ManejaCaducidad` | Para los pocos productos que caducan |
| `Activa` | |

**Presentacion**: `VarianteId`, `Nombre` ("Pieza", "Paquete 12", "Caja 144"), `Factor` (unidades base que contiene,
entero ≥ 1), `EsBase`. Cada variante tiene una presentación base con factor 1.

**CodigoBarras**: `Codigo` (único en todo el catálogo), `PresentacionId`, `Origen` (`Fabrica` o `Interno`). El
código apunta a la presentación, no a la variante: escanear el código de la caja vende una caja. Una presentación
puede tener varios códigos.

**Precio**: `PresentacionId`, `SucursalId` (vacío = precio base), `CantidadMinima` (1 = menudeo; 12, 50… =
escalas de mayoreo), `PrecioCentavos`.

- Para cobrar se busca primero el precio de la sucursal y, si no hay, el base. Luego se usa la escala más alta que
  alcance la cantidad.
- Cada presentación tiene precio propio (un paquete no tiene por qué costar pieza × 12). Ver [pregunta 2](#preguntas).
- Cada cambio de precio se copia a `PrecioHistorial` (presentación, sucursal, escala, precio anterior y nuevo, fecha).
  Sirve para el análisis y para la bitácora de la fase 1.

**Proveedor**: `Nombre`, `Telefono`, `Rfc`, `Notas`, `Activo`.

**ProveedorVariante**: `ProveedorId`, `VarianteId`, `CodigoProveedor`, `UltimoCostoCentavos` (por unidad base),
`UltimaCompraUtc`.

## Kardex

**MovimientoInventario**: solo se agregan filas; nunca se editan ni se borran. Un error se corrige con otro
movimiento.

| Campo | Notas |
|---|---|
| `SucursalId`, `VarianteId`, `FechaUtc` | |
| `Tipo` | `InventarioInicial`, `Venta`, `Devolucion`, `Compra`, `Ajuste`, `Merma`, `TraspasoSalida`, `TraspasoEntrada`, `ConsumoServicio`, `AperturaPaquete`, `Conteo` |
| `Cantidad` | Con signo, en unidad base: + entra, − sale |
| `CostoCentavos` | **Costo total** del movimiento, con signo (no unitario) |
| `DocumentoTipo`, `DocumentoId` | De dónde salió (venta, compra, conteo…). Vacíos en fase 0 salvo la importación |
| `Motivo` | Obligatorio en ajustes y mermas |

Por qué costo total y no unitario: el costo de una pieza que viene en una caja de 144 por $100 es 69.44 centavos, y
con centavos enteros se perdería. Con el total, el valor del inventario es exacto:

- **Existencia** = Σ `Cantidad`
- **Valor** = Σ `CostoCentavos`
- **Costo promedio** = valor ÷ existencia (se calcula, no se guarda)
- Una **entrada** (compra) lleva su costo real. Una **salida** lleva `costo promedio × cantidad`, redondeado al
  centavo.
- Con existencia en cero o negativa (se permite vender sin existencia), la salida usa el último costo conocido.

El costo promedio es **por sucursal**: un traspaso sale de la matriz a su promedio y entra a la sucursal 2 con ese
mismo costo. Ver [pregunta 3](#preguntas).

**Existencia** (`SucursalId`, `VarianteId`, `Cantidad`, `CostoCentavos`): copia de las sumas de arriba para que la
caja no tenga que sumar todo el kardex en cada consulta. Se actualiza en la misma transacción que el movimiento y se
puede reconstruir desde el kardex en cualquier momento; una prueba verifica que siempre coincidan.

El **disponible** (existencia menos lo apartado en pedidos) llega con los pedidos, en la fase 4.

## Histórico importado

El historial de eleventa no tiene folios por caja, turnos ni cortes, así que no se mezcla con las ventas del POS.
Solo alimenta el análisis ABC, la temporada y el sugerido de compra.

**VentaHistorica**: `SucursalId`, `FechaUtc`, `FolioOrigen`, `TotalCentavos`, `Cancelada`.

**VentaHistoricaLinea**: `VentaHistoricaId`, `VarianteId`, `Cantidad`, `PrecioCentavos`, `CostoCentavos` (como lo
guardó eleventa).

**MapeoImportacion**: `Origen` (`eleventa`), `Entidad`, `IdOrigen`, `IdNuevo`. Permite repetir la importación sin
duplicar y rastrear de dónde salió cada registro. En fase 0 lo usa el generador de datos ficticios, para probar ese
camino desde el principio.

## Cómo queda en el código

- **`PapeleriaPos.Nucleo`**: las entidades como clases simples, sin atributos de EF, y las reglas (precio que
  aplica, costo de una salida, existencia). No conoce la base de datos.
- **`PapeleriaPos.Datos`**: el `DbContext`, la configuración de cada tabla (Fluent API), las conversiones (enums a
  texto) y las migraciones.
- **`PapeleriaPos.Herramientas`** (nuevo, consola):
  - `generar-ficticios`: crea una base con productos de papelería inventados y una semilla fija (siempre salen
    iguales). Unas 200 familias para llegar a ~3,000 variantes, con presentaciones, mayoreo, proveedores y un año de
    ventas con el pico de regreso a clases.
  - `diagnostico`: el Excel, primero sobre la base ficticia.
  - `importar-eleventa`: al final de la fase.
- **`PapeleriaPos.Pruebas`**: reglas del núcleo sin base de datos, y pruebas de la capa de datos contra SQLite en
  memoria.

## Preguntas

1. **¿Se vende algo con fracciones?** Listón o forro por metro, algo a granel… La propuesta usa cantidades enteras
   en una unidad chica (centímetros para el listón). Si hay casos que no encajan, se cambia a decimales.
2. **Precio del paquete:** ¿siempre tiene precio propio o a veces es solo pieza × cantidad? La propuesta da precio
   propio a cada presentación, y el generador lo sugiere al darlo de alta.
3. **Costo promedio por sucursal o uno solo para todas.** La propuesta es por sucursal: es más exacto si una
   sucursal compra directo a un proveedor más caro.
4. **Variantes en texto libre** ("Azul") o con atributos (Color = Azul, Tamaño = Carta). La propuesta es texto
   libre por ahora. Los atributos sirven para filtrar ("todas las plumas azules") y se pueden agregar después.
5. **IEPS:** ¿se vende algo que lo lleve (dulces, refrescos)? Si sí, `Impuesto` necesita esa opción.
