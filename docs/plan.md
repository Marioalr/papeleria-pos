# Papelería POS — Plan

> **Lluvia de ideas.** Las decisiones de abajo son el punto de partida y pueden cambiar mientras se cierra el plan.

> **Foco actual (2026-10-08):** completar el POS para tener en la matriz lo que hoy se tiene con eleventa, con una caja
> principal y una secundaria, más las funciones ya planeadas: servicios y pedidos, WhatsApp y el módulo de impresión
> que reemplaza a Control Print, y **saber exactamente cuánto se gana** ([qué falta](#para-el-piloto-con-dos-cajas)).
> **Primero (2026-10-10): la versión unicaja de la caja nueva,** para desarrollo, y después **el piloto en la caja 2**,
> que vende con la caja nueva y manda cada venta a eleventa ([camino](#la-versión-unicaja-y-el-piloto-en-la-caja-2)).
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
  el grabador y un router Tenda; todo por cable salvo la caja 2, que va por Wi-Fi. No hay ningún no-break. El internet
  es de Megacable, y casi siempre que se va, también se cae toda la red local; ya pasaba antes del Tenda
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
| Lenguaje | C# / .NET 10 en Windows (impresoras, spooler), el servicio y la web; la app Android, en Kotlin (2026-10-10) |
| Interfaz | Componentes Blazor para la caja (ventana de Windows) y el panel del dueño. Todo operable con teclado; diseño para 1366×768. Rediseño aprobado (2026-10-10) ([propuesta](propuesta-interfaz.md)); sus preguntas se contestan antes de cada entrega |
| Servidor de sucursal | Vive en la **caja principal**, como en eleventa, como servicio de Windows (corre sin sesión iniciada y se reinicia solo) |
| Central | **Pospuesto** (2026-10-08) con la multisucursal. La propuesta: un servidor virtual siempre prendido (Vultr Ciudad de México) para administrar ante un suceso, precios por sucursal y cualquier flujo de mercancía, ensayado antes en una máquina propia con Linux ([propuesta](servidor-remoto.md), [prueba](prueba-sucursales-linux.md)) |
| Base de datos | SQLite en todas partes; en cada lugar el servicio es el único que escribe |
| Núcleo | Sin dependencias de Windows: lo comparten la caja y los servidores; la app Android usa la API del servicio |
| Inventario | Kardex: solo se agregan movimientos y la existencia es la suma. La existencia nunca detiene una venta: si alguien trae el producto a la caja, existe y la base es la que está mal (2026-10-10, [inventario](inventario.md)) |
| IDs, dinero y fechas | UUIDv7, centavos enteros, UTC; folios legibles por caja (A-000123) |
| Contingencia | Cada caja guarda copia del catálogo y una bandeja de salida: si la caja principal se apaga, las demás siguen vendiendo |
| Escaneo | Siempre contra la copia local del catálogo, también en línea (2026-10-10): la red no entra en el escaneo. Los productos nuevos se dan de alta pocos días a la semana, así que la copia casi siempre está al día; el cobro lleva su versión para que los precios coincidan ([propuesta](propuesta-interfaz.md#rendimiento)) |
| Roles | Sin roles (2026-10-10): cada usuario tiene sus **permisos**, cada uno en «puede», «con autorización» o «no». Al instalar se ponen los de defecto y la primera cuenta tiene todos ([propuesta](propuesta-interfaz.md#usuarios-y-permisos)) |
| Clientes | El celular solo identifica un **pedido** al recogerlo. **Clientes registrados**: pocos, los de confianza |
| Impresión | Lo que hace Control Print se reescribe como módulo del POS ([detalle](impresion.md)), para las tres impresoras de inyección de la matriz (dos WF-6590 y una L5590) |
| WhatsApp | API oficial en modo coexistencia; nunca librerías no oficiales. Antes de eso, captura manual desde la PC. Sin central, la API recibe los mensajes en un receptor aparte, nunca con un túnel en la caja principal ([WhatsApp](whatsapp.md)) |
| Alertas y autorización remota | Bot de Telegram en la caja principal, desde el piloto. Con el central (pospuesto): un solo bot con foto del suceso y botones, alerta que suena aunque el celular esté en silencio, llamada y acciones a distancia con PIN o huella ([propuesta](servidor-remoto.md)) |
| Panel del dueño | Pospuesto con el central ([propuesta](servidor-remoto.md)). Mientras, Telegram: el resumen diario y `/resumen` |
| Cámaras | Cruzarlas con el sistema queda pospuesto ([cámaras](camaras.md); con una sola sucursal, [idea](ideas.md#cámaras-con-una-sola-sucursal)). Ya, sin costo: configurar los grabadores por seguridad ([lista](camaras.md#configurar-el-grabador)) |
| App Android | Nativa en Kotlin (2026-10-10), con la misma API del servicio; sin publicar en la tienda |
| Fiscal | Fuera de alcance (hoy no se factura nada), pero se guardan clave SAT, unidad y desglose de impuestos para poder facturar después |
| Nube | Por ahora: el respaldo cifrado fuera de la tienda (Backblaze B2), la vigilancia de la caja principal (Healthchecks.io) y, con la API de WhatsApp, su receptor aparte. El central, al retomar la multisucursal ([propuesta](servidor-remoto.md)). Azure for Students no sirve: es solo para estudiantes y para uso educativo no comercial |
| Código | La fase 0 ya se programa; las demás fases, hasta cerrar su parte del plan |
| Idioma del código | Dominio en español (`Producto`, `Existencia`, `Corte`); lo técnico en inglés |
| Acceso a datos | EF Core con migraciones; SQL directo donde convenga (kardex, reportes) |
| Pruebas | xUnit |
| Datos reales | Nunca entran al repositorio. Viven fuera de él, en `Documents\TPV` de la máquina de desarrollo |
| Migración | El inventario **se depura** al pasar al POS nuevo: el catálogo y las existencias de eleventa son punto de partida, no se conservan tal cual. Si un producto cambia de nombre, el de eleventa queda como su sinónimo, para que el que se recuerda lo siga encontrando (2026-10-10). Las existencias de arranque salen del conteo físico. Antes, el piloto: la caja 2 vende con el POS nuevo y manda cada venta a eleventa, que sigue siendo el oficial (2026-10-10, [convivencia](migracion-eleventa.md#el-piloto-convivencia-con-eleventa)) |
| Diagnóstico | Se entrega en Excel, una hoja por análisis |
| Precios | Precio al público con IVA incluido; el desglose de impuestos se guarda aparte. Precio por unidad base: el paquete cuesta las piezas que trae, salvo excepción. Por sucursal: pospuesto con la multisucursal (el modelo ya lo permite) |
| Impuestos | IVA 16 %, 0 % o exento; sin IEPS |
| Cantidades | Se vende por metro y a granel: enteros en milésimas de la unidad base |
| Marca | Catálogo propio de marcas: distingue productos parecidos y sirve para buscar |
| Variantes | Texto libre ("Azul") en la fase 0; con atributos (Color, Tamaño) más adelante |
| Costo promedio | Por sucursal |
| Formas de pago | Efectivo, tarjeta (terminal externa) y transferencia (por verificar). Hoy casi todo es efectivo |
| Impresora de tickets | POS-8360, térmica de 80 mm, ESC/POS; el ticket conserva el formato y el logo del de eleventa. En la matriz hay una sola para las dos cajas y no hay cajón ([notas](notas.md#impresoras)) |
| Red de la tienda | Todo por la red local, sin USB (respuesta del dueño). Propuesta, por aprobar, con lo que ya hay (sin compras, 2026-10-09): IP fijas puestas en cada equipo, todo configurado por IP y los equipos de la tienda en el switch, para que la venta y la impresión no dependan del router ni del internet ([red de la matriz](arquitectura.md#la-red-de-la-matriz)) |
| Reportes | Ventas por día, mes y año a la fecha contra el año anterior, y la ganancia real del mes; la meta es saber exactamente cuánto se gana ([detalle](#para-el-piloto-con-dos-cajas)). Pospuestos (2026-10-09) hasta después de la [prueba con una persona real](#prueba-con-una-persona-real) y de que el dueño la apruebe. La utilidad y la ganancia solo las ve el dueño (2026-10-09) |
| Administración | Inversión de apertura, gastos fijos e insumos del mes por sucursal: la ganancia mensual real y el punto de equilibrio ([detalle](#administración-y-punto-de-equilibrio)) |
| Git | Ramas cortas por tema, fusionadas a `main` |
| Instalación | La hace el administrador de la PC, una vez y en cada actualización; el día a día corre en un usuario común de Windows, sin privilegios de administrador ([detalle](#instalación-y-usuarios-de-windows)) |

### Cómo han cambiado

| Antes | Ahora | Motivo |
|---|---|---|
| Go + Tauri + Svelte | .NET 10 | Un lenguaje para Windows, Android y web, con acceso nativo a impresoras y spooler |
| WPF (caja) + Blazor (panel) + MAUI (Android): tres interfaces | Una sola interfaz Blazor | Control Print ya no condiciona: se escribe una vez para los tres |
| Roles de dueño, encargado y cajero, con reglas por rol | Permisos por usuario; la primera cuenta, con todos | Decisión del 2026-10-10 ([propuesta](propuesta-interfaz.md#usuarios-y-permisos)) |
| App Android en MAUI con la interfaz Blazor | Kotlin nativo, con la misma API | Decisión del 2026-10-10: la interfaz Blazor se diseña solo para la caja, con la mayor rapidez ([propuesta](propuesta-interfaz.md)) |
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
| 5. Piloto en la matriz | Primero el piloto en la caja 2: vende con la caja nueva y manda cada venta a eleventa, que sigue siendo el oficial ([piloto](#la-versión-unicaja-y-el-piloto-en-la-caja-2)). Después, dejar eleventa con las dos cajas: depuración del inventario, conteo físico inicial, arranque, eleventa como respaldo. Antes: la red de la tienda arreglada y las tres impresoras de inyección; los reportes, después de la prueba con una persona real y su aprobación. Desde el arranque, respaldo cifrado fuera de la tienda (Backblaze B2) y vigilancia de la caja principal (Healthchecks.io) ([qué falta](#para-el-piloto-con-dos-cajas)) |
| 6. WhatsApp y central | La API de WhatsApp → pedidos cuando el dueño la quiera, con un receptor aparte ([WhatsApp](whatsapp.md)). **Pospuesto:** el central, con sus entregas 6.1 a 6.6 ([propuesta](servidor-remoto.md#entregas)) |
| 7. Sucursal 2 | **Pospuesta.** Precios por sucursal, traspasos, compras que se reparten, inversión de apertura y punto de equilibrio ([propuesta](servidor-remoto.md)). Si abre antes, abre con eleventa |
| 8. App Android | Preventa y pedidos desde el celular. Preventa: el ticket se arma en el celular, se elige a qué caja mandarlo y ahí llega en espera, con un efecto para que se note ([propuesta](propuesta-interfaz.md#desde-el-celular)) |
| 9. A la medida | Listas escolares avanzadas, dispositivos propios, [piloto automático](ideas.md#asistente-inteligente-piloto-automático) (se apoyaría en el central, pospuesto) y lo que se vaya describiendo |

### Fase 0

1. **Modelo con datos ficticios.** No se importan datos reales de momento: el modelo se construye y se prueba con
   productos, precios y movimientos inventados (un generador de datos de prueba).
   - Solo entra lo que pide la importación: sucursal, departamento, marca, producto y variante, códigos, presentaciones,
     precios (incluido mayoreo), costo, proveedor, kardex e historial de ventas. Ventas, turnos y usuarios esperan a
     la fase 1.
   - El historial de eleventa va en tablas propias de "histórico importado", sin mezclarse con las ventas del POS
     (no tiene folios por caja, turnos ni cortes).
   - Los productos se importan como variantes sueltas; agruparlas ("Lapicero azul" + "Lapicero rojo") se hace después.
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
- **Gastos fijos del mes** por sucursal: renta y sueldos, y cualquier otro fijo, como el internet. La luz y los
  insumos, que el dueño también nombró (2026-10-09), van en sus renglones de abajo. **Cada gasto se captura cuando se
  paga,** para que al cierre del mes se sepa cuánto se ganó de verdad: si se paga con el efectivo de la caja, como
  salida (tecla F8) con su categoría; si se paga por fuera, se captura aparte. Son los mismos gastos que se restan en
  la [ganancia neta de los servicios](servicios.md#tres-niveles-de-costo).
- **De las ventas:** la utilidad bruta de cada mes (venta − costo de lo vendido), que ya sale del POS.
- **Insumos del negocio:** lo que la tienda gasta pero no vende, comprado cuando se necesita: un rollo de papel
  térmico a medio mes, cinta, bolsas, artículos de limpieza, tóner de la impresora de tickets. Se registran con
  fecha, sucursal, concepto, categoría, monto y comprobante, y entran como gasto del mes en la ganancia mensual.
  - Si se pagan con el efectivo de la caja, la salida F8 lleva la categoría «Consumibles» (los insumos, como les dice
    el dueño) y queda registrado solo, sin capturarlo dos veces.
  - Los materiales de los servicios (espirales, micas, papel para copias) no son insumos: están en el inventario y se
    descuentan con la [receta](servicios.md#receta-de-un-servicio). Así no se cuentan dos veces.
- **Equipo y luz:** el equipo (impresoras, engargoladora, enmicadora, guillotina, PC) se deprecia por uso o por mes,
  nunca las dos. No hay fecha de compra; propuesta, por aprobar: depreciarlo desde el arranque, con lo que vale hoy y
  los años que le quedan ([notas](notas.md#luz-y-costo-de-los-servicios)). De la luz, cuenta como gasto el recibo menos
  lo que ya se cargó a cada servicio, una sola vez ([servicios](servicios.md#tres-niveles-de-costo)).

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

### La versión unicaja y el piloto en la caja 2

**Decisiones del dueño (2026-10-10):**

- **La versión unicaja es para desarrollo.** La [caja nueva](propuesta-interfaz.md) corre sola en una PC, con su
  servicio y una base de prueba, para ver cómo avanza y cómo se comporta. No es para vender con productos ni clientes
  reales: no le hacen falta respaldos ni importación, y lo que se capture no importa. Va aparte de eleventa, con su
  propio puerto y su propia carpeta, como la [prueba con una persona real](#prueba-con-una-persona-real).
- **El piloto es en la caja 2,** la secundaria de la matriz. Durante un tiempo vende con el sistema nuevo y guarda en su
  propia base, pero manda cada venta a la caja principal igual que hoy lo hace eleventa multicaja: la venta queda en los
  dos lados. Eleventa sigue siendo el sistema oficial en la caja principal
  ([convivencia](migracion-eleventa.md#el-piloto-convivencia-con-eleventa)).

#### Primero: la versión unicaja (entrega 0)

Una primera versión que ya venda, hecha sobre lo que ya funciona: el mismo servicio, el mismo cobro, el mismo ticket y
la misma base. Cambia la pantalla de venta; lo que todavía no se rediseña abre lo de hoy, con sus mismas teclas. Cada
caja elige si usa la pantalla nueva o la de hoy (Configuración › Esta caja). Se instala con el instalador de prueba que
ya existe, y cada entrega que sigue se ve en esa misma versión.

| Qué | Cómo, por ahora |
|---|---|
| El cuadro de captura | El lector en una sola llamada y en orden; las teclas esperan a que termine el último escaneo (lo anotado en el #56). El código, `12*código`, y el folio, que abre la devolución de hoy |
| Buscar por nombre | Los resultados debajo del cuadro: pedazos de palabra en cualquier orden, el nombre de eleventa como sinónimo, primero lo elegido antes con ese texto y luego lo más vendido; hasta 30, con ↓ |
| La lista del ticket | La cantidad con + y −, Supr para quitar y la línea que se ilumina al agregarse. Sin sonidos por ahora |
| Los rápidos | Copia B/N ($2) y CURP ($10, que vende 10 impresiones mientras Control Print siga aparte), con Alt+1 y Alt+2 y la cantidad antes |
| El panel del total | El total, F12 al «Pagó con» con el exacto en gris, el cambio y cobrar. Sin billetes: se escribe lo que dio. Tarjeta, transferencia y crédito abren el cobro de hoy |
| Tickets en espera | F6 y F5 con sus fichas, guardados en la caja |
| Lo demás | Descuento, precio, artículo común, mayoreo, cliente, verificador, impresiones, entradas y salidas, corte, productos e inventario: lo de hoy |
| Para aprender | Guarda lo que se busca: el texto, cuántos resultados hubo y qué se eligió |

No trae, a propósito: los colores y detalles finales, los permisos por usuario (siguen los roles de hoy), el cliente
como flecha, el cobro mixto dentro del panel, las familias de colores, las fichas para afinar, la bandeja de
impresiones, ni el mostrador y la temporada. Llegan con las entregas que siguen.

**Avance (2026-10-10):** la primera parte ya está:
- la pantalla nueva junto a la de hoy («Probar la pantalla nueva»);
- el cuadro de captura leído en JavaScript, con las teclas que esperan al último escaneo;
- los rápidos, configurables por sucursal (la base de prueba trae Copia B/N y CURP);
- el panel del total con F12 y el cobro en efectivo;
- los tickets en espera con sus fichas, F6 y F5.

La segunda parte, la búsqueda debajo del cuadro, también:
- pedazos de palabra en cualquier orden, con lo escrito resaltado;
- primero lo que esa caja eligió antes con el mismo texto, luego lo más vendido en 90 días;
- ↑ ↓ y Enter, o clic, y `3*texto` para la cantidad;
- Enter sin elegir agrega el primero;
- cada búsqueda se guarda (tabla `Busquedas`), también las que no encontraron nada.

Y la tercera, los tickets en espera que sobreviven a un reinicio o a un apagón:
- se guardan en `caja-local.db` cada vez que cambia el ticket que se atiende (F6, F5, retomar, cobrar);
- también el que se atiende, como estaba en ese momento;
- al entrar a la venta vuelven a esperar, armados con los precios de cuando se agregó cada producto;
- «Empezar de cero» los borra.

Con eso, la entrega 0 queda completa. El nombre de eleventa como sinónimo llega cuando la limpieza del catálogo cambie
nombres; mientras tanto, los nombres son los mismos de eleventa.

**Avance (2026-10-11), entregas 2 y 3, primera parte:** lo que se ve en la pantalla de venta ya es como el
[prototipo](propuesta-interfaz/prototipo.html):
- **La base visual:** sus colores, letras, medidas e íconos.
- **Arriba:**
  - la caja, principal o secundaria;
  - los módulos que tiene quien entró;
  - el estado: el servicio aquí o la conexión con la principal, la impresora de tickets y las impresiones por cobrar;
  - el usuario, con su menú (Ctrl+U para cambiar de usuario).
- **Los permisos de la sesión en una sola consulta** (`GET /api/usuarios/{id}/permisos`, también sin conexión, con la
  copia de la caja):
  - lo que no puede no aparece, ni su tecla;
  - lo que pide autorización lleva el candado con el nombre de quien autoriza;
  - lo que tiene límite lo dice («Descuento hasta 10 %»).
  Siguen los roles de hoy: los permisos por usuario son la entrega 1.
- **«/»:** todas las acciones que tiene, con su tecla, y se filtran escribiendo («/desc»).
- **Las acciones del artículo,** pegadas a su fila: precio, descuento, mayoreo y quitar.
- **El panel del total:**
  - el cliente como flecha, con lo que debe y «Abonar»;
  - tarjeta y transferencia junto a «Pagó con»;
  - el ahorro por mayoreo.
- **La barra de teclas** de la propuesta, con el conteo del día a la derecha.

Falta, en este orden:
1. los paneles a la derecha en lugar de las ventanas que tapan el ticket, con la autorización dentro;
2. el cobro mixto en el panel y «Más» en los tickets en espera;
3. la búsqueda con familias de colores, fichas para afinar, el error de dedo y los sinónimos de la tienda.

#### Después: el piloto en la caja 2

Mientras se arma el puente con eleventa, la versión unicaja sigue creciendo con la venta completa (entrega 3) y la base
visual (entrega 2). Para el piloto hace falta, en este orden:

| # | Qué | Por qué | Tamaño |
|---|---|---|---|
| 1 | **Leer de eleventa** el catálogo, los precios, el mayoreo y las existencias, cada pocos minutos, y con eso armar la base de la caja 2 | La caja 2 vende con lo mismo que la principal; eleventa sigue mandando | Mediano |
| 2 | **Entender cómo escribe eleventa una venta,** en un eleventa de pruebas con una copia de la base (nunca en la tienda): qué tablas toca, en qué orden, los folios, el turno y el corte | Para escribir como lo hace hoy la caja 2 de eleventa | Mediano |
| 3 | **Mandar cada venta a eleventa,** por la red, como una caja más (Firebird en la caja principal). Con bandeja de salida: si la principal o la red no contestan, la caja 2 sigue vendiendo y manda lo pendiente al volver, sin duplicar | La venta queda en los dos lados aunque se caiga la red de la tienda | Grande |
| 4 | **Cancelaciones, devoluciones, turno y corte** de la caja 2, también en eleventa | Que los cortes y las existencias de eleventa sigan cuadrando | Mediano |
| 5 | **Semanas de prueba contra una copia de eleventa,** comparando la base antes y después de cada venta | Escribir en una base ajena puede descuadrarla | — |
| 6 | **La base de la caja 2 contra apagones** (entrega 10), su respaldo y cómo se actualiza con un arreglo | Desde el primer día de ventas reales | Chico |
| 7 | **En la tienda, un sábado o domingo:** la impresora de tickets que comparten las dos cajas y el lector (si lee QR, qué manda con los códigos de 11 cifras y su ritmo al teclear) | Lo que no se puede simular ([guía](pruebas-en-tienda.md)) | Un fin de semana |
| 8 | **Enseñar** a quien atiende la caja 2 | — | Una tarde |

- **A eleventa va lo que entiende:** la venta, el cobro y lo que descuenta de la existencia. Lo que eleventa no tiene
  (servicios con receta, pedidos, lo que se buscó) se queda solo en la caja nueva.
- **Decidido por el dueño (2026-10-10):** durante el piloto, las altas de productos y los cambios de precio se siguen
  haciendo en eleventa y llegan a la caja 2 con la lectura; y el ticket de la caja 2 lleva el folio de eleventa, para
  que una devolución se pueda hacer en cualquiera de las dos cajas.
- **Decidido por el dueño (2026-10-11):** si la caja principal o el Wi-Fi no contestan en unos 2 segundos, el ticket sale
  con el folio propio de la caja 2 y la venta se manda a eleventa cuando vuelva la red; su devolución se hace en la
  caja 2. La caja no deja de vender por esperar a eleventa.
- **Aclarado por el dueño (2026-10-11):** la compatibilidad con eleventa hace falta porque es el POS que se usa hoy, pero
  la base nueva se concentra en sus propias metas (el máximo rendimiento, etc.) y no se adapta a eleventa. Durante el
  piloto, la caja 2 guarda todo en su propia base y a la caja principal le manda solo lo necesario para que su eleventa
  lo registre como una venta más. Lo de eleventa vive en el puente (`PapeleriaPos.Eleventa` y la bandeja de salida).
  Más adelante habrá una opción para exportar la base a una versión de eleventa.

**Avance (2026-10-10):** el paso 1 está hecho ([cómo funciona](migracion-eleventa.md#la-lectura-de-cada-pocos-minutos-paso-1)).
El servicio de la caja 2 lee eleventa por la red cada pocos minutos, solo leyendo, y pone al día su base: altas,
precios, promociones, costos y existencias. Se probó con la base real, primero toda local con un Firebird 2.5 propio y
después (2026-10-11) desde la caja 2 contra el Firebird 2.0 de la caja principal: lee todo en menos de un segundo.

**Avance (2026-10-11):** el paso 2 va a la mitad ([lo que se sabe](migracion-eleventa.md#cómo-escribe-eleventa-una-venta-paso-2)).
De una copia ya se sabe qué deja una venta en efectivo: el ticket, sus artículos, el inventario y los acumulados del
turno, del día y del departamento. También que el folio lo calcula el programa (último + 1) y que alguna vez se
repitió. Mirándolo trabajar en la caja 2 con `observar-eleventa`, se confirmó que eleventa escribe cada artículo en la
caja principal al escanearlo (con su apartado de inventario), y eso explica sus pausas por Wi-Fi. También se vio qué
escribe al abrir la caja y al cobrar. Falta mirar cancelar, devolver, entradas y salidas, cerrar el turno y el corte,
cuando pasen de verdad.

**Avance (2026-10-11):** del paso 3 ya está la escritura ([cómo](migracion-eleventa.md#mandar-cada-venta-paso-3)). Una
venta en efectivo de la caja nueva entra a eleventa como la escribe una caja de eleventa al cobrar, en una sola
transacción y sin duplicarse si se manda dos veces. Contra una copia de la base real deja lo mismo que dejó eleventa en
la caja 2.

Después, el mismo día, la bandeja de salida: cada venta cobrada entra a la bandeja junto con el cobro y se intenta
mandar enseguida. El ticket sale con el folio de eleventa si contestó a tiempo; si no, con el de la caja 2, y la venta
se manda en la siguiente vuelta. Con eso queda el paso 3. Se probó de punta a punta contra copias, apagando y prendiendo
el Firebird de prueba. Lo siguiente es el paso 4: cancelaciones, devoluciones, turno y corte.

**Avance (2026-10-11):** el paso 4, sin las entradas y salidas de dinero, que quedan para después (lo pidió el dueño)
([cómo](migracion-eleventa.md#cancelar-devolver-y-cerrar-el-turno-paso-4)). De una copia se sacó cómo escribe eleventa
una cancelación, una devolución parcial y el cierre del turno con su faltante o sobrante. La caja 2 los manda por la
misma bandeja, en orden. Se probó contra copias: los cortes de eleventa cuadran con los de la caja 2. Mientras no vayan
las entradas y salidas de la caja 2, el corte de eleventa no las tiene. Falta verlos en vivo en la tienda cuando pasen de
verdad.

**Avance (2026-10-11):** con las entradas y salidas de dinero (F7 y F8), el paso 4 queda completo
([cómo](migracion-eleventa.md#entradas-y-salidas-de-dinero-paso-4)). De la copia se sacó cómo las escribe eleventa: un
movimiento del corte con el comentario, que suma al turno y al día si ese día ya tiene renglón (eso corrige lo que se
había dicho del faltante al cerrar). La caja 2 las manda por la misma bandeja, también las de contingencia y las de antes
de la primera venta, y con eso eleventa cierra el turno con lo contado. Se probó contra copias. Falta ver en vivo una
entrada o salida después de medianoche en un turno que ya vendió ese día.

**En vivo (2026-10-11):** se vieron en eleventa una devolución parcial, una cancelación y un cierre de turno
([qué salió](migracion-eleventa.md#verlo-en-vivo)). La devolución y el cierre salieron como los escribe la caja 2. La
cancelación también le resta lo devuelto al subtotal, y `NUMERO_ARTICULOS` baja solo las piezas, no lo de granel (también
al devolver una parte); la copia lo confirma y la caja 2 ya lo hace igual. Faltan por ver en vivo las entradas y salidas.

#### Al terminar el piloto

Cada parte entra sin detener a la caja 2:

1. **Terminar la venta** (lo que falta de la entrega 3) y **la base visual** (entrega 2), con lo que enseñen el piloto y
   las búsquedas guardadas.
2. **Los permisos por usuario** (entrega 1) y **la entrada y el corte** (entrega 6).
3. **Dejar eleventa:** el servicio nuevo en la caja principal y la caja 2 conectada a él, con su rendimiento (entrega 5:
   escaneo contra la copia local, cambios avisados, plazos y estados de la red); la red de la tienda arreglada; la
   impresora de tickets compartida con reintento; el día pico con el simulador; los reportes de ventas (entrega 9), que
   el dueño usa hoy en eleventa; la depuración del catálogo y el conteo físico. Eleventa queda de consulta.
4. **El resto del rediseño:** la configuración (7), las pantallas de atrás (8) y lo que se señala (13).
5. **Lo que eleventa no tiene:** el módulo de impresión con las tres impresoras de inyección y la bandeja (entrega 4),
   que retira Control Print; servicios con costo real y pedidos (fase 4); la ganancia real del mes; WhatsApp
   (entrega 11); el ticket desde el celular (entrega 12, fase 8). El bot de Telegram de verdad, cuando se quiera: es
   chico.

### Para el piloto con dos cajas

Lo que falta para tener en la matriz lo que hoy hace eleventa, con la caja principal y la secundaria, más lo ya
planeado. Revisado el 2026-10-08 contra el plan, el código y los PR abiertos. **El orden ahora lo da
[la versión unicaja y el piloto en la caja 2](#la-versión-unicaja-y-el-piloto-en-la-caja-2) (2026-10-10):** esta tabla
queda como la lista completa.

**Las pruebas en la tienda solo se pueden sábado y domingo:** entre semana se programa, se simula y se deja todo listo
para esos días ([forma de trabajar](notas.md#forma-de-trabajar), [guía de los fines de semana](pruebas-en-tienda.md)).

| Qué | Dónde está | Estado |
|---|---|---|
| **La red de la tienda:** diagnóstico en la próxima caída real (desconectar el módem no la reproduce), con el script de diagnóstico en las dos cajas, y el arreglo, por aprobar, con lo que ya hay: el módem solo al internet del Tenda, red local nueva, IP fijas puestas en cada equipo, los equipos de la tienda en el switch que ya hay y no en el Tenda, e impresoras por IP sin WSD | [Red de la matriz](arquitectura.md#la-red-de-la-matriz), [notas](notas.md#red-de-la-tienda) | Falta: el script (chico) y dos fines de semana ([guía](pruebas-en-tienda.md)) |
| En el POS, por la red: que la caja 2 vuelva a buscar sola a la principal, avisos de lo configurado por nombre o con WSD, la franja «sin internet» o «sin red local» y la bitácora de red | [Red de la matriz](arquitectura.md#la-red-de-la-matriz) | Falta (chico) |
| **Las tres impresoras de inyección** (dos WF-6590 y una L5590): cada una con sus costos, su cola en cada PC, su contador, su calibración y sus hojas por cobrar | [Impresión](impresion.md), [notas](notas.md#impresoras) | Falta (mediano); antes, leer la L5590 por SNMP |
| **La impresora de tickets compartida:** reintento si las dos cajas imprimen a la vez y botón «Reintentar». No hay cajón | [Notas](notas.md#impresoras) | Falta (chico) |
| **Reportes de ventas:** día contra hace un año (el mismo día de la semana o la misma fecha: por decidir), mes y año a la fecha contra el año anterior, meses cerrados; venta neta, tickets, ticket promedio, costo, utilidad y margen; por departamento, caja, cajero y forma de pago; exportar a Excel y líneas en el resumen de Telegram; la utilidad solo la ve el dueño. El año anterior sale del historial de eleventa (su utilidad, con el costo capturado en eleventa) | [Notas](notas.md#reportes) | Pospuesto: después de la prueba con una persona real y su aprobación (2026-10-09) |
| **Ganancia real del mes:** el reporte (gastos fijos, consumibles del mes, recibo de luz, equipo con su depreciación y comisión de la tarjeta; utilidad bruta − mermas y faltantes − gastos = ganancia; punto de equilibrio y cierre del mes por Telegram). La captura de cada gasto cuando se paga no se pospone: propuesta, por aprobar, hacerla con la entrega 3 de la fase 3 (consumibles del mes), para todos los gastos | [Administración](#administración-y-punto-de-equilibrio), [servicios](servicios.md#tres-niveles-de-costo) | El reporte, pospuesto: después de la prueba con una persona real y su aprobación (2026-10-09) |
| Fusionar a `main`, en orden, los PR abiertos (#27 en adelante) | `main` llega hasta la entrega 2 de la fase 3 | Hecho el 2026-10-10: del #27 al #52, con el #45. En Windows pasan todas las pruebas |
| Probar en Windows con las dos cajas reales: servicio, alta de la caja 2, contingencia, respaldos, ventana de la caja y ticket desde las dos (el cajón, cuando lo haya) | Fase 1, entregas 1 y 3 | Falta: un sábado o domingo, sin tocar los datos de eleventa ([guía](pruebas-en-tienda.md#después-las-dos-cajas-con-el-pos)) |
| Crear el bot de Telegram de verdad y probarlo | Fase 1, entrega 4 | Falta |
| Probar la impresión con la cola de laboratorio y después con la real | Fase 1, entrega 5 | Falta |
| Prueba con una persona real | [Ver arriba](#prueba-con-una-persona-real) | Hecha en el PR #31, ya en `main`; falta instalarla en una PC con Windows limpia y dársela a quien prueba. Su hito ya pasó |
| Medir un día pico (~120 tickets) con las dos cajas, en la caja principal real | Hace falta el simulador de cajas (no existe; [descrito aquí](prueba-sucursales-linux.md#lo-que-hay-que-arreglar-o-construir)) | Falta |
| Precios y clientes | [Fase 3](fase-3-precios-clientes.md) | Las entregas 1 y 2 ya están en `main`. Propuesta, por aprobar: que la entrega 3 (consumibles del mes) capture todos los gastos del mes cuando se pagan; el reporte de la ganancia va con los reportes |
| Servicios con costo real (engargolado y enmicado con depreciación, luz y consumibles; impresiones por impresora), pedidos y WhatsApp desde la PC. Apartados y cotizaciones guardadas quedan como mejora: el eleventa de la tienda no los tiene | Fase 4 ([servicios](servicios.md), [pedidos](pedidos.md), [WhatsApp](whatsapp.md)) | Falta su plan |
| Instalador de la tienda, soporte a distancia (NetBird gratis u otro) y cómo se actualiza | [Por definir](#pendientes) | Falta |
| Respaldo cifrado fuera de la tienda y vigilancia de la caja principal: se reporta a Healthchecks.io cada pocos minutos en horario, y si no abre o se apaga, Healthchecks.io avisa por Telegram (el bot del POS no puede avisar de su propia caja) | Fase 5 ([notas](notas.md#respaldo-fuera-de-la-tienda-y-aviso-de-apertura)) | Falta (chico) |
| Depuración, conteo físico, importación final y arranque | Fase 5 y [depuración](#depuración-del-inventario) | Falta |
| Promociones y kits, si el piloto queda cerca del regreso a clases | [Fase 3](fase-3-precios-clientes.md) | Pospuestas por ahora |

**Respuestas del dueño (2026-10-08):** usa el reporte de ventas por periodo (día, mes y año a la fecha) y la comparación
contra hace un año, y se agregarán más; la meta es saber exactamente cuánto se gana. Las recargas y el pago de servicios
quedan pospuestos. No hay nada fiscal. Su eleventa no tiene apartados ni cotizaciones. Todas las impresoras van por la
red local.

**Respuestas del dueño (2026-10-09):** los reportes se retoman después de la prueba con una persona real y su
aprobación; no quiere lista de compras: el arreglo de la red se hace con lo que ya hay; la utilidad y la ganancia solo
las ve el dueño. Los gastos son renta, luz, insumos y sueldos, y todo lo que se gaste se captura en el momento. No hay
contador por ahora (está en los planes). El equipo no tiene fecha de compra. No hay cajón. Todo va por cable salvo la
caja 2, que va por Wi-Fi. El problema de la red es anterior al Tenda: el módem solo ya perdía la red local, y se cambió
el módem sin que se arreglara. En esas caídas esperaban a que volviera el internet y, para seguir trabajando, usaban los
datos del celular y conectaban la impresora directo a la PC con un cable de red. Se cae toda la red local, casi siempre
que se va el internet, y solo en una caída real: si se desconecta el módem o el cable de internet, no se reproduce.
Durante la falla no contesta el ping y los celulares no se conectan al Wi-Fi. Cuando pasa, la red se cae en cuanto se va
el internet y vuelve en cuanto regresa. El Tenda está bien configurado y conectado a su switch. En el último medio año
se fue el internet 3 veces, y suele tardar unas 12 horas en volver. No hay ningún no-break.

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
- [x] Entrega 2: compras con recepción ciega y el documento del proveedor
- [x] Entrega 3: ajustes, mermas y conteos
- [x] Entrega 4: etiquetas y sugerido de compra

Prueba con una persona real (al cerrar la entrega 2 de la fase 2, [ver arriba](#prueba-con-una-persona-real);
hecha en el PR #31):

- [x] Instalador de prueba de la caja principal (servicio, caja, base ficticia y usuarios), armado en GitHub Actions
- [x] La caja principal se da de alta sola en la prueba
- [x] Guía de prueba dentro de la caja, con las respuestas en un archivo
- [x] Simular lo que no hay: ticket en pantalla, avisos de Telegram en una lista, impresiones de ejemplo
- [x] Franja de prueba y botón para empezar de cero
- [ ] Instalarlo en una PC con Windows limpia antes de dárselo a quien prueba

Fase 3:

- [x] Revisar el [plan de la fase 3](fase-3-precios-clientes.md) (precios y clientes) y contestar sus preguntas
- [x] Mayoreo: el F11 de eleventa vende el paquete a su precio; pasa a escalas por cantidad que se aplican solas
  ([decisión](fase-3-precios-clientes.md#decisiones-de-la-revisión))
- [x] Entrega 1: mayoreo y redondeo al peso
- [x] Entrega 2: clientes registrados y crédito
- [ ] Entrega 3: consumibles del mes
- [ ] Promociones y kits: antes del próximo regreso a clases

Piloto con dos cajas ([ver arriba](#para-el-piloto-con-dos-cajas)):

- [ ] Primer fin de semana en la tienda: conocer la red, las impresoras, los grabadores y el equipo; solo mirar y
  anotar, salvo los grabadores y, si el dueño lo aprueba, el script y el registro Dhcp-Client
  ([guía](pruebas-en-tienda.md#primer-fin-de-semana-conocer-la-tienda))
- [ ] Script de diagnóstico de red, probado antes en una PC con Windows, corriendo en las dos cajas si el dueño lo
  aprueba, con su ícono para el personal en la caja 2 y la instrucción ([qué registra](notas.md#red-de-la-tienda))
- [ ] Segundo fin de semana, si el dueño lo aprueba: arreglar la red
  ([guía](pruebas-en-tienda.md#segundo-fin-de-semana-arreglar-la-red))
- [ ] En el POS, por la red: la caja 2 vuelve a buscar a la principal, avisos de nombres o WSD, la franja de «sin
  internet» y la bitácora de red
- [ ] Las tres impresoras de inyección en el módulo de impresión
- [ ] Impresora de tickets compartida: reintento y botón «Reintentar»
- [ ] Corregir Telegram para que cumpla «solo el dueño»: rechazar el vínculo desde un grupo y revisar quién aprieta el
  botón (hoy se autoriza por chat; [notas](notas.md#reportes))
- [ ] Después de la prueba con una persona real y su aprobación: reportes de ventas por periodo contra el año anterior
- [ ] Después de la prueba con una persona real y su aprobación: el reporte de la ganancia real del mes y el punto de
  equilibrio
- [ ] Capturar cada gasto cuando se paga (propuesta, por aprobar: con la entrega 3 de la fase 3)
- [ ] Probar en Windows con las dos cajas reales: servicio, alta de la caja 2, contingencia, respaldos, ventana de la
  caja y ticket desde las dos (el cajón, cuando lo haya)
- [ ] Medir un día pico (~120 tickets) con las dos cajas, en la caja principal real, con el simulador de cajas
- [ ] Antes del piloto: respaldo cifrado en Backblaze B2, con una restauración de ensayo, y vigilancia de la caja
  principal en Healthchecks.io ([notas](notas.md#respaldo-fuera-de-la-tienda-y-aviso-de-apertura))
- [ ] Soporte a distancia para instalar las actualizaciones (NetBird gratis u otro, [notas](notas.md#cuentas-y-pagos))
- [ ] Ya, sin costo: configurar los grabadores de las dos sucursales por seguridad
  ([lista](camaras.md#configurar-el-grabador))
- [x] Correr las pruebas en cada PR, en Windows y en Linux, y arreglar las 8 que fallaban en Linux
  ([notas](notas.md#código-y-pruebas))
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

- [x] Revisar la [propuesta de rediseño de la interfaz](propuesta-interfaz.md) con su prototipo: aprobada el 2026-10-10
- [x] Las preguntas de la venta para la entrega 0 (2026-10-10): rápidos Copia B/N ($2) y CURP ($10, 10 impresiones);
  sin billetes rápidos (se escribe lo que dio); sin sonidos por ahora
- [x] Para el piloto en la caja 2 (2026-10-10): las altas y los cambios de precio se siguen haciendo en eleventa, y el
  ticket de la caja 2 lleva el folio de eleventa ([piloto](#la-versión-unicaja-y-el-piloto-en-la-caja-2))
- [ ] Contestar las demás [preguntas de la propuesta](propuesta-interfaz.md#preguntas-para-el-dueño) antes de cada
  entrega: antes de la 1, las de los permisos (el corte del turno, el PIN tras unos minutos); antes de la 4, bandeja o
  tarjeta para las impresiones
- [ ] Probar el lector en la tienda: si lee QR, qué manda con los códigos de 11 cifras y su ritmo al teclear
- [ ] Instalador de la tienda para el administrador: servicio, cajas, firewall, carpeta de la caja con permisos y las
  colas de las impresoras de inyección ([detalle](#instalación-y-usuarios-de-windows)). Hoy son los scripts de
  `instalacion/`

- [ ] Si la sucursal 2 abre antes de la fase 7: guardar la lista de gastos de apertura con comprobantes
- [ ] Administración: insumos del mes (papel térmico, cinta, limpieza…) para la ganancia mensual
  ([detalle](#administración-y-punto-de-equilibrio)); el dueño lo pasó a la fase 3 al revisar la fase 2 (es la
  [entrega 3](fase-3-precios-clientes.md#3-consumibles-del-mes), «Consumibles del mes»). Propuesta, por aprobar: que
  esa entrega capture todos los gastos del mes cuando se pagan (renta, luz, insumos, sueldos y lo demás); el reporte de
  la ganancia, después de la prueba con una persona real
- [ ] Módulo de convivencia: el POS nuevo como una caja más de eleventa. Decidido el 2026-10-10: es el piloto en la
  caja 2 ([convivencia](migracion-eleventa.md#el-piloto-convivencia-con-eleventa))
- [ ] Exportar la base a una versión de eleventa (pedido por el dueño el 2026-10-11), como opción aparte: la base nueva
  no se adapta a eleventa
- [ ] Demás funciones a la medida (las describe el dueño)
- [x] Privilegios de los clientes registrados: descuento, mayoreo, crédito, pedido sin anticipo y apartar sin pagar;
  nada más ([decisión](fase-3-precios-clientes.md#decisiones-de-la-revisión))
- [ ] Regla para pedidos de no registrados: monto máximo que se prepara sin anticipo, y días para borrar el celular
- [x] Reportes que usa el dueño: ventas por día, mes y año a la fecha, y contra el año anterior; se agregarán más
  ([para el piloto](#para-el-piloto-con-dos-cajas))
- [x] Recargas y pago de servicios: pospuestos ([idea](ideas.md#recargas-y-pago-de-servicios))
- [x] Apartados y cotizaciones: el eleventa de la tienda no los tiene
- [x] Impresoras de la matriz, todas por red: una de tickets para las dos cajas, dos WF-6590 y una L5590
- [x] La utilidad y la ganancia solo las ve el dueño (2026-10-09)
- [ ] Con los reportes, después de la prueba con una persona real: ¿contra el mismo día de la semana del año pasado o
  contra la misma fecha?
- [x] Gastos del mes (2026-10-09): renta, luz, insumos y sueldos; todo lo que se gaste se captura en el momento para
  saber al cierre cuánto se ganó ([administración](#administración-y-punto-de-equilibrio))
- [x] «La primera prueba» después de la que se retoman los reportes es la prueba con una persona real (2026-10-09)
- [ ] Con los reportes, después de la prueba con una persona real: ¿el sueldo del dueño es gasto? ¿Qué terminal de
  tarjeta y qué comisión? ¿Un faltante de corte lo absorbe el negocio o el cajero?
- [ ] Equipo y luz: no hay fecha de compra (2026-10-09). Propuesta, por aprobar: depreciarlo desde el arranque. Falta la
  lista del equipo con lo que vale hoy y los años de uso que le quedan (las tres impresoras de inyección, la de tickets,
  engargoladora, enmicadora, guillotina, PC); el recibo de CFE y la marca y modelo de cada equipo se anotan el primer
  fin de semana
- [x] Impresoras y cajón (2026-10-09): lo de las impresoras ya estaba dicho ([notas](notas.md#impresoras)); no hay
  cajón (ya se sabía). Qué cola tiene cada PC, para qué se usa cada impresora y cuánto se cobra en cada una se anotan
  el primer fin de semana, con el personal y en Control Print
- [x] Red (2026-10-09): todo por cable salvo la caja 2 (Wi-Fi), y ya hay un switch; el internet se fue 3 veces en el
  último medio año y suele tardar unas 12 horas en volver; no hay ningún no-break; el problema ya pasaba con el módem
  solo y con otro módem, y solo pasa en una caída real (desconectar el módem no lo reproduce). Los modelos del módem y
  del Tenda se anotan el primer fin de semana
- [x] Red, para el diagnóstico (2026-10-09): se cae toda la red local, no solo la caja 2; pasa casi siempre que se va
  el internet; antes del Tenda esperaban a que volviera, usaban los datos del celular y conectaban la impresora directo
  a la PC con un cable de red
- [x] Sin lista de compras (2026-10-09): el arreglo de la red se hace con lo que ya hay
- [x] Red (2026-10-09): cuando pasa, la red local se cae en cuanto se va el internet y vuelve en cuanto regresa
  ([notas](notas.md#red-de-la-tienda))
- [ ] ¿Se deja el script de diagnóstico corriendo en las dos cajas desde el primer fin de semana, y se activa el
  registro Dhcp-Client de Windows? El script solo lee y arranca desde un acceso directo, sin cambiar la configuración
  de Windows; el registro guarda lo que pasa con las direcciones en la próxima caída
- [ ] Durante el piloto, las recargas y los pagos de servicios: ¿se registran a mano en el POS como cobro a terceros
  para que el corte cuadre, si su efectivo entra al mismo cajón?
- [ ] Mientras la sucursal 2 use eleventa: ¿le manda mercancía la matriz? Si sí, se registra en el POS como salida con
  motivo «A sucursal 2», al costo promedio, hasta que existan los traspasos
- [ ] Autorizar por Telegram: hoy basta con tener el chat vinculado. ¿Se pide además un PIN antes del piloto (el bot lo
  pide, lo borra y la caja principal lo verifica; no necesita el central) o un monto máximo para aprobar así?
- [x] Fiscal: hoy no se factura nada; de eleventa solo se usa el punto de venta (ni pulso ni facturación)
- [x] Contador (2026-10-09): no hay por ahora (está en los planes); mientras, el IVA de la luz y de las compras es costo
  ([idea](ideas.md#otras-ideas-que-han-salido))
- [ ] WhatsApp: ver [pendientes](whatsapp.md#pendientes)
- [ ] Dispositivos propios: ver [ideas](ideas.md)
