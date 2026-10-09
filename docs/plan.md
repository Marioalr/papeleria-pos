# Papelería POS — Plan

> **Lluvia de ideas.** Las decisiones de abajo son el punto de partida y pueden cambiar mientras se cierra el plan.

> **Foco actual (2026-10-08):** completar el POS para tener en la matriz lo que hoy se tiene con eleventa, con una caja
> principal y una secundaria, más las funciones ya planeadas: servicios y pedidos, WhatsApp y el módulo de impresión
> que reemplaza a Control Print, y **saber exactamente cuánto se gana** ([qué falta](#para-el-piloto-con-dos-cajas)).
> **La multisucursal queda pospuesta, no
> retirada:** su propuesta y lo investigado están en [notas](notas.md) e [ideas](ideas.md#varias-sucursales-pospuesto).

## Contexto

- Papelería con servicios: impresiones, copias, engargolado, enmicado, recargas, pago de servicios y trámites (CURP,
  actas…). Las recargas y el pago de servicios quedan fuera del POS por ahora
  ([idea](ideas.md#recargas-y-pago-de-servicios)).
- Hoy: **1 sucursal con 2 cajas**, la principal y una secundaria (se planea una 3.ª). **2.ª sucursal en camino** y una
  3.ª en unos dos años; en el POS, la multisucursal está [pospuesta](#varias-sucursales-pospuesto).
- Unos **60 tickets al día**, con picos de ~120. La temporada de regreso a clases pesa mucho.
- Inventario de ~500 mil MXN a costo, que creció 10 veces en 4 años; margen bruto anual de ~65 %.
- Se usa **eleventa multicaja** (sin el módulo de clientes, sin apartados ni cotizaciones, y sin nada fiscal). De
  eleventa solo se usa el punto de venta (ni pulso ni facturación); aparte sigue Control Print.
- **Todo por la red local,** sin USB: una impresora de tickets para las dos cajas, dos Epson WF-6590, una EcoTank L5590,
  el grabador y un router Tenda. El internet es de Megacable, y a veces, cuando se va, también se cae la red local
  ([red de la matriz](arquitectura.md#la-red-de-la-matriz)).
- Un número de WhatsApp Business por sucursal, en un celular (también para llamadas); el grueso del trabajo se hace
  desde la PC.
- Las cajas principales se apagan de noche; las sucursales probablemente tendrán horarios distintos.
- **Cámaras Dahua con grabador** en la matriz y en la sucursal 2, que hoy se ven con la app DMSS
  ([cámaras](camaras.md)).
- Posibles dispositivos propios: contador de clientes, presencia en la zona del efectivo, vigilancia nocturna
  ([ideas](ideas.md)).
- **Objetivo:** que el dueño no tenga que estar presente en cada sucursal y tenga control preciso de todas.
- **Fuera de alcance por ahora:** facturación y temas fiscales.

## Decisiones vigentes

| Tema | Decisión |
|---|---|
| Lenguaje | C# / .NET 10 en todo: Windows (impresoras, spooler), Android y web con un solo lenguaje |
| Interfaz | Una sola, en componentes Blazor: la caja (ventana de Windows), la app Android (MAUI) y el panel del dueño. Todo operable con teclado; diseño para 1366×768 |
| Servidor de sucursal | Vive en la **caja principal**, como en eleventa, como servicio de Windows (corre sin sesión iniciada y se reinicia solo) |
| Central | **Pospuesto** (2026-10-08) con la multisucursal. La propuesta: un servidor virtual siempre prendido (Vultr Ciudad de México) para administrar ante un suceso, precios por sucursal y cualquier flujo de mercancía, ensayado antes en una máquina propia con Linux ([propuesta](servidor-remoto.md), [prueba](prueba-sucursales-linux.md)) |
| Base de datos | SQLite en todas partes; en cada lugar el servicio es el único que escribe |
| Núcleo | Sin dependencias de Windows: lo comparten la caja, los servidores y la app Android |
| Inventario | Kardex: solo se agregan movimientos y la existencia es la suma |
| IDs, dinero y fechas | UUIDv7, centavos enteros, UTC; folios legibles por caja (A-000123) |
| Contingencia | Cada caja guarda copia del catálogo y una bandeja de salida: si la caja principal se apaga, las demás siguen vendiendo |
| Roles | Dueño, encargado y cajero; lo que puede hacer cada uno es **configurable** |
| Clientes | El celular solo identifica un **pedido** al recogerlo. **Clientes registrados**: pocos, los de confianza |
| Impresión | Lo que hace Control Print se reescribe como módulo del POS ([detalle](impresion.md)), para las tres impresoras de inyección de la matriz (dos WF-6590 y una L5590) |
| WhatsApp | API oficial en modo coexistencia; nunca librerías no oficiales. Antes de eso, captura manual desde la PC. Sin central, la API recibe los mensajes en un receptor aparte, nunca con un túnel en la caja principal ([WhatsApp](whatsapp.md)) |
| Alertas y autorización remota | Bot de Telegram en la caja principal, desde el piloto. Con el central (pospuesto): un solo bot con foto del suceso y botones, alerta que suena aunque el celular esté en silencio, llamada y acciones a distancia con PIN o huella ([propuesta](servidor-remoto.md)) |
| Panel del dueño | Pospuesto con el central ([propuesta](servidor-remoto.md)). Mientras, Telegram: el resumen diario y `/resumen` |
| Cámaras | Cruzarlas con el sistema queda pospuesto ([cámaras](camaras.md); con una sola sucursal, [idea](ideas.md#cámaras-con-una-sola-sucursal)). Ya, sin costo: configurar los grabadores por seguridad ([lista](camaras.md#configurar-el-grabador)) |
| App Android | .NET MAUI con la misma interfaz, sin publicar en la tienda |
| Fiscal | Fuera de alcance (hoy no se factura nada), pero se guardan clave SAT, unidad y desglose de impuestos para poder facturar después |
| Nube | Por ahora: el respaldo cifrado fuera de la tienda (Backblaze B2), la vigilancia de la caja principal (Healthchecks.io) y, con la API de WhatsApp, su receptor aparte. El central, al retomar la multisucursal ([propuesta](servidor-remoto.md)). Azure for Students no sirve: es solo para estudiantes y para uso educativo no comercial |
| Código | La fase 0 ya se programa; las demás fases, hasta cerrar su parte del plan |
| Idioma del código | Dominio en español (`Producto`, `Existencia`, `Corte`); lo técnico en inglés |
| Acceso a datos | EF Core con migraciones; SQL directo donde convenga (kardex, reportes) |
| Pruebas | xUnit |
| Datos reales | Nunca entran al repositorio. Viven fuera de él, en `Documents\TPV` de la máquina de desarrollo |
| Migración | El inventario **se depura** al pasar al POS nuevo: el catálogo y las existencias de eleventa son punto de partida, no se conservan tal cual. Las existencias de arranque salen del conteo físico |
| Diagnóstico | Se entrega en Excel, una hoja por análisis |
| Precios | Precio al público con IVA incluido; el desglose de impuestos se guarda aparte. Precio por unidad base: el paquete cuesta las piezas que trae, salvo excepción. Por sucursal: pospuesto con la multisucursal (el modelo ya lo permite) |
| Impuestos | IVA 16 %, 0 % o exento; sin IEPS |
| Cantidades | Se vende por metro y a granel: enteros en milésimas de la unidad base |
| Marca | Catálogo propio de marcas: distingue productos parecidos y sirve para buscar |
| Variantes | Texto libre ("Azul") en la fase 0; con atributos (Color, Tamaño) más adelante |
| Costo promedio | Por sucursal |
| Formas de pago | Efectivo, tarjeta (terminal externa) y transferencia (por verificar). Hoy casi todo es efectivo |
| Impresora de tickets | POS-8360, térmica de 80 mm, ESC/POS; el ticket conserva el formato y el logo del de eleventa. En la matriz hay una sola para las dos cajas: por decidir una segunda con su cajón |
| Red de la tienda | Todo por la red local, sin USB (respuesta del dueño). Propuesta, por aprobar después del diagnóstico: por cable, con IP fijas y todo configurado por IP, para que la venta y la impresión no dependan del internet ([red de la matriz](arquitectura.md#la-red-de-la-matriz)) |
| Reportes | Ventas por día, mes y año a la fecha contra el año anterior, y la ganancia real del mes; la meta es saber exactamente cuánto se gana ([detalle](#para-el-piloto-con-dos-cajas)) |
| Administración | Inversión de apertura, gastos fijos e insumos del mes por sucursal: la ganancia mensual real y el punto de equilibrio ([detalle](#administración-y-punto-de-equilibrio)) |
| Git | Ramas cortas por tema, fusionadas a `main` |
| Instalación | La hace el administrador de la PC, una vez y en cada actualización; el día a día corre en un usuario común de Windows, sin privilegios de administrador ([detalle](#instalación-y-usuarios-de-windows)) |

### Cómo han cambiado

| Antes | Ahora | Motivo |
|---|---|---|
| Go + Tauri + Svelte | .NET 10 | Un lenguaje para Windows, Android y web, con acceso nativo a impresoras y spooler |
| WPF (caja) + Blazor (panel) + MAUI (Android): tres interfaces | Una sola interfaz Blazor | Control Print ya no condiciona: se escribe una vez para los tres |
| PostgreSQL en cada sucursal | SQLite | Cero administración en un sitio sin técnico; el volumen es bajo |
| Mini PC dedicado como servidor | Caja principal | Decisión del negocio (como eleventa); la contingencia de las cajas cubre que se apague |
| Central en la nube con PostgreSQL, luego en la caja principal de la matriz | Servidor virtual con SQLite (pospuesto con la multisucursal) | Las cajas se apagan de noche, los horarios serán distintos y podría haber vigilancia nocturna |
| Integrar Control Print por API | Reescribirlo como módulo del POS | Fue una prueba de concepto; así hay un solo inicio de sesión, base e instalador |
| Niveles de confianza automáticos por celular | Celular solo para el pedido; clientes registrados a mano | No guardar números de más |
| Central desde que abra la sucursal 2 | Multisucursal pospuesta; foco en una sucursal con 2 cajas | Decisión del dueño (2026-10-08): primero igualar lo que hoy hace eleventa, con las funciones planeadas |

## Fases

Sin tiempos: se avanza cuando la fase anterior funciona en la tienda. **Foco:** de la 3 a la 5 y la API de WhatsApp
(fase 6); el central (resto de la 6) y la 7 quedan pospuestos.

| Fase | Contenido |
|---|---|
| 0. Base e importación | Modelo de datos probado con datos y productos **ficticios**; al final, importación de prueba desde eleventa y diagnóstico con los datos reales ([detalle](#fase-0)) |
| 1. Caja | Venta, cobro mixto, ticket, turnos y corte ciego, roles y autorizaciones, bitácora, contingencia, Telegram, módulo de impresión |
| 2. Inventario | Compras, recepción ciega, presentaciones, conteos cíclicos, etiquetas, sugerido de compra |
| 3. Precios y clientes | Mayoreo, promociones, kits, clientes registrados, crédito |
| 4. Servicios y pedidos | Primero, servicios con costo real: recetas con los consumibles del servicio (micas, espirales, portadas), depreciación del equipo y luz (engargolado y enmicado). Después pedidos, cola compartida, charolas, recogida por celular, duplicados, apartados, listas escolares, captura manual desde WhatsApp |
| 5. Piloto en la matriz | Con la caja principal y la secundaria: depuración del inventario, conteo físico inicial, arranque, eleventa como respaldo. Antes: la red de la tienda arreglada, las tres impresoras de inyección y los reportes. Desde el arranque, respaldo cifrado fuera de la tienda (Backblaze B2) y vigilancia de la caja principal (Healthchecks.io) ([qué falta](#para-el-piloto-con-dos-cajas)) |
| 6. WhatsApp y central | La API de WhatsApp → pedidos cuando el dueño la quiera, con un receptor aparte ([WhatsApp](whatsapp.md)). **Pospuesto:** el central, con sus entregas 6.1 a 6.6 ([propuesta](servidor-remoto.md#entregas)) |
| 7. Sucursal 2 | **Pospuesta.** Precios por sucursal, traspasos, compras que se reparten, inversión de apertura y punto de equilibrio ([propuesta](servidor-remoto.md)). Si abre antes, abre con eleventa |
| 8. App Android | Preventa y pedidos desde el celular |
| 9. A la medida | Listas escolares avanzadas, dispositivos propios, [piloto automático](ideas.md#asistente-inteligente-piloto-automático) (se apoyaría en el central, pospuesto) y lo que se vaya describiendo |

### Fase 0

1. **Modelo con datos ficticios.** No se importan datos reales de momento: el modelo se construye y se prueba con
   productos, precios y movimientos inventados (un generador de datos de prueba).
   - Solo entra lo que pide la importación: sucursal, departamento, marca, producto y variante, códigos, presentaciones,
     precios (incluido mayoreo), costo, proveedor, kardex e historial de ventas. Ventas, turnos y usuarios esperan a
     la fase 1.
   - El historial de eleventa va en tablas propias de "histórico importado", sin mezclarse con las ventas del POS
     (no tiene folios por caja, turnos ni cortes).
   - Los productos se importan como variantes sueltas; agruparlas ("Pluma azul" + "Pluma roja") se hace después.
2. **Diagnóstico en Excel**, primero sobre los datos ficticios.
3. **Al final, prueba con datos reales:** importación desde una copia de eleventa y el diagnóstico con esos datos.
   - Fechas de eleventa: vienen en hora local; se pasan a UTC con la zona de Ciudad de México (con horario de verano
     antes de octubre de 2022).

Solución: `PapeleriaPos.Nucleo` (sin dependencias de Windows), `PapeleriaPos.Datos` (EF Core + SQLite),
`PapeleriaPos.Herramientas` (generador de ficticios, diagnóstico e importador) y `PapeleriaPos.Pruebas` (xUnit).
Nada de interfaz ni servicio hasta la fase 1.

### Depuración del inventario

El POS nuevo es el salto a un control total, así que no hereda los problemas de eleventa. Antes del piloto se limpia
el catálogo con ayuda del diagnóstico y de la importación ("para revisar"), y el conteo físico completo da las
existencias de arranque. No se trata solo de lo que marcó el diagnóstico: también hay productos que ya no existen y
existencias que no son correctas.

- **Importar, limpiar y volver a importar:** la limpieza se puede hacer sobre la base importada, sin tocar eleventa,
  mientras eleventa sigue en uso. Lo que se decida (dar de baja, juntar, cambiar unidad) se guarda para aplicarlo
  igual en la importación final, justo antes del arranque.
- **Existencias:** las de eleventa solo sirven de referencia para el conteo; las que valen son las contadas.

### Administración y punto de equilibrio

Para saber cuánto se gana de verdad cada mes y decidir la tercera sucursal con números reales, no con intuición:
cuánto costó abrir la segunda y cuándo se pagó sola.

- **Inversión de apertura** por sucursal, una sola vez: mobiliario, equipo (impresoras, computadoras, engargoladoras),
  adecuaciones del local, depósito de renta, trámites e inventario inicial. Con fecha, monto y comprobante.
- **Gastos fijos del mes** por sucursal: renta, sueldos, internet (la luz va aparte: el recibo menos lo ya cargado a
  los servicios). Son los mismos gastos que se restan en la
  [ganancia neta de los servicios](servicios.md#tres-niveles-de-costo).
- **De las ventas:** la utilidad bruta de cada mes (venta − costo de lo vendido), que ya sale del POS.
- **Insumos del negocio:** lo que la tienda gasta pero no vende, comprado cuando se necesita: un rollo de papel
  térmico a medio mes, cinta, bolsas, artículos de limpieza, tóner de la impresora de tickets. Se registran con
  fecha, sucursal, concepto, categoría, monto y comprobante, y entran como gasto del mes en la ganancia mensual.
  - Si se pagan con el efectivo de la caja, la salida F8 lleva la categoría "Insumo" y queda registrado solo, sin
    capturarlo dos veces.
  - Los materiales de los servicios (espirales, micas, papel para copias) no son insumos: están en el inventario y se
    descuentan con la [receta](servicios.md#receta-de-un-servicio). Así no se cuentan dos veces.
- **Equipo y luz:** el equipo (impresoras, engargoladora, enmicadora, guillotina, PC) se deprecia por uso o por mes,
  nunca las dos; el recibo de luz se resta una sola vez, menos lo que ya se cargó a cada servicio
  ([servicios](servicios.md#tres-niveles-de-costo)).

Con eso, un reporte por sucursal:

| Indicador | Cómo se calcula |
|---|---|
| Punto de equilibrio mensual | Venta necesaria para cubrir los gastos: (gastos fijos + insumos + luz y depreciación no cargadas a los servicios + mermas y faltantes + comisión de la tarjeta) ÷ margen bruto |
| Ganancia del mes | Utilidad bruta − mermas y faltantes − gastos fijos − insumos − luz y depreciación no cargadas a los servicios − comisión de la tarjeta |
| Recuperación de la inversión | Mes en que la suma de (ganancia + depreciación) alcanza la inversión de apertura |
| Rendimiento | Ganancia anual ÷ inversión |

- El equipo de la matriz que hoy está en el inventario de eleventa (impresoras, engargoladoras) entra aquí como
  inversión, no como mercancía.
- Si la sucursal 2 abre antes de que el POS llegue a la fase 7, hay que **guardar desde ya la lista de gastos de
  apertura con sus comprobantes** para capturarla después; si no, el cálculo empieza incompleto.

### Prueba con una persona real

Alguien de la tienda (no quien programa) prueba solo lo que ya funciona, sin ayuda, en una PC con Windows y sin tocar
eleventa. Sirve para encontrar lo que estorba al usarlo de verdad, mucho antes del piloto.

**Hito: al cerrar la entrega 2 de la fase 2 (compras completas), antes de empezar la entrega 3.** Es lo más pronto en
que tiene sentido:

- Ahí el día de la tienda ya se hace completo en el POS: abrir turno, vender y cobrar, cancelar y devolver, cortes,
  corregir el catálogo y recibir mercancía con el documento del proveedor.
- Antes no conviene: sin la parte 2 de compras, quien prueba capturaría la nota a mano, justo lo que esa parte
  reemplaza, y sus observaciones serían de algo que va a cambiar.
- Esperar a toda la fase 2 tampoco: lo que más importa probar (la venta y el control) ya está, y los conteos y las
  etiquetas se suman después.

La preparación (instalador, guía, simulaciones) se hace al cerrar esa entrega. Desde ahí, cada entrega agrega sus
tareas a la guía y la prueba se puede repetir con la versión nueva (con los conteos de la entrega 3, por ejemplo).

- **Solo la caja principal:** el servicio y la caja en la misma PC; no hace falta una segunda caja ni la red. La caja
  se da de alta sola, sin pedir la dirección ni el PIN del dueño.
- **Un instalador:** un solo archivo (`PapeleriaPos-Prueba.exe`) que deja todo listo: el servicio, la caja, una base
  ficticia con los usuarios de prueba y un acceso en el escritorio; se desinstala sin dejar nada. Se arma en GitHub
  Actions (Windows), porque la PC de desarrollo es Linux. Como en la tienda, lo instala el administrador para todos los
  usuarios de la PC y quien prueba lo usa con una cuenta común de Windows
  ([instalación](#instalación-y-usuarios-de-windows)).
- **Autoconclusiva:** trae su guía dentro de la caja: tareas cortas (vender, cobro mixto, cancelar, devolver, corte X
  y Z, F3 Productos, alta rápida, recibir una compra…) con lo que debe pasar en cada una. Quien prueba marca si salió
  bien y escribe lo que vio; al final queda un archivo con sus respuestas para mandarlo.
- **Simula lo que no hay:** sin impresora de tickets, el ticket se ve en pantalla; sin Telegram, los avisos que le
  llegarían al dueño se ven en una lista; sin la WF-6590, hay impresiones de ejemplo para cobrar.
- **Se nota que es prueba:** una franja "Prueba: datos ficticios" en la caja y un botón para empezar de cero con la
  base ficticia.
- **Aparte de eleventa:** su propio puerto y su propia carpeta; no lee ni toca la base de eleventa.

### Instalación y usuarios de Windows

El sistema lo instala el administrador de la PC; después, la gran mayoría de las veces se usa desde una cuenta común
de Windows, sin privilegios de administrador. Nada de lo diario puede pedir administrador.

Aquí "usuario" es la **cuenta de Windows** de la PC, no el usuario del POS: el cajero, el encargado y el dueño entran a
la caja con su usuario y PIN del POS, todos desde la misma cuenta común de Windows.

- **Lo que hace el administrador** (al instalar y al actualizar):
  - El servicio de la caja principal como servicio de Windows: arranca con la PC, sin que nadie inicie sesión.
  - Las cajas en Archivos de programa, con su acceso para todos los usuarios.
  - Las reglas del firewall para la red local.
  - La carpeta de datos de la caja, con permiso de escritura para los usuarios.
  - En cada PC que imprime, una cola local por impresora (las dos WF-6590 y la L5590), con puerto TCP/IP estándar y
    "Conservar documentos impresos" (cambiarlo pide administrador).
- **Lo que se hace desde la cuenta común de Windows:** vender y todo lo de la caja, dar de alta la caja (pide el PIN
  del dueño, no administrador), trabajar sin conexión, cotizar e imprimir.
- **Dónde se escribe:** la base de la sucursal la escribe solo el servicio. Lo de cada caja (su configuración y la
  copia para trabajar sin conexión) va en `C:\ProgramData\PapeleriaPos\Caja`, que el instalador deja con escritura para
  las cuentas de Windows: sirve igual si la dio de alta el administrador o si la PC tiene otra cuenta común.
- **Las impresoras de inyección:** el agente lee los trabajos del usuario que tiene abierta la caja. Si alguien imprime
  desde otra cuenta de Windows, esa cuenta necesita "Administrar documentos" en la cola (lo da el administrador).
- **La prueba con una persona real** se instala igual: el administrador la instala para todas las cuentas de Windows
  y quien prueba la usa con una cuenta común.

### Para el piloto con dos cajas

Lo que falta para tener en la matriz lo que hoy hace eleventa, con la caja principal y la secundaria, más lo ya
planeado. Revisado el 2026-10-08 contra el plan, el código y los PR abiertos.

**Las pruebas en la tienda solo se pueden sábado y domingo:** entre semana se programa, se simula y se deja todo listo
para esos días ([forma de trabajar](notas.md#forma-de-trabajar), [guía de los fines de semana](pruebas-en-tienda.md)).

| Qué | Dónde está | Estado |
|---|---|---|
| **La red de la tienda:** diagnóstico, y el arreglo (el módem solo al internet del Tenda, red local nueva, IP fijas, switch, impresoras por IP sin WSD) | [Red de la matriz](arquitectura.md#la-red-de-la-matriz), [notas](notas.md#red-de-la-tienda) | Falta: dos fines de semana ([guía](pruebas-en-tienda.md)) y un script de diagnóstico (chico) |
| En el POS, por la red: que la caja 2 vuelva a buscar sola a la principal, avisos de lo configurado por nombre o con WSD, la franja «sin internet» o «sin red local» y la bitácora de red | [Red de la matriz](arquitectura.md#la-red-de-la-matriz) | Falta (chico) |
| **Las tres impresoras de inyección** (dos WF-6590 y una L5590): cada una con sus costos, su cola en cada PC, su contador, su calibración y sus hojas por cobrar | [Impresión](impresion.md), [notas](notas.md#impresoras) | Falta (mediano); antes, leer la L5590 por SNMP |
| **La impresora de tickets compartida:** reintento si las dos cajas imprimen a la vez y botón «Reintentar»; decidir una segunda impresora con su cajón (≈2,700 MXN) | [Notas](notas.md#impresoras) | Falta (chico) y la decisión |
| **Reportes de ventas:** día contra hace un año (el mismo día de la semana o la misma fecha: por decidir), mes y año a la fecha contra el año anterior, meses cerrados; venta neta, tickets, ticket promedio, costo, utilidad y margen; por departamento, caja, cajero y forma de pago; exportar a Excel y líneas en el resumen de Telegram. El año anterior sale del historial de eleventa (su utilidad, con el costo capturado en eleventa) | [Notas](notas.md#reportes) | Falta (mediano) |
| **Ganancia real del mes:** gastos fijos, consumibles del mes (propuesta, por aprobar: juntarlos aquí con la entrega 3 de la fase 3), recibo de luz, equipo con su depreciación y comisión de la tarjeta; utilidad bruta − mermas y faltantes − gastos = ganancia; punto de equilibrio y cierre del mes por Telegram | [Administración](#administración-y-punto-de-equilibrio), [servicios](servicios.md#tres-niveles-de-costo) | Falta (mediano) |
| Fusionar a `main`, en orden, los PR abiertos (#27 en adelante) | Hoy `main` llega hasta la entrega 1 de la fase 2 | Lo fusiona el dueño. Ojo: el #45 no está en la pila (sale de la rama del #43): decidir si se pasa encima de la punta o se cierra |
| Probar en Windows con las dos cajas reales: servicio, alta de la caja 2, contingencia, respaldos, ventana de la caja, ticket desde las dos y cajón | Fase 1, entregas 1 y 3 | Falta: un sábado o domingo, sin tocar los datos de eleventa ([guía](pruebas-en-tienda.md#después-las-dos-cajas-con-el-pos)) |
| Crear el bot de Telegram de verdad y probarlo | Fase 1, entrega 4 | Falta |
| Probar la impresión con la cola de laboratorio y después con la real | Fase 1, entrega 5 | Falta |
| Prueba con una persona real | [Ver arriba](#prueba-con-una-persona-real) | Hecha en el PR #31; falta fusionarla, instalarla en una PC con Windows limpia y dársela a quien prueba. Su hito ya pasó |
| Medir un día pico (~120 tickets) con las dos cajas, en la caja principal real | Hace falta el simulador de cajas (no existe; [descrito aquí](prueba-sucursales-linux.md#lo-que-hay-que-arreglar-o-construir)) | Falta |
| Precios y clientes | [Fase 3](fase-3-precios-clientes.md) | Las entregas 1 y 2 están en PR abiertos. Propuesta, por aprobar: juntar los consumibles del mes (entrega 3) con la ganancia real del mes |
| Servicios con costo real (engargolado y enmicado con depreciación, luz y consumibles; impresiones por impresora), pedidos y WhatsApp desde la PC. Apartados y cotizaciones guardadas quedan como mejora: el eleventa de la tienda no los tiene | Fase 4 ([servicios](servicios.md), [pedidos](pedidos.md), [WhatsApp](whatsapp.md)) | Falta su plan |
| Instalador de la tienda, soporte a distancia (NetBird gratis u otro) y cómo se actualiza | [Por definir](#pendientes) | Falta |
| Respaldo cifrado fuera de la tienda y vigilancia de la caja principal: se reporta a Healthchecks.io cada pocos minutos en horario, y si no abre o se apaga, Healthchecks.io avisa por Telegram (el bot del POS no puede avisar de su propia caja) | Fase 5 ([notas](notas.md#respaldo-fuera-de-la-tienda-y-aviso-de-apertura)) | Falta (chico) |
| Depuración, conteo físico, importación final y arranque | Fase 5 y [depuración](#depuración-del-inventario) | Falta |
| Promociones y kits, si el piloto queda cerca del regreso a clases | [Fase 3](fase-3-precios-clientes.md) | Pospuestas por ahora |

**Respuestas del dueño (2026-10-08):** usa el reporte de ventas por periodo (día, mes y año a la fecha) y la comparación
contra hace un año, y se agregarán más; la meta es saber exactamente cuánto se gana. Las recargas y el pago de servicios
quedan pospuestos. No hay nada fiscal. Su eleventa no tiene apartados ni cotizaciones. Todas las impresoras van por la
red local.

### Varias sucursales (pospuesto)

El 2026-10-08 el dueño pospuso la multisucursal para completar primero el POS de una sucursal. No se retira:

- **La propuesta** ([central](servidor-remoto.md)): un servidor virtual siempre prendido para administrar ante un
  suceso a cualquier hora (con cámaras y contador), precios por sucursal y cualquier flujo de mercancía, con la mejor
  calidad-precio. Las [cámaras](camaras.md) con un puente por tienda, y la [prueba](prueba-sucursales-linux.md) con la
  laptop o la PC del dueño como central de ensayo.
- **Lo investigado** y lo que sí aplica ya con una sucursal está en [notas](notas.md); las ideas, en
  [ideas](ideas.md#varias-sucursales-pospuesto).
- **El modelo de datos** ya lleva la sucursal en todo registro. Lo que hoy da por hecho una sola sucursal está en
  [notas](notas.md#para-retomar-la-multisucursal) para arreglarlo al retomarla; lo nuevo (fase 4) no debe sumar más
  supuestos de ese tipo.
- **Cuándo retomarla:** cuando el dueño decida pasar la sucursal 2 (o la 3.ª) al POS, unos 6 meses antes de esa fecha;
  mientras, la sucursal 2 abre con eleventa ([señales](notas.md#para-retomar-la-multisucursal)).

## Pendientes

Fase 0:

- [x] Copia de la base de eleventa: `PDVDATA.FDB` (≈ 90 MB), guardada fuera del repositorio
- [x] Revisar el [modelo de datos](modelo-datos.md) propuesto y contestar sus preguntas
- [x] Modelo en código: núcleo, base SQLite y pruebas
- [x] Generador de datos ficticios
- [x] Diagnóstico en Excel sobre los datos ficticios
- [x] Importación de prueba desde eleventa y diagnóstico con los datos reales

Fase 1:

- [x] Impresora de tickets: POS-8360 de 80 mm; formato del ticket de eleventa
- [x] Revisar el [plan de la fase 1](fase-1-caja.md) y contestar sus preguntas (turnos, teclas, fondo, logo)
- [x] Entrega 1: vender y cobrar en una caja (falta probar el cajón cuando lo haya)
- [x] Revisar el [plan de la entrega 2](fase-1-control.md) (control) y contestar sus preguntas
- [x] Entrega 2: control (cancelar, devolver, cambio de precio, transferencias, reglas y bitácora)
- [x] Revisar el [plan de la entrega 3](fase-1-varias-cajas.md) (varias cajas y contingencia) y contestar sus preguntas
- [x] Entrega 3: varias cajas (falta instalarla en la caja principal y probarla con las cajas de la matriz)
- [x] Revisar el [plan de la entrega 4](fase-1-telegram.md) (Telegram) y contestar sus preguntas
- [x] Entrega 4: Telegram (falta crear el bot de verdad y probarlo)
- [x] Revisar el [plan de la entrega 5](fase-1-impresion.md) (módulo de impresión y descuentos) y contestar sus preguntas
- [x] Entrega 5: módulo de impresión, cotizar, la impresora y descuentos (falta probar con la cola de laboratorio)
- [x] Descuentos: en la mercancía, con clientes especiales (entrega 5, parte 3)

Fase 2:

- [x] Revisar el [plan de la fase 2](fase-2-inventario.md) (inventario) y contestar sus preguntas
- [x] Entrega 1: catálogo (F3 Productos) y depuración
- [ ] Entrega 2: compras con recepción ciega y el documento del proveedor
- [ ] Entrega 3: ajustes, mermas y conteos
- [ ] Entrega 4: etiquetas y sugerido de compra

Prueba con una persona real (al cerrar la entrega 2 de la fase 2, [ver arriba](#prueba-con-una-persona-real);
hecha en el PR #31):

- [ ] Instalador de prueba de la caja principal (servicio, caja, base ficticia y usuarios), armado en GitHub Actions
- [ ] La caja principal se da de alta sola en la prueba
- [ ] Guía de prueba dentro de la caja, con las respuestas en un archivo
- [ ] Simular lo que no hay: ticket en pantalla, avisos de Telegram en una lista, impresiones de ejemplo
- [ ] Franja de prueba y botón para empezar de cero
- [ ] Instalarlo en una PC con Windows limpia antes de dárselo a quien prueba

Fase 3:

- [x] Revisar el [plan de la fase 3](fase-3-precios-clientes.md) (precios y clientes) y contestar sus preguntas
- [x] Mayoreo: el F11 de eleventa vende el paquete a su precio; pasa a escalas por cantidad que se aplican solas
  ([decisión](fase-3-precios-clientes.md#decisiones-de-la-revisión))
- [ ] Entrega 1: mayoreo y redondeo al peso
- [ ] Entrega 2: clientes registrados y crédito
- [ ] Entrega 3: consumibles del mes
- [ ] Promociones y kits: antes del próximo regreso a clases

Piloto con dos cajas ([ver arriba](#para-el-piloto-con-dos-cajas)):

- [ ] Primer fin de semana en la tienda: conocer la red, las impresoras, los grabadores y el equipo, sin cambiar nada
  ([guía](pruebas-en-tienda.md#primer-fin-de-semana-conocer-la-tienda))
- [ ] Script de diagnóstico de red en el escritorio de la caja 2, y la instrucción al personal
- [ ] Segundo fin de semana: arreglar la red ([guía](pruebas-en-tienda.md#segundo-fin-de-semana-arreglar-la-red))
- [ ] En el POS, por la red: la caja 2 vuelve a buscar a la principal, avisos de nombres o WSD, la franja de «sin
  internet» y la bitácora de red
- [ ] Las tres impresoras de inyección en el módulo de impresión
- [ ] Impresora de tickets compartida: reintento y botón «Reintentar»; decidir una segunda con su cajón
- [ ] Reportes de ventas por periodo contra el año anterior
- [ ] Ganancia real del mes (con los consumibles de la fase 3, si el dueño aprueba juntarlos)
- [ ] Probar en Windows con las dos cajas reales: servicio, alta de la caja 2, contingencia, respaldos, ventana de la
  caja, ticket desde las dos y cajón
- [ ] Medir un día pico (~120 tickets) con las dos cajas, en la caja principal real, con el simulador de cajas
- [ ] Antes del piloto: respaldo cifrado en Backblaze B2, con una restauración de ensayo, y vigilancia de la caja
  principal en Healthchecks.io ([notas](notas.md#respaldo-fuera-de-la-tienda-y-aviso-de-apertura))
- [ ] Soporte a distancia para instalar las actualizaciones (NetBird gratis u otro, [notas](notas.md#cuentas-y-pagos))
- [ ] Ya, sin costo: configurar los grabadores de las dos sucursales por seguridad
  ([lista](camaras.md#configurar-el-grabador))
- [ ] Correr las pruebas en Windows en cada PR (hoy solo corren si cambia `instalacion/`) y arreglar las 8 que fallan en
  Linux ([notas](notas.md#código-y-pruebas))
- [ ] Escribir el plan de la fase 4 (primero, servicios con costo real) y revisarlo con el dueño

Varias sucursales ([pospuesto](#varias-sucursales-pospuesto)):

- [ ] Retomar la [propuesta del central](servidor-remoto.md), la de [cámaras](camaras.md) y la [prueba en
  Linux](prueba-sucursales-linux.md) cuando el dueño decida pasar la sucursal 2 al POS (unos 6 meses antes), y contestar
  sus preguntas

Depuración del inventario (antes del piloto, [ver arriba](#depuración-del-inventario)):

- [ ] Dar de baja los productos que ya no existen
- [ ] Sacar del inventario el equipo de la tienda (impresoras, engargoladoras, enmicadoras, mobiliario) y registrarlo como [inversión](#administración-y-punto-de-equilibrio)
- [ ] Juntar los productos capturados dos veces (los que marca el diagnóstico)
- [ ] Corregir unidades: los productos a granel que eleventa tiene en KG (listón, micas, hojas sueltas…)
- [ ] Pasar a servicios lo que eleventa vende como producto a granel (impresiones, copias, engargolado, enmicado,
  escaneo)
- [ ] Marcar las recargas y los pagos de servicios como cobro a terceros, antes de comparar contra el año anterior
  (falta esa decisión en la depuración y la opción en la ficha; tampoco existe todavía la decisión «es equipo»)
- [ ] Conteo físico completo: reemplaza todas las existencias de eleventa

Por definir:

- [ ] Instalador de la tienda para el administrador: servicio, cajas, firewall, carpeta de la caja con permisos y las
  colas de las impresoras de inyección ([detalle](#instalación-y-usuarios-de-windows)). Hoy son los scripts de
  `instalacion/`

- [ ] Si la sucursal 2 abre antes de la fase 7: guardar la lista de gastos de apertura con comprobantes
- [ ] Administración: insumos del mes (papel térmico, cinta, limpieza…) para la ganancia mensual
  ([detalle](#administración-y-punto-de-equilibrio)); el dueño lo pasó a la fase 3 al revisar la fase 2 (es la
  [entrega 3](fase-3-precios-clientes.md#3-consumibles-del-mes), «Consumibles del mes»). Propuesta: se junta con la
  ganancia real del mes, porque la captura es la misma
- [ ] Módulo de convivencia: el POS nuevo como una caja más de eleventa durante la adaptación
  ([idea](migracion-eleventa.md#convivencia-con-eleventa-idea-a-evaluar))
- [ ] Demás funciones a la medida (las describe el dueño)
- [x] Privilegios de los clientes registrados: descuento, mayoreo, crédito, pedido sin anticipo y apartar sin pagar;
  nada más ([decisión](fase-3-precios-clientes.md#decisiones-de-la-revisión))
- [ ] Regla para pedidos de no registrados: monto máximo que se prepara sin anticipo, y días para borrar el celular
- [x] Reportes que usa el dueño: ventas por día, mes y año a la fecha, y contra el año anterior; se agregarán más
  ([para el piloto](#para-el-piloto-con-dos-cajas))
- [x] Recargas y pago de servicios: pospuestos ([idea](ideas.md#recargas-y-pago-de-servicios))
- [x] Apartados y cotizaciones: el eleventa de la tienda no los tiene
- [x] Impresoras de la matriz, todas por red: una de tickets para las dos cajas, dos WF-6590 y una L5590
- [ ] Reportes: ¿contra el mismo día de la semana del año pasado o contra la misma fecha? ¿Quién ve la utilidad y la
  ganancia: solo el dueño o también el encargado (hoy el resumen de Telegram la manda a todos los chats vinculados)?
- [ ] Ganancia real: ¿cuáles son los gastos fijos (renta, sueldos, internet, teléfono, contador…) y se pagan con el
  efectivo de la caja o fuera? ¿El sueldo del dueño es gasto? ¿Qué terminal de tarjeta y qué comisión? ¿Un faltante de
  corte lo absorbe el negocio o el cajero?
- [ ] Equipo y luz: la lista del equipo con precio, fecha de compra y años de uso (las tres impresoras de inyección, la
  de tickets, engargoladora, enmicadora, guillotina, PC); el recibo de CFE (tarifa, si es mensual o bimestral, $/kWh);
  marca y modelo de la engargoladora y la enmicadora
- [ ] Impresoras: ¿desde qué PC se imprime a cada una? ¿Para qué se usa cada una? ¿El cliente paga lo mismo en las
  tres? ¿Hay cajón de dinero y cuántos? En eleventa, ¿la caja 2 cobra en efectivo y en qué cajón lo guarda?
- [ ] Red: modelo del módem de Megacable (y si es por cable o fibra) y del Tenda; ¿las cajas y las impresoras van por
  cable o por Wi-Fi? ¿Cada cuánto se cae y cuánto dura? ¿Hay no-break para el módem y el Tenda?
- [ ] Durante el piloto, las recargas y los pagos de servicios: ¿se registran a mano en el POS como cobro a terceros
  para que el corte cuadre, si su efectivo entra al mismo cajón?
- [ ] Mientras la sucursal 2 use eleventa: ¿le manda mercancía la matriz? Si sí, se registra en el POS como salida con
  motivo «A sucursal 2», al costo promedio, hasta que existan los traspasos
- [ ] Autorizar por Telegram: hoy basta con tener el chat vinculado. ¿Se pide además un PIN antes del piloto (el bot lo
  pide, lo borra y la caja principal lo verifica; no necesita el central) o un monto máximo para aprobar así?
- [x] Fiscal: hoy no se factura nada; de eleventa solo se usa el punto de venta (ni pulso ni facturación)
- [ ] ¿El negocio tiene contador y declara IVA? Define si el IVA de la luz y de las compras es costo
- [ ] WhatsApp: ver [pendientes](whatsapp.md#pendientes)
- [ ] Dispositivos propios: ver [ideas](ideas.md)
