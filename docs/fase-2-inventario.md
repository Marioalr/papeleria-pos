# Fase 2 — Inventario

> **Revisado.** Con la fase 1 la caja vende, controla y cobra impresiones; el catálogo y las existencias todavía son
> los que llegaron de eleventa y no hay dónde cambiarlos (F3 Productos y F4 Inventario están deshabilitados). Esta
> fase le da al POS el control del inventario: qué hay, qué llega, qué se pierde y qué comprar. Parte del diseño de
> [catálogo e inventario](inventario.md) y de la [depuración](plan.md#depuración-del-inventario) que va antes del piloto.

**Avance:** la entrega 1 (catálogo) está completa.

Parte 1:

- **F3 Productos:** buscar por nombre, marca o código (también los inactivos y lo que está por completar) y abrir la
  ficha.
- **La ficha:** variantes, presentaciones con sus códigos, precios de menudeo y mayoreo, existencia y kardex. El costo
  promedio y los proveedores solo los ven el dueño y el encargado. Lo guardan ellos dos (regla "Editar el catálogo").
- **Precios:** cada cambio queda en el historial y en la bitácora y avisa por Telegram, salvo los del dueño.
- **Código interno:** un botón genera un EAN-13 que empieza con 2 (el rango de uso interno) para lo que no trae código.
- **Alta rápida:** si se escanea un código que no existe, el cajero lo da de alta con nombre, precio y departamento,
  entra al ticket y queda "por completar".

Parte 2, la depuración (en la ficha, solo el dueño y el encargado):

- **Dar de baja:** sale de la venta y su existencia queda en cero con un ajuste; su historia se conserva.
- **Juntar con otro:** los códigos (con sus presentaciones), la existencia con su costo y los proveedores pasan al que se
  queda; el otro queda inactivo, con su historia y apuntando al que se quedó. Se quedan los precios del que se queda.
- **Cambiar la unidad** (el listón en metro, no en kilo): con la conversión, la existencia y las presentaciones se
  convierten; sin ella, la existencia queda en cero para contarla. El precio de menudeo es el nuevo.
- **Las decisiones** se ven en F3 → "Decisiones de depuración" y guardan el ID de eleventa. En la importación final se
  repiten con:

  ```
  dotnet run --project src/PapeleriaPos.Herramientas -- aplicar-depuracion --desde <base depurada> --base <importación nueva>
  ```

  Lo que no vino de eleventa (productos creados en el POS) y los cambios de la ficha (precios, presentaciones) no se
  repiten solos.

## Qué ya existe

- **Catálogo** importado de eleventa: productos y variantes, códigos, departamentos, marcas, presentaciones (pieza,
  paquete, caja), precios con mayoreo, proveedores con su último costo.
- **Kardex:** la existencia es la suma de movimientos. Hoy solo se mueve con el inventario inicial, las ventas y las
  devoluciones.
- **Reglas, autorizaciones, bitácora y Telegram** de la fase 1: lo nuevo de esta fase usa lo mismo.

## Entregas

| # | Entrega | Qué incluye |
|---|---|---|
| 1 | **Catálogo (F3 Productos)** | Alta y edición de productos y variantes, códigos (de fábrica e internos), presentaciones, precios con historial, departamentos, marcas y proveedores; dar de baja; juntar productos capturados dos veces |
| 2 | **Compras (F4 Inventario)** | Recepción ciega de mercancía, el documento del proveedor (PDF o XML) cargado para no capturar a mano, orden de compra opcional, costo promedio y último costo por proveedor, aviso de margen bajo, abrir paquete |
| 3 | **Ajustes, mermas y conteos** | Ajustes y mermas con motivo y regla, conteo cíclico ciego diario en la caja, conteo completo por departamento desde el celular |
| 4 | **Etiquetas y sugerido de compra** | Etiquetas y códigos internos en hojas adhesivas en la WF-6590, hoja de códigos para lo diminuto, mínimos y máximos con el historial y la temporada, lista de compra por proveedor |

## 1. Catálogo (F3 Productos)

- **Buscar y editar** como en eleventa: por nombre, código o departamento. La ficha de un producto junta sus variantes,
  códigos, presentaciones, precios (con mayoreo), proveedores, existencia y su kardex.
- **Quién:** el dueño y el encargado dan de alta y editan. El cajero solo da de alta desde la caja un código que
  escaneó y no existe (nombre, precio, departamento); queda "por completar" para que el encargado lo termine.
- **Códigos internos** para lo que no trae código: se generan solos (Code 128 corto) y se imprimen en la entrega 4.
- **Precios:** cada cambio queda en el historial y en la bitácora, y te avisa por Telegram salvo los que hiciste tú.
  Las cajas reciben el catálogo nuevo sin reiniciar (ya lo hacen con la versión de datos).
- **Dar de baja** un producto lo quita de la venta pero conserva su historia. **Juntar** dos productos capturados dos
  veces pasa códigos, existencia y kardex a uno.
- **La depuración se hace aquí,** sobre la base importada y mientras eleventa sigue en uso. Lo que se decide (dar de
  baja, juntar, cambiar la unidad a metro o kilo) se guarda para repetirlo igual en la importación final, antes del
  piloto.

## 2. Compras (F4 Inventario)

- **Recepción ciega:** quien recibe escanea o busca cada producto y escribe cuántos llegaron, **sin ver** cuántos
  decía la nota o la orden. Después se capturan los costos de la nota; las diferencias (faltó, sobró, llegó otro)
  quedan marcadas para reclamarlas al proveedor.
- **El documento del proveedor:** siempre llega uno (nota o factura). Se carga su PDF, o el XML si es factura, y el
  POS lee sus renglones (código del proveedor, descripción, cantidad y costo) para no capturarlos a mano. Lo que no
  reconoce se liga una vez al producto y la siguiente vez ya lo sabe. El documento lo carga quien captura los
  costos; quien recibe sigue contando a ciegas, y el POS compara las dos cosas.
- **Orden de compra opcional:** se arma (o sale del sugerido de la entrega 4) y al recibir se compara con lo que llegó.
- **Costo:** cada compra actualiza el **costo promedio** (el que usa la utilidad) y el **último costo** de ese
  proveedor. Si con el costo nuevo el margen de un producto baja de un mínimo, avisa y sugiere precio; el precio no
  cambia solo.
- **Abrir paquete:** convierte un paquete o una caja en piezas sin perder el rastro (ya existe el movimiento).
- Todo entra al kardex con su documento (la recepción) y queda en la bitácora.

## 3. Ajustes, mermas y conteos

- **Ajuste:** corrige una existencia con motivo obligatorio; según la regla, pide autorización (como las de la
  fase 1). **Merma:** producto dañado, caducado o perdido; su costo es pérdida en el resumen.
- **Conteo cíclico diario:** el POS elige 15 productos al día, primero los de más valor y rotación y los que no se
  cuentan hace tiempo. Se cuentan **a ciegas** en la caja y la diferencia queda como movimiento de conteo; si pasa
  de un umbral, avisa por Telegram. Se pide al abrir el turno o en un rato tranquilo; más adelante lo puede pedir el
  [asistente inteligente](ideas.md#asistente-inteligente-piloto-automático) cuando detecte la calma.
- **Conteo completo** (el de arranque antes del piloto, y uno al año): por departamento, con varias personas contando
  a la vez desde el celular (de quienes estén presentes) conectado al wifi de la tienda. Al cerrar un departamento,
  lo contado reemplaza la existencia.

## 4. Etiquetas y sugerido de compra

- **Etiquetas** en hojas adhesivas tamaño carta, impresas en la WF-6590: código interno (Code 128) y nombre. Casi
  nada lleva precio, así que el precio solo va si se pide (y no hay que reimprimir cuando cambia).
  Se imprimen sueltas o por lote (lo recibido hoy, los productos nuevos).
- **Hoja de códigos** enmicada para el mostrador: lo diminuto que no lleva etiqueta (gomas, clips sueltos).
- **Sugerido de compra:** mínimos y máximos por producto calculados con las ventas (incluido el historial de
  eleventa) y la temporada (regreso a clases). La lista sale agrupada por proveedor y se vuelve orden de compra.

## Decisiones de la revisión

| Pregunta | Respuesta | Efecto |
|---|---|---|
| Catálogo | El dueño y el encargado; el cajero solo da de alta lo escaneado que no existe | Los cambios de precio del catálogo avisan por Telegram, salvo los que hace el dueño |
| Recepción | Siempre llega un documento del proveedor | Se carga el PDF (o el XML) y el POS lee los renglones; la recepción sigue a ciegas |
| Margen mínimo | Sí: uno general (30 %) y por departamento si hace falta | Aviso y precio sugerido después de una compra; el precio no cambia solo |
| Conteo cíclico | 15 al día, en la caja | Se diseña para que después lo dispare el [asistente inteligente](ideas.md#asistente-inteligente-piloto-automático) |
| Conteo completo | Con los celulares de quienes estén presentes, en el wifi de la tienda | La página de conteo funciona en el navegador del celular |
| Etiquetas | Casi nada lleva precio | Código y nombre por omisión; el precio, solo si se pide |
| Insumos del mes | En la siguiente fase | Sale de esta fase y pasa a la fase 3 |
| Orden | Sí | 1 catálogo, 2 compras, 3 ajustes y conteos, 4 etiquetas y sugerido |

El dueño también propuso un **asistente inteligente**: piloto automático cuando él no está y tareas pedidas en el
momento oportuno (contar en un rato tranquilo, ordenar una zona después de mucho movimiento, recoger los listones
después de ciertos metros vendidos, reacomodar paquetes). Queda en las [ideas](ideas.md#asistente-inteligente-piloto-automático)
para una fase posterior; esta fase no le cierra la puerta.
