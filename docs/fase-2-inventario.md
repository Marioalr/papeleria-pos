# Fase 2 — Inventario

> **Por revisar.** Con la fase 1 la caja vende, controla y cobra impresiones; el catálogo y las existencias todavía son
> los que llegaron de eleventa y no hay dónde cambiarlos (F3 Productos y F4 Inventario están deshabilitados). Esta
> fase le da al POS el control del inventario: qué hay, qué llega, qué se pierde y qué comprar. Parte del diseño de
> [catálogo e inventario](inventario.md) y de la [depuración](plan.md#depuración-del-inventario) que va antes del piloto.

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
| 2 | **Compras (F4 Inventario)** | Recepción ciega de mercancía con la nota del proveedor, orden de compra opcional, costo promedio y último costo por proveedor, aviso de margen bajo, abrir paquete |
| 3 | **Ajustes, mermas y conteos** | Ajustes y mermas con motivo y regla, conteo cíclico ciego diario en la caja, conteo completo por departamento desde el celular |
| 4 | **Etiquetas y sugerido de compra** | Etiquetas y códigos internos en hojas adhesivas en la WF-6590, hoja de códigos para lo diminuto, mínimos y máximos con el historial y la temporada, lista de compra por proveedor |
| 5 | **Insumos del mes** | La categoría "Insumo" en las salidas F8 y la captura de insumos pagados fuera de la caja, para la ganancia mensual ([detalle](plan.md#administración-y-punto-de-equilibrio)) |

## 1. Catálogo (F3 Productos)

- **Buscar y editar** como en eleventa: por nombre, código o departamento. La ficha de un producto junta sus variantes,
  códigos, presentaciones, precios (con mayoreo), proveedores, existencia y su kardex.
- **Alta rápida desde la caja:** si se escanea un código que no existe, quien tenga permiso lo da de alta ahí mismo
  (nombre, precio, departamento) y se completa después.
- **Códigos internos** para lo que no trae código: se generan solos (Code 128 corto) y se imprimen en la entrega 4.
- **Precios:** cada cambio queda en el historial y en la bitácora. Las cajas reciben el catálogo nuevo sin
  reiniciar (ya lo hacen con la versión de datos).
- **Dar de baja** un producto lo quita de la venta pero conserva su historia. **Juntar** dos productos capturados dos
  veces pasa códigos, existencia y kardex a uno.
- **La depuración se hace aquí,** sobre la base importada y mientras eleventa sigue en uso. Lo que se decide (dar de
  baja, juntar, cambiar la unidad a metro o kilo) se guarda para repetirlo igual en la importación final, antes del
  piloto.

## 2. Compras (F4 Inventario)

- **Recepción ciega:** quien recibe escanea o busca cada producto y escribe cuántos llegaron, **sin ver** cuántos
  decía la nota o la orden. Después se capturan los costos de la nota; las diferencias (faltó, sobró, llegó otro)
  quedan marcadas para reclamarlas al proveedor.
- **Orden de compra opcional:** se arma (o sale del sugerido de la entrega 4) y al recibir se compara con lo que llegó.
- **Costo:** cada compra actualiza el **costo promedio** (el que usa la utilidad) y el **último costo** de ese
  proveedor. Si con el costo nuevo el margen de un producto baja de un mínimo, avisa y sugiere precio; el precio no
  cambia solo.
- **Abrir paquete:** convierte un paquete o una caja en piezas sin perder el rastro (ya existe el movimiento).
- Todo entra al kardex con su documento (la recepción) y queda en la bitácora.

## 3. Ajustes, mermas y conteos

- **Ajuste:** corrige una existencia con motivo obligatorio; según la regla, pide autorización (como las de la
  fase 1). **Merma:** producto dañado, caducado o perdido; su costo es pérdida en el resumen.
- **Conteo cíclico diario:** el POS elige unos productos al día, primero los de más valor y rotación y los que no se
  cuentan hace tiempo. Se cuentan **a ciegas** en la caja y la diferencia queda como movimiento de conteo; si pasa
  de un umbral, avisa por Telegram.
- **Conteo completo** (el de arranque antes del piloto, y uno al año): por departamento, con varias personas contando
  a la vez desde el celular conectado a la red de la tienda. Al cerrar un departamento, lo contado reemplaza la
  existencia.

## 4. Etiquetas y sugerido de compra

- **Etiquetas** en hojas adhesivas tamaño carta, impresas en la WF-6590: código interno (Code 128), nombre y precio.
  Se imprimen sueltas o por lote (lo recibido hoy, lo que cambió de precio).
- **Hoja de códigos** enmicada para el mostrador: lo diminuto que no lleva etiqueta (gomas, clips sueltos).
- **Sugerido de compra:** mínimos y máximos por producto calculados con las ventas (incluido el historial de
  eleventa) y la temporada (regreso a clases). La lista sale agrupada por proveedor y se vuelve orden de compra.

## 5. Insumos del mes

- **Salidas F8 con categoría:** "Insumo" (papel térmico, cinta, limpieza…) junto a las que ya hay. Lo pagado con la
  caja queda registrado una sola vez.
- **Insumos pagados fuera de la caja:** con fecha, sucursal, concepto, categoría, monto y foto del comprobante.
- Entran como gasto del mes; el reporte de ganancia mensual completo llega con la administración (gastos fijos e
  inversión).

## Preguntas

1. **Catálogo:** ¿quién da de alta y edita productos? Propuesta: el dueño y el encargado; el cajero solo da de alta
   desde la caja lo que se escaneó y no existe (queda "por completar"). ¿Los cambios de precio del catálogo te avisan
   por Telegram, como los de la venta?
2. **Recepción:** ¿quién recibe la mercancía, y llega con nota o factura del proveedor? ¿Se hacen órdenes de compra
   hoy, o se pide por teléfono o WhatsApp y se recibe con la nota? Propuesta: la orden es opcional y la recepción
   siempre a ciegas.
3. **Margen mínimo:** ¿con qué margen avisa que un producto quedó barato después de una compra? Propuesta: un margen
   general (por ejemplo 30 %) y uno por departamento si hace falta.
4. **Conteo cíclico:** ¿cuántos productos al día y cuándo? Propuesta: 15 al día, en la caja, al abrir el turno o en un
   rato tranquilo; quien cuenta no ve la existencia.
5. **Conteo completo:** ¿se puede contar con celulares (de los empleados o de la tienda) conectados al wifi de la
   tienda? ¿Cuántas personas contarían?
6. **Etiquetas:** ¿qué hojas adhesivas usan hoy (medida o cuántas etiquetas por hoja)? ¿Las etiquetas llevan precio,
   o solo código y nombre (para no reimprimir cuando cambia)?
7. **Insumos:** ¿entran en esta fase (entrega 5) o esperan a la administración completa?
8. **Orden:** ¿te parece este orden de entregas? La depuración y el conteo completo son lo que hace falta antes del
   piloto en la matriz.
