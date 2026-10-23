# Fase 3 — Precios y clientes

> **Revisado** (ver [decisiones](#decisiones-de-la-revisión)). Con las fases 1 y 2 la caja vende, controla, compra y
> cuenta. Esta fase termina el mayoreo que en eleventa se hacía con F11, redondea el cobro al peso y trae a los
> clientes de confianza con su crédito; también los consumibles del mes, que el dueño pasó aquí al revisar la fase 2.
> Las promociones con fechas, que solo se usan en el regreso a clases, ya están (2026-10-22): el dueño pidió seguir
> hasta igualar a eleventa. Los kits siguen pospuestos: la tienda no los usa en eleventa. Parte de los
> [precios](inventario.md#precios), los [clientes registrados](pedidos.md#el-celular-y-los-clientes-registrados) y la
> [administración](plan.md#administración-y-punto-de-equilibrio).

## Qué ya existe

- **Precios:** por unidad base (el paquete de 12 cuesta 12 piezas), precio propio de una presentación (raro) y
  **escalas por cantidad** que la caja aplica sola (las de eleventa, `PROMOCIONES_POR_CANTIDAD`). Se editan en F3
  («Desde 1 es el menudeo; más cantidad, mayoreo») y cada cambio queda en el historial y en la bitácora.
- **El mayoreo de eleventa** (`PMAYOREOFINAL`, un segundo precio que el cajero activa a mano) no se importó: la
  importación avisa cuántos productos lo tenían. F11 Mayoreo está apagada en la caja.
- **Descuentos (CTRL+D)** con su regla, y **clientes especiales** con un descuento fijo que se eligen de una lista.
  Nada queda debajo del costo sin autorización.
- **Promociones con fechas** (2026-10-22, [abajo](#promociones-y-kits)): 3×2, el segundo a mitad de precio, un porcentaje y
  el precio especial de unos días, en Productos (F3) › Promociones; se aplican solas, también sin conexión.
- **El tipo de producto Kit** existe en el modelo, sin uso todavía.
- **Formas de pago:** efectivo, tarjeta y transferencia (por verificar). Las salidas de efectivo (F8) llevan motivo.

## Entregas

| # | Entrega | Qué incluye |
|---|---|---|
| 1 | **Mayoreo y redondeo** | El mayoreo de eleventa pasa a escalas por cantidad (sin F11), el ticket dice cuándo se aplicó y cuánto se ahorró, y el cobro en efectivo se redondea al peso |
| 2 | **Clientes registrados y crédito** | Los clientes de confianza (unos 25) con sus privilegios y su evaluación, el crédito para la siguiente compra, los abonos en la caja, el estado de cuenta y los avisos de lo vencido |
| 3 | **Consumibles del mes** | La categoría «Consumibles» en las salidas F8, lo pagado fuera de la caja con su comprobante y el gasto del mes |
| 4 | **Promociones con fechas** | **Hecho (2026-10-22).** Lleva N y paga M, el segundo a mitad de precio, un porcentaje y el precio especial de unos días (dejar al costo lo estratégico, el F11 del regreso a clases), con sus fechas, días y sucursal; se aplican solas, no se acumulan y nunca debajo del costo sin autorización ([abajo](#promociones-y-kits)) |
| — | **Kits** | Pospuestos: la tienda no los usa en eleventa (decidido, por revisar) |

## 1. Mayoreo y redondeo

En eleventa F11 se usa de dos formas, y ninguna necesita que el cajero decida el precio:

- **Paquetes:** el precio del paquete se calcula al dar de alta el producto; al venderlo se escriben las piezas que
  trae y se oprime F11. En el POS eso es una **escala por cantidad** (desde las piezas del paquete, al precio por pieza
  del paquete), que se aplica sola: se escriben las piezas o se escanea el código del paquete y ya sale a su precio.
- **Regreso a clases:** con F11 se dejaban al costo los productos estratégicos. En el POS eso es una
  [promoción](#promociones-y-kits) con fechas: «Dejar al costo», en Productos › Promociones (2026-10-22).

Lo que trae la entrega:

- **La importación pasa el mayoreo de eleventa a escalas:** de las ventas con F11 de cada producto sale cuántas piezas
  se vendían normalmente con mayoreo (el paquete); esa es la escala, al precio de mayoreo. Lo que no se pueda deducir
  queda «para revisar» en la depuración, con el precio de mayoreo de eleventa a la vista, para capturar el paquete a
  mano en F3.
- **F11 ya no cambia el precio:** muestra las escalas del producto seleccionado («Desde 12: $2.50 c/u»), para quien
  la oprima por costumbre.
- **El ticket dice cuándo se aplicó el mayoreo** y cuánto se ahorró, junto con los descuentos.
- **Redondeo al peso** del cobro en efectivo, al peso más cercano ($37.49 → $37, $37.50 → $38). La tarjeta y la
  transferencia se cobran exactas. El redondeo queda en la venta y en el corte, aparte. Las impresiones tienen sus
  propias reglas: no se redondean por ahora y se revisan al terminar el módulo de impresión.

## 2. Clientes registrados y crédito

- **Clientes registrados:** los de confianza, unos 25 hoy, dados de alta a mano por el dueño o el encargado: nombre,
  teléfono y notas (sin RFC). Los clientes especiales que haya en el POS se depuran y pasan a ser clientes
  registrados con su descuento. Los clientes de impresiones son aparte (módulo de impresión).
- **Privilegios por cliente:** descuento fijo, mayoreo siempre, crédito y, para la fase 4, pedido sin anticipo y
  apartar sin pagar. Nada más.
- **Su evaluación:** cuánto compra (por mes) y qué tan puntual paga (días que tarda en liquidar lo fiado), que es lo
  que hoy se mira para darle o quitarle privilegios.
- **En la caja:** se elige al cliente (por nombre o teléfono) y el ticket lleva su nombre y sus privilegios. Si debe
  algo, la caja lo dice al elegirlo, para cobrarlo en esa compra.
- **Crédito:** una forma de pago más, solo para clientes con crédito y hasta su límite; normalmente se paga en la
  siguiente compra. Plazo de 30 días por omisión, sin recargos. Pasar el límite lo autoriza el encargado o el dueño.
  El ticket lleva lo que quedó a deber y el saldo.
- **Abonos:** se reciben en cualquier caja, en efectivo, tarjeta o transferencia, y entran al corte como un ingreso
  aparte (no son venta: la venta ya se contó al fiar). El abono imprime su comprobante con el saldo.
- **Estado de cuenta** por cliente: compras a crédito, abonos y saldo; se imprime o se manda como texto.
- **Avisos por Telegram:** un crédito que pasó su plazo, y el resumen del día con lo fiado y lo abonado.
- **Sin conexión:** la caja fía hasta el saldo que conoce y lo marca para revisarlo al volver la conexión.

## 3. Consumibles del mes

- **Salidas F8 con categoría:** «Consumibles» (cinta, artículos de limpieza…) junto a las que ya hay. Lo pagado con
  la caja queda registrado una sola vez.
- **Lo pagado fuera de la caja:** con fecha, sucursal, concepto, monto y la foto del comprobante, que se puede subir
  desde el celular como en el conteo completo. Lo capturan el dueño y el encargado.
- **El gasto del mes** por sucursal. El reporte de ganancia mensual completo llega con la administración (gastos fijos
  e inversión).
- No son consumibles las tintas ni la caja de mantenimiento (las lleva el módulo de impresión) ni los materiales de
  los servicios (espirales, micas, papel para copias): están en el inventario y se descuentan con la
  [receta](servicios.md#receta-de-un-servicio) en la fase 4.

## Promociones y kits

Solo se usan en el regreso a clases. El dueño las había dejado para antes del próximo; el 2026-10-22 pidió seguir hasta
que el POS iguale a eleventa, y las promociones se hicieron entonces. Los kits no: la tienda no tiene kits en eleventa
([paridad](paridad-eleventa.md#productos-y-precios): «No la usan»).

**Promociones: hecho (2026-10-22)** ([avance](plan.md#primero-la-versión-unicaja-entrega-0)). En Productos (F3) ›
Promociones, para quien tiene «Productos, precios y etiquetas»:

- **Los tipos:** lleva N y paga M (3×2), el segundo a mitad de precio, un porcentaje menos (en productos, un departamento
  o una marca) y el precio especial de cada producto por unos días. «Dejar al costo» arma uno de esos con los productos
  marcados, cada uno a su costo: lo que en el regreso a clases se hacía con F11. Lo ve quien tiene «Ver costos, utilidad y
  margen».
- **Su vigencia:** desde un día y hasta otro (o hasta que se termine), los días de la semana que se elijan, y en esta
  sucursal o en todas. La lista dice qué corre hoy, qué viene y qué terminó; «Terminar ahora» la quita desde ya.
- **Se aplican solas y no se acumulan:** se queda la que más le conviene al cliente, también contra el mayoreo y contra el
  descuento fijo del cliente registrado (en cada renglón, la promoción o su precio con su descuento). Cuentan cada producto
  aparte, en piezas como el mayoreo. Un descuento a mano (Ctrl+D) va encima de la promoción; un precio cambiado con Ctrl+E
  la quita del renglón (la ventana lo dice).
- **Nunca debajo del costo sin autorización:** al guardarla, como Ctrl+D, con su precio más bajo (también el de una
  presentación con precio propio, como una caja de 12); lo hace quien tiene «Vender debajo del costo» (ve antes cuáles
  quedan debajo) o con la autorización de quien lo tiene, y queda en la bitácora. Lo que después quedaría debajo sin
  autorizarse (llegó a su departamento o su marca, bajó su precio, subió su costo) no la lleva: las cajas no se la aplican y
  su detalle lo dice («Fuera por el costo»); guardarla otra vez con la autorización se la da. Cuánto falta para el costo
  solo lo ve quien ve la utilidad.
- **El ticket** dice la promoción y lo que se ahorró, como el mayoreo, y la pantalla de venta la dice en el renglón.
- **En las cajas, también sin conexión:** van en su copia con sus fechas, y cada caja decide con la suya: la que empieza o
  termina a medianoche se aplica sin bajar nada. Al cobrar, la caja principal acepta el día de la caja si es el suyo o, a
  menos de una hora de su medianoche, el de al lado; si no (el reloj de la PC está mal), cobra con el suyo, la caja pone el
  ticket al día con él y avisa que la fecha de la PC está mal. El navegador de desarrollo sin base local no tiene copia: no
  las aplica (es solo para probar).
- **En el piloto con eleventa** no se aplican en la caja 2 ni se dan de alta: los precios se ponen en eleventa.

**Kits: pospuestos** (decidido, por revisar): un producto con su precio propio y su lista de piezas; al venderlo se
descuenta cada pieza, y se pueden armar por adelantado. Las listas escolares no son kits: llegan como pedidos en la
fase 4.

## Decisiones de la revisión

| Pregunta | Respuesta | Efecto |
|---|---|---|
| Mayoreo (F11) | Se usa para vender el paquete a su precio (se escriben sus piezas y F11) y, en el regreso a clases, para dejar al costo los productos estratégicos | El paquete pasa a escalas por cantidad que se aplican solas (la importación las deduce de las ventas con F11); lo del regreso a clases, a promociones. F11 solo muestra las escalas |
| Redondeo | Al peso, excepto las impresiones (tienen sus propias reglas) | El cobro en efectivo se redondea al peso más cercano; las impresiones se revisan al terminar el módulo de impresión |
| Clientes registrados | Hoy no hay formales (si los hay, se depuran); unos 25; sin RFC; se evalúa cuánto compran y si pagan puntual. Los de impresiones son aparte | Alta a mano, sin RFC, con su evaluación (compra y puntualidad) |
| Crédito | Solo para esos clientes, para su siguiente compra (rara vez más de un mes), sin recargos | Plazo de 30 días por omisión; la caja recuerda el adeudo al elegir al cliente |
| Privilegios | Solo esos: descuento, mayoreo, crédito, pedido sin anticipo, apartar sin pagar | Sin más privilegios |
| Promociones | Solo en el regreso a clases; falta casi un año | Pospuestas para antes del próximo regreso a clases. Hechas el 2026-10-22, cuando el dueño pidió seguir hasta igualar a eleventa |
| Kits | Igual que las promociones | Pospuestos: no hay kits en eleventa (decidido el 2026-10-22, por revisar) |
| Insumos | Consumibles (cinta, limpieza…); lo de impresiones (tintas, caja de mantenimiento) aparte | Entrega 3: «Consumibles del mes» |
| Orden | Sí | 1 mayoreo y redondeo, 2 clientes y crédito, 3 consumibles |

Quedan como propuesta, para corregir si no es así: el límite de crédito lo pone el dueño por cliente; pasarlo lo
autoriza el encargado o el dueño; sin conexión se fía hasta el saldo que conoce la caja; lo pagado fuera de la caja lo
capturan el dueño y el encargado.
