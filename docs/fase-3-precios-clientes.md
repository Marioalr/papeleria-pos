# Fase 3 — Precios y clientes

> **Por revisar.** Con las fases 1 y 2 la caja vende, controla, compra y cuenta. Los precios todavía son los de
> eleventa: menudeo, escalas por cantidad y descuentos a mano; el mayoreo que el cajero activaba con F11 no se
> importó y la tecla está apagada. Esta fase termina los precios (mayoreo, promociones, kits) y trae a los clientes
> de confianza con su crédito. También los insumos del mes, que el dueño pasó aquí al revisar la fase 2. Parte de los
> [precios](inventario.md#precios), los [clientes registrados](pedidos.md#el-celular-y-los-clientes-registrados) y la
> [administración](plan.md#administración-y-punto-de-equilibrio).

## Qué ya existe

- **Precios:** por unidad base (el paquete de 12 cuesta 12 piezas), precio propio de una presentación (raro) y
  **escalas por cantidad** que la caja aplica sola (las de eleventa, `PROMOCIONES_POR_CANTIDAD`). Se editan en F3
  («Desde 1 es el menudeo; más cantidad, mayoreo») y cada cambio queda en el historial y en la bitácora.
- **El mayoreo de eleventa** (`PMAYOREOFINAL`, un segundo precio que el cajero activa a mano) no se importó: la
  importación avisa cuántos productos lo tenían. F11 Mayoreo está reservada en la caja.
- **Descuentos (CTRL+D)** con su regla, y **clientes especiales** con un descuento fijo que se eligen de una lista.
  Nada queda debajo del costo sin autorización.
- **El tipo de producto Kit** existe en el modelo, sin uso todavía.
- **Formas de pago:** efectivo, tarjeta y transferencia (por verificar). Las salidas de efectivo (F8) llevan motivo.

## Entregas

| # | Entrega | Qué incluye |
|---|---|---|
| 1 | **Mayoreo y redondeo** | El mayoreo a mano con F11 (si se decide) con su regla, el mayoreo de un cliente, el ticket que dice cuándo se aplicó y el redondeo del cobro en efectivo |
| 2 | **Clientes registrados y crédito** | Los clientes de confianza con sus privilegios, la venta a crédito con límite y plazo, los abonos en la caja, el estado de cuenta y los avisos de lo vencido |
| 3 | **Promociones** | Lleva N y paga M, porcentaje por producto, departamento o marca, precio especial por unos días; con vigencia por fechas, días y sucursal |
| 4 | **Kits** | Un producto con precio propio que al venderse descuenta cada pieza que lo forma; armar kits por adelantado |
| 5 | **Insumos del mes** | La categoría «Insumo» en las salidas F8, los insumos pagados fuera de la caja con su comprobante y el gasto del mes |

## 1. Mayoreo y redondeo

- **Escalas por cantidad:** ya se cobran solas y se editan en F3. Falta que el ticket diga cuándo se aplicó el
  mayoreo («3 o más: $2.50 c/u») y cuánto se ahorró.
- **Mayoreo a mano (F11)**, si se decide conservarlo: el cajero lo activa en la venta y todo el ticket se cobra al
  precio de mayoreo de cada producto. Lleva su regla, como los descuentos («Dar precio de mayoreo»: el cajero con
  autorización, el encargado solo) y queda en la bitácora. El precio de mayoreo se captura en F3; el de eleventa se
  puede importar si se decide usarlo.
- **Mayoreo de un cliente:** a un cliente registrado se le puede dar el mayoreo siempre, sin autorización (entrega 2).
- **Redondeo:** con las escalas, las promociones y los descuentos salen centavos sueltos ($37.83). Propuesta: el
  cobro en efectivo se redondea a los 50 centavos más cercanos y el redondeo queda en el corte, aparte; la tarjeta y
  la transferencia se cobran exactas.

## 2. Clientes registrados y crédito

- **Clientes registrados:** pocos, los de confianza (maestros, escuelas, negocios vecinos), dados de alta a mano por el
  dueño o el encargado: nombre, teléfono, notas y, para facturar después, RFC. Los clientes especiales de hoy pasan a
  ser clientes registrados con su descuento.
- **Privilegios por cliente:** descuento fijo, mayoreo siempre, crédito (con límite y plazo) y, para la fase 4, pedido
  sin anticipo y apartar sin pagar.
- **En la caja:** se elige al cliente (por nombre o teléfono) y el ticket lleva su nombre y sus privilegios.
- **Venta a crédito:** una forma de pago más, solo para clientes con crédito y hasta su límite. Pasar el límite pide
  autorización. El ticket lleva lo que quedó a deber y el saldo.
- **Abonos:** se reciben en cualquier caja, en efectivo, tarjeta o transferencia, y entran al corte como un ingreso
  aparte (no son venta: la venta ya se contó al fiar). El abono imprime su comprobante con el saldo.
- **Estado de cuenta** por cliente: compras a crédito, abonos y saldo; se imprime o se manda como texto.
- **Avisos por Telegram:** un crédito que pasó su plazo, y el resumen del día con lo fiado y lo abonado.
- **Sin conexión:** la caja no sabe el saldo de ese momento. Propuesta: se fía hasta el saldo que conoce la caja y se
  avisa; si se prefiere, se bloquea el crédito mientras no haya conexión.

## 3. Promociones

- **Tipos:** lleva N y paga M (3×2), el segundo a mitad de precio, porcentaje en un producto, un departamento o una
  marca, y precio especial por unos días.
- **Vigencia:** de una fecha a otra, ciertos días de la semana y en ciertas sucursales. Las da de alta el dueño o el
  encargado, y las cajas las reciben con el catálogo.
- **Al vender** se aplican solas y el ticket dice cuál y cuánto se ahorró. Propuesta: no se acumulan; si a un
  artículo le tocan varias (escala, promoción, descuento del cliente), se queda la que más le conviene al cliente.
  Nunca debajo del costo sin autorización.
- **Resultado:** cuánto se vendió con cada promoción y cuánto se dejó de cobrar.

## 4. Kits

- **Un kit** (por ejemplo «Kit escolar básico») es un producto con su precio propio y su lista de piezas: 2 cuadernos,
  1 caja de colores, 3 lápices.
- **Al venderlo** se descuenta del inventario cada pieza; el costo del kit es la suma de los costos. Su existencia es
  cuántos se pueden armar con lo que hay.
- **Armarlos por adelantado** (si se dejan listos en bolsa): «Armar kits» saca las piezas y entra el kit, como abrir un
  paquete al revés; «Desarmar» lo regresa.
- Las listas escolares no son kits: llegan como pedidos en la fase 4.

## 5. Insumos del mes

- **Salidas F8 con categoría:** «Insumo» junto a las que ya hay, con su concepto (papel térmico, cinta, limpieza…).
  Lo pagado con la caja queda registrado una sola vez.
- **Insumos pagados fuera de la caja:** con fecha, sucursal, concepto, categoría, monto y la foto del comprobante,
  que se puede subir desde el celular como en el conteo completo.
- **El gasto del mes** por sucursal y categoría. El reporte de ganancia mensual completo llega con la administración
  (gastos fijos e inversión).
- Los materiales de los servicios (espirales, micas, papel para copias) no son insumos: están en el inventario y se
  descuentan con la [receta](servicios.md#receta-de-un-servicio) en la fase 4.

## Preguntas

1. **Mayoreo:** ¿cómo se usa hoy el F11 de eleventa? ¿A quién se le da (maestros, negocios, a cualquiera que lleve
   cierta cantidad) y quién decide dárselo? Propuesta: las escalas por cantidad se cobran solas, el mayoreo a mano se
   queda con F11 con la regla «Dar precio de mayoreo» (el cajero con autorización) y los clientes registrados lo
   tienen siempre. ¿Desde cuántas piezas empieza el mayoreo en hojas, folders y clips?
2. **Redondeo:** ¿hoy se redondea el cobro en efectivo? ¿A 50 centavos o al peso?
3. **Clientes registrados:** ¿quiénes serían y cuántos, más o menos? ¿Los clientes especiales de hoy pasan a ser
   clientes registrados? ¿Hace falta guardar el RFC desde ahora?
4. **Crédito:** ¿hoy se fía? ¿A quién, hasta cuánto y a qué plazo? ¿Quién lo da de alta y quién autoriza pasar el
   límite? ¿Se cobran recargos por atraso? (Propuesta: no.) ¿Sin conexión se fía hasta el saldo que conoce la caja o
   se bloquea?
5. **Privilegios:** además del descuento, el mayoreo, el crédito, el pedido sin anticipo y apartar sin pagar, ¿qué
   más se le daría a un cliente de confianza?
6. **Promociones:** ¿qué promociones hacen hoy o quieren hacer (3×2, regreso a clases, Buen Fin)? ¿Se acumulan con el
   descuento del cliente? Propuesta: no se acumulan; se aplica la que más le conviene al cliente.
7. **Kits:** ¿qué kits venden o quieren vender? ¿Se arman por adelantado o al momento de venderlos?
8. **Insumos:** ¿qué categorías usarían (papel térmico, limpieza, papelería de la tienda, agua y café,
   mantenimiento…)? ¿Quién captura lo que se paga fuera de la caja?
9. **Orden:** ¿te parece este orden? Propuesta: primero el mayoreo (está pendiente desde la fase 1 y la caja lo usa a
   diario) y los clientes con crédito (dinero que hoy no se controla); luego promociones, kits e insumos.
