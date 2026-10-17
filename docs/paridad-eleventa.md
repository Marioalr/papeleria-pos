# Paridad con eleventa

> **La meta (dueño, 2026-10-15):** «no olvides el objetivo de alcanzar a eleventa». El POS nuevo tiene que reemplazar a
> eleventa en la tienda. Primero va lo que eleventa ya hace (vender, cobrar, los cortes, el inventario y los reportes que
> usa el dueño) y lo que pide dejarlo (el piloto en la caja 2, el puente y el rendimiento). Lo que eleventa no tiene va
> después ([plan](plan.md#la-versión-unicaja-y-el-piloto-en-la-caja-2)).
>
> Revisado el 2026-10-16 contra `main` (4c5f2e5) y el PR #100, abierto, con los documentos, el código del puente (las
> tablas de eleventa que lee y escribe) y las páginas públicas de eleventa ([fuentes](#fuentes)). No se abrió ninguna
> base de la tienda.

## Cómo leer la tabla

- **Uso** en la tienda:
  - **La usan:** lo dicen los documentos o el dueño, o se ve en su base por lo que el puente ya conoce.
  - **Por confirmar:** viene en eleventa, pero no se sabe si lo usan. Se mira en la tienda un fin de semana
    ([lista](#qué-mirar-en-la-tienda)), sin preguntárselo al dueño.
  - **No la usan:** lo dicen los documentos.
- **Dónde:** la entrega de la [propuesta](propuesta-interfaz.md#entregas), el paso del
  [piloto](plan.md#después-el-piloto-en-la-caja-2) o su
  [diseño](migracion-eleventa.md#lo-que-falta-para-el-piloto-diseño-2026-10-12-por-aprobar), un
  [pendiente](plan.md#pendientes) del plan, o «sin plan».
- **Tamaño:** chico, mediano o grande, como en el plan. «—» si no falta nada.
- **La versión de eleventa:** sus páginas describen la de hoy (la 5.50). La de la tienda se ve el fin de semana del 17 y
  18; su base ya trae en el turno las columnas de transferencia y cheque, que según su historial de cambios llegaron con
  la 5.10.02, así que probablemente es reciente.

## La tabla

### Venta y cobro

| eleventa | Uso | El POS hoy | Falta | Dónde | Tamaño |
|---|---|---|---|---|---|
| Escanear y Enter; `5*código`; + y − | La usan | Igual. Con el PR #100, contra la copia de la caja | — | — | — |
| Ins, «Varios»: pide el código y la cantidad | Por confirmar | Ins abre el artículo común, igual que Ctrl+P | Que Ins pida el código y la cantidad, si los cajeros lo usan | Sin plan | Chico |
| Ctrl+P, artículo común | La usan | Ins o Ctrl+P, a la bitácora. En el piloto va a eleventa como su artículo común | — | — | — |
| F10, buscar: el principio del nombre (con `%`, en cualquier parte) | La usan («el camino de siempre») | Bajo el cuadro: pedazos de palabra, error de dedo, sinónimos, familias y lo elegido antes | El nombre de eleventa como sinónimo, cuando la depuración cambie nombres | Entrega 3, lo que falta | Chico |
| F9, verificador de precios | La usan | F9, por código: precio y existencia | — | — | — |
| F6 y F5, tickets pendientes | La usan, poco | F6 y F5 con fichas y «Más»; sobreviven a un apagón | — | — | — |
| Supr, borrar un artículo | La usan | Supr, a la bitácora | — | — | — |
| F11, mayoreo: se escriben las piezas del paquete y F11 pone el precio de mayoreo | La usan | Escalas por cantidad que se aplican solas: las promociones por cantidad de eleventa y las deducidas de las ventas con F11. F11 las muestra | El precio de mayoreo a mano no existe: lo que no tenga escala se cobra a menudeo hasta revisarlo en «Mayoreo por revisar» (F3) | Pendiente nuevo, antes del piloto | Chico, con el encargado |
| Promociones: hoy, F11 al costo en el regreso a clases | La usan, solo en esa temporada (dueño) | Escalas por cantidad; no hay promociones con fechas | Promociones con fechas, con los kits | Fase 3, antes del próximo regreso a clases | Mediano |
| Ctrl+D, descuento; cambiar el precio en la venta | Por confirmar | Ctrl+D y Ctrl+E, con permiso y límite | — | — | — |
| F12, cobrar en efectivo: pagó con y cambio | La usan | F12 al «Pagó con» con el exacto en gris; redondeo al peso | — | — | — |
| Al cobrar: F2, sin imprimir el ticket; F4, notas en el ticket | Por confirmar | Siempre imprime (o lo muestra en pantalla si no hay impresora); sin notas | Cobrar sin imprimir, y reimprimir después sin pedir motivo, si la tienda lo usa | Sin plan | Chico |
| Otras formas de pago: tarjeta, transferencia, vales, cheque, dólares, crédito, mixto | No la usan: en su base todo va en efectivo | Tarjeta, transferencia por verificar, crédito y mixto; apagadas en el piloto | — | — | — |
| Ventas del día y devoluciones: por folio, día, cajero o caja; imprimir copia; devolver y cancelar | La usan (devoluciones y cancelaciones vistas en vivo) | Las de hoy de esa caja (desde la lista «/»), con reimprimir. Devolver hasta 7 días y cancelar en el mismo turno, por el folio, también el de eleventa | Ver las de otro día, otro cajero u otra caja | Sin plan | Chico |
| El ticket: logotipo, datos del negocio y notas al pie | La usan | Encabezado, pie y logotipo; sin QR por ahora, como el de eleventa (2026-10-16). En el piloto, con el folio de eleventa | — | — | — |
| Granel; báscula | Granel sí; báscula no: la medida KG está desactivada en su eleventa | Fracciones por metro, kilo y litro, sin báscula | — | — | — |
| Clientes en la venta y facturar | No la usan: sin el módulo de clientes y sin nada fiscal | Clientes registrados (fase 3); se guardan los datos para facturar después | — | — | — |
| Cotizaciones y apartados | Su eleventa no los tiene (dueño, 2026-10-08) | — (los apartados, con los pedidos de la fase 4) | — | — | — |

### Efectivo, turnos y cortes

| eleventa | Uso | El POS hoy | Falta | Dónde | Tamaño |
|---|---|---|---|---|---|
| Abrir el turno con el fondo; un turno por cajero | La usan | Abrir turno con fondo (y el sugerido); un turno por cajero | En el piloto: adoptar el turno que eleventa abre en la caja 2 | Piloto, diseño | Mediano |
| Entradas y salidas de efectivo, con comentario; se pueden cancelar | La usan (pocas a mano) | F7 y F8 con motivo; comprobante impreso de las salidas. En el piloto van a eleventa | Cancelar una equivocada, si lo usan (hoy se compensa con la contraria) | Sin plan | Chico |
| Límite de efectivo en caja | Por confirmar | Límite con aviso de retiro (apagado en el piloto) | — | — | — |
| Corte del turno, con faltante o sobrante | La usan | Corte del turno a ciegas, solo con el total, impreso; la diferencia a nombre del cajero | En el piloto el corte se hace en eleventa y la caja nueva todavía no lo toma: hoy solo avisa | Piloto, diseño | Mediano |
| Corte del día y de todas las cajas: ventas, ganancia, formas de pago, ventas y ganancia por departamento, entradas, salidas, devoluciones; se imprime y se ve el de otra fecha | Por confirmar | Corte del día de hoy, de todas las cajas, para quien tiene el permiso: lo vendido, el efectivo, las formas de pago, las salidas, las devoluciones, lo fiado y los abonos. Sin ganancia ni departamentos; no se imprime | Imprimirlo; la ganancia y los departamentos (para quien ve la utilidad); el de otra fecha; la hora para recordar el cierre | La hora, entrega 6 (lo que falta); lo demás, sin plan | Chico |
| Historial de cortes: por fecha, cajero, turno y caja | Por confirmar | No hay: solo el corte de hoy | Consultar y reimprimir un corte anterior | Sin plan | Chico |
| El corte por correo al cerrar cada turno | Por confirmar | No hay (el resumen del día iba por Telegram, en pausa) | Si lo usan, que el corte le llegue al dueño sin Telegram | Sin plan | Chico |

### Productos y precios

| eleventa | Uso | El POS hoy | Falta | Dónde | Tamaño |
|---|---|---|---|---|---|
| Alta y cambios de productos: código, nombre, precios, costo, departamento, tipo de venta | La usan. En el piloto se siguen haciendo en eleventa (dueño, 2026-10-10) | F3 Productos con lista y detalle, la ficha, alta rápida desde la venta y «por completar» | — | — | — |
| Departamentos: nuevo, cambiar el nombre, eliminar | La usan | Llegan de eleventa y la lectura del piloto los pone al día; en el POS no hay dónde | Darlos de alta, cambiarles el nombre y desactivarlos | Pendiente nuevo, para dejar eleventa | Chico |
| Precio de costo, ganancia y precio de venta | La usan | Costo promedio y último costo por proveedor; margen mínimo con el precio sugerido al comprar | — | — | — |
| Productos a granel (metro, kilo, litro) | La usan, con unidades mal puestas | Fracciones; cambiar la unidad en la depuración | Corregir las unidades | Depuración | Trabajo de la tienda |
| Eliminar productos | La usan | Dar de baja con su historia; juntar los duplicados | — | — | — |
| Impuestos por producto | No la usan: no se desglosa el IVA | IVA 16 %, 0 % o exento | — | — | — |
| Kits | No la usan: no hay kits en su base | El tipo existe, sin uso | Con las promociones (arriba) | — | — |
| Importar y exportar productos en Excel; cambiar precios de varios a la vez | Por confirmar | Nada de eso: los precios se cambian en la ficha, uno por uno | Lo que la tienda use | Sin plan | Chico cada uno |

### Inventario

| eleventa | Uso | El POS hoy | Falta | Dónde | Tamaño |
|---|---|---|---|---|---|
| Existencia por producto, que baja con cada venta | La usan | Kardex; la existencia nunca detiene la venta. En el piloto la trae la lectura de eleventa cada 5 minutos | — | — | — |
| F4: agregar inventario y ajustes | Por confirmar cómo los usan | Compras a ciegas con la factura XML o la nota PDF, ajustes, mermas y conteos | — | — | — |
| Movimientos (por fecha, cajero, producto, departamento y tipo) y kardex | Por confirmar | El kardex de la ficha (sus últimos 200, sin costo ni filtro de fechas) y los últimos 8 en Productos | Los movimientos por fecha, si los usan | Sin plan | Chico |
| Productos bajos en inventario: mínimo y máximo por producto | Por confirmar | El filtro «Poca existencia» (3 o menos) y el sugerido, con mínimo y máximo calculados | Un mínimo propio por producto, si lo usan | Sin plan | Chico |
| Reporte de inventario: lo que vale a costo, por departamento | Por confirmar | Solo en el diagnóstico en Excel | Verlo en pantalla | Sin plan | Chico |
| Compras y proveedores: órdenes, compras sugeridas | No la usan: sus proveedores están vacíos | Compras, órdenes, proveedores y sugerido (fase 2) | — | — | — |

### Clientes y crédito

| eleventa | Uso | El POS hoy | Falta | Dónde | Tamaño |
|---|---|---|---|---|---|
| Clientes, crédito, abonos, estado de cuenta y saldos | No la usan: «sin el módulo de clientes» | Clientes registrados con crédito, abonos, estado de cuenta y puntualidad (fase 3); apagados en el piloto | — | — | — |

### Reportes

| eleventa | Uso | El POS hoy | Falta | Dónde | Tamaño |
|---|---|---|---|---|---|
| Reportes › Reporte de ventas: semana, mes, año o un periodo; por día, mes o año; ganancia; por departamento y por forma de pago | La usan: ventas por día, mes y año a la fecha, y contra el año anterior (dueño, 2026-10-08) | Las ventas de hoy de cada caja, el corte del día y el diagnóstico en Excel (solo con lo importado de eleventa) | La pestaña de reportes, con el año anterior del historial de eleventa | Entrega 9, después de aprobar la prueba (2026-10-09) | Grande |
| F3 › Ventas por período: fechas y caja; a Excel o a la impresora de tickets | Por confirmar | No hay | Con la entrega 9 | Entrega 9 | En la 9 |
| Ventas por cliente; Pulso y los reportes en la nube | No la usan: sin clientes; de eleventa solo usan el punto de venta | — | — | — | — |

### Configuración, usuarios y equipo

| eleventa | Uso | El POS hoy | Falta | Dónde | Tamaño |
|---|---|---|---|---|---|
| Cajeros con usuario, contraseña y permisos | La usan | Permisos por usuario, con fichas y PIN (entrega 1) | — | — | — |
| Configurar el ticket | La usan | Configuración › Ticket (entrega 7) | — | — | — |
| Impresora de tickets en red, una para las dos cajas | La usan | ESC/POS por la red, configurada en cada PC; «Reintentar» si falla | Reintentar sola si la otra caja imprime e imprimir aparte de la pantalla; verla en la tienda | Piloto, paso 7; plan, pendientes; entrega 5 | Chico |
| Cajón de dinero | No hay cajón | Manda el pulso al cobrar en efectivo | — | — | — |
| Lector de código de barras | La usan | Reconoce su ritmo; un escaneo en «Pagó con» no se toma como dinero | Medirlo en la tienda: 11 cifras y ritmo | Piloto, paso 7 | — |
| Respaldo al salir, respaldar ahora y restaurar | Por confirmar (ya está en la guía del 17 y 18) | Diario y en cada corte Z, 30 días, en la caja principal; si la base se daña, avisa con el respaldo de antes | La copia fuera de la tienda; restaurar desde la pantalla. En el piloto, el respaldo propio de eleventa y ver si se restauró | Plan, pendientes; piloto, diseño | Chico; mediano |
| Licencia y actualizaciones del programa | La usan (la versión, por ver) | Acerca de; el instalador de prueba | Instalador de la tienda, actualizaciones y soporte a distancia | Plan, pendientes | Mediano |
| Recargas, pago de servicios y terminal de tarjeta integrada | No la usan: las recargas y los pagos se cobran fuera | — (pospuestos) | — | — | — |

### Multicaja

| eleventa | Uso | El POS hoy | Falta | Dónde | Tamaño |
|---|---|---|---|---|---|
| La caja principal guarda la base y tiene que estar abierta; las demás se conectan por la red (puertos 3050 y 3309) | La usan | El servicio de Windows en la caja principal, sin sesión abierta, y las cajas con su llave | Las dos cajas reales y el día pico; antes, el modo multicaja con el puente | Plan, pendientes; piloto, al aprobarlo | Mediano |
| Las ventas de todas las cajas en una sola base | La usan | En el piloto, cada venta de la caja 2 va a eleventa: pasos 1 a 4, en código | Semanas contra una copia en un Firebird 2.0.1 y la primera escritura real | Piloto, paso 5 | Semanas |
| Un solo folio para todas las cajas | La usan | Una serie por caja (A-, B-). En el piloto, el ticket lleva el folio de eleventa | Revisar los folios repetidos | Piloto, diseño | Chico |
| Devolver en una caja lo vendido en la otra | La usan (por eso el folio de eleventa en el ticket, 2026-10-10) | Por folio en cualquier caja, con el servicio | De ida y vuelta con eleventa | Piloto, diseño | Mediano |
| Sin la principal o sin red, la caja 2 no vende | — | Contingencia: sigue vendiendo y sube lo pendiente al volver | — | — | — |
| Cada escaneo va a la principal: las pausas por Wi-Fi | — | Contra la copia de la caja (PR #100) | Lo demás de la entrega 5 | Entrega 5 | Grande |

## El orden

Va con el orden aprobado de las entregas (0, 10, 3, 2, 1, 6, 5, 9, 7, 8, 13, 4, 11, 12; ya están la 0, la 10, la 3,
la 2, la 1, la 6, la 7 y la 8) y con las decisiones del [plan](plan.md#la-versión-unicaja-y-el-piloto-en-la-caja-2).

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
| 7 | **Si la impresora rechaza el segundo ticket:** que la caja reintente sola (hoy, «Reintentar» a mano) | Chico |
| 8 | **Paso 6, lo que falta:** cómo se actualiza la caja 2 con un arreglo, sin perder su base ni la configuración del puente | Chico |
| 9 | **Paso 5:** semanas de prueba contra una copia servida por un Firebird 2.0.1; después, **la primera escritura en el eleventa real**, con el visto bueno del dueño | Semanas |
| 10 | **Paso 8:** enseñar a quien atiende la caja 2 (también lo que cambia de eleventa: Ins, F11 y cobrar sin ticket) | Una tarde |
| 11 | **Tomar el corte de eleventa:** al ver cerrado el turno adoptado, cerrar el de la caja nueva con lo contado allá. Puede entrar con el piloto andando: mientras, la caja avisa que el corte se hace en eleventa | Mediano |

Del 2 al 5 es el [diseño por aprobar](migracion-eleventa.md#lo-que-falta-para-el-piloto-diseño-2026-10-12-por-aprobar):
es puente y se quita al dejar eleventa. Si en la tienda se ve que casi no se imprime el ticket o que los cajeros usan
Ins para la cantidad, esos dos cambios chicos conviene hacerlos antes del piloto. La entrega 5 no detiene el piloto:
ahí la caja 2 tiene su propio servicio en la misma PC, y la red solo entra al mandar a eleventa, con su bandeja.

**Avance (2026-10-17):** el 2, el 3, el 4, el 5 y el 11 ya están en código en la rama del puente (PR #109, #110 y #111,
con los arreglos de su revisión): la caja nueva adopta el turno de eleventa y toma su corte, los folios repetidos, las
devoluciones de ida y vuelta y los respaldos de eleventa ([avance](migracion-eleventa.md#lo-que-falta-para-el-piloto-diseño-2026-10-12-por-aprobar)).
Falta probarlos contra la copia (paso 5) y ver en la tienda lo que todavía queda por revisar: cómo anota eleventa la
devolución de otra caja o de otro turno, y dónde está `gbak.exe` en la caja 2.

### 2. Para dejar eleventa

Primero el modo multicaja, al aprobar el piloto (decisión del dueño, 2026-10-12); al final, apagar el puente.

| # | Qué | Tamaño |
|---|---|---|
| 1 | **La entrega 5, lo que falta después del PR #100:** buscar contra la copia, bajar solo lo que cambió, plazos y estados de la red, imprimir aparte (con el reintento), pintar menos y la prueba de la ráfaga. La caja 2 pasa a trabajar contra el servicio de la principal | Grande |
| 2 | **El modo multicaja con el puente:** un solo servicio en la caja principal y el puente escribiendo por las dos cajas. Antes, revisar los respaldos que hace eleventa al cerrarse y medir que la principal aguante eleventa, Firebird y el servicio juntos | Mediano |
| 3 | **La red de la tienda** (el arreglo, por aprobar) y, en el POS, volver a buscar a la principal, «sin internet» o «sin red local» y la bitácora de red | Chico, más dos fines de semana |
| 4 | **Las dos cajas reales en Windows y el día pico** con el simulador de cajas, que no existe | Mediano |
| 5 | **La entrega 9, los reportes que el dueño usa en eleventa:** ventas por día, mes y año a la fecha contra el año anterior, con la ganancia para quien la ve; después por cajero, departamento y forma de pago, lo más vendido y Excel | Grande |
| 6 | **El corte del día como el de eleventa:** impreso, de otra fecha y con la ganancia y los departamentos; y el historial de cortes, si la tienda los usa | Chico |
| 7 | **Departamentos en el POS:** darlos de alta, cambiarles el nombre y desactivarlos | Chico |
| 8 | **Respaldo fuera de la tienda** con una restauración de ensayo, y la vigilancia de la caja principal | Chico |
| 9 | **Instalador de la tienda, actualizaciones y soporte a distancia** | Mediano |
| 10 | **Depuración, conteo físico e importación final,** sin las ventas que ya mandó el puente | Grande: la depuración y el conteo son trabajo de la tienda; lo del puente ya está en código (2026-10-17, [importación final](migracion-eleventa.md#la-importación-final)) |
| 11 | **Promociones con fechas y kits,** si se deja eleventa antes del próximo regreso a clases | Mediano |
| 12 | **Lo «por confirmar» que sí usen:** ventas de otros días, cancelar una entrada o salida, mínimos por producto, lo que vale el inventario, los movimientos, Excel | Chico cada uno |

Al final: enseñar a todos, eleventa de consulta unas semanas y apagar el puente.

### 3. Después: lo que eleventa no tiene

En el orden aprobado: lo que se señala, exhibidos y temporada (entrega 13); la bandeja de impresiones con las tres
impresoras de inyección (entrega 4), que retira Control Print; WhatsApp (entrega 11); el ticket desde el celular
(entrega 12). También los servicios con receta y los pedidos (fase 4), los consumibles y la ganancia real del mes, las
etiquetas de cajones y ganchos (para después, 2026-10-15) y Telegram (en pausa).

### Recomendaciones

Para decidir; no cambian ninguna decisión:

- **La entrega 9 justo después de aprobar la prueba,** antes de la 13 y la 4, como ya va en el orden. Es lo único que el
  dueño usa hoy en eleventa y el POS no tiene. Puede empezar por lo que hace el reporte de ventas de eleventa (un
  periodo, por día, mes o año, con la ganancia y por departamento) más la comparación contra el año anterior, y dejar lo
  demás de la 9 para después.
- **Dejar eleventa en dos pasos:** el modo multicaja con el puente escribiendo, mientras eleventa sigue completo y
  regresar es inmediato; y apagar el puente cuando el POS tenga la entrega 9 y un mes cerrado que cuadre con eleventa.
  Así, exportar la base a eleventa (pendiente desde el 2026-10-11) no hace falta para poder regresar mientras dure el
  puente.
- **Con Telegram en pausa, el dueño no ve la ganancia del día:** venía en el resumen diario. El corte del día podría
  mostrarla a quien tiene «Ver costos, utilidad y margen», como el de eleventa.
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

## Qué mirar en la tienda

Un fin de semana, en las pantallas de eleventa y viendo trabajar a la caja, sin preguntárselo al dueño. Solo lo que
cambia lo que falta ([guía](pruebas-en-tienda.md#fin-de-semana-del-17-y-18-de-octubre)):

- **La versión de eleventa** (Configuración ➜ Mantenimiento ➜ Licencia, ya en la guía).
- **Al vender:** si los cajeros usan Ins para la cantidad; si al cobrar usan F2 (sin ticket) o el ticket sale siempre,
  y si le ponen notas (F4); si dan descuentos o cambian precios, para dejarle los mismos permisos a quien atiende la
  caja 2.
- **El corte:** si además del corte de cada cajero se hace el del día o el de todas las cajas; si se imprime y se guarda
  con el efectivo; si se mira la ganancia o las ventas por departamento; si se manda por correo.
- **Otros días:** si se abren ventas o cortes de otros días (Ventas del día y devoluciones, Historial de cortes), y
  desde qué caja.
- **El efectivo:** si está puesto el límite de efectivo en caja y si se cancelan entradas o salidas.
- **Los reportes:** qué pantalla da hoy las ventas por día, mes y año (Reportes › Reporte de ventas, o F3 › Ventas por
  período), para que la entrega 9 empiece por esa.
- **El inventario:** cómo se registra lo que llega (Agregar inventario) y si se usan los productos bajos en inventario
  (con un mínimo por producto), el reporte de inventario o los movimientos.
- **Los productos:** si se importan o exportan en Excel y si se cambian precios de varios a la vez.

## Fuentes

- **El POS:** el código de `main` (4c5f2e5) y el PR #100; los documentos de este repositorio y sus versiones anteriores
  (las respuestas del dueño del 2026-10-08 al 2026-10-15).
- **La base de eleventa,** por lo que ya sabe el puente ([migración](migracion-eleventa.md)): las formas de pago, el
  mayoreo, las promociones por cantidad, los kits, los turnos y los cortes. Sin abrir ninguna base de la tienda.
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
