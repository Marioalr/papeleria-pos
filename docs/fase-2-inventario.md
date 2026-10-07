# Fase 2 — Inventario

> **Revisado.** Con la fase 1 la caja vende, controla y cobra impresiones; el catálogo y las existencias todavía son
> los que llegaron de eleventa y no hay dónde cambiarlos (F3 Productos y F4 Inventario están deshabilitados). Esta
> fase le da al POS el control del inventario: qué hay, qué llega, qué se pierde y qué comprar. Parte del diseño de
> [catálogo e inventario](inventario.md) y de la [depuración](plan.md#depuración-del-inventario) que va antes del piloto.

**Avance:** las entregas 1 (catálogo) y 2 (compras) están completas y la
[prueba con una persona real](plan.md#prueba-con-una-persona-real) está lista para instalarse. De la entrega 3 está
la parte 1 (ajustes y mermas).

Entrega 1, parte 1:

- **F3 Productos:** buscar por nombre, marca o código (también los inactivos y lo que está por completar) y abrir la
  ficha.
- **La ficha:** variantes, presentaciones con sus códigos, precios de menudeo y mayoreo, existencia y kardex. El costo
  promedio y los proveedores solo los ven el dueño y el encargado. Lo guardan ellos dos (regla "Editar el catálogo").
- **Precios:** cada cambio queda en el historial y en la bitácora y avisa por Telegram, salvo los del dueño.
- **Código interno:** un botón genera un EAN-13 que empieza con 2 (el rango de uso interno) para lo que no trae código.
- **Alta rápida:** si se escanea un código que no existe, el cajero lo da de alta con nombre, precio y departamento,
  entra al ticket y queda "por completar".

Entrega 1, parte 2, la depuración (en la ficha, solo el dueño y el encargado):

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

Entrega 2, parte 1, recibir y costear (F4 Inventario → Compras):

- **Recibir a ciegas:** quien recibe (también el cajero) elige el proveedor, escribe el número de la nota si lo trae y
  escanea lo que llegó: cada código suma uno de su presentación (el de la caja cuenta una caja) y la cantidad se corrige
  en la tabla. No ve la nota ni los costos.
- **La nota** la capturan el dueño o el encargado (regla "Capturar la nota del proveedor y cerrar la compra") cuando el
  conteo terminó: producto, presentación, cantidad, importe del renglón y la clave del proveedor si la trae. La pantalla
  compara por producto lo de la nota contra lo que llegó (faltó, sobró, no llegó, no está en la nota) con el costo
  nuevo, el anterior y el margen.
- **Al cerrar,** lo contado entra al kardex con el costo de la nota. Lo que faltó no entra (se reclama); lo que llegó sin
  estar en la nota entra a su último costo, y sin ninguno hay que agregarlo a la nota. El costo promedio se ajusta solo
  y cada proveedor guarda su último costo y su clave. Las diferencias quedan en la bitácora.
- **El costo es lo que se paga, con IVA,** como el precio (y como el costo de eleventa): así se comparan directo.
- **Margen mínimo:** 30 % general; el dueño lo cambia y puede poner uno por departamento (F4 → Margen mínimo). Si con
  el costo nuevo un producto queda debajo, se sugiere el precio que lo deja en el mínimo, subido a los siguientes 50
  centavos. El precio no cambia solo: "Poner $…" lo cambia como cualquier precio del catálogo (bitácora y Telegram).
- **Telegram:** una compra con diferencias o con margen bajo le avisa al dueño (la alerta se puede apagar).
- **Sin vender:** el encargado y el dueño entran a F3 y F4 desde la pantalla de entrada sin cortar el turno del cajero
  ni abrir uno propio. Con una sola caja, así se captura la nota o se corrige el catálogo.
- **Proveedores:** alta y edición en F4 → Proveedores (el dueño y el encargado).

Entrega 2, parte 2, el documento del proveedor (en la misma pantalla, quien captura la nota):

- **Se carga el XML de la factura o el PDF de la nota** y llena los renglones. El documento se guarda con la compra.
- **XML (CFDI 3.3 o 4.0):** cada concepto con su clave, descripción, cantidad y unidad; el importe es lo que se paga
  (importe − descuento + IVA e IEPS trasladados). Si es en dólares, se pasa a pesos con su tipo de cambio. Una nota de
  crédito no se toma, y la misma factura (por su folio fiscal) no se carga en dos compras. Si el proveedor no tenía
  RFC se guarda el de la factura; si tenía otro, avisa.
- **PDF:** cada proveedor tiene su formato, así que se toma como producto el renglón que termina en precio e importe y
  tiene una cantidad que cuadra con ellos; la primera palabra es la clave si lo parece. Si el total de la nota no es
  la suma de los renglones (precios sin IVA, descuento general), la diferencia se reparte en proporción y se avisa. Un
  PDF escaneado (sin texto) no se puede leer: esa nota se captura a mano.
- **Reconocer cada renglón:** por la clave que ya se ligó con ese proveedor (o por su descripción, si no trae clave),
  o por el código de barras si la clave es uno. Lo que no reconoce queda "sin ligar": se dice una vez qué producto y
  presentación es («Ligar») y la próxima vez ese renglón llega ligado. Lo que no es mercancía (un flete) se quita.
  Mientras haya renglones sin ligar, la compra no se cierra.
- **Corregir:** cualquier renglón del documento se puede corregir (otra presentación, cantidad o importe); lo que dice
  el documento se queda a la vista.

Entrega 2, parte 3, la orden de compra y abrir paquete (F4):

- **Órdenes de compra** (el dueño y el encargado, regla "Hacer órdenes de compra"): se elige el proveedor y se escanea o
  busca cada producto, con su último costo con ese proveedor y la existencia. Abajo queda el texto del pedido, con las
  claves del proveedor, para copiarlo a WhatsApp o a un correo.
- **Son opcionales:** al capturar la nota se dice de qué orden viene (una abierta del mismo proveedor). La comparación
  muestra también lo pedido; lo que se pidió y no vino aparece como "no llegó" y entra en las diferencias. Al cerrar la
  compra, la orden queda recibida. Quien cuenta no ve la orden.
- **Abrir paquete:** cuando el paquete y la pieza son productos distintos (como vienen de eleventa), sale el paquete a
  su costo y entran sus piezas con ese mismo costo, en dos movimientos del kardex con el mismo documento. La siguiente
  vez se propone la misma pieza y cuántas trae. Lo puede hacer el cajero (queda en la bitácora). Si el paquete y la
  pieza son presentaciones del mismo producto no hace falta: la existencia ya está en piezas.

Entrega 2, parte 4, todo el pedido desde el documento (lo pidió el dueño al revisar la parte 2):

- **Se precarga todo:** al cargar el XML o el PDF, cada renglón llega ligado (lo reconoció), con la pregunta «¿Es…?»
  (se parece a un producto del catálogo; se confirma con un clic) o como **producto nuevo en borrador**, con nombre,
  departamento (donde más productos tiene ese proveedor), piezas en cada unidad, precio que deja el margen mínimo y el
  código de barras si el documento trae uno libre.
- **Todo se revisa y se corrige** en una tabla, renglón por renglón y hasta cerrar la compra: el producto, la
  presentación, la cantidad, el importe y cada dato de los nuevos. Los que siguen sin ligar se marcan como nuevos de
  una vez («Los que faltan son nuevos»; los parecidos no, para no darlos de alta dos veces).
- **Dar de alta los nuevos:** quedan en el catálogo con su pieza (y su caja, si cada unidad trae varias), su precio y su
  código; su renglón queda ligado y la próxima vez llega ligado. La compra no se cierra con nuevos pendientes.
- **Lo que llegó de un producto nuevo:** quien recibe escanea su código, el POS dice que no está en el catálogo y lo
  cuenta aparte, a ciegas; al darlo de alta se le asigna. Si no tenía código, lo escribe quien captura la nota (queda
  en la bitácora).
- **Un código de barras nuevo de un producto que ya existe** (el proveedor cambió el empaque): al ligar ese renglón, el
  código se le agrega al producto y lo que se contó con él ya es suyo.

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

Entrega 3, parte 1, ajustes y mermas (F4 → Ajustes y mermas):

- **Ajuste:** se escribe la existencia real y entra o sale la diferencia, con motivo obligatorio; lo que entra vale el
  costo promedio. Por omisión el cajero necesita al encargado (regla "Ajustar una existencia"); el encargado lo hace solo.
- **Merma:** cuántos, la causa (dañado, caducado, perdido u otro, que pide qué pasó) y una nota. Sale a su costo: es
  pérdida. El cajero la registra solo hasta $200 de costo; arriba, autoriza el encargado (regla "Registrar una merma").
- Cada uno es un movimiento del kardex con su documento (quién, quién autorizó, por qué) y queda en la bitácora. El
  costo solo lo ven el dueño y el encargado.
- **Avisos:** un ajuste o una merma de $200 o más le llega al dueño por Telegram (los suyos no; el umbral se cambia en
  la configuración de Telegram). El resumen del día suma las mermas y los ajustes.

Lo que sigue de la entrega 3: el conteo cíclico diario en la caja (parte 2) y el conteo completo desde el celular
(parte 3).

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
