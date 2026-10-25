# Paridad con eleventa

> **La meta (dueño, 2026-10-15):** «no olvides el objetivo de alcanzar a eleventa». El POS nuevo tiene que reemplazar a
> eleventa en la tienda. Primero va lo que eleventa ya hace (vender, cobrar, los cortes, el inventario y los reportes que
> usa el dueño) y lo que pide dejarlo (el piloto en la caja 2, el puente y el rendimiento). Lo que eleventa no tiene va
> después ([plan](plan.md#la-versión-unicaja-y-el-piloto-en-la-caja-2)).
>
> Revisado el 2026-10-16 contra `main` (4c5f2e5) y el PR #100, abierto, con los documentos, el código del puente (las
> tablas de eleventa que lee y escribe) y las páginas públicas de eleventa ([fuentes](#fuentes)). No se abrió ninguna
> base de la tienda.
>
> **Con la base de la tienda (2026-10-24):** el dueño trajo la base de la caja principal de ese día para abrir la
> sucursal 2. Se contó en una copia de esa copia lo que se usó del 2025-10-24 al 2026-10-24, sin sacar datos al
> repositorio. Lo que en «Uso» dice «base del 2026-10-24» sale de ahí.

## Cómo leer la tabla

- **Uso** en la tienda:
  - **La usan:** lo dicen los documentos o el dueño, o se ve en su base por lo que el puente ya conoce, o en la base del
    2026-10-24.
  - **Por confirmar:** viene en eleventa, pero no se sabe si lo usan. Se mira en la tienda un fin de semana
    ([lista](#qué-mirar-en-la-tienda)), sin preguntárselo al dueño.
  - **No la usan:** lo dicen los documentos, o no aparece en la base del 2026-10-24.
- **Dónde:** la entrega de la [propuesta](propuesta-interfaz.md#entregas), el paso del
  [piloto](plan.md#después-el-piloto-en-la-caja-2) o su
  [diseño](migracion-eleventa.md#lo-que-falta-para-el-piloto-diseño-2026-10-12-por-aprobar), un
  [pendiente](plan.md#pendientes) del plan, o «sin plan».
- **Tamaño:** chico, mediano o grande, como en el plan. «—» si no falta nada.
- **La versión de eleventa:** sus páginas describen la de hoy (la 5.50). La base del 2026-10-24 dice 5.10.3.21
  (`ultima.version.enlared` en `CONFIGURACION`), y ya trae en el turno las columnas de transferencia y cheque, que según
  su historial de cambios llegaron con la 5.10.02.

## La tabla

### Venta y cobro

| eleventa | Uso | El POS hoy | Falta | Dónde | Tamaño |
|---|---|---|---|---|---|
| Escanear y Enter; `5*código`; + y − | La usan | Igual. Con el PR #100, contra la copia de la caja | — | — | — |
| Ins, «Varios»: pide el código y la cantidad | Por confirmar | **Hecho (2026-10-22).** Ins abre «Varios: cantidad y código»: cuántos (con las reglas de `5*código`; «5» e Ins también) y luego el código, escaneado o escrito, y Enter lo agrega de la copia de la caja. El cursor se queda en el cuadro de captura: lo que escanea el lector siempre es el código, también si llega mientras se abre el panel (si llega antes que la cantidad, se queda y se pide la cantidad). El artículo común sigue en Ctrl+P | — | — | — |
| Ctrl+P, artículo común | La usan | Ctrl+P (desde el 2026-10-22, Ins es «Varios», como en eleventa), a la bitácora. En el piloto va a eleventa como su artículo común. Los de eleventa pasan al historial en un solo producto, «Artículo común de eleventa» (2026-10-24; antes, uno por cada uno) | — | — | — |
| F10, buscar: el principio del nombre (con `%`, en cualquier parte) | La usan («el camino de siempre») | Bajo el cuadro: pedazos de palabra, error de dedo, sinónimos, familias y lo elegido antes. Si la depuración le cambia el nombre a un producto, el de antes ya no se busca, igual que en eleventa: buscarlo también por el de antes va con lo que eleventa no tiene (2026-10-24, decidido, por revisar) | — | — | — |
| F9, verificador de precios | La usan | F9, por código: precio y existencia | — | — | — |
| F6 y F5, tickets pendientes | La usan, poco | F6 y F5 con fichas y «Más»; sobreviven a un apagón | — | — | — |
| Supr, borrar un artículo | La usan | Supr, a la bitácora | — | — | — |
| F11, mayoreo: se escriben las piezas del paquete y F11 pone el precio de mayoreo | La usan (base del 2026-10-24: unas mil ventas al año con el precio de mayoreo; uno de cada tres productos tiene un mayoreo más barato que el menudeo) | Escalas por cantidad que se aplican solas: las promociones por cantidad de eleventa y las deducidas de las ventas con F11. F11 las muestra. En el piloto (2026-10-24), lo cobrado con una escala va a eleventa como el F11 (`USA_MAYOREO` y lo ahorrado en el ticket), y el mayoreo que cambia allá llega: su escala toma el precio nuevo o queda por revisar; bajar el menudeo un rato no la quita, ni subir el precio de uno sin mayoreo inventa uno (revisión del PR #139) | El precio de mayoreo a mano no existe: lo que no tenga escala se cobra a menudeo hasta revisarlo en «Mayoreo por revisar» (F3) | Pendiente nuevo, antes del piloto | Chico, con el encargado |
| Promociones: hoy, F11 al costo en el regreso a clases | La usan, solo en esa temporada (dueño) | **Hecho (2026-10-22).** Productos › Promociones: lleva N y paga M (3×2), el segundo a mitad de precio, un porcentaje (en productos, un departamento o una marca) y el precio especial de unos días; «Dejar al costo» pone los marcados a su costo, como el F11 del regreso a clases. Con fechas, días de la semana y sucursal; se aplican solas, también sin conexión y a medianoche; no se juntan con el mayoreo ni con el descuento del cliente registrado (la que más le conviene al cliente) y nunca quedan debajo del costo sin autorización (lo que después quedaría debajo no la lleva y su detalle lo dice). El renglón y el ticket dicen la promoción y lo que se ahorró. Las promociones por cantidad de eleventa ya eran escalas. En el piloto se siguen haciendo en eleventa: en la caja 2 no se aplican, porque los precios se ponen allá | — | — | — |
| Ctrl+D, descuento; cambiar el precio en la venta | El descuento, poco (base del 2026-10-24: unos 75 renglones al año); cambiar el precio, por confirmar | Ctrl+D y Ctrl+E, con permiso y límite. En el piloto va a eleventa como su descuento: `PORCENTAJE_DESCUENTO` en el renglón y lo ahorrado en el ticket (revisión del PR #139) | — | — | — |
| F12, cobrar en efectivo: pagó con y cambio | La usan | F12 al «Pagó con» con el exacto en gris; redondeo al peso | — | — | — |
| Al cobrar: F2, sin imprimir el ticket; F4, notas en el ticket | F2, por confirmar. Las notas no: ningún ticket del último año tiene nota (base del 2026-10-24) | **Cobrar sin imprimir: hecho (2026-10-17).** Configuración › Ticket › «Al cobrar», para toda la tienda: «Imprimir el ticket» (lo de siempre; sin impresora, se ve en pantalla), «Ofrecerlo» (se cobra igual y el panel ofrece «Imprimir ticket», Ctrl+I, hasta el primer escaneo del siguiente cliente) o «No imprimirlo» (si lo piden, se imprime desde «Ventas del día»; sin conexión no se puede, así que se ofrece). Nunca pregunta antes de cobrar; lo fiado sale siempre. **El que no salió, sin motivo: hecho (2026-10-22).** En «Ventas del día», la venta cuyo ticket nunca salió (con «Ofrecerlo» sin Ctrl+I, con «No imprimirlo», o porque la impresora no lo sacó y se dejó con «No imprimirlo» en la cola) dice «Ticket sin imprimir» y lleva «Imprimir», que no pide motivo ni el permiso de reimprimir: no es una reimpresión, y queda en la bitácora como primera impresión. Después ya es «Reimprimir», con su motivo; también si se canceló o se devolvió algo, y lo cobrado sin conexión, que cuenta como impreso. **Notas (F4): hecho (2026-10-22).** F4 con el cursor en el cobro (o «Nota», junto a «Pagó con») abre un renglón en el panel del total: hasta 80 letras, que con «Nota: » caben en dos renglones del ticket. Enter (o F4) la deja, Esc la deja como estaba, y el cobro la lleva. Se guarda con la venta, sale al final del ticket (antes del pie) y en sus reimpresiones, y en «Ventas del día»; también sin conexión, y se queda con el ticket en espera. En el piloto se queda en el POS: el puente no la manda a eleventa, y no hace falta, porque allá no ponen notas | — | — | — |
| Otras formas de pago: tarjeta, transferencia, vales, cheque, dólares, crédito, mixto | No la usan: en su base todo va en efectivo (base del 2026-10-24: todos los tickets del último año, sin pagos mixtos) | Tarjeta, transferencia por verificar, crédito y mixto; apagadas en el piloto | — | — | — |
| Ventas del día y devoluciones: por folio, día, cajero o caja; imprimir copia; devolver y cancelar | La usan (devoluciones y cancelaciones vistas en vivo; base del 2026-10-24: unas 55 devoluciones y 25 cancelaciones al año) | **Hecho (2026-10-17).** «Ventas del día» (lista «/»): las de hoy de esa caja, para todos; otro día (← → o la fecha) u otra caja, con «Ver reportes de ventas»; por cajero y por folio en cualquier día (con su serie, de cualquier caja; solo el número, sin ese permiso, de esa caja). Cada una con la caja, cómo se pagó y si se canceló o se devolvió en parte; reimprimir y «Devolver o cancelar» (hasta 7 días; cancelar, en el mismo turno). Sin conexión, lo dice | — | — | — |
| El ticket: logotipo, datos del negocio y notas al pie | La usan | Encabezado, pie y logotipo; sin QR por ahora, como el de eleventa (2026-10-16). En el piloto, con el folio de eleventa | — | — | — |
| Granel; báscula | Granel sí; báscula no: la medida KG está desactivada en su eleventa | Fracciones por metro, kilo y litro, sin báscula | — | — | — |
| Clientes en la venta y facturar | No la usan: sin el módulo de clientes y sin nada fiscal (base del 2026-10-24: ningún ticket con cliente ni factura) | Clientes registrados (fase 3); se guardan los datos para facturar después | — | — | — |
| Cotizaciones y apartados | Su eleventa no los tiene (dueño, 2026-10-08) | — (los apartados, con los pedidos de la fase 4) | — | — | — |

### Efectivo, turnos y cortes

| eleventa | Uso | El POS hoy | Falta | Dónde | Tamaño |
|---|---|---|---|---|---|
| Abrir el turno con el fondo; un turno por cajero | La usan | Abrir turno con fondo (y el sugerido); un turno por cajero. En el piloto, la caja 2 adopta el turno que abre eleventa (#109, 2026-10-21) | Probarlo contra la copia | Piloto, paso 5 | — |
| Entradas y salidas de efectivo, con comentario; se pueden cancelar | La usan, pocas a mano (base del 2026-10-24: unas 8 salidas al año, ninguna entrada, y nunca cancelaron una) | F7 y F8 con motivo; comprobante impreso de las salidas. En el piloto van a eleventa. **Cancelar una equivocada: hecho (2026-10-22).** Debajo de F7 y F8, las del turno, y «Cancelar» en cada una para quien tiene «Cancelar una entrada o salida» (el dueño, y a quien él se lo dé en «Usuarios y permisos»; el cajero, de inicio, «No»), con motivo, en su caja y en su mismo turno, antes del corte. Queda en la bitácora, se abre el cajón y el corte ya no la cuenta y dice cuántas se cancelaron. Antes se compensaba con la contraria. En el piloto se ven, pero no se cancelan: van a eleventa, y no se sabe cómo anota eleventa una cancelada; la pantalla lo dice (si fue un error, la contraria). No hace falta más: en eleventa nunca cancelaron una | — | — | — |
| Límite de efectivo en caja | No la usan: está en 0 en sus cuatro cajas (base del 2026-10-24) | Límite con aviso de retiro (apagado en el piloto) | — | — | — |
| Corte del turno, con faltante o sobrante | La usan | Corte del turno a ciegas, solo con el total, impreso; la diferencia a nombre del cajero. En el piloto el corte se hace en eleventa y la caja 2 lo toma al verlo cerrado (#109, 2026-10-21) | Probarlo contra la copia | Piloto, paso 5 | — |
| Corte del día y de todas las cajas: ventas, ganancia, formas de pago, ventas y ganancia por departamento, entradas, salidas, devoluciones; se imprime y se ve el de otra fecha | Por confirmar | Como el de eleventa (2026-10-17): de todas las cajas, para quien tiene el permiso, el de hoy o el de otro día (← → o la fecha; el de otro día solo se ve). Lo vendido, el efectivo, las formas de pago, las ventas por departamento, las entradas y salidas, las devoluciones, lo fiado y los abonos; la utilidad (también la de cada departamento), para quien ve la utilidad. «Imprimir» en la de tickets, también el de otro día. A la hora de cierre de la tienda se le recuerda cerrarlo a quien lo cierra | — | — | — |
| Historial de cortes: por fecha, cajero, turno y caja | Por confirmar | **Hecho (2026-10-17).** En el corte, «Historial» (también en «/»): los turnos de cualquier día, por caja y cajero, con lo esperado, lo contado, la diferencia, quién y cuándo, y reimprimir el corte Z. Para quien hace el corte de un turno o ve el del día; en el piloto, «Corte en eleventa» | — | — | — |
| El corte por correo al cerrar cada turno | No la usan: el envío del corte, del turno y del día, está apagado y sin correos (base del 2026-10-24) | No hay (el resumen del día iba por Telegram, en pausa) | — | — | — |

### Productos y precios

| eleventa | Uso | El POS hoy | Falta | Dónde | Tamaño |
|---|---|---|---|---|---|
| Alta y cambios de productos: código, nombre, precios, costo, departamento, tipo de venta | La usan. En el piloto se siguen haciendo en eleventa (dueño, 2026-10-10) | F3 Productos con lista y detalle, la ficha, alta rápida desde la venta y «por completar» | — | — | — |
| Departamentos: nuevo, cambiar el nombre, eliminar | La usan | **Hecho (#104, 2026-10-17).** En Productos (F3): darlos de alta, cambiarles el nombre y desactivarlos (sus productos activos pasan a otro). En el piloto llegan de eleventa y en el POS solo se ven | — | — | — |
| Precio de costo, ganancia y precio de venta | La usan | Costo promedio y último costo por proveedor; margen mínimo con el precio sugerido al comprar | — | — | — |
| Productos a granel (metro, kilo, litro) | La usan, con unidades mal puestas (base del 2026-10-24: unos 270 productos a granel; la mitad de los renglones vendidos son de ellos, sobre todo las impresiones con fracciones) | Fracciones; cambiar la unidad en la depuración. Las impresiones (a granel con la medida pieza en eleventa) pasan como «pieza a granel»: fracciones sin «kg» en el ticket (2026-10-24), y también lo que tiene la medida KG, desactivada en la tienda (revisión del PR #139): su unidad, en la depuración | Corregir las unidades | Depuración | Trabajo de la tienda |
| Eliminar productos | La usan | Dar de baja con su historia; juntar los duplicados | — | — | — |
| Impuestos por producto | No la usan: no se desglosa el IVA (base del 2026-10-24: los impuestos, apagados) | IVA 16 %, 0 % o exento | — | — | — |
| Kits | No la usan: no hay kits en su base (tampoco en la del 2026-10-24) | El tipo existe, sin uso | Pospuestos (2026-10-22, decidido, por revisar): no hacen falta para dejar eleventa | — | — |
| Cambiar precios de varios a la vez («Actualizar varios»): un porcentaje, una cantidad o un precio fijo, con un resumen antes de aplicar | Por confirmar | **Hecho (2026-10-17).** Productos › «Cambiar precios»: los de un departamento, una marca, lo que coincide con un texto o los marcados en la lista; subir o bajar un porcentaje o una cantidad, o un precio fijo, sobre el menudeo, con el mayoreo en la misma proporción y el redondeo a los 50 centavos o al peso. Primero se ve cómo quedan (el margen, para quien ve la utilidad) y se aplica todo junto, con la bitácora. En el piloto, lo de eleventa se cambia allá | — | — | — |
| Importar y exportar productos en Excel | Por confirmar | **Hecho (2026-10-22).** Productos › «Productos en Excel»: exportar la lista como se ve (la búsqueda, que también busca la marca; el departamento; «por completar», «poca existencia» o «margen bajo»; con o sin los inactivos), una fila por variante con sus códigos, nombre, departamento, marca, unidad, precio, mayoreo (desde cuántas y a cuánto), existencia, mínimo, máximo y si se vende; el costo y el margen, solo con «Ver costos, utilidad y margen»; las cajas y paquetes en otra hoja. Importar el mismo Excel ya cambiado: cada producto por su código (uno que no tiene nadie es nuevo), siempre con la vista de lo que cambia, lo nuevo y lo que se ignora con su porqué; Ctrl+Enter lo aplica todo junto, con la bitácora y el historial de precios, con las reglas de la ficha y de «Cambiar precios». Hasta 10,000 renglones. En el piloto no se importa (se exporta igual). **Después de la revisión (2026-10-23):** un renglón sin código se reconoce por la clave escondida de su variante; un Excel exportado antes no regresa lo que cambió después (va aparte hasta «Aplicarlos de todos modos»); la vista trae todo, por páginas; y la bitácora guarda lo completo de cada importación («Lo que cambió», en Excel) | — | — | — |

### Inventario

| eleventa | Uso | El POS hoy | Falta | Dónde | Tamaño |
|---|---|---|---|---|---|
| Existencia por producto, que baja con cada venta | La usan | Kardex; la existencia nunca detiene la venta. En el piloto la trae la lectura de eleventa cada 5 minutos | — | — | — |
| F4: agregar inventario y ajustes | Por confirmar cómo los usan | Compras a ciegas con la factura XML o la nota PDF, ajustes, mermas y conteos | — | — | — |
| Movimientos (por fecha, cajero, producto, departamento y tipo) y kardex | Por confirmar | Hecho (2026-10-17): Inventario › Movimientos, de unos días (hoy, ayer, 7 o 30 días, el mes o de una fecha a otra), por tipo, producto (también escaneado), departamento y quién, de 50 en 50; cada uno con su folio o su motivo, lo que quedó y quién; el costo, con «Ver costos, utilidad y margen». También el kardex de la ficha y los últimos 8 en Productos. **A Excel (2026-10-22):** todos los de esos filtros, sin las páginas (el costo, solo con el permiso; hasta 60,000 por archivo) | — | — | — |
| Productos bajos en inventario: mínimo y máximo por producto | La usan: mínimos en cerca de 4 de cada 10 productos, iguales al máximo (base del 2026-10-24) | Hecho (2026-10-17): cada variante con su mínimo y su máximo en la ficha (F3); «Poca existencia» usa el propio (sin él, 3 o menos) e Inventario › Bajos en inventario lista lo que está en su mínimo o debajo, por departamento, con cuánto pedir (hasta el máximo; sin él, lo del sugerido, y al menos lo que falta para el mínimo). **A Excel (2026-10-22),** todos los de esos filtros. **Los de eleventa (2026-10-24):** la importación trae el mínimo y el máximo de cada producto (`DINVMINIMO`, `DINVMAXIMO`) en su unidad (a granel, metros o piezas sueltas); 0 o vacío es sin mínimo. El mínimo igual al máximo, como los pone la tienda, sale con eso o menos y pide lo que falta para el máximo. En el piloto los pone la lectura cada pocos minutos, también un cambio solo del mínimo (el inventario se lleva en eleventa), y la ficha los enseña sin dejarlos cambiar, con la nota de lo demás de eleventa; la importación final se los pone a lo que aquí no tenga ninguno, una sola vez (revisión del PR #139: repetirla no regresa el que se quitó en F3) | — | — | — |
| Reporte de inventario: lo que vale a costo, por departamento | Por confirmar | Hecho (2026-10-17): Inventario › Lo que vale el inventario, a costo promedio, en total y por departamento, con cuántos están en cero, en negativo o sin costo, y los productos de cada departamento; solo con «Ver costos, utilidad y margen». También en el diagnóstico en Excel. **A Excel desde la caja (2026-10-22):** cada departamento y los productos que se ven, sin las páginas | — | — | — |
| Compras y proveedores: órdenes, compras sugeridas | No la usan: sus proveedores están vacíos (base del 2026-10-24: ninguna orden de compra; lo que llega se registra con «Agregar inventario», sin proveedor) | Compras, órdenes, proveedores y sugerido (fase 2) | — | — | — |

### Clientes y crédito

| eleventa | Uso | El POS hoy | Falta | Dónde | Tamaño |
|---|---|---|---|---|---|
| Clientes, crédito, abonos, estado de cuenta y saldos | No la usan: «sin el módulo de clientes» (base del 2026-10-24: ni crédito ni abonos) | Clientes registrados con crédito, abonos, estado de cuenta y puntualidad (fase 3); apagados en el piloto | — | — | — |

### Reportes

| eleventa | Uso | El POS hoy | Falta | Dónde | Tamaño |
|---|---|---|---|---|---|
| Reportes › Reporte de ventas: semana, mes, año o un periodo; por día, mes o año; ganancia; por departamento y por forma de pago | La usan: ventas por día, mes y año a la fecha, y contra el año anterior (dueño, 2026-10-08) | **Hecho (2026-10-22; se adelantó).** La pestaña Reportes: hoy, la semana, el mes y el año a la fecha, y con ← → los anteriores completos (un mes cerrado, un año), siempre contra el mismo periodo del año pasado (el historial de eleventa más lo de este POS); la venta neta, los tickets, el ticket promedio y la utilidad con su margen (con «Ver costos, utilidad y margen»); la gráfica por hora, día o mes, también como tabla; por departamento (con su margen), por cajero (con sus diferencias de corte), por forma de pago y lo que más se vende. En el piloto, solo lo de la caja 2. Del historial de eleventa, lo devuelto cuenta el día de la venta (el ticket ya lo trae restado): un mes cerrado puede diferir un poco del reporte de eleventa. **Parte 2:** «Exportar a Excel», una hoja por tabla (la utilidad y el costo, solo con el permiso); un periodo de una fecha a otra, en «Ventas por período» (abajo) | — | — | — |
| F3 › Ventas por período: fechas y caja; a Excel o a la impresora de tickets | Por confirmar | **Hecho (2026-10-22, entrega 9, parte 2).** Reportes › «Ventas por período»: de una fecha a otra (o hoy, ayer, esta semana, este mes, el mes pasado) y de una caja o de todas; los tickets con su folio, fecha y hora, caja, cajero, total, forma de pago y si se canceló o se devolvió, de 100 en 100, con los totales al final; a Excel (todos) o, el resumen por día y forma de pago, a la impresora de tickets. En el piloto, solo lo de la caja 2. Va con «Ver reportes de ventas», no en F3 | — | — | — |
| Ventas por cliente; Pulso y los reportes en la nube | No la usan: sin clientes; de eleventa solo usan el punto de venta | — | — | — | — |

### Configuración, usuarios y equipo

| eleventa | Uso | El POS hoy | Falta | Dónde | Tamaño |
|---|---|---|---|---|---|
| Cajeros con usuario, contraseña y permisos | La usan | Permisos por usuario, con fichas y PIN (entrega 1) | — | — | — |
| Configurar el ticket | La usan | Configuración › Ticket (entrega 7) | — | — | — |
| Impresora de tickets en red, una para las dos cajas | La usan | ESC/POS por la red, configurada en cada PC; imprime aparte de la pantalla y, si no contesta (la otra caja imprime), reintenta sola unos 15 s; si aun así no sale, «Reintentar» o «No imprimirlo» (PR #108) | Verla en la tienda con las dos cajas | Piloto, paso 7 | — |
| Cajón de dinero | No hay cajón | Manda el pulso al cobrar en efectivo | — | — | — |
| Lector de código de barras | La usan | Reconoce su ritmo; un escaneo en «Pagó con» no se toma como dinero | Medirlo en la tienda: 11 cifras y ritmo | Piloto, paso 7 | — |
| Respaldo al salir, respaldar ahora y restaurar | Por confirmar (ya está en la guía del 17 y 18) | Diario y en cada corte Z, 30 días, en la caja principal; si la base se daña, avisa con el respaldo de antes. **Respaldar ahora: hecho (2026-10-22).** Configuración › Respaldos, para quien tiene «Configuración de la tienda» (el dueño): el último (cuándo y cuánto mide), dónde se guardan, y «Respaldar ahora», que hace la copia en ese momento con el mismo servicio de la diaria (la copia en línea de SQLite: la base completa, sin detener la venta), en segundo plano (la pantalla dice cómo va; uno a la vez), y dice dónde quedó y cuánto mide; queda en la bitácora. **Restaurar desde la pantalla: hecho (2026-10-23).** «Restaurar un respaldo» › «Elegir respaldo…»: la lista (cuándo, si es diario, de un corte Z o de «Respaldar ahora», y cuánto mide; el de antes del apagón señalado) y «Restaurar este», que dice lo que se pierde y pide el PIN otra vez. La caja principal se reinicia sola y, antes de abrir la base, revisa el respaldo, aparta la de antes sin borrarla y lo pone; las cajas bajan su copia completa y lo que tenían por subir entra en la restaurada. Si el servicio ni arranca, `restaurar.ps1` ([cómo](pruebas-en-tienda.md#restaurar-un-respaldo)). En el piloto, eleventa no se toca y lo ya mandado no se manda otra vez | La copia fuera de la tienda. En el piloto, el respaldo propio de eleventa y ver si se restauró | Plan, pendientes; piloto, diseño | Chico; mediano |
| Licencia y actualizaciones del programa | La usan (la 5.10.3, según la base del 2026-10-24) | Acerca de, con la versión; el instalador de prueba; los scripts de instalación y `actualizar.ps1`, con respaldo y vuelta atrás (2026-10-21) | Instalador de la tienda, actualizaciones que se bajan solas y soporte a distancia | Plan, pendientes | Mediano |
| Recargas, pago de servicios y terminal de tarjeta integrada | No la usan: las recargas y los pagos se cobran fuera | — (pospuestos) | — | — | — |

### Multicaja

| eleventa | Uso | El POS hoy | Falta | Dónde | Tamaño |
|---|---|---|---|---|---|
| La caja principal guarda la base y tiene que estar abierta; las demás se conectan por la red (puertos 3050 y 3309) | La usan | El servicio de Windows en la caja principal, sin sesión abierta, y las cajas con su llave | Las dos cajas reales y el día pico; antes, el modo multicaja con el puente | Plan, pendientes; piloto, al aprobarlo | Mediano |
| Las ventas de todas las cajas en una sola base | La usan (base del 2026-10-24: la principal cobra casi todo; la otra caja, unos 700 tickets al año) | En el piloto, cada venta de la caja 2 va a eleventa: pasos 1 a 4, en código | Semanas contra una copia en un Firebird 2.0.1 y la primera escritura real | Piloto, paso 5 | Semanas |
| Un solo folio para todas las cajas | La usan | Una serie por caja (A-, B-). En el piloto, el ticket lleva el folio de eleventa, y los folios repetidos se revisan justo después de guardar (#109, 2026-10-21) | Probarlo contra la copia | Piloto, paso 5 | — |
| Devolver en una caja lo vendido en la otra | La usan (por eso el folio de eleventa en el ticket, 2026-10-10). En la base del 2026-10-24, las devoluciones del último año se hicieron todas en la misma caja, el mismo día y el mismo turno de la venta, así que en el piloto la caja 2 cubre lo de siempre | Por folio en cualquier caja, con el servicio. En el piloto, de ida y vuelta con eleventa (#109, 2026-10-21): la caja 2 devuelve lo del turno de eleventa en curso; lo demás se devuelve en eleventa y llega con la lectura. Varias parciales de una misma línea (2026-10-24), con la regla de la caja también en el puente: lo que anota eleventa (cada devolución, su total, el ticket, el turno y el día) es lo que salió de la caja 2, sin un centavo de diferencia, y lo devuelto en eleventa suma aquí lo de la línea. Devolver todo lo que le queda a un ticket va a eleventa como su cancelación, como lo hace la tienda (2026-10-24) | Ver en vivo la de otra caja o de otro turno; probarlo contra la copia | Piloto, paso 5 | — |
| Sin la principal o sin red, la caja 2 no vende | — | Contingencia: sigue vendiendo y sube lo pendiente al volver | — | — | — |
| Cada escaneo va a la principal: las pausas por Wi-Fi | — | **Hecho (entrega 5, #100, #105 y #108, 2026-10-21).** Contra la copia de la caja, que baja solo lo que cambió, avisada por la principal; la red con plazos y tres estados; imprimir aparte; pintar menos | Correr el día pico en la caja principal real | Pruebas en tienda | — |

## El orden

Va con el orden aprobado de las entregas (0, 10, 3, 2, 1, 6, 5, 9, 7, 8, 13, 4, 11, 12; ya están la 0, la 10, la 3,
la 2, la 1, la 6, la 7, la 8 y la parte 1 de la 9) y con las decisiones del
[plan](plan.md#la-versión-unicaja-y-el-piloto-en-la-caja-2).

**Lo que cambia el piloto:** mientras la caja 2 manda cada venta a eleventa, eleventa sigue completo. Sus reportes, sus
cortes, sus existencias y sus altas siguen sirviendo como hoy. Por eso al piloto lo detienen el puente y lo básico de la
caja 2; los reportes y lo demás de la tabla detienen el último paso, apagar el puente.

### 1. Para el piloto en la caja 2

| # | Qué | Tamaño |
|---|---|---|
| 1 | **El fin de semana del 17 y 18** (paso 7): dos tickets a la vez en la impresora compartida, el lector (11 cifras y su ritmo) y lo que se mira de eleventa ([guía](pruebas-en-tienda.md#fin-de-semana-del-17-y-18-de-octubre)) | Un fin de semana |
| 2 | **Eleventa abierto en la caja 2, en el mismo turno:** la caja nueva adopta el turno que abre eleventa | Mediano |
| 3 | **Devoluciones y cancelaciones de ida y vuelta** con eleventa | Mediano |
| 4 | **Los respaldos de eleventa:** detectar una restauración y dejar de escribir; el respaldo propio con `gbak` antes de la primera escritura del día | Mediano |
| 5 | **Folios repetidos:** revisar justo después de guardar | Chico |
| 6 | **El mayoreo por revisar:** antes de vender, revisar en F3 lo que el F11 de eleventa vendía a mano y no tiene escala | Chico, con el encargado |
| 7 | **Si la impresora rechaza el segundo ticket:** que la caja reintente sola. Ya está desde el PR #108: la cola de la impresora de tickets (`ColaDeImpresion`) vuelve a intentar sola, unos 15 s, cuando no puede conectarse (la otra caja está imprimiendo); como todavía no le mandó nada, no sale un ticket doble. Si aun así no sale, «Reintentar» o «No imprimirlo» | — |
| 8 | **Paso 6, lo que falta:** cómo se actualiza la caja 2 con un arreglo, sin perder su base ni la configuración del puente. Ya está (2026-10-21): lo de la PC en `C:\ProgramData\PapeleriaPos\servicio.json` y `actualizar.ps1` con respaldo y vuelta atrás ([cómo](pruebas-en-tienda.md#actualizar-la-caja-2-del-piloto)) | — |
| 9 | **Paso 5:** semanas de prueba contra una copia servida por un Firebird 2.0.1; después, **la primera escritura en el eleventa real**, con el visto bueno del dueño | Semanas |
| 10 | **Paso 8:** enseñar a quien atiende la caja 2 (también lo que cambia de eleventa: F11 y cobrar sin ticket; Ins ya es «Varios», como allá) | Una tarde |
| 11 | **Tomar el corte de eleventa:** al ver cerrado el turno adoptado, cerrar el de la caja nueva con lo contado allá. Puede entrar con el piloto andando: mientras, la caja avisa que el corte se hace en eleventa | Mediano |

Del 2 al 5 es el [diseño por aprobar](migracion-eleventa.md#lo-que-falta-para-el-piloto-diseño-2026-10-12-por-aprobar):
es puente y se quita al dejar eleventa. Ins ya es «Varios», como en eleventa (2026-10-22): la cantidad y el código.
Cobrar sin imprimir ya está (2026-10-17): si casi no se imprime el ticket, se elige en Configuración › Ticket › «Al
cobrar». La entrega 5 no detiene el piloto: ahí la caja 2 tiene su propio servicio en la misma PC, y la red solo entra
al mandar a eleventa, con su bandeja.

**Avance (2026-10-17):** el 2, el 3, el 4, el 5 y el 11 ya están en código en la rama del puente (PR #109, #110 y #111,
con los arreglos de su revisión): la caja nueva adopta el turno de eleventa y toma su corte, los folios repetidos, las
devoluciones de ida y vuelta y los respaldos de eleventa ([avance](migracion-eleventa.md#lo-que-falta-para-el-piloto-diseño-2026-10-12-por-aprobar)).
Falta probarlos contra la copia (paso 5) y ver en la tienda lo que todavía queda por revisar: cómo anota eleventa la
devolución de otra caja o de otro turno, y dónde está `gbak.exe` en la caja 2.

**Avance (2026-10-21):** el 7 ya estaba desde el PR #108 (la cola de la impresora reintenta sola, sin ticket doble) y el 8
ya está: la caja 2 se actualiza con `actualizar.ps1`, que respalda la base y los programas, no toca `servicio.json` (la
configuración del puente) y regresa sola a la versión de antes si la nueva no arranca
([cómo](pruebas-en-tienda.md#actualizar-la-caja-2-del-piloto)).

### 2. Para dejar eleventa

Primero el modo multicaja, al aprobar el piloto (decisión del dueño, 2026-10-12); al final, apagar el puente.

| # | Qué | Tamaño |
|---|---|---|
| 1 | **La entrega 5, lo que falta después del PR #100:** buscar contra la copia, bajar solo lo que cambió, plazos y estados de la red, imprimir aparte (con el reintento), pintar menos y la prueba de la ráfaga. La caja 2 pasa a trabajar contra el servicio de la principal | Grande |
| 2 | **El modo multicaja con el puente:** un solo servicio en la caja principal y el puente escribiendo por las dos cajas. Antes, revisar los respaldos que hace eleventa al cerrarse y medir que la principal aguante eleventa, Firebird y el servicio juntos | Mediano |
| 3 | **La red de la tienda** (el arreglo, por aprobar) y, en el POS, volver a buscar a la principal, «sin internet» o «sin red local» y la bitácora de red | Chico, más dos fines de semana |
| 4 | **Las dos cajas reales en Windows y el día pico** con el simulador de cajas (ya está desde el 2026-10-17; falta correrlo en la caja principal, [guía](pruebas-en-tienda.md#el-día-pico-en-la-caja-principal-real)) | Mediano |
| 5 | **La entrega 9, los reportes que el dueño usa en eleventa:** ventas por día, mes y año a la fecha contra el año anterior, con la ganancia para quien la ve; después por cajero, departamento y forma de pago, lo más vendido y Excel. Ya está (2026-10-22, se adelantó): la parte 1 y la 2 (Excel, guardar archivos desde la caja y «Ventas por período») | Hecho; falta verlo en Windows |
| 6 | **El corte del día como el de eleventa:** impreso, de otra fecha y con la ganancia y los departamentos, y el historial de cortes. Ya están los dos (2026-10-17) | Chico |
| 7 | **Departamentos en el POS:** darlos de alta, cambiarles el nombre y desactivarlos | Chico |
| 8 | **Respaldo fuera de la tienda** con una restauración de ensayo, y la vigilancia de la caja principal | Chico |
| 9 | **Instalador de la tienda, actualizaciones y soporte a distancia** (a mano ya se actualiza con `actualizar.ps1`, 2026-10-21) | Mediano |
| 10 | **Depuración, conteo físico e importación final,** sin las ventas que ya mandó el puente | Grande: la depuración y el conteo son trabajo de la tienda; lo del puente ya está en código (2026-10-17, [importación final](migracion-eleventa.md#la-importación-final)) |
| 11 | **Promociones con fechas:** ya están (2026-10-22). Los kits siguen pospuestos: la tienda no los usa | — |
| 12 | **Lo «por confirmar» que sí usen:** ya está todo en el POS: cancelar una entrada o salida, importar y exportar productos en Excel y los Excel de los reportes, del inventario y de «Ventas por período» (2026-10-22 y 23); las ventas de otros días, los mínimos por producto, lo que vale el inventario y los movimientos, desde el 2026-10-17 (los mínimos sí los usan: pasan de eleventa con la importación y la lectura, 2026-10-24). Falta verlos en la tienda | — |

Al final: enseñar a todos, eleventa de consulta unas semanas y apagar el puente.

### 3. Después: lo que eleventa no tiene

En el orden aprobado: lo que se señala, exhibidos y temporada (entrega 13); la bandeja de impresiones con las tres
impresoras de inyección (entrega 4), que retira Control Print; WhatsApp (entrega 11); el ticket desde el celular
(entrega 12). También los servicios con receta y los pedidos (fase 4), los consumibles y la ganancia real del mes, las
etiquetas de cajones y ganchos (para después, 2026-10-15), buscar un producto también por su nombre de antes si la
depuración se lo cambia (2026-10-24) y Telegram (en pausa).

### Recomendaciones

Para decidir; no cambian ninguna decisión:

- **La entrega 9 justo después de aprobar la prueba,** antes de la 13 y la 4, como ya va en el orden. Es lo único que el
  dueño usa hoy en eleventa y el POS no tiene. Puede empezar por lo que hace el reporte de ventas de eleventa (un
  periodo, por día, mes o año, con la ganancia y por departamento) más la comparación contra el año anterior, y dejar lo
  demás de la 9 para después. Se adelantó (2026-10-22): ya está toda, con todo eso, lo demás de la propuesta, Excel y
  «Ventas por período».
- **Dejar eleventa en dos pasos:** el modo multicaja con el puente escribiendo, mientras eleventa sigue completo y
  regresar es inmediato; y apagar el puente cuando el POS tenga la entrega 9 y un mes cerrado que cuadre con eleventa.
  Así, exportar la base a eleventa (pendiente desde el 2026-10-11) no hace falta para poder regresar mientras dure el
  puente.
- **Con Telegram en pausa, el dueño no ve la ganancia del día:** venía en el resumen diario. El corte del día podría
  mostrarla a quien tiene «Ver costos, utilidad y margen», como el de eleventa. Hecho (2026-10-17): el corte del día la
  muestra, también la de cada departamento.
- **Alinear dos líneas:** la tabla de fases del plan (fase 5) y [impresión](impresion.md) ponen las tres impresoras de
  inyección antes del piloto. Con la meta del 2026-10-15 van después, con la entrega 4: Control Print sigue aparte
  mientras tanto.

## Lo que el POS ya hace y eleventa no

Solo para completar el cuadro:

- **Vende sin la caja principal ni la red:** la caja sigue con su copia y sube lo pendiente al volver. Con eleventa, la
  caja 2 sin red no vende.
- **El escaneo no viaja por la red** (PR #100). Eleventa escribe cada artículo en la caja principal al escanearlo.
- **La búsqueda:** pedazos de palabra en cualquier orden, error de dedo, sinónimos de la tienda, familias y lo que esa
  caja eligió antes.
- **Entrar con la ficha y un PIN de 4 cifras;** Ctrl+U cambia de usuario.
- **El corte del turno a ciegas:** se escribe lo contado sin ver lo esperado, y la diferencia queda a nombre del cajero.
  Eleventa muestra lo esperado o solo confirma el cierre.
- **Permisos con límite** («descuento hasta 10 %», «devolución hasta $200») y la bitácora de lo sensible.
- **Recibir a ciegas** y llenar la compra con la factura XML o la nota PDF del proveedor.
- **El conteo del día a ciegas** en la caja y el conteo completo por departamento desde el celular.
- **Transferencias por verificar.**
- **El diagnóstico en Excel:** dinero parado, 80-20, margen por departamento, catálogo por revisar y temporada.
- **Las impresiones por cobrar** del módulo de impresión, lo de Control Print.
- **Al cambiar precios de varios:** el mayoreo en la misma proporción, el redondeo como pone sus precios la tienda y el
  margen de cada uno antes de aplicar (2026-10-17).

## Qué mirar en la tienda

Un fin de semana, en las pantallas de eleventa y viendo trabajar a la caja, sin preguntárselo al dueño. Solo lo que
cambia lo que falta ([guía](pruebas-en-tienda.md#fin-de-semana-del-17-y-18-de-octubre)):

- **La versión de eleventa** (Configuración ➜ Mantenimiento ➜ Licencia, ya en la guía).
- **Al vender:** si los cajeros usan Ins («Varios») para la cantidad (ya está como en eleventa, 2026-10-22: anotar si lo
  usan con la cantidad primero o con el código primero); si al cobrar usan F2 (sin ticket) o el ticket sale siempre
  (para dejar igual «Al cobrar» en Configuración › Ticket, 2026-10-17), y quién da descuentos o cambia precios, para
  dejarle los mismos permisos a quien atiende la caja 2.
- **El corte:** si además del corte de cada cajero se hace el del día o el de todas las cajas; si se imprime y se guarda
  con el efectivo; si se mira la ganancia o las ventas por departamento.
- **Otros días:** si se abren ventas o cortes de otros días (Ventas del día y devoluciones, Historial de cortes), y
  desde qué caja y quién. Ya están en el POS (2026-10-17): lo que se vea dice a quién darle «Ver reportes de ventas»
  (otros días y otras cajas) y quién necesita el historial de cortes.
- **Los reportes:** qué pantalla da hoy las ventas por día, mes y año (Reportes › Reporte de ventas, o F3 › Ventas por
  período). Los dos ya están en el POS (2026-10-22), con Excel y el resumen a la impresora de tickets: anotar quién los
  usa, para darle «Ver reportes de ventas», y si se mira cada ticket o solo los totales.
- **El inventario:** si se usan el reporte de inventario o los movimientos, y quién abre «Productos bajos en
  inventario» y qué hace con cuánto pedir (los mínimos de eleventa ya pasan solos con la importación y la lectura,
  2026-10-24). El POS ya los tiene (2026-10-17), también a Excel (2026-10-22).
- **Los productos:** si se importan o exportan en Excel y si se cambian precios de varios a la vez. Los dos ya están en el POS
  («Cambiar precios», 2026-10-17; «Productos en Excel», 2026-10-22): anotar quién los usa y qué cambia en Excel (precios,
  altas, mínimos), para enseñárselo con el POS.

Ya no hace falta mirarlo, porque lo dice la base del 2026-10-24: las notas en el ticket (nadie las pone), el corte por
correo (apagado), el límite de efectivo (en 0), si se cancelan entradas o salidas (nunca), cómo se registra lo que llega
(«Agregar inventario», de un producto a la vez y sin proveedor) y si hay mínimos (sí, en cerca de 4 de cada 10
productos).

## Fuentes

- **El POS:** el código de `main` (4c5f2e5) y el PR #100; los documentos de este repositorio y sus versiones anteriores
  (las respuestas del dueño del 2026-10-08 al 2026-10-15).
- **La base de eleventa,** por lo que ya sabe el puente ([migración](migracion-eleventa.md)): las formas de pago, el
  mayoreo, las promociones por cantidad, los kits, los turnos y los cortes. Sin abrir ninguna base de la tienda.
- **La base de la tienda del 2026-10-24** (la de la caja principal, que el dueño trajo para abrir la sucursal 2): leída
  en una copia con el Firebird 2.5 embebido de 32 bits, solo para contar lo que se usó del 2025-10-24 al 2026-10-24.
  Ningún dato salió al repositorio.
- **Las páginas de eleventa,** consultadas el 2026-10-16. Describen la versión de hoy, no necesariamente la de la
  tienda:
  - Venta y cobro: [agregar productos](https://eleventa.com/aprender/agregar-productos-venta),
    [búsqueda](https://eleventa.com/aprender/busqueda-de-productos),
    [verificador](https://eleventa.com/aprender/verificador-de-precios),
    [mayoreo y descuentos](https://eleventa.com/aprender/descuentos-precio-mayoreo),
    [tickets pendientes](https://eleventa.com/aprender/atender-a-varios),
    [cobrar](https://eleventa.com/aprender/cobrando-una-venta),
    [formas de pago](https://eleventa.com/aprender/habilitar-formas-pago),
    [ventas del día y devoluciones](https://eleventa.com/aprender/ventas-devoluciones-y-cancelaciones),
    [ticket](https://eleventa.com/aprender/ticket-de-venta),
    [granel](https://eleventa.com/aprender/productos-a-granel), [báscula](https://eleventa.com/aprender/bascula).
  - Efectivo y cortes: [entradas y salidas](https://eleventa.com/aprender/entradas-y-salidas),
    [límite de efectivo](https://eleventa.com/aprender/limitar-efectivo-en-caja),
    [corte del turno](https://eleventa.com/aprender/corte-del-turno),
    [corte del día](https://eleventa.com/aprender/corte-del-dia),
    [historial de cortes](https://eleventa.com/aprender/historial-de-cortes),
    [configurar el corte](https://eleventa.com/aprender/corte-de-caja),
    [corte por correo](https://eleventa.com/aprender/corte-por-correo-electronico).
  - Productos e inventario: [productos](https://eleventa.com/aprender/crear-modificar-eliminar-productos),
    [departamentos](https://eleventa.com/aprender/departamentos),
    [promociones](https://eleventa.com/aprender/promociones),
    [kits](https://eleventa.com/aprender/productos-compuestos-kit),
    [importar](https://eleventa.com/aprender/importar-productos),
    [exportar](https://eleventa.com/aprender/exportar-productos-a-excel),
    [precios de varios](https://eleventa.com/aprender/actualizar-precios-masivo),
    [inventario](https://eleventa.com/aprender/manejo-de-inventario),
    [productos bajos](https://eleventa.com/aprender/productos-bajos-en-inventario),
    [reporte de inventario](https://eleventa.com/aprender/reporte-de-inventario),
    [movimientos](https://eleventa.com/aprender/movimientos-de-inventario),
    [compras y proveedores](https://eleventa.com/aprender/compras-y-proveedores).
  - Clientes y reportes: [clientes](https://eleventa.com/aprender/administracion-de-clientes),
    [estado de cuenta](https://eleventa.com/aprender/estado-de-cuenta),
    [reporte de ventas](https://eleventa.com/aprender/reporte-de-ventas),
    [ventas por período](https://eleventa.com/aprender/ventas-por-periodo),
    [Pulso](https://eleventa.com/aprender/resumen-pulso-eleventa).
  - Equipo y multicaja: [cajeros](https://eleventa.com/aprender/cajeros),
    [impresora de tickets](https://eleventa.com/aprender/configurando-impresora-de-tickets),
    [cajón](https://eleventa.com/aprender/cajon-de-dinero),
    [lector](https://eleventa.com/aprender/lector-codigo-de-barras),
    [respaldo automático](https://eleventa.com/aprender/respaldo-automatico),
    [restaurar](https://eleventa.com/aprender/restaurar-eleventa),
    [actualizaciones](https://eleventa.com/aprender/actualizaciones-automaticas),
    [licencia y versión](https://eleventa.com/aprender/verificar-datos-licencia),
    [multicaja](https://eleventa.com/aprender/conexion-multicaja),
    [historial de cambios](https://descargas.eleventa.com/HistorialCambios-Produccion.html).
