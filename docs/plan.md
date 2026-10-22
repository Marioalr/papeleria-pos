# Papelería POS — Plan

> **Lluvia de ideas.** Las decisiones de abajo son el punto de partida y pueden cambiar mientras se cierra el plan.

> **Foco actual (2026-10-08):** completar el POS para tener en la matriz lo que hoy se tiene con eleventa, con una caja
> principal y una secundaria, más las funciones ya planeadas: servicios y pedidos, WhatsApp y el módulo de impresión
> que reemplaza a Control Print, y **saber exactamente cuánto se gana** ([qué falta](#para-el-piloto-con-dos-cajas)).
> **Primero (2026-10-10): la versión unicaja de la caja nueva,** para desarrollo, y después **el piloto en la caja 2**,
> que vende con la caja nueva y manda cada venta a eleventa ([camino](#la-versión-unicaja-y-el-piloto-en-la-caja-2)).
> **La meta (2026-10-15): alcanzar a eleventa.** Primero lo que eleventa ya hace y lo que pide dejarlo; lo que no tiene,
> después ([paridad con eleventa](paridad-eleventa.md)).
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
| Ticket | Sin QR por ahora, como el de eleventa (2026-10-16): «primero igualar a eleventa». Para devolver se escribe el folio impreso: el de la caja (A-000123) o, en el piloto, el de eleventa |
| Contingencia | Cada caja guarda copia del catálogo y una bandeja de salida: si la caja principal se apaga, las demás siguen vendiendo |
| Escaneo | Siempre contra la copia local del catálogo, también en línea (2026-10-10): la red no entra en el escaneo. Los productos nuevos se dan de alta pocos días a la semana, así que la copia casi siempre está al día; el cobro lleva su versión para que los precios coincidan ([propuesta](propuesta-interfaz.md#rendimiento)) |
| Roles | Sin roles (2026-10-10): cada usuario tiene sus **permisos**, cada uno en «puede», «con autorización» o «no». Al instalar se ponen los de defecto y la primera cuenta tiene todos ([propuesta](propuesta-interfaz.md#usuarios-y-permisos)). Casi no se pedirá autorización: o puede o no puede, y quien no puede le pide a quien sí que entre (Ctrl+U). Por ahora, en Administración solo hay «puede» o «no»; más adelante se decide qué acciones sí pueden pedir autorización (2026-10-15) |
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
| La pantalla de venta de antes (como eleventa) junto a la del rediseño, a elegir por caja | Solo la del rediseño | Decisión del dueño (2026-10-14): «no volveremos a esa versión» |

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
| 5. Piloto en la matriz | Primero el piloto en la caja 2: vende con la caja nueva y manda cada venta a eleventa, que sigue siendo el oficial ([piloto](#la-versión-unicaja-y-el-piloto-en-la-caja-2)). Después, dejar eleventa con las dos cajas: depuración del inventario, conteo físico inicial, arranque, eleventa como respaldo. Antes: la red de la tienda arreglada; los reportes, después de la prueba con una persona real y su aprobación. Las tres impresoras de inyección van con la entrega 4, después de alcanzar a eleventa (2026-10-15); mientras, Control Print sigue aparte. Desde el arranque, respaldo cifrado fuera de la tienda (Backblaze B2) y vigilancia de la caja principal (Healthchecks.io) ([qué falta](#para-el-piloto-con-dos-cajas)) |
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

**Avance (2026-10-12):** la primera prueba fue el 2026-10-10 con la pantalla de hoy. Según el dueño fue precaria: la
pantalla era muy mala, y por eso se rediseñó la interfaz; la propuesta lo convenció. Antes del siguiente fin de semana
(17 y 18 de octubre) la pantalla nueva tiene que estar lista para la siguiente prueba
([propuesta](propuesta-interfaz.md)).

La preparación (instalador, guía, simulaciones) se hace al cerrar esa entrega. Desde ahí, cada entrega agrega sus
tareas a la guía y la prueba se puede repetir con la versión nueva (con los conteos de la entrega 3, por ejemplo).

- **Solo la caja principal:** el servicio y la caja en la misma PC; no hace falta una segunda caja ni la red. La caja
  se da de alta sola, sin pedir la dirección ni el PIN del dueño.
- **Un instalador:** un solo archivo (`PapeleriaPos-Prueba.exe`) que deja todo listo: el servicio, la caja, una base
  ficticia con los usuarios de prueba y un acceso en el escritorio; se desinstala sin dejar nada. Se arma en GitHub
  Actions (Windows), porque la PC de desarrollo es Linux. Como en la tienda, lo instala el administrador para todos los
  usuarios de la PC y quien prueba lo usa con una cuenta común de Windows
  ([instalación](#instalación-y-usuarios-de-windows)).
- **Autoconclusiva:** trae su guía dentro de la caja: tareas cortas (entrar con la ficha, vender, cobro mixto, cancelar,
  devolver, cambiar de usuario, el corte del turno y el del día, F3 Productos, alta rápida, recibir una compra…) con lo
  que debe pasar en cada una. Quien prueba marca si salió
  bien y escribe lo que vio; al final queda un archivo con sus respuestas para mandarlo.
- **Una guía por versión (2026-10-14):** la segunda prueba (17 y 18 de octubre) es con la pantalla nueva y su guía
  (la versión 3) empieza por entrar con la ficha y vender; siguen cambiar de usuario, el corte del turno y el del día
  (entrega 6); después, con otro turno del cajero, las páginas de Productos, Inventario y Clientes (entrega 8), y al
  final la Configuración (entrega 7). Las respuestas se
  guardan con la versión de la guía y las de otra no se cargan (su archivo se queda). Al terminar una persona, «Guardar respuestas y
  dejar la guía en blanco» deja su archivo en el escritorio y la guía en blanco para la siguiente.
- **Simula lo que no hay:** sin impresora de tickets, el ticket se ve en pantalla; sin Telegram, los avisos que le
  llegarían al dueño se ven en una lista; sin la WF-6590, hay impresiones de ejemplo para cobrar.
- **Se nota que es prueba:** una franja "Prueba: datos ficticios" en la caja y un botón para empezar de cero con la
  base ficticia.
- **Aparte de eleventa:** su propio puerto y su propia carpeta; no lee ni toca la base de eleventa.

### Instalación y usuarios de Windows

El sistema lo instala el administrador de la PC; después, la gran mayoría de las veces se usa desde una cuenta común
de Windows, sin privilegios de administrador. Nada de lo diario puede pedir administrador.

Aquí "usuario" es la **cuenta de Windows** de la PC, no el usuario del POS: el cajero, el encargado y el dueño entran a
la caja con su ficha y su PIN del POS, todos desde la misma cuenta común de Windows.

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

**La meta (dueño, 2026-10-15): alcanzar a eleventa.** Primero lo que eleventa ya hace (vender, cobrar, los cortes, el
inventario y los reportes que usa el dueño) y lo que pide dejarlo (el piloto, el puente y el rendimiento). Lo que
eleventa no tiene va después: las etiquetas de cajones y ganchos, WhatsApp, el ticket desde el celular, los exhibidos y
la temporada, y el módulo de impresión. Qué hace eleventa que el POS todavía no, y en qué orden cerrarlo:
[paridad con eleventa](paridad-eleventa.md) (2026-10-16).

#### Primero: la versión unicaja (entrega 0)

Una primera versión que ya venda, hecha sobre lo que ya funciona: el mismo servicio, el mismo cobro, el mismo ticket y
la misma base. Cambia la pantalla de venta; lo que todavía no se rediseña abre lo de hoy, con sus mismas teclas. Desde
la segunda prueba con una persona real (17 y 18 de octubre de 2026) todas las cajas entran a la pantalla nueva. La de
antes ya no existe: el dueño la quitó el 2026-10-14 («no volveremos a esa versión»); el menú del usuario tiene «Cambiar
de usuario» y «Salir», y una PC que había vuelto a ella la olvida al entrar a la venta. Se instala con
el instalador de prueba que ya existe, y cada entrega que sigue se ve en esa misma versión. Al actualizar, la base de
trabajo de la prueba se queda: en la PC de la primera prueba, «Empezar de cero» (en la franja) antes de la segunda, o
los rápidos seguirán con los precios que inventó la papelería ficticia ($2.30 y $27.00), no con los de abajo.

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
- la pantalla nueva junto a la de hoy («Probar la pantalla nueva»; la de hoy se quitó el 2026-10-14);
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
- también el que se atiende, como estaba en ese momento, y otra vez al cambiar de usuario y al cerrar el turno con el
  corte (2026-10-14);
- son de la caja, no del turno, como en el prototipo: el corte no los borra y vuelven en el siguiente turno, sea de quien
  sea (el corte lo dice);
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

**Avance (2026-10-11), segunda parte: los paneles.** En la pantalla nueva, lo de la venta ya no tapa el ticket: va en el
panel de la derecha y vuelve con Esc.
- **Qué va en el panel:**
  - cliente (Ctrl+L) y abono (Ctrl+B);
  - descuento (Ctrl+D) y precio (Ctrl+E);
  - artículo común (Ins), entrada y salida (F7 y F8);
  - mayoreo (F11) y verificador (F9);
  - impresiones por cobrar (F2), ventas del día, devolución y cotizar;
  - el cobro con tarjeta, transferencia o crédito, el alta rápida, la reimpresión y el ticket en pantalla.
- **Lo que pide autorización la pide ahí mismo,** en el mismo panel.
- **Las listas anchas van como tarjetas,** para que quepan: clientes, impresiones, ventas del día y lo que se devuelve.
- **Los módulos de arriba** (productos, inventario, clientes, corte y configuración) y el conteo siguen encima.
- **F10** solo pone el cursor en el cuadro, donde salen los resultados.
- **«Tarjeta» y «Transf.»** abren el cobro con esa forma y el total en ella.
- **Al cobrar, el cambio en grande** se queda en el panel hasta que se escanea al siguiente cliente. Si el ticket no
  salió, ahí mismo están «Reintentar», «No imprimirlo» y «Verlo en pantalla».
- **F12 mueve el cursor a «Pagó con» en el navegador mismo,** sin esperar al servicio: lo que se teclea enseguida ya no
  cae en el cuadro de captura.
- **Con un panel abierto, las teclas siguen** (2026-10-14, antes solo Esc): la del mismo panel lo cierra (F9 o Esc
  regresan a vender); los botones de la barra, igual. Supr, las flechas, + y − son del panel.
  - Los que se dejan sin perder nada (verificador, mayoreo, cliente, ventas del día, el ticket en pantalla, cotizar y
    transferencias) se dejan con cualquier tecla: la de otro panel cambia a ese, F12 va al cobro y las demás hacen lo de
    siempre.
  - Los que tienen campos (artículo común, descuento, precio, entrada y salida, abono, devolución, alta rápida,
    impresiones por cobrar) esperan su Enter o Esc, y con otra tecla lo dicen en el panel: lo escrito no se pierde ni
    se cobra sin ello. Igual la autorización de un cobro (fiar arriba del límite, debajo del costo).
  - Lo que va encima, solo con Esc.

**Avance (2026-10-11), tercera parte: el cobro mixto y «Más».** En la pantalla nueva todo el cobro va en el panel del
total, también con cliente registrado; la ventana de cobro quedaba solo en la pantalla de hoy, y se quitó con ella
(2026-10-14).
- **«Tarjeta», «Transf.» y «Crédito»** (o «A favor», con saldo a favor) agregan cada uno su renglón con lo que falta:
  - el monto se cambia si es solo una parte, y «Pagó con» cubre el resto en efectivo, al peso;
  - tarjeta y transferencia llevan su referencia, y la transferencia queda por verificar;
  - el crédito dice lo que debe y lo que le queda. Si pasa el límite, el panel lo avisa con quién lo autoriza, y al
    cobrar se autoriza ahí mismo;
  - si las otras formas cubren todo, F12 lleva el cursor a «Cobrar» y otro F12 (o Enter) cobra.
- **Si al cobrar algo queda debajo del costo,** después de autorizarlo se sigue con el cobro, sin volver a pedirlo.
- **Los tickets en espera:** 3 fichas a la vista y los demás en «Más (N)», con la hora, los artículos y el total. Con
  más de 5, el menú lleva flechas que lo deslizan al pasar el mouse; se cierra con Esc, con un clic fuera o al elegir uno.

**Avance (2026-10-11), cuarta parte: quién autoriza, por su nombre.** En la pantalla nueva la autorización ya no pide
escribir el usuario de quien autoriza:
- **arriba dice quién lo autoriza** («Lo autoriza Encargado de prueba o Dueño de prueba») y, debajo, qué se autoriza;
- **si son varios, una ficha por cada uno** (primero el encargado, al final el dueño). Se toca la de quien está ahí y
  el cursor pasa a «PIN de …»; si es uno solo, el cursor ya está en su PIN;
- **vale igual en el panel y en lo que se abre encima** (inventario, clientes…), con Telegram como antes;
- **los permisos de la sesión traen el usuario** de cada quien autoriza, no solo su nombre
  (`PermisoDeSesion.Autorizan`, también sin conexión). El servicio sigue revisando el PIN y el monto.
- La pantalla de hoy, donde se escribía el usuario, se quitó el 2026-10-14. Escribir el usuario de quien autoriza solo
  queda cuando no se tienen los permisos de quien entró: sin conexión y sin la copia, o en «Productos e inventario, sin
  cortar», que no los pide.

**Avance (2026-10-11), quinta parte: las familias en la búsqueda.** Lo que solo cambia en una cosa va en un renglón, con
el nombre sin eso y los valores como fichas. Un análisis de los nombres del catálogo, con la copia local, mostró qué
cambia más entre productos casi iguales: la medida, el color y la marca, y menos el contenido y, en cuadernos, el rayado
(los números se comentaron en el chat).
- **Qué se despliega:** lo primero que cambia entre los resultados, en este orden: color, tamaño, marca, contenido (en
  cuadernos, el rayado primero). Con «lapicero negro» el color ya no cambia y se despliega el tamaño. **Tab** (o un clic
  arriba de la lista) despliega por otra cosa, y la caja lo recuerda para ese texto.
- **El renglón:** el nombre de eleventa sin lo que cambia, las fichas (el color con un punto de referencia, no el tono
  exacto) y el precio, «desde» si cambia según el valor (el costo de compra es distinto, decisión del dueño). Un clic en
  una ficha la agrega.
- **Enter agrega siempre el más vendido de la familia** (decisión del dueño, 2026-10-11), que va marcado. **→** (con el
  cursor al final del texto) o la flecha del renglón la despliegan: cada valor con su código, precio y existencia, y el
  cursor en el más vendido; **↑ ↓** y Enter eligen otro; **←** o **Esc** la cierran. El Enter del lector nunca se
  aplica a la lista.
- **Cómo se lee el nombre** (`LectorDeNombres`, en el núcleo; igual en línea y sin conexión):
  - la primera palabra es el producto; los colores, con su tono («Azul Rey»), sin género ni plural;
  - el surtido («V/Colores», «Colores Surt.») es un color más, al final, con un punto de varios colores;
  - las medidas se comparan igual aunque se escriban distinto («1.0 Mm», «1mm», «1.0»), y lo que acompaña a los
    milímetros («P/Fino», «P/Mediano») va con la medida;
  - la marca es lo último del nombre que no es color, medida ni contenido; se aprende de lo que se repite al final de
    nombres de varios productos, y valen las dadas de alta;
  - los juegos y paquetes no se juntan con la pieza; el tipo (gel, push, borrable) va en otro renglón, porque casi
    siempre cambia el precio.
- **Vocabulario confirmado con el dueño:** en los nombres de eleventa, «P/» es «para»; Kilométrico, Magistral, Tatoo,
  Colorella y Diurex se tratan como marca. Los surtidos siguen como un solo producto.
- **La búsqueda** manda con cada resultado cuántas veces se vendió en 90 días y el nombre en pedazos; la pantalla nueva
  pide hasta 200 productos para que las familias salgan completas y muestra 30 renglones.

**Avance (2026-10-11), sexta parte: afinar y el error de dedo.**
- **Afinar con fichas:** con un texto general, arriba de la lista salen las palabras que separan los resultados, de
  la que más separa a la que menos («folder» → carta, oficio, la marca). Un clic la agrega a lo escrito; también se
  puede escribir su principio. Lo que ya sale como ficha en los renglones no se repite.
- **El error de dedo** (`ErrorDeDedo`, igual en línea y sin conexión): si nada coincide exacto, cada pedazo de 4 letras o
  más puede diferir en una letra (cambiada, de más, de menos o dos volteadas). La lista lo dice: «Parecidos a «sobre
  manial»: nada coincide exacto». Primero los que coinciden exacto en más palabras.
- **Se busca también por lo que lee la caja del nombre:** «negro» encuentra «Negra», «plata» el «Plateado», «surtido»
  los «V/Colores» y «1mm» el «1.0 Mm». Así una ficha de medida afina aunque la medida esté escrita distinto.
- **Esc y escribir enseguida** ya no pierde las primeras letras: el cuadro se vaciaba otra vez desde el servicio.

**Avance (2026-10-11), séptima parte: los sinónimos de la tienda.**
- **Configuración › Sinónimos** (desde la entrega 7, Configuración → Venta y cobro → «Sinónimos de la búsqueda»): quien edita el catálogo (el dueño y el encargado) los da de alta uno por uno: «al
  escribir *boligrafo*, también busca *pluma*», con «y al revés» marcado de entrada; una sola palabra de cada lado, sin
  acentos. Quitarlos los desactiva. Queda en la bitácora. Ninguno viene puesto, y lo que se buscó y no se encontró no los
  arma (decisión del dueño).
- **Viajan con el catálogo:** suben la versión, así que el servicio y la copia sin conexión de cada caja los tienen al
  momento (`Sinonimo`, `SinonimosDeBusqueda`, `PaqueteContingenciaDto.Sinonimos`).
- **En la búsqueda:** cada palabra escrita vale por sí misma o por sus sinónimos; desde 4 letras también vale el
  principio («bolig»). Lo que salió por un sinónimo va después de lo que se llama así y lo dice: «por «boligrafo»».

Falta, en este orden:
1. ~~cuántas fichas de espera se ven, en Configuración~~ (hecho con la entrega 7, 2026-10-15);
2. que un surtido se pueda vender por color, como opción del producto (decisión del dueño, 2026-10-11);
3. el nombre de eleventa como sinónimo de cada producto, cuando la depuración cambie nombres.

**Avance (2026-10-14): la pantalla de antes se quitó.** El dueño: «quita la opción de ver la vista actual, no volveremos
a esa versión». Todas las cajas entran a la venta del rediseño, sin «Volver a la pantalla de hoy»; con ella se fueron
la ventana de cobro y la barra de eleventa. El menú del usuario tiene «Cambiar de usuario» (Ctrl+U) y «Salir», que llevan
a la entrada. Ctrl+U cambia de usuario también con una ventana o un panel abiertos, y F1 regresa a vender desde lo que va
encima del ticket. «Productos e inventario, sin cortar» lleva arriba la barra de la venta (Productos F3, Inventario F4,
Clientes y el menú del usuario). Un código que no existe ofrece, según los permisos, «Darlo de alta aquí» y «Venderlo
como artículo común».

**Avance (2026-10-14), entrega 6: la entrada y el corte,** como en el [prototipo](propuesta-interfaz/prototipo.html):
- **«¿Quién atiende?»:** una ficha por cada usuario activo de la sucursal (y el dueño), con su letra y lo que puede
  hacer. La letra es la inicial; si ya es de otro, la de su apellido o la siguiente de su nombre; se reparten por fecha
  de alta. El PIN es de 4 cifras, con el teclado de la pantalla o el de la PC, y entra solo con la cuarta; si no es, lo
  dice con los intentos que quedan. Esc regresa a las fichas. Sin conexión, las fichas y el PIN salen de la copia de la
  caja. A la izquierda: la caja, la hora, la conexión, la impresora y el turno abierto.
- **Abrir el turno y el turno de otro cajero** van con el mismo acomodo. Quien tiene el corte lo hace ahí mismo, sin
  volver a escribir su PIN; quien no, ve quién lo hace. Ctrl+U y «Volver» llevan a las fichas.
- **El corte del turno** lo hace quien entró y tiene el permiso (hoy el encargado y el dueño): escribe el total del
  efectivo, a ciegas, sin billete por billete ni otra vez el PIN; al cerrar ve lo esperado, lo contado y la diferencia a
  nombre del cajero del turno, y sigue a abrir su turno. Quien no tiene el permiso ve el corte X (sin el efectivo), quién
  lo hace y «Que entre a esta caja». Un escaneo en «Efectivo contado» no se toma como dinero.
- **El corte del día,** para quien tiene «Ver y cerrar el corte del día» (hoy el dueño): todas las cajas y turnos del
  día, lo vendido, el efectivo esperado y contado, las formas de pago, las salidas por motivo, las devoluciones, las
  cancelaciones, lo fiado, los abonos y las impresiones cobradas. Un turno que viene de otro día cuenta solo lo de hoy (lo
  esperado es de todo el turno). «Cerrar el día» espera a que no quede turno abierto ni nada por subir de otra caja; si
  una caja deja de avisar, es solo un aviso. El resumen por Telegram sigue saliendo con cada corte Z que deja la
  sucursal sin turno abierto; «Cerrar el día» no lo manda otra vez si ya salió con alguno de ese día. En el piloto con
  eleventa se ve, pero el día se cierra con el corte de eleventa.
- **API:** `GET api/cajas/{caja}/usuarios` (las fichas, sin usuario, PIN ni hash), `POST api/cajas/{caja}/sesion`,
  `GET api/turnos/{turno}/corte-informativo`, `POST api/turnos/{turno}/corte-z` con el total,
  `GET api/cajas/{caja}/corte-del-dia` y `POST api/cajas/{caja}/corte-del-dia/cierre`.
- **Por ahora, la propuesta** en lo que el dueño no ha contestado: el corte del turno, solo con el permiso; el corte del
  día, solo el dueño (el permiso se le puede dar al encargado en las reglas).
- **Los tickets en espera son de la caja,** como en el prototipo: cambiar de usuario guarda también el que se atendía, y
  el corte no los borra; vuelven en el siguiente turno. Antes el corte decía que se perdían, y no era cierto.
- **«Productos e inventario, sin cortar»** imprime el estado de cuenta de un cliente igual que la venta (en la impresora
  de tickets o en pantalla); antes ahí «Imprimir» no hacía nada.

Falta: ~~la hora para recordar el cierre del día~~ (hecho el 2026-10-17, con la hora de cierre de la tienda); el largo
del PIN y que dar de baja a alguien no mueva las letras de los demás. El fondo sugerido y el nombre de la tienda ya están
(entrega 7, 2026-10-15). El conteo desde el celular sigue con el usuario escrito.

**Avance (2026-10-17): el corte del día como el de eleventa** («Corte del día y de todas las cajas» en la
[paridad](paridad-eleventa.md#efectivo-turnos-y-cortes)), para quien tiene «Ver y cerrar el corte del día»:
- **El de otro día:** ← y → mueven de día, o se elige en la fecha; abre en el de hoy. El de un día que ya pasó solo se ve
  (con lo que pasó ese día y si se cerró) y se imprime; «Cerrar el día» es el de hoy, con sus reglas. El corte de un turno
  va en el día en que se hizo: el día en que se abrió dice «Corte el …», sin su diferencia, que así no se cuenta dos veces.
  Un día del piloto se ve como tal aunque después se deje el piloto: el turno que cerró el corte de eleventa sigue en el
  día que le toca, «En eleventa». Si pasa la medianoche con el de hoy a la vista, la caja lo nota (lo revisa cada minuto)
  y lo deja solo para verlo; y «Cerrar el día» manda el día que se ve: si ya no es hoy, el servicio no cierra nada, ni ese
  día ni el nuevo, y la pantalla dice por qué (revisión del 2026-10-17).
- **Las ventas por departamento,** como en eleventa, las ve quien ve el corte del día: cada departamento con lo que vendió
  ese día, las impresiones y el artículo común aparte; lo devuelto ese día en la tienda se resta de su departamento. En el
  piloto también lo que se devolvió en eleventa de un ticket de la caja 2, aunque el turno de esa venta ya se haya cortado
  (se resta el día en que se devolvió; el efectivo de los turnos no cambia: no salió de esta caja).
- **La utilidad,** solo para quien tiene «Ver costos, utilidad y margen» (a los demás el servicio no les manda ningún
  costo): la utilidad bruta arriba, y en las ventas por departamento la utilidad y el margen de cada uno. Es lo vendido
  menos el costo que guardó cada venta al cobrar (el promedio del inventario; en las impresiones, el de cada trabajo).
  Como en el resumen del día, el artículo común no entra en la utilidad, y los servicios cuentan sin costo hasta que
  tengan receta.
- **«Imprimir»** manda a la impresora de tickets, a 48 columnas, lo mismo que se ve, también el de otro día: el día y si se
  cerró, lo vendido y sus formas de pago, cada turno con lo que se contó, los movimientos, las ventas por departamento y,
  solo con el permiso, la utilidad (también la de cada departamento). Va por donde se imprime el corte del turno (la cola
  de la impresora lo tomará después).
- **El recordatorio:** desde la hora de cierre de Configuración › Tienda, si el día de hoy no se ha cerrado, quien lo cierra
  ve en la venta «Falta cerrar el día», con los demás avisos del panel y con «Ir al corte»: sin ventana, sin sonido y sin un
  paso más en el cobro. Se va al cerrar el día; si después se abre otro turno ese día, vuelve. Sin conexión con la caja
  principal o en el piloto (el día se cierra con el corte de eleventa), no sale.
- **API:** `GET api/cajas/{caja}/corte-del-dia?fecha=AAAA-MM-DD` (sin fecha, hoy; uno que no ha llegado se rechaza),
  `GET api/cajas/{caja}/corte-del-dia/recordatorio` (204 si no toca) y, al cerrar, `fecha` en `SolicitudCierreDia` (el
  día que se ve; una caja de antes no la manda y es hoy). Sin cambios en la base.
- **Decidido mientras el dueño no estaba, por revisar:**
  - las ventas por departamento, para todos los que ven el corte del día, como en eleventa; su utilidad y su margen, solo
    con «Ver costos, utilidad y margen»;
  - lo devuelto ese día se resta de la venta y la utilidad de su departamento, como en eleventa (una cancelación no: el
    ticket cancelado ya no está en lo vendido); «Vendido hoy» sigue sin restarlo, como estaba, y la utilidad del resumen de
    Telegram tampoco lo resta (un día con devoluciones, las dos no coinciden);
  - la hora del recordatorio es la de cierre de Configuración › Tienda, que siempre tiene una (21:00 si no se cambia, la
    misma del aviso de «fuera de horario»); para no tener recordatorio habría que poder dejarla vacía, y no se hizo;
  - en el piloto no hay recordatorio, porque el día se cierra con el corte de eleventa.

**Avance (2026-10-15), entrega 1: los permisos por usuario,** como en la [propuesta](propuesta-interfaz.md#usuarios-y-permisos):
- **Sin roles.** Cada usuario tiene sus permisos, uno por acción: Puede (con su límite, si lo tiene), Con autorización
  (con candado y quién autoriza) o No (no aparece). Los usuarios nuevos empiezan con los «Permisos por defecto»;
  cambiarlos no toca a los que ya existen. La primera cuenta (el dueño más antiguo) tiene todos, y siempre debe quedar
  alguien con «Usuarios y permisos».
- **Al actualizar, cada quien queda con lo que podía hacer:** el dueño, todo; el encargado y el cajero, las reglas de su
  rol (las de su sucursal, si no las generales y si no las iniciales), con sus límites. Una prueba compara las decisiones
  de antes y de ahora acción por acción, y salen iguales. Las reglas y el rol de antes se quedan en la base, sin decidir.
- **Permisos nuevos:** «Ver costos, utilidad y margen» (el costo de las impresiones, el margen, los costos en conteos y
  compras), «Ver reportes de ventas» (para la entrega 9) y «Configuración de la tienda» (el límite de efectivo; con la
  entrega 7, lo demás de la tienda).
- **«Usuarios y permisos»** reemplaza a «Reglas» en Configuración, como en el prototipo: la lista con los de defecto y
  cada usuario con su resumen, nuevo usuario, y en cada uno la tabla por grupos con Puede / Con autorización / No y su
  límite, «Copiar los permisos de…», «Todos», «Los de defecto», cambiar el PIN y darlo de baja. Se guarda solo.
- **Las pantallas deciden con los permisos de quien entró** (ya no con «dueño o encargado»), también sin conexión.

**Avance (2026-10-15), entrega 7: la Configuración nueva,** como en el [prototipo](propuesta-interfaz/prototipo.html):
el buscador arriba, los grupos a la izquierda (cada uno se ve solo con su permiso en «Puede», sin candados), cada ajuste
con su alcance («Toda la tienda») y guardado solo, con «Guardado a las HH:MM», sin botón ni PIN: el servicio revisa el
permiso de quien entró. También se abre sin cortar, desde «Productos e inventario».
- **Tienda:** nombre (sale en la entrada), dirección, teléfono y horario.
- **Venta y cobro:** cuántos tickets en espera se ven (3), los rápidos (hasta 6, con nombre, cantidad y orden), los
  sinónimos de la búsqueda y los días para devolver (7, informativo).
- **Efectivo y corte:** el límite de efectivo, el fondo sugerido (lo propone «Abrir turno») y el aviso del corte que no cuadra.
- **Ticket:** encabezado y pie a 48 columnas con «Así sale», el logotipo y el ticket de prueba.
- **Impresoras e impresiones:** la impresora de inyección (IP, aviso de tinta, lecturas), la calibración y el desglose.
- **Inventario:** el margen mínimo (salió de F4) y lo que cubre el sugerido.
- **Usuarios y permisos, Cajas, Bitácora, Teclas** (la lista) **y Acerca de.** Avisos por Telegram, como estaba, «en pausa».
- No se pusieron los grupos sin nada real detrás (WhatsApp, Seguridad, Respaldos, Esta caja) ni los ajustes que todavía
  no existen; se agregan cuando exista lo que configuran.

**Avance (2026-10-15), entrega 8: las pantallas de atrás con lista y detalle,** como en el
[prototipo](propuesta-interfaz/prototipo.html): Productos (F3), Inventario (F4) y Clientes ya no son ventanas encima de
la venta, son páginas.
- **Páginas, no ventanas:** bajo la barra de arriba (que se queda, con sus chips y el usuario) se ve la venta o una
  página, con su pestaña marcada. La venta no se deja: se oculta con su ticket, los tickets en espera, lo escrito en el
  cobro y el panel que tuviera, y «Venta» (F1) la regresa tal cual. Abajo, «F1 Volver a la venta» y lo que hacen las
  teclas ahí. Esc cierra lo que esté abierto en la página (la ficha, una tarea, el estado de cuenta) y, sin nada abierto,
  regresa a la venta, como en el prototipo. Ctrl+U cambia de usuario desde donde sea. El corte y la Configuración siguen
  encima, como ventanas. Sin cortar van las mismas páginas, con su barra.
- **El lector y el teclado:** en Productos y Clientes, lo que se escribe o se escanea con el cursor fuera de un campo va
  a su búsqueda (en la venta, al cuadro de captura, como siempre); ↑ ↓ eligen de la lista, Enter abre, y al entrar el
  cursor está en la búsqueda. En las tareas del inventario, el escaneo va a su cuadro del código.
- **Productos abre llena** (antes, vacía hasta buscar): los 50 más vendidos en 90 días, lo mismo con que ordena la
  búsqueda de la venta, con «Ver 50 más» y cuántos hay. Filtros: «Por completar», «Poca existencia» (3 o menos: el
  catálogo no tiene un mínimo por producto) y, para quien ve la utilidad, «Margen bajo» (con el mínimo de Configuración ›
  Inventario). A la derecha, el elegido: precio y mayoreo, existencia, costo promedio y margen (cada uno con su permiso),
  sus últimos movimientos del kardex con su folio, «Etiqueta» (a las etiquetas por imprimir de Inventario) y «Editar»,
  que abre la ficha de siempre ahí mismo. «Nuevo producto», el mayoreo por revisar y las decisiones de la depuración
  siguen ahí.
- **Inventario por tareas:** una tarjeta por tarea, solo las de quien entró, con lo pendiente cuando se lee rápido (lo que
  se está recibiendo, las órdenes en camino, las notas por capturar, lo que falta del conteo del día, las etiquetas por
  imprimir), su límite («tú: hasta $200.00») o quién la autoriza. Cada tarjeta abre la pantalla de siempre de esa tarea,
  con «← Inventario»; por dentro no cambiaron. El sugerido no lleva cuenta: calcularlo lee un año de ventas.
- **Clientes:** la búsqueda por nombre o teléfono, «Solo los que deben» y «Por revisar»; a la derecha, sus datos (mayoreo y
  descuento, lo que debe y su vencimiento, su límite y quién lo cambia, cuánto compra y si paga a tiempo) y su cuenta: el
  estado de cuenta, «Límite y plazo», abonar, devolver el saldo a favor, ajustar el saldo y los movimientos. «Editar», ahí
  mismo. Quien no los administra los consulta, como antes.
- **Lo nuevo del servicio, solo para leer:** la lista de Productos, los últimos movimientos de un producto, lo pendiente
  del inventario (solo cuentas) y quién puede hacer algo, por su nombre. Sin cambios en la base.

**Avance (2026-10-15), entrega 5, parte 1: escanear contra la copia, siempre, y el cobro con su versión,** como lo
aprobó el dueño el 2026-10-10 ([propuesta](propuesta-interfaz.md#rendimiento)):
- **La red ya no entra en el escaneo:** cada caja arma una foto de su copia del catálogo (un diccionario con cada código,
  también los de las presentaciones, y la versión de la copia) fuera del hilo de la pantalla, al abrir y cada vez que baja
  una copia, y la cambia de un jalón. Escanear es buscar ahí, también en línea. Abrir la caja ya no espera a leer la copia.
  `12*código`, el folio que abre la devolución, el orden de los escaneos y los rápidos siguen igual. Sin conexión (y
  mientras sube lo que hizo sin ella) se escanea solo contra la copia, que es contra lo que se cobra sin conexión.
- **Lo que no está en la copia** (recién dado de alta en otra caja) entra en su lugar como «Buscando…» y se le pregunta a
  la caja principal en segundo plano, con medio segundo; lo que se escanea después no lo espera. Si llega, toma su lugar;
  si no existe, lo de siempre (darlo de alta o el artículo común, según los permisos); si no contesta a tiempo, la caja
  lo dice y se escanea otra vez. El cobro, las teclas del ticket (también Ctrl+E y Ctrl+D) y los botones de cada fila (y
  «5*») lo esperan: nunca se cobra un «Buscando…». Lo que se hace con un renglón (su precio, su descuento, su mayoreo,
  los botones de su fila) es de ese renglón aunque mientras tanto llegue un «Buscando…»; con una ventana abierta, la
  selección no se mueve. Una palabra sin números que no está en la copia («SOBRE-A») se pregunta primero como código y,
  si no, se busca como nombre, como antes.
- **El cobro lleva la versión de la copia** (y cada línea la de su precio: la pieza y el paquete del mismo producto
  pueden venir de copias distintas). Si el precio o el mayoreo de algo del ticket cambió después, el servicio no cobra y
  contesta con los precios de ahora; la caja pone el ticket al día y dice «Cambió el precio de …: revisa el total y
  cobra otra vez». No se repite solo. Una caja de antes, y el cobro sin conexión, como siempre. En la base, el historial
  de precios guarda con qué versión quedó cada cambio: una escala de mayoreo que se quita solo deja rastro ahí.
- **Un solo escritor para la base de la caja:** todo lo que se escribe en `caja-local.db` pasa por una cola en su propio
  hilo. La venta y la sincronización ya no se esperan entre sí; las lecturas siguen a la par. La copia de la caja
  principal (varios MB) se guarda en pedazos: lo que la venta escribe mientras tanto espera a lo más uno.
- **Se mide:** del Enter a la línea pintada, con el reloj de la página (la mitad, el 95 % y el 99 % de los últimos 500;
  un escaneo que esperó en la cola cuenta todo lo que esperó), en Configuración › Acerca de › Escanear; y el vigía del
  hilo de la pantalla anota en el registro de la caja cuando se traba más de 30 ms en una ráfaga de escaneos, con lo que
  pasaba.
- **Para la parte 2:** ~~buscar al escribir contra la copia~~ (hecho en la parte 3, abajo); bajar solo lo que cambió,
  avisado por SSE (y que la versión no suba con un PIN mal tecleado ni al abrir un turno, que hoy obligan a bajar la copia
  entera); un plazo por operación y los estados de la red; ~~imprimir aparte; pintar menos~~ (en la parte 3); la prueba
  de la ráfaga con la red simulada; y medir en las PC de la tienda. Mientras tanto, «Quedan 3» y «Sin existencia» al
  escanear salen de la copia: pueden tener hasta media hora (las ventas no cambian la versión); con lo que avise el SSE
  se ponen al día.

**Avance (2026-10-16), entrega 5, parte 3: la pantalla,** como lo aprobó el dueño el 2026-10-10
([propuesta](propuesta-interfaz.md#rendimiento)):
- **Buscar al escribir, contra la copia y sin la red:** la búsqueda es una sola para el servicio y para la copia de cada
  caja (`IndiceDeBusqueda`, en el núcleo) y, con los mismos datos, da lo mismo y en el mismo orden: lo elegido antes en
  esa caja, lo más vendido, los sinónimos, el error de dedo y las familias. La copia lleva lo que hace falta para
  ordenar igual (lo más vendido en 90 días, lo que esa caja eligió con cada texto, todos los códigos y las marcas con
  que se leen los nombres). Buscar toma menos de 2 ms con 5,800 productos (antes, 150 ms de espera y una consulta por la
  red): lo que se escribe a mano se busca en cuanto se teclea, y lo que teclea el lector no busca nada. Enter con un
  nombre agrega el primero, también de la copia. Sin copia (la caja recién instalada, el navegador), como antes.
- **La búsqueda con la copia al día** (con la parte 2): lo que cambia en la caja principal llega a la búsqueda de cada
  caja con el aviso y lo que cambió, en segundos, sin bajar la copia completa: cada producto que cambió, con todos sus
  códigos; si cambió alguno, las marcas con que se leen los nombres (salen de los productos); los
  sinónimos que cambiaron; lo más vendido, completo, cuando la caja principal lo calcula otra vez (una vez al día); y lo
  que esa caja eligió en la búsqueda, solo lo nuevo. La caja pide desde las versiones de lo que tiene. Cada foto puesta
  al día trae su búsqueda: si cambió algo de los productos, las marcas o los sinónimos, su índice se arma otra vez; si
  solo cambiaron existencias, lo más vendido o lo elegido, se usa el mismo. La copia puesta al día queda igual que una
  recién bajada, también en esto (lo cuida una prueba), y entre productos que se llaman igual la búsqueda ordena por
  antigüedad (antes, por el orden en que llegaban): ordena igual que la completa y que la caja principal. Al escanear, los
  códigos que solo cambian en mayúsculas («ZZ-01» y «zz-01») se resuelven igual en las dos: el igual tal cual o, si
  ninguno, el producto más antiguo (antes ganaba el primero de la lista, y en la copia puesta al día lo que cambió va al
  final). Lo elegido en la búsqueda mientras se arman los cambios ya no se pierde. Al agregar un sinónimo ya no se baja la
  copia: llega con el aviso. El formato de la copia pasó a 2: una de antes se baja completa una vez.
- **Pintar menos:** cada renglón del ticket es su propio componente: al escanear se pinta el renglón nuevo y el que
  estaba elegido, y la pantalla una sola vez. Con 20 renglones, un escaneo le mandaba a la página 449 cambios y cambiaba
  134 manejadores de eventos; ahora, unos 40 y 5.
- **Imprimir aparte:** todo lo de la impresora de tickets (el ticket, las reimpresiones, los comprobantes, el corte y el
  ticket de prueba) va a una cola de la caja y la pantalla queda libre al momento: cobrar ya no espera a que salga el
  ticket, ni el aviso de retiro. Si la impresora no contesta al conectarse (puede estar imprimiendo lo de la otra caja),
  se vuelve a intentar sola unos 15 s, sin que salga dos veces. Si no sale, la cola se detiene y lo dice con
  «Reintentar» y «No imprimirlo» (en el panel del cobro, arriba del total, en el estado de arriba, en el corte y en la
  entrada); lo que sigue espera en su orden. «No imprimirlo» lo deja sin imprimir, sin preguntar, lo anota en el
  registro de la caja y la cola sigue: es para cuando el cliente ya se fue, o cuando no salió con «no confirmó» y pudo
  salir ya (con «Reintentar» saldría dos veces); el ticket se reimprime desde las ventas del día. Un ticket que ni se
  puede armar para la impresora (el logo dañado) también queda como «no salió», con «Verlo en pantalla». Si se cierra
  la caja con algo sin imprimir, pregunta antes y lo anota en el registro. El pulso del cajón va al principio del
  ticket: si se mandó, el cajón se abre aunque el papel no salga (en la tienda no hay cajón).
- **F12** ya pasaba el cursor a «Pagó con» en la misma tecla, sin esperar a .NET (menos de 1 ms); ahora lo cuida una
  prueba.
- **Abrir la caja:** al publicar para Windows va ya compilada (ReadyToRun). La entrada sale con la copia, sin esperar a
  la red (la caja, su turno y las fichas), y la caja principal la pone al día cuando contesta; el registro del día anota
  a los cuántos segundos salió. Las letras ya eran las de Windows (Segoe UI), ninguna de internet.
- **Falta, en la tienda (un sábado o domingo):** dos tickets a la vez desde las dos cajas a la POS-8360, para ver si
  espera, rechaza o corta (si no conecta, la cola reintenta sola; si corta a medio ticket, dice «no salió» y espera
  «Reintentar» o «No imprimirlo»); y medir allá lo que tarda escribir, escanear y abrir la caja. La parte 2 (la red y la
  copia al día) ya va junto con esta (abajo).

**Avance (2026-10-16), entrega 5, parte 2: la red y la copia al día,** como lo pide la
[propuesta](propuesta-interfaz.md#rendimiento) (puntos 4, 5 y 6):
- **La versión solo sube con lo que lleva la copia:** los productos, presentaciones, códigos, precios y mayoreo, las
  marcas y los sinónimos; los usuarios (nombre, usuario, el hash del PIN, si está activo y su sucursal) y sus permisos; la
  caja, el ticket, la configuración que se usa sin conexión y los clientes. Ya no la suben un PIN mal tecleado, entrar,
  abrir o cerrar un turno, el último costo de un proveedor (cada compra), el margen de un departamento ni lo que la copia
  no lleva de la configuración (los rápidos, las alertas, las etiquetas, el sugerido). Antes cada una de esas hacía que
  las dos cajas bajaran la copia entera. Lo que se quita (un código, una presentación, una escala de mayoreo, un permiso)
  queda anotado con su versión, y la versión sube dentro de la transacción del cambio: dos cambios a la vez ya no pueden
  quedar con la misma.
- **Las existencias con su propia versión:** una venta sube esa y no la del catálogo. «Quedan 3» y «Sin existencia» al
  escanear se ponen al día en segundos (antes, hasta media hora). La existencia sigue sin detener la venta.
- **Solo lo que cambió, avisado por SSE:** la caja principal avisa a cada caja (`api/cajas/{id}/avisos`, un canal de solo
  ida, con la llave de la caja) sus versiones cuando cambian, y un «vivo» cada 15 s. La caja pide lo que cambió desde la
  suya (`api/cajas/{id}/cambios`): los productos que cambiaron, completos; los que ya no van; las existencias que
  cambiaron; y de lo demás, solo lo que cambió. Lo aplica de fondo, arma su foto como siempre y la cambia de un jalón; lo
  del catálogo se guarda al momento en su base, lo de existencias cada 10 minutos (al abrir, pide las que cambiaron). Si
  el aviso se corta, se vuelve a conectar sola; el latido queda de respaldo y también pide lo que cambió.
- **Cuándo baja la copia completa, como antes:** si no tiene copia, si la caja principal se reinició o empezó de cero (otra
  base), si cambió el formato de la copia, si son muchos cambios (una cuarta parte del catálogo), si pedir los cambios
  falla, y por si acaso cada 4 horas. Una caja de antes funciona igual con el servicio nuevo, y una caja nueva con un
  servicio de antes hace lo de siempre (latido y copia completa). Si la caja y la caja principal arman la copia en
  formatos distintos (mientras se actualiza una antes que la otra; la caja principal dice el suyo en el latido y en el
  aviso), la caja no pide cambios y la baja completa solo cuando cambia el catálogo o cada media hora; antes la bajaba
  completa con cada latido y cada aviso.
- **La red con plazos:** dos conexiones aparte, una para lo que espera el cajero y otra para lo de fondo (el aviso, la copia,
  la bandeja). Lo de la venta que también se puede hacer con la copia lleva plazo: leer, 800 ms; cobrar y lo demás que
  escribe, 2.5 s (los 2 s que la caja principal espera el folio de eleventa en el piloto, y medio segundo para la red). El
  plazo es solo de esa operación: lo que se pide después (la cuenta del turno) ya no lo lleva. Lo de atrás (reportes,
  catálogo, inventario) y el corte Z, hasta 15 s, como antes; el corte Z lleva su Id desde la caja: si la respuesta se
  pierde y la caja lo hace con su cuenta, al subir ya está (antes se quedaba con «El turno ya está cerrado»). Keepalive
  de TCP (pregunta tras 5 s sin nada, cada 2 s, 3 veces) y conexiones que se renuevan cada 2 minutos: una conexión medio
  muerta tras reconectarse el Wi-Fi se nota en unos 11 s.
- **El cobro que no llega a tiempo** se hace con la copia y va a la bandeja con el mismo Id. Si la caja principal sí lo había
  guardado (solo se perdió la respuesta), al subir queda una venta, con el mismo folio que se imprimió (la serie de la caja
  sigue igual por los dos caminos), una sola vez en el turno y una sola vez hacia eleventa, y lo de su bitácora (el
  descuento, el precio cambiado) una sola vez. La pantalla enseña un solo ticket. También si las dos llegan a la vez. Si
  con la copia tampoco se puede (algo recién dado de alta que la copia no tiene, impresiones), no se sabe si quedó: el
  panel lo dice, ese folio se aparta (el siguiente cobro sin conexión no lo repite) y el ticket solo se cobra en línea con
  el mismo Id; al volver la red, si ya había quedado, sale el mismo y el panel dice «Ese ticket ya había quedado cobrado».
  Si una caja sube con el Id de una venta que ya está otra distinta, no se descarta callada: queda por revisar.
- **Buena, lenta o caída:** el latido mide lo que tarda la caja principal en contestar. Lenta: dos latidos seguidos de
  300 ms o más, o algo que el cajero esperaba y no llegó a tiempo; caída: no contesta en 2 s. Vuelve a buena con tres
  latidos seguidos de menos de 200 ms (15 s; un Wi-Fi de 150 ms ya no se queda lenta para siempre tras un tropiezo); de una
  caída, en cuanto contesta en menos de 100 ms, salvo que la red vaya y venga (otra caída en menos de 2 minutos): entonces
  pasa por lenta. En lenta la caja trabaja como sin conexión (su copia, su folio, la bandeja), y su copia se sigue poniendo
  al día: si llegó otro precio de algo del ticket, no se cobra con un precio que el cajero no vio (el ticket se pone al día
  y el panel lo dice, como cuando lo dice la caja principal). Lo que no se puede hacer con la copia (devolver, el catálogo)
  se intenta con la caja principal. Se ve donde ya se veía la conexión: «Conectada a la principal · 9 ms», «Red lenta» y
  la franja de arriba.
- **Nada pesado dentro de una petición del servicio:** el catálogo en memoria se recarga de fondo y mientras se contesta con
  el vigente (lo que no tiene, como un producto recién dado de alta, sí espera la recarga), y el cobro se revisa con esa
  misma versión. Las versiones y las llaves de las cajas, en memoria. El cobro lee los productos y su último costo antes de
  tomar el candado de escritura.
- **La prueba de la ráfaga:** 20 códigos (2 ms por carácter, 30 ms entre códigos) con la red lenta (300 ms) y con 30 % de
  pérdidas, mientras sube la versión del catálogo y la otra caja cobra. Quedan las 20 líneas en orden, sin códigos cortados
  y sin ninguna petición a la red de la venta (se cuentan en la red de prueba, no con el reloj).
- **Medido** en la PC de desarrollo, sin red de por medio (el servicio en el mismo proceso), con el catálogo ficticio copiado
  hasta 5,832 productos (el generador da 1,458; la propuesta midió con unos 6,000):

  | | Pesa | Tarda |
  |---|---|---|
  | Copia completa | 4.1 MB | 60–100 ms armarla y mandarla; 90–160 ms leerla; en la caja, ~100 ms armar su foto y 130–300 ms guardarla |
  | Un precio que cambió | 1.1 KB | ~5 ms; en la caja, 150–200 ms esperando que la caja principal recargue su catálogo y 70–130 ms aplicarlo |
  | Una venta (existencias) | 383 bytes | ~2 ms; en la caja, 4–13 ms aplicarla |
  | 40 precios | 29 KB | ~5 ms |

  Por Wi-Fi lo que más pesa es la copia completa (4 a 6 MB), y ahora casi nunca se baja. Se repite con la prueba
  `MedicionCopiaTests` y la variable `MEDIR_COPIA=4` (cuántas veces se copia el catálogo ficticio).
- **Falta:** medir en la tienda, un sábado o domingo: los umbrales de la red lenta con el Wi-Fi de la caja 2, el plazo
  del cobro en el piloto con eleventa, que el aviso vuelva solo tras una caída del Wi-Fi, lo que tarda la copia completa
  por Wi-Fi, y la ráfaga de noche en una PC como la caja 2 con un proxy que agregue retardo y pérdidas. Lo de la pantalla
  (la parte 3: buscar al escribir contra la copia, pintar menos, imprimir aparte y el arranque) ya va junto con esto, con
  lo que usa la búsqueda dentro de lo que cambió (arriba).

**Avance (2026-10-16): los departamentos, en Productos.** Como en eleventa («Departamentos: nuevo, cambiar el nombre,
eliminar»), abajo de la lista de Productos (F3) está «Departamentos», con cuántos productos activos tiene cada uno (el
número los enseña en la lista). Antes solo llegaban de eleventa, y Compras pedía darlos de alta donde no se podía.
- **Alta, nombre, desactivar y reactivar,** con «Productos, precios y etiquetas» de quien entró y en la bitácora. Los
  nombres no se repiten, sin importar mayúsculas, acentos ni espacios de más.
- **Nada se borra:** uno desactivado ya no se ofrece en el filtro, la ficha, el alta rápida ni las compras; el producto
  que se quedó en uno lo sigue diciendo, «(desactivado)». Al desactivar uno con productos activos, pasan antes al que se
  elija; los inactivos se quedan.
- **En el piloto con eleventa** (con su lectura prendida) solo se consultan: se cambian allá, como las altas y los
  precios (2026-10-10), y llegan con la lectura. Un cambio hecho aquí, la siguiente lectura lo regresaría.

**Avance (2026-10-17): cambiar precios de varios a la vez, en Productos.** Como «Actualizar varios» de eleventa, para
cuando un proveedor sube sus precios ([paridad](paridad-eleventa.md#productos-y-precios)). Antes se cambiaban uno por uno
en la ficha. Abajo de la lista de Productos (F3), «Cambiar precios» se abre a la derecha:
- **Cuáles:** los productos activos de un departamento, de una marca, los que coinciden con un texto (como la búsqueda de
  la lista, también con su departamento) o los marcados en la lista. Mientras está abierto, un toque en la lista marca o
  desmarca y, con el cursor en la búsqueda, ↑ ↓ eligen y Enter marca. Hasta 1,000 a la vez.
- **Cómo:** subir o bajar un porcentaje o una cantidad, o un precio fijo, sobre el menudeo; si se quiere, el mayoreo (las
  escalas y los precios por paquete) en la misma proporción; y el redondeo a los 50 centavos o al peso más cercano. El
  redondeo nunca deja un precio al revés de lo pedido (si se sube, no baja) ni en $0, y un precio fijo queda tal cual.
- **Lo que se cobra queda en orden:** el mayoreo nunca queda igual o más caro que el menudeo ni que una escala de menos
  piezas, y un paquete no cuesta más que sus piezas a menudeo; si ya tenía descuento, lo sigue teniendo. Si el redondeo
  lo empareja o lo pasa, ese precio va al paso de abajo (sin ir al revés de lo pedido) o se queda como estaba, y la
  vista lo marca «ajustado»; si ni así cabe, o si sin el mayoreo el menudeo bajaría hasta él, el producto no cambia y la
  vista dice por qué. Además, la caja nunca cobra una escala más cara que el menudeo ni un paquete más caro que sus piezas,
  aunque así hayan quedado capturados.
- **Primero se ve cómo quedan** (Enter): cada producto con su precio de ahora y el nuevo, y lo que no cambia y por qué.
  Quien tiene «Ver costos, utilidad y margen» ve también el margen de cada uno y un aviso si queda debajo del mínimo.
- **«Aplicar» (o Ctrl+Enter) los cambia todos de una vez,** con un solo renglón en la bitácora (cuántos, cuáles, la regla
  y quién) y el historial de cada precio, como en la ficha; las cajas los toman con la versión del catálogo. Si algo
  cambió desde que se vio (otro precio, un producto que entró), no se aplica y la vista se vuelve a pedir; si se pierde
  la respuesta y se oprime otra vez, no se aplica dos veces. Lo hace quien tiene «Productos, precios y etiquetas».
- **En el piloto con eleventa,** el menudeo y las promociones de lo de eleventa se cambian allá, como en la ficha: una
  nota lo dice. Lo dado de alta aquí cambia completo y, de lo de eleventa, su mayoreo de aquí, sin pasar de su menudeo ni
  de sus promociones de eleventa (para subirlo más, primero se sube allá).
- **Por revisar** (decidido mientras el dueño no estaba): el precio fijo, que eleventa tiene; el mayoreo y el redondeo a
  50 centavos ya marcados al abrir; el redondeo que nunca queda al revés de lo pedido; el mayoreo que no cabe más barato,
  al paso de abajo o como estaba (o el producto no cambia); solo productos activos (sin kits), y hasta 1,000 a la vez;
  Ctrl+Enter para aplicar, para que un doble Enter no aplique sin querer.

**Avance (2026-10-16): Productos e Inventario en el piloto.** Con la lectura de eleventa prendida, lo que ella pone no
se cambia en la caja 2: si no, la siguiente lectura lo regresaría sin avisar. Una nota lo dice en cada página y el
servicio lo rechaza ([qué se queda fijo](migracion-eleventa.md#la-lectura-de-cada-pocos-minutos-paso-1)).
- **Productos:** de un producto de eleventa, el nombre, el departamento, si se vende, el código de la pieza, el menudeo
  y sus promociones se ven sin cambiarse, y no se depura. Las escalas del F11 o de F3, las presentaciones, la marca y
  lo dado de alta aquí, sí.
- **Inventario:** no se ofrece lo que mueve existencias (recibir compras, los conteos, ajustes, mermas y abrir un
  paquete): el inventario se lleva en eleventa. Las órdenes, el sugerido, las etiquetas y la hoja de códigos, sí.

**Avance (2026-10-17): las ventas de otros días y el historial de cortes,** como «Ventas del día y devoluciones» y el
«Historial de cortes» de eleventa ([paridad](paridad-eleventa.md#la-tabla)).
- **«Ventas del día»** (en «/», el mismo panel): abre en las de hoy de esa caja, de todos los cajeros, como antes. Con
  «Ver reportes de ventas» (al migrar, solo el dueño) se cambia de día con ← → o la fecha, y de caja; cualquiera filtra
  por cajero y busca un folio de cualquier día: con su serie («B-000123»), en cualquier caja, como al escribirlo para
  devolver; solo el número, sin ese permiso, en las de esa caja (si no, recorriendo los números se verían las ventas de
  las otras). Cada venta dice su folio (en el piloto, también el de eleventa), la hora, quién cobró, la caja, el total,
  cómo se pagó y si se canceló o se devolvió en parte; reimprimir pide el motivo, como siempre, y «Devolver o cancelar»
  abre la devolución con el folio, con sus reglas.
- **El historial de cortes** va en el corte, junto a «Turno» y «Del día» («/» lo abre ahí): los turnos de cualquier día,
  por caja y cajero, con lo esperado, lo contado, la diferencia, quién lo hizo y cuándo, y «Reimprimir el corte», que
  sale igual que el corte Z al cerrar. Lo ve quien hace el corte de un turno o ve el del día (al migrar, el encargado y
  el dueño); el cajero no: su corte es a ciegas. En el piloto, «Corte en eleventa».
- **Sin conexión,** la caja solo tiene su copia para vender: las ventas y los cortes de todas las cajas están en la caja
  principal, y los dos lo dicen.
- **Lo nuevo del servicio, solo para leer:** `GET api/cajas/{caja}/ventas` (un día, la caja, el cajero o un folio) y
  `GET api/cajas/{caja}/cortes` (un día, la caja y el cajero), con el usuario de la sesión. Sin cambios en la base.

**Avance (2026-10-17): los reportes del inventario, como en eleventa.** Lo que eleventa tiene en F4 y el POS solo tenía
en el diagnóstico en Excel o en la ficha del producto ([paridad](paridad-eleventa.md#inventario)): tres tarjetas nuevas
en Inventario (F4). Solo leen, así que también van en el piloto con eleventa, y salen con su permiso en «Puede»
(consultar no se autoriza).
- **Lo que vale el inventario** (su «Reporte de inventario»): la existencia a costo promedio, el que usa el POS, en
  total y por departamento, con cuántos productos tienen existencia y cuántos están en cero, en negativo o sin costo (lo
  negativo no resta y lo que no tiene costo no suma). A un lado, los productos del departamento que se elige, de lo que
  más vale a lo que menos, o los que están en cero, en negativo o sin costo, de 50 en 50. Solo con «Ver costos, utilidad
  y margen»: los demás no ven la tarjeta, y el servicio lo revisa.
- **Movimientos:** el kardex de toda la tienda en unos días (hoy, ayer, 7 o 30 días, el mes o de una fecha a otra), por
  tipo (ventas, devoluciones, compras, ajustes, mermas, conteos, paquetes, inventario inicial y la lectura de eleventa),
  producto (nombre o código, también escaneado), departamento y quién lo hizo, de 50 en 50 (Re Pág y Av Pág). Cada uno
  con su folio o su motivo, lo que entró o salió, lo que quedó y quién; el costo, solo con «Ver costos, utilidad y
  margen». La ve quien lleva el inventario (el catálogo, los ajustes, las compras, las órdenes, el conteo completo o los
  costos): hoy el dueño y el encargado. Con un año de la papelería ficticia, cada página sale en menos de una décima de
  segundo, también con cinco veces más movimientos.
- **Bajos en inventario:** cada variante puede llevar su mínimo y su máximo en la ficha (F3), como en eleventa, y «Poca
  existencia» usa el suyo (sin él, 3 o menos, como antes). La tarjeta dice cuántos están en su mínimo o debajo; la lista
  va por departamento, de 100 en 100, con cuánto pedir: lo que falta para su máximo, contando lo que ya viene en órdenes
  abiertas, o, sin máximo, lo que pide el sugerido de compra, pero al menos lo que falta para llegar a su mínimo (el
  sugerido no lo conoce). Es un número en la pantalla: los pedidos siguen en las órdenes y el sugerido. La ve quien pide
  la mercancía o pone los mínimos.
- **El mínimo y el máximo** se guardan en la variante, con su migración, y suben la versión del catálogo como cualquier
  cambio de la ficha. Son del POS: la lectura de eleventa no lee los suyos, así que en el piloto también se ponen aquí.
  Cada cambio queda en la bitácora, y una caja de antes que guarda la ficha no los borra. La venta sigue diciendo
  «Quedan 2» con 3 o menos: la copia de las cajas no lleva el mínimo.
- **Falta:** exportarlos a Excel, como eleventa (la caja todavía no guarda archivos), y, si la tienda usa los mínimos de
  eleventa, pasarlos en la importación final (la importación no los lee).

**Avance (2026-10-17): la red de la tienda en el POS,** como lo pide la [red de la
matriz](arquitectura.md#la-red-de-la-matriz) («En el POS»). Casi siempre que se va el internet de Megacable se cae
también la red local; esto no la arregla (eso es el [arreglo propuesto](arquitectura.md#la-red-de-la-matriz), sin
compras), pero hace que el POS lo aguante, lo diga bien y lo anote:
- **La caja vuelve a encontrar sola a la caja principal** si le cambió la IP (el DHCP del router): si la dirección
  guardada no contesta 30 s, la busca en la red (un paquete por UDP, sin recorrerla) y solo se cambia a la que prueba,
  con la huella de la llave de esa caja y con la llave propia del servicio, que es su caja principal: un reto nuevo
  cada vez, firmado también con la dirección en que llegó, así que otro equipo no puede pasarle la pregunta a la de
  verdad y quedarse en medio. La llave de la caja no viaja. Revisa cada IP que contesta una vez (hasta 64), así que
  respuestas falsas no dejan fuera a la de verdad, y se cambia solo si una sola lo prueba. Guarda la dirección nueva en
  `caja.json` (de un jalón: un apagón a media escritura no lo deja a medias). Mientras la guardada contesta, nada; si no
  la encuentra, busca cada vez más espaciado (hasta cada 5 minutos, con la marca de tiempo de la PC, no la hora) y lo
  anota una vez; «Volver a buscarla», en la franja, busca al momento. El servicio contesta solo a su red local, a lo más
  5 veces por segundo a cada equipo y 200 entre todos, y solo el de la caja principal.
- **La llave propia de la caja principal** (`pos.llave`, junto a la base, fuera de ella y de sus respaldos; en Windows,
  cifrada para esa PC): las cajas la aprenden al darse de alta, o las de antes la primera vez que su dirección de
  siempre prueba quién es. Así, otra PC con una copia de la base (la caja principal de antes, un respaldo en una laptop)
  nunca gana. Después de una caída y al abrir, la caja no manda su llave hasta que la dirección guardada pruebe otra vez
  quién es; mientras, vende con su copia.
- **Avisos de lo configurado por nombre o con WSD:** la impresora de tickets por nombre, compartida desde otra PC o con
  un puerto WSD (la caja de Windows lee el puerto de la cola en el registro) dice «Tickets · configúrala por IP» en el
  estado de arriba y en la entrada; la caja principal por nombre, en Configuración › Acerca de › Red; la PC de eleventa
  por nombre, en la bitácora del servicio.
- **La franja:** el servicio revisa cada 30 s si hay internet (una conexión a 1.1.1.1 y 8.8.8.8 por su IP, 2 s, sin
  cuentas) y si contesta su router, y lo dice en su latido. La caja lo junta con lo suyo: «Sin internet: la venta sigue.
  No desconectes nada.» si llega a la caja principal; «Sin red local: esta caja trabaja con su copia» si no llega; en la
  caja principal sin saber que su router contesta (trabado, o una 169.254 sin puerta de enlace), «Sin internet ni red
  local»; con todo bien, nada. Arriba, el chip «Sin internet» del prototipo. Sin modal y fuera del cobro.
- **La bitácora de red:** cada caja y el servicio anotan solos, con fecha y hora, cuando se pierde y vuelve el internet,
  la caja principal, el router, la impresora de tickets (solo ping) y cada caja (siempre, aunque el archivo del día
  llegue a su tope); los cambios de la IP y de dónde viene, del router y su MAC; el DNS del router trabado, una tormenta
  de difusión y cuándo arrancó la PC; y un resumen cada 15 minutos, que cuenta lo que va y viene (la red lenta, el DNS
  del router, una caja que deja de latir) en vez de una línea cada vez. El Wi-Fi y su señal, solo si se prende su
  detalle. Unos archivos por día (`red-caja-…` y `red-servicio-…`) que se guardan 10 días (lo viejo se quita también al
  cambiar el día), y las últimas caídas, con desde, hasta y cuánto duraron, en Configuración › Acerca de › Red
  ([cómo se lee](pruebas-en-tienda.md#después-de-una-caída-la-bitácora-de-red)).
- **Revisión (2026-10-17):** una revisión aparte no encontró fallas en la prueba de quién es, pero sí siete cosas, ya
  arregladas, cada una con su prueba: la caja principal que arranca sin router ya no dice «no desconectes nada»; otra
  PC con una copia de la base ya no gana (la llave propia del servicio, y con dos que prueban no se cambia sola); un
  equipo de la red ya no detiene la búsqueda con respuestas falsas; la bitácora del servicio se limpia al cambiar el día
  y cuenta lo que va y viene; la búsqueda no se mueve con la hora de Windows; el Wi-Fi solo con su detalle prendido;
  y los clientes de la caja no usan proxy. Además, si se cambia la PC de la caja principal, la caja ofrece darse de
  alta otra vez sin quitarle la venta a quien atiende.
- **Decidido, por revisar** (el dueño no estaba; van a «Decidido mientras el dueño no estaba» al juntarlo con #112):
  - si la caja principal estaba guardada por su nombre y no contesta, la caja se cambia a su IP cuando la encuentra y la
    prueba (en el POS, todo por IP);
  - en la caja principal, sin internet y sin saber que su router contesta (el router trabado, o la PC arrancó sin que
    el router le diera dirección), la franja dice «Sin internet ni red local: esta caja sigue vendiendo», sin «no
    desconectes nada»: la guía de la tienda pide juntar los datos y desconectar el cable del módem. Si su propio
    servicio no contesta (al actualizarse), «El servicio de esta PC no contesta», y ahí ya no sale la franja de la red
    lenta, como no salía el chip;
  - el internet se revisa contra 1.1.1.1 y 8.8.8.8 por su IP (`Red:Internet` lo cambia), el DNS del router con
    `tendawifi.com` (`Red:NombreDelRouter`) y otros equipos con ping si se ponen en `Red:Equipos` (el grabador, el
    módem); en el piloto, también la PC de eleventa, solo con ping. Un equipo que nunca contestó el ping (su firewall)
    no cuenta como caído;
  - la bitácora se guarda 10 días; también la anota la prueba con una persona real (su servicio en la misma PC);
  - «Volver a buscarla» no entra en el orden del tabulador, para no quitarle el foco a la venta: se oprime con el ratón,
    y la caja busca sola de todos modos;
  - la llave propia del servicio va en `pos.llave`, junto a la base y fuera de los respaldos (en Windows, cifrada para
    esa PC): reinstalar o cambiar la PC de la caja principal, o restaurar una copia en la de repuesto, pide dar de alta
    cada caja otra vez. La franja lo ofrece («Dar de alta otra vez», con el PIN del dueño) solo sin nadie en la venta, y
    desde ahí se puede volver sin dar de alta;
  - una caja de antes aprende esa llave la primera vez que su dirección de siempre (la que puso el dueño; si no, con la
    que abrió) prueba quién es, y la guarda en `caja.json`. Ya con la llave, un servicio de antes (sin la prueba) no le
    basta;
  - después de una caída y al abrir, la llave de la caja no sale hasta que la dirección guardada pruebe quién es: al
    abrir, cada llamada espera esa prueba a lo más 3 s y, si no llega, va con la copia;
  - si contestan dos que prueban ser su caja principal (o solo una que sabe de la caja pero no firma con su llave), la
    caja no se cambia sola: la franja dice «Hay dos cajas principales en la red: avisa al dueño» (o «La caja principal
    no probó ser la misma de siempre») y ofrece darla de alta otra vez. La misma PC por cable y por Wi-Fi (la misma
    llave) cuenta como una;
  - si la caja principal ya no conoce a la caja (la quitaron), la pantalla del alta espera a que salga quien atiende;
    mientras, vende con la copia;
  - en la bitácora, las veces que la red se puso lenta van solo en el resumen de 15 minutos; del DNS del router y de las
    cajas que dejan de latir, la primera vez de cada periodo lleva su línea y las demás se cuentan en el resumen; lo que
    se pierde y vuelve, siempre;
  - el detalle del Wi-Fi viene apagado: se prende en `caja.json` de la caja 2 para diagnosticar
    ([cómo](pruebas-en-tienda.md#después-de-una-caída-la-bitácora-de-red)), revisando que no salga la pregunta de la
    ubicación;
  - el servicio contesta por UDP a lo más 5 veces por segundo a cada equipo y 200 entre todos.
- **Falta, en Windows** (el CI y una PC): el Wi-Fi con su detalle prendido (el nombre de su perfil de red, sin pedir el
  permiso de ubicación) y su señal, la MAC del router, el puerto de la cola en el registro, guardar la dirección nueva y
  la llave del servicio en `caja.json`, `pos.llave` cifrado para la PC (y que copiado a otra no sirva) y que un «puerto
  inalcanzable» no corte la búsqueda. **En la tienda, un sábado o domingo:** cambiarle la IP a la caja principal con
  la caja 2 abierta, prender el detalle del Wi-Fi en la caja 2, y la próxima caída de verdad, con la bitácora.

**Avance (2026-10-17): al cobrar, imprimir el ticket, ofrecerlo o no imprimirlo,** como pide la
[propuesta](propuesta-interfaz.md#configuración) en Configuración › Ticket y como cobra eleventa sin ticket con F2
([paridad](paridad-eleventa.md#venta-y-cobro)):
- **Configuración › Ticket › «Al cobrar»,** para toda la tienda y con «Configurar el ticket», como el encabezado y el
  pie: las dos cajas comparten la impresora y los clientes. Tres opciones en un segmento, como en el prototipo, que se
  guardan al tocarlas y quedan en la bitácora: «Imprimir el ticket» (por omisión, lo de siempre), «Ofrecerlo» y «No
  imprimirlo». El «preguntar» de la propuesta quedó en «Ofrecerlo»: nunca se pregunta antes de cobrar, porque F12 ya es
  la confirmación (2026-10-14).
- **«Ofrecerlo»:** se cobra igual y la caja queda lista para el siguiente cliente. El panel del cambio dice «Ticket A-…
  · Imprimir ticket (Ctrl+I)» y, si se vuelve al total con Esc, sigue abajo, junto a «Último», en lugar de «Reimprimir».
  Sale una vez, sin pedir motivo (no es una reimpresión: no había salido), y el panel dice cómo va en la cola. Con el
  primer escaneo del siguiente cliente (o al retomar otro de la espera) deja de ofrecerse y ya no vuelve, aunque se
  quite lo escaneado. Si mientras tanto se reimprime con su motivo («/»), tampoco: no queda una copia más sin motivo; ni
  si se cancela o se devuelve algo de esa venta (saldría completo el ticket de una venta cancelada), y entonces su
  cambio también deja el panel. Con el ratón, «Imprimir ticket» y «Volver al total» regresan el cursor al cuadro de
  captura, como Ctrl+I y Esc. Sin impresora, Ctrl+I lo enseña en pantalla.
- **«No imprimirlo»:** a la impresora no le llega nada y el panel dice «Ticket A-… sin imprimir». Si el cliente lo pide,
  «Reimprimir» (abajo o en «Ventas del día»), con su motivo, como toda reimpresión. Sin conexión no se puede reimprimir:
  lo que se cobra con la copia de la caja (sin conexión o con la red lenta) se ofrece, como con «Ofrecerlo».
- **Lo que sale siempre:** lo fiado, porque el ticket lleva «SU CUENTA» (lo que debe y hasta cuándo paga) y el cliente
  se lo lleva. El ajuste es solo del ticket de la venta al cobrar: los comprobantes de devoluciones y cancelaciones, de
  entradas y salidas de efectivo y de abonos, el estado de cuenta, el corte, las reimpresiones y el ticket de prueba
  salen como siempre.
- **El cobro no espera nada más:** lo que se hace con el ticket llega con el ticket del cobro (en línea, lo de la
  sucursal en ese momento; sin conexión, lo de la copia de la caja, que lo trae con lo que cambió). Sin papel no va el
  pulso del cajón (va al principio del ticket; en la tienda no hay cajón). La base guarda el ajuste en
  `ConfiguracionesTicket` (migración `TicketAlCobrar`); las que ya existían siguen imprimiendo.
- **Decidido, por revisar** (el dueño no estaba): para toda la tienda y no por caja; lo fiado como la única excepción
  del ticket de venta; reimprimir después sigue pidiendo motivo; sin papel no se abre el cajón; Ctrl+I como su tecla;
  sin conexión, «No imprimirlo» se ofrece como «Ofrecerlo» (sin la caja principal no hay reimpresión; de la revisión del
  PR #118).

**Avance (2026-10-17), entrega 13, parte 1: lo que se señala,** como en la
[propuesta](propuesta-interfaz.md#lo-que-se-señala-exhibidos-y-temporada) y el
[prototipo](propuesta-interfaz/prototipo.html), todavía sin las fotos (en su lugar, las iniciales del producto):
- **Exhibidores y temporadas, en la base:** cada uno con sus lugares de arriba abajo («Repisa de arriba», «Mesa de la
  entrada») y los productos de cada lugar de izquierda a derecha. La temporada tiene un día y mes de inicio y de fin que
  se repiten cada año; Navidad cruza el año. La migración deja «Mostrador», con «Repisa de arriba» y «Repisa de abajo»
  vacías, y las temporadas del dueño, cada una con «Mesa de la entrada»: 15 de septiembre (del 1 al 16 de septiembre),
  Todos Santos (del 16 de septiembre al 5 de noviembre), Navidad (del 20 de noviembre al 1 de enero) y Primavera, sin
  fechas: sin fechas no sale en la venta. El 16 de septiembre corren dos.
- **Ctrl+M en la venta** (también en la barra de teclas y en «/»): un panel a la derecha, como los demás, sin tapar el
  ticket. Primero las temporadas que corren hoy y luego los exhibidores, cada lugar con sus productos: el lugar de la
  foto, el nombre y el precio; la mesa de la temporada de 3 en 3, más grandes, y la vitrina de 4 en 4. Tocar uno lo
  agrega; con un número antes en el cuadro, esa cantidad. El panel se cierra y queda el total, listo para F12. El cursor
  se queda en el cuadro de captura: las flechas eligen la pieza, Tab cambia de exhibidor o temporada y Enter agrega la
  elegida. Lo que escanea el lector con el panel abierto entra al ticket como siempre, nunca como cantidad. Sale de la
  copia de la caja, sin la red: también sin conexión.
- **En el verificador (F9):** Ctrl+M (o su botón) abre el mostrador con el cuadro en ámbar; tocar una pieza, o escanear,
  solo enseña su precio y su existencia, y «Agregarlo al ticket» la agrega.
- **El chip de la temporada:** mientras corre, arriba de la venta, con su nombre; abre Ctrl+M en ella. Al terminar sus
  fechas se quita solo (se revisa cada minuto y cuando cambia la copia). En las páginas de atrás no sale.
- **Acomodar,** en el mismo Ctrl+M, para quien tiene «Productos, precios y etiquetas»: se busca por nombre o se escanea
  y Enter, y entra después de la pieza elegida («Agregar aquí» lo pone al final de un lugar); Shift y las flechas lo
  mueven, Supr lo quita. Cada cambio se guarda al momento en la caja principal y llega a todas las cajas. Al acomodar
  también salen las temporadas que no corren, para prepararlas antes.
- **Configuración › Mostrador y temporadas:** «Exhibidores» y «Temporadas», con el mismo permiso: nuevo, el nombre, las
  fechas («del 16 de septiembre al 5 de noviembre», «16/9 al 5/11» o «el 30 de abril») y los lugares (agregar, cambiar
  el nombre, subir, bajar y quitar: sus productos pasan al lugar de al lado). Quitar un exhibidor o una temporada lo
  oculta. La bitácora anota de cada exhibidor y temporada el alta, el nombre, las fechas y si se quitó; el acomodo y los
  lugares, no. Sin conexión no se cambia: se dice.
- **En la copia de cada caja:** suben la versión del catálogo y llegan con lo que cambió, toda la lista cuando cambia
  algo (es chica). La copia pasa al formato 3: una caja cuya copia no los trae baja la completa una sola vez. La prueba
  de que la copia con los cambios encima queda igual que la completa ya los incluye.
- **En el piloto con eleventa** se acomoda igual: el acomodo es solo del POS y no toca lo que trae la lectura.
- **La base de la prueba** (`preparar-prueba`) ya trae productos en el mostrador y en cada temporada.
- **Decidido mientras tanto, por revisar con el dueño:** acomodar en Ctrl+M (como el prototipo) y no en Productos; el
  permiso «Productos, precios y etiquetas», sin autorización; los nombres de los lugares que deja la migración; que sea
  el mismo en todas las cajas y sucursales; y que con el panel abierto un número de hasta 4 cifras y Enter sea la
  cantidad de la pieza elegida, salvo que sea el código de un producto («45»): ese se escanea, y «45*» siempre es la
  cantidad. Al tocar una pieza, lo escrito solo cuenta si es una cantidad de hasta 4 cifras (un código que se quedó en
  el cuadro no).
- **Para las partes que siguen:** las fotos (desde la app Android o la PC), la hoja de la temporada y las tarjetas de
  precio, empezar y terminar la temporada con el precio del año pasado, y lo más preguntado.

**Avance (2026-10-17), entrega 13, parte 2: las fotos de los productos,** como en la
[propuesta](propuesta-interfaz.md#lo-que-se-señala-exhibidos-y-temporada) (punto 7):
- **Una por producto** (por variante: la de cada color o tamaño), la que sale en Ctrl+M, en la lista de la búsqueda y en el
  verificador. Subir otra la reemplaza; también se quita. La pone o la quita quien tiene «Productos, precios y etiquetas»,
  sin volver a escribir su PIN; el servicio lo revisa y queda en la bitácora («Puso la foto de «…» (desde el celular)»).
- **Se guarda chica:** el servicio revisa que sea una foto (JPEG, PNG, WebP, GIF o BMP, de hasta 15 MB), la endereza como
  se tomó (el celular en vertical) y la deja en un JPEG de a lo más 240 píxeles por lado, completa (sin recortarla) y unos
  15 KB o menos; lo transparente queda blanco. La original nunca se guarda. Medido aquí: fotos de 2 a 10 MB quedan de 4 a
  6 KB, en 15 a 100 ms. Con 300 fotos, menos de 5 MB.
- **Sin gastar de más en la caja principal** (de la revisión: un PNG en blanco de 300 KB que decía medir 10,000 × 10,000
  ocupaba 900 MB al leerlo, y un JPG progresivo así, 600). Antes de apartar memoria se revisa lo que dice medir: la foto JPG
  de una cámara o un celular (de un solo barrido) se lee ya reducida y va hasta 100 megapíxeles; lo que se lee completo
  (PNG, WebP, GIF, BMP y el JPG progresivo, que guarda la imagen entera aunque se lea reducida) va hasta 16 megapíxeles; y
  nada que diga medir mucho más de lo que su archivo puede traer. Después se achica a la mitad las veces que haga falta, sin
  copias. Las fotos se preparan de una en una: si llegan varias a la vez, esperan su turno. Lo más que ocupa la más grande
  que se acepta: unos 100 MB por un momento.
- **En la misma base** (pos.db), y no en una carpeta aparte: el respaldo diario y el de cada corte Z las llevan, y restaurar
  uno las regresa como estaban. Cada respaldo pesa unos 5 MB más con 300 fotos.
- **Desde el celular,** en la página del conteo (`/conteo`), sin esperar a la app Android: quien puede ve «Contar» y
  «Fotos». Se escanea o se busca el producto (la lista dice cuáles ya tienen foto), «Tomar la foto» abre la cámara, se ve
  cómo quedó y se guarda; después se escanea el siguiente. El celular la achica antes de mandarla (una de 4 MB viaja como de
  unos 150 KB por el Wi-Fi). El código QR de la página también sale en la ficha del producto («Con el celular…»): en el
  piloto con eleventa, el conteo (donde estaba) no se ofrece.
- **Desde la PC,** en la ficha del producto (Productos, F3), en cada variante: «Elegir una foto…» o Ctrl+V con el cursor en
  el cuadro de la foto (una imagen copiada de una página o de una captura), se ve y «Guardar la foto», o «Quitar la foto».
  Se guarda al momento, aparte de «Guardar» de la ficha.
- **A las cajas, solo lo que cambia:** la copia lleva de qué producto es cada foto y su huella (unos 80 bytes por foto), no
  la foto. Cada caja baja aparte las que no tiene (`api/fotos/{variante}`, de fondo, una a una), las guarda en su base
  (caja-local.db) por el escritor de siempre, en lotes chicos, y quita las que ya no van. Una foto nueva, cambiada o quitada
  sube la versión del catálogo (como acomodar) y llega con lo que cambió, por el aviso; no obliga a la caja a guardar su
  copia entera ni a armar otra vez su búsqueda. La prueba de que la copia con los cambios encima queda igual que la
  completa ya las incluye. La copia pasa al formato 4: cada caja la baja completa una vez después de actualizarse.
- **En la pantalla, de la base de la caja, sin la red:** la pieza de Ctrl+M con foto la enseña en lugar de sus iniciales; la
  lista de la búsqueda, chica a la izquierda; el verificador, grande. Sin foto (o mientras baja), las iniciales, como antes.
  La ventana de Windows se las da a su página de lo que ya bajó; sin conexión se siguen viendo. La caja del navegador en la
  PC de la caja principal las pide al servicio de esa misma PC.
- **En el piloto con eleventa** se toman igual: las fotos son solo del POS.
- **Decidido mientras tanto, por revisar con el dueño:** guardarlas en la base y no en una carpeta; JPEG (lo leen todas las
  cajas, los celulares y la hoja de la temporada que sigue) de 240 por lado, sin recortar; el permiso «Productos, precios y
  etiquetas» (si alguien debe tomar fotos sin poder cambiar precios, se agrega uno propio); una por variante; quitarla sin
  confirmar (se vuelve a tomar), como el logotipo del ticket; Ctrl+V en el cuadro de la foto y no un botón «Pegar» (leer el
  portapapeles pediría permiso al navegador); y que la foto de un producto desactivado o juntado con otro se quede.
- **Falta:** probar en un celular de la tienda (la cámara desde la página, el Wi-Fi) y ver las fotos en la ventana de
  Windows de la caja 2 ([guía del fin de semana](pruebas-en-tienda.md)). La hoja de la temporada y las tarjetas de precio
  (la parte 3) ya pueden usarlas.

**Avance (2026-10-17), entrega 13, parte 3: empezar y terminar la temporada, su hoja, las tarjetas de precio y lo más
preguntado,** como en la [propuesta](propuesta-interfaz.md#lo-que-se-señala-exhibidos-y-temporada) (puntos 3 a 6). Todo en
Productos (F3), con la caja principal; nada de esto viaja en la copia de las cajas:
- **«Temporadas»** (abajo de la lista, para quien tiene «Productos, precios y etiquetas»): arriba se elige la temporada (la
  que corre, primero) y el paso: 1 · Empezar: precios, 2 · Hoja, 3 · Tarjetas y 4 · Terminar. Esc regresa al producto.
- **Empezar la temporada:** la que corre hoy o, si no, la siguiente (Navidad, el 17 de octubre: la del 20 de noviembre al
  1 de enero), con la lista del año pasado: lo acomodado en su mesa, en su orden, cada producto con el precio que tuvo
  entonces, el de hoy y si tiene foto (tocarlo abre su ficha para ponérsela; o con el celular, en la página del conteo). El
  precio de entonces sale de su historial al terminar el primer día de esa temporada (lo que se rebajó al final para sacar lo
  que sobraba no cuenta); si su historial no llega tan atrás (el primer año, con lo que vino de eleventa), del precio de
  menudeo al que más veces se cobró en esa temporada: de este POS, sin lo que se cobró a precio de mayoreo; de eleventa, que no
  dice si fue mayoreo, lo vendido de a una pieza, o si solo hay de varias, el más alto que se cobró (el mayoreo siempre es más
  barato). Quien tiene «Ver costos, utilidad y margen» ve además el costo de
  hoy (el de su última compra; sin compras, el promedio de lo que hay), el margen con el precio de hoy y un sugerido: el más
  alto entre el de hoy y el del año pasado, subido al que deja el margen mínimo (el del departamento o el general) si deja
  menos, a los 50 centavos de arriba; nunca baja un precio. Los demás no reciben esas columnas: el servicio no las manda.
- **Los precios se confirman una vez:** se escribe el de cada uno (Enter pasa al siguiente; «Todos al del año pasado»,
  «Todos al sugerido» o tocar el de un renglón) y «Confirmar los precios» (Ctrl+Enter) los manda todos juntos, en una sola
  transacción: cambian los que cambiaron (con su historial, un renglón en la bitácora con cuántos y cuáles, y las cajas los
  toman con la versión del catálogo) y los demás quedan confirmados como están («Luis confirmó los precios el 10/10»). Las
  mismas reglas que cambiar precios de varios: el mayoreo no se toca y nunca queda igual o más caro que el menudeo, ni un
  paquete más caro que sus piezas; si un precio lo dejaría así, nada se confirma y se dice cuál (la página ya lo marca en
  rojo). Tampoco si un precio cambió mientras tanto (otra caja): lo escrito recuerda el de hoy que se veía al escribirlo, y si
  al volver a abrir la lista ya es otro, el renglón lo marca («el de hoy cambió») hasta escribirlo otra vez. Si la respuesta se
  pierde y se confirma otra vez, lo que ya quedó no se vuelve a cambiar. Al confirmar dice cuántos de los que cambiaron tienen
  tarjeta de precio.
- **La hoja de la temporada:** cada lugar de la mesa con sus productos de tres en tres, cada uno con su foto (sin foto, sus
  iniciales), su nombre, su precio y su código dibujado (también se cobra escaneándolo desde la hoja); a lo que no tiene
  código se le da uno interno, como al imprimir etiquetas. Hoja carta con su título y «Hoja 1 de 2»; un lugar que sigue en
  otra hoja repite su nombre. «Vista previa» y «Imprimir» como la hoja de códigos: la caja arma la página
  (PapeleriaPos.Impresion, HojaDeTemporada) en un marco y la manda a la impresora de inyección o la láser, al 100 %.
- **Las tarjetas de precio:** una por producto, 18 por hoja carta (6.5 × 4.2 cm) con su línea para recortar: el nombre, el
  precio grande y el código para escanearla en lugar del producto (HojaDeTarjetas). Al imprimirlas queda anotado con qué
  precio y qué código salió cada una. En la temporada (paso 3) van marcadas las que no tiene y las que cambiaron; en
  Productos › «Tarjetas de precio» hay tres listas: «Por reimprimir» (su precio de hoy, o su código, ya no es el de la
  tarjeta: el POS dice cuáles; al imprimirlas salen de la lista, y «Ya no tiene» quita la que se tiró), «Lo más preguntado»
  y cada exhibidor o temporada en su acomodo. El botón de Productos dice cuántas hay por reimprimir.
- **Lo más preguntado:** el verificador (F9) cuenta cada producto que enseña (escaneado, escrito o tocado en Ctrl+M), sin
  esperar ni estorbar; sin conexión no se cuenta, como lo que se busca. «Lo más preguntado» enseña lo que más se preguntó en
  los últimos 90 días y no tiene tarjeta: lo primero a lo que conviene ponérsela.
- **Terminar la temporada:** la última que empezó, con el resumen de cada producto de su primer día a hoy: lo que había (al
  empezar, más lo que llegó), lo que se vendió (menos lo devuelto) y lo que quedó, con «se acabó», «no se vendió» o qué parte
  se vendió. Lo que sobró se cuenta en «Contado» (con «Conteo completo de un departamento»; lo vacío se queda como está):
  cada uno se guarda en la caja principal al salir del cuadro (o con Enter), con lo que decía el sistema en ese momento, como
  las capturas del conteo completo; al terminarla entra la diferencia, así lo que se venda mientras se cuenta lo demás (o antes
  de terminarla) sigue contando, y lo contado no se pierde si se cierra la página. Más de tres decimales no es una cantidad: lo
  dice. «Terminar la temporada» guarda antes lo escrito que falte, pregunta una vez y guarda el resumen, que ya no cambia; se
  termina una vez por año. Si la tienda empezó a usar este POS ya empezada la temporada, el resumen va desde ese día y lo
  dice; si ya había terminado, no hay resumen.
- **En el piloto con eleventa:** los precios de lo de eleventa se cambian allá (se ven sin cuadro y se confirman como están;
  lo dado de alta aquí sí cambia), y lo que sobró no se cuenta aquí: el inventario se lleva en eleventa y la página lo dice
  (lo que se hubiera contado fuera del piloto, antes de volver a él, no se aplica). El resumen sí: lo que bajó la existencia con la lectura de eleventa cuenta como vendido en la caja principal, y lo que subió,
  como lo que llegó. La hoja, las tarjetas y las consultas, igual: son del POS.
- **La base:** las migraciones TemporadaTarjetasYConsultas (la temporada de cada año con su resumen, las tarjetas impresas y
  las consultas del verificador) y ContadoDeLaTemporada (lo contado al terminarla, con lo que decía el sistema). Ninguna sube
  la versión del catálogo.
- **Decidido mientras tanto, por revisar con el dueño:** todo en Productos y no en Ctrl+M (el prototipo tenía ahí «Imprimir
  la hoja» y «Tarjetas para la vitrina»: imprimir pide la vista previa y es trabajo de atrás); que empezar sea la que corre o
  la siguiente, y terminar la última que empezó; el precio de entonces al terminar el primer día de la temporada pasada; el
  costo de hoy, el de la última compra; el sugerido, que nunca baja un precio; que el mayoreo no se mueva al confirmar (si
  estorba, se cambia en la ficha) y que se confirme todo o nada; la tarjeta queda anotada al oprimir «Imprimir», aunque se
  cancele el diálogo de la impresora (se vuelve a imprimir desde la misma lista); 18 tarjetas por hoja; contar las consultas
  solo con conexión y 90 días para lo más preguntado; el resumen desde el primer día de la temporada hasta que se termina (lo
  que se venda después de su último día y antes de terminarla, cuenta); terminarla con una pregunta antes (no se deshace); que
  terminarla no quite su aviso arriba de la venta (sigue sus fechas); y en el piloto, lo que bajó con la lectura de eleventa
  como vendido.
- **Falta:** imprimir la hoja y una hoja de tarjetas en la WF-6590 y la L5590 al 100 % y escanear una tarjeta recortada con
  el lector de la caja 2 ([guía del fin de semana](pruebas-en-tienda.md)); ver en la ventana de Windows la vista previa con
  las fotos.

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
| 7 | **En la tienda, un sábado o domingo:** la impresora de tickets que comparten las dos cajas y el lector (qué manda con los códigos de 11 cifras y su ritmo al teclear) | Lo que no se puede simular ([guía](pruebas-en-tienda.md)) | Un fin de semana |
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
- **Decidido por el dueño (2026-10-12):**
  - **Solo efectivo en la caja 2 durante el piloto:** tarjeta, transferencia, crédito, saldo a favor y cobro mixto se
    desactivan mientras la caja 2 manda sus ventas a eleventa.
  - **Eleventa no se cierra en la caja 2:** si algo falla, se cambia a eleventa de inmediato. Las dos venden en el mismo
    turno de esa caja.
  - **El corte de la caja 2 se hace en eleventa** y la caja nueva toma ese corte. Por ahora, al intentar el corte o al
    salir, la caja nueva avisa que el corte se hace en eleventa.
  - **Devoluciones y cancelaciones de ida y vuelta:** la caja 2 devuelve tickets cobrados en eleventa, y lo que se
    devuelve o cancela en eleventa de un ticket de la caja 2 regresa a su base.
    - *Mientras tanto (2026-10-17; decidido, por revisar con el dueño):* hasta ver cómo anota eleventa la devolución de
      un ticket de otro turno o de otra caja, la caja 2 solo devuelve los del turno de eleventa de ahora; los demás se
      devuelven en eleventa y llegan solos con la lectura
      ([detalle](migracion-eleventa.md#lo-que-falta-para-el-piloto-diseño-2026-10-12-por-aprobar)).
  - **La regla de adaptación:** lo que solo sirve para convivir con eleventa se distingue de lo que se queda cuando se
    apruebe todo; lo primero vive en el puente y se quita al dejar eleventa.
  - **Al aprobar el piloto, el modo multicaja:** las dos cajas con la caja nueva, para que las dos se sigan
    acostumbrando. Antes hay que revisar las copias de seguridad que eleventa suele hacer al cerrarse.

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

**Avance (2026-10-16):** lo que faltaba del puente para el piloto, después del análisis de paridad
([diseño](migracion-eleventa.md#lo-que-falta-para-el-piloto-diseño-2026-10-12-por-aprobar)). Primera parte, en código:
la caja nueva adopta el turno que eleventa tiene abierto en la caja 2 y toma su corte (cierra el suyo como «corte hecho
en eleventa», con lo contado allá); lo que pasa en la caja nueva va al turno de eleventa de su hora, y lo de antes de un
corte que no alcanzó a llegar queda por revisar sin detener lo demás; y los folios repetidos: la venta toma el siguiente
justo al guardar y, si otra caja lo repite después, se reimprime con el folio de la caja y al devolver se elige entre los
dos tickets por la hora y el total (el ticket ya no lleva QR). Probado contra el Firebird de prueba.

Segunda parte, en código: las devoluciones de ida y vuelta. En la caja 2 se devuelve un ticket cobrado en eleventa, leído
en vivo (solo efectivo, 7 días y las reglas de la caja nueva): el dinero sale de la caja nueva y la devolución va a
eleventa por la bandeja. Un ticket de otra caja o de otro turno no se devuelve en la caja 2 sino en eleventa, hasta
ver en vivo cómo lo anota eleventa (2026-10-17, por revisar con el dueño). Lo que se devuelve o cancela en eleventa de
un ticket de la caja 2 entra a su venta con la lectura, sin efectivo (el corte de la caja nueva no lo cuenta), y lo
que quede por revisar se ve arriba en la pantalla de la caja 2. Lo raro que no pasa solo (el turno de eleventa se
cortó antes de mandarlo) se anota a mano allá, y al marcarlo aquí se elige cuál es su anotación: no se adivina.

Tercera parte (2026-10-17), en código: los respaldos de eleventa. Antes de cada escritura y en cada lectura, el puente
revisa que eleventa siga teniendo lo último que le mandó la caja 2 (sus tickets con su folio y su marca, el turno que
adoptó, y que sus contadores no hayan regresado); si no, eleventa se restauró: el puente deja de escribir y de aplicar
la lectura, la pantalla de la caja 2 lo dice, y solo se reanuda a mano, con «Configuración de la tienda» y en la
bitácora; lo que se perdió allá queda por revisar. Y antes de lo primero que escribe cada día, su propio respaldo de
eleventa con `gbak`, en la caja 2 y fuera de OneDrive, con 5 días de historia; sin él no se escribe (se puede quitar
esa condición en la configuración). Probado con un gbak de mentiras y contra el Firebird de prueba.

#### Al terminar el piloto

Cada parte entra sin detener a la caja 2:

1. **Terminar la venta** (lo que falta de la entrega 3) y **la base visual** (entrega 2), con lo que enseñen el piloto y
   las búsquedas guardadas.
2. **Los permisos por usuario** (entrega 1) y **la entrada y el corte** (entrega 6) ya están, desde el 2026-10-15 y el
   2026-10-14 ([avance](#primero-la-versión-unicaja-entrega-0)).
3. **Dejar eleventa:** el servicio nuevo en la caja principal y la caja 2 conectada a él, con su rendimiento (entrega 5:
   escaneo contra la copia local, cambios avisados, plazos y estados de la red); la red de la tienda arreglada; la
   impresora de tickets compartida con reintento; el día pico con el simulador; los reportes de ventas (entrega 9), que
   el dueño usa hoy en eleventa; la depuración del catálogo y el conteo físico. Eleventa queda de consulta
   ([lo que falta, en orden](paridad-eleventa.md#2-para-dejar-eleventa)).
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
| En el POS, por la red: que la caja 2 vuelva a buscar sola a la principal, avisos de lo configurado por nombre o con WSD, la franja «sin internet» o «sin red local» y la bitácora de red | [Red de la matriz](arquitectura.md#la-red-de-la-matriz) | En código (2026-10-17, [avance](#la-versión-unicaja-y-el-piloto-en-la-caja-2)): falta verlo en Windows y en la tienda |
| **Las tres impresoras de inyección** (dos WF-6590 y una L5590): cada una con sus costos, su cola en cada PC, su contador, su calibración y sus hojas por cobrar | [Impresión](impresion.md), [notas](notas.md#impresoras) | Falta (mediano); antes, leer la L5590 por SNMP |
| **La impresora de tickets compartida:** reintento si las dos cajas imprimen a la vez y botón «Reintentar». No hay cajón | [Notas](notas.md#impresoras) | En código (entrega 5, parte 3, 2026-10-16): la cola de cada caja; falta probarla en la tienda con las dos cajas |
| **Reportes de ventas:** día contra hace un año (el mismo día de la semana o la misma fecha: por decidir), mes y año a la fecha contra el año anterior, meses cerrados; venta neta, tickets, ticket promedio, costo, utilidad y margen; por departamento, caja, cajero y forma de pago; exportar a Excel y líneas en el resumen de Telegram; la utilidad solo la ve el dueño. El año anterior sale del historial de eleventa (su utilidad, con el costo capturado en eleventa) | [Notas](notas.md#reportes) | Pospuesto: después de la prueba con una persona real y su aprobación (2026-10-09) |
| **Ganancia real del mes:** el reporte (gastos fijos, consumibles del mes, recibo de luz, equipo con su depreciación y comisión de la tarjeta; utilidad bruta − mermas y faltantes − gastos = ganancia; punto de equilibrio y cierre del mes por Telegram). La captura de cada gasto cuando se paga no se pospone: propuesta, por aprobar, hacerla con la entrega 3 de la fase 3 (consumibles del mes), para todos los gastos | [Administración](#administración-y-punto-de-equilibrio), [servicios](servicios.md#tres-niveles-de-costo) | El reporte, pospuesto: después de la prueba con una persona real y su aprobación (2026-10-09) |
| Fusionar a `main`, en orden, los PR abiertos (#27 en adelante) | `main` llega hasta la entrega 2 de la fase 3 | Hecho el 2026-10-10: del #27 al #52, con el #45. En Windows pasan todas las pruebas |
| Probar en Windows con las dos cajas reales: servicio, alta de la caja 2, contingencia, respaldos, ventana de la caja y ticket desde las dos (el cajón, cuando lo haya) | Fase 1, entregas 1 y 3 | Falta: un sábado o domingo, sin tocar los datos de eleventa ([guía](pruebas-en-tienda.md#después-al-dejar-eleventa-las-dos-cajas-con-el-pos)) |
| Crear el bot de Telegram de verdad y probarlo | Fase 1, entrega 4 | Falta |
| Probar la impresión con la cola de laboratorio y después con la real | Fase 1, entrega 5 | Falta |
| Prueba con una persona real | [Ver arriba](#prueba-con-una-persona-real) | La primera fue el 2026-10-10, con la pantalla de hoy, y fue precaria (por eso el rediseño). La siguiente es el fin de semana del 17 y 18 de octubre, con la pantalla nueva lista; de su aprobación dependen los reportes |
| Medir un día pico (~120 tickets) con las dos cajas, en la caja principal real | El simulador de cajas (`simular-cajas`, [avance abajo](#para-el-piloto-con-dos-cajas)) y la [guía](pruebas-en-tienda.md#el-día-pico-en-la-caja-principal-real) | El simulador ya está (2026-10-17) y se midió en la PC de desarrollo; falta un fin de semana en la caja principal |
| Precios y clientes | [Fase 3](fase-3-precios-clientes.md) | Las entregas 1 y 2 ya están en `main`. Propuesta, por aprobar: que la entrega 3 (consumibles del mes) capture todos los gastos del mes cuando se pagan; el reporte de la ganancia va con los reportes |
| Servicios con costo real (engargolado y enmicado con depreciación, luz y consumibles; impresiones por impresora), pedidos y WhatsApp desde la PC. Apartados y cotizaciones guardadas quedan como mejora: el eleventa de la tienda no los tiene | Fase 4 ([servicios](servicios.md), [pedidos](pedidos.md), [WhatsApp](whatsapp.md)) | Falta su plan |
| Instalador de la tienda, soporte a distancia (NetBird gratis u otro) y cómo se actualiza | [Por definir](#pendientes) | Falta |
| Respaldo cifrado fuera de la tienda y vigilancia de la caja principal: se reporta a Healthchecks.io cada pocos minutos en horario, y si no abre o se apaga, Healthchecks.io avisa por Telegram (el bot del POS no puede avisar de su propia caja) | Fase 5 ([notas](notas.md#respaldo-fuera-de-la-tienda-y-aviso-de-apertura)) | Falta (chico) |
| Depuración, conteo físico, importación final y arranque | Fase 5 y [depuración](#depuración-del-inventario) | Falta |
| Promociones y kits, si el piloto queda cerca del regreso a clases | [Fase 3](fase-3-precios-clientes.md) | Pospuestas por ahora |

**Avance (2026-10-17): el simulador de cajas y el día pico en la PC de desarrollo.** Lo que pedían la fila del día pico
y la [prueba de las sucursales](prueba-sucursales-linux.md#lo-que-hay-que-arreglar-o-construir): un día pico con varias
cajas contra el servicio de verdad, como le hablan las cajas, también con la red caída. Es `simular-cajas`, en
Herramientas ([cómo se corre](../README.md#el-día-pico-con-varias-cajas-simulador); en la tienda,
`scripts\simular-dia-pico.cmd` con [su guía](pruebas-en-tienda.md#el-día-pico-en-la-caja-principal-real)). Es una
herramienta de prueba: no cambia nada del POS, y no corre contra un servicio que no sea de prueba ni contra uno en el
piloto con eleventa.
- **Las cajas son las de verdad, sin la pantalla:** cada caja simulada usa las clases de la caja (`ClienteCaja`; la
  contingencia con su latido, su aviso y su copia al día; su `caja-local.db` en una carpeta temporal; la foto del
  catálogo para escanear y buscar; sus dos conexiones y sus plazos) y le habla al servicio por HTTP, con la llave de su
  caja. Las da de alta con el PIN del dueño (o usa las que ya había), entra quien atiende y abre su turno.
- **El día:** tickets de 1 a 8 renglones de lo más vendido, con su cantidad; algunos buscados por nombre (letra por
  letra, contra la copia) y los rápidos; efectivo con billetes, tarjeta, transferencia y mixto; una entrada y dos salidas
  de efectivo por caja; cancelaciones (con el PIN del encargado) y devoluciones, también de tickets de la otra caja. Al
  40 % del día el dueño cambia el precio de lo más vendido mientras una caja lo tiene en el ticket (la caja principal
  rechaza ese cobro y se cobra otra vez con el precio nuevo); al 60 % llega un producto que nadie había dado de alta: la
  caja 1 lo da de alta ahí mismo y lo vende, y después la otra lo escanea con lo que le llegó a su copia. Al final, el
  corte Z de cada turno, contando lo que espera el corte. Las horas siguen las de la tienda, de 9:00 a 20:30 (más gente
  a mediodía y a la salida de la escuela), comprimidas en los minutos que se pidan; con la misma base sale el mismo día.
- **La caída** (`--caida 30s@50%`): corta la red de la caja 2 (la del Wi-Fi) como un interruptor: no conecta, y lo que
  iba a medias, también el aviso, se corta. La caja cobra con su copia, lo deja en su bandeja y lo sube al volver.
- **Qué mide:** lo que espera quien atiende (escanear, buscar, cobrar en línea y sin conexión), el latido, cuánto tarda un
  precio en llegar a la copia de cada caja, cuánto tarda en subir la bandeja al volver la red, abrir la caja y cada
  llamada a la caja principal por su ruta (la mitad, 19 de cada 20, el 99 %, la peor y cuánto pesa); y la memoria y el
  procesador del servicio. Lo compara con las [metas de la propuesta](propuesta-interfaz.md#metas) y las de
  [la prueba de las sucursales](prueba-sucursales-linux.md#qué-se-mide-y-cuándo-sale-bien).
- **El cuadre, al final:** cada venta que cobró cada caja está una sola vez en la caja principal, con su folio y su total,
  y no hay ninguna de más; los folios de cada caja, seguidos; lo hecho sin conexión subió una vez y la bandeja quedó
  vacía; el turno y el corte de cada caja (ventas, canceladas, tarjeta, transferencia y lo contado contra lo esperado) y
  el corte del día; las existencias bajaron lo vendido menos lo devuelto y son la suma de su kardex, en toda la base;
  ningún folio repetido; la copia de cada caja quedó al día. Si algo no cuadra, el informe dice qué y cuánto, y el
  comando sale con 1. La prueba automática (`SimuladorCajasTests`) corre 2 cajas × 10 tickets con la caída y revisa el
  cuadre, no los tiempos.
- **Medido en la PC de desarrollo** (i9-13900HX con Linux; el servicio en Release con una copia de la base ficticia de un
  año, 1,458 productos, y en la última columna con el catálogo copiado 4 veces, 5,832, como el de la tienda; 2 cajas ×
  120 tickets en 10 minutos; la PC no estaba sola, pudo tener compilaciones y pruebas de otras ramas a la par). Sin la
  pantalla: lo que tarda en pintarse se mide en la caja de Windows. 19 de cada 20 veces y, entre paréntesis, la peor:

  | Qué | Meta | Sin caída | Con la caída de 30 s | Catálogo ×4, con la caída |
  |---|---|---|---|---|
  | Escanear → la línea (contra la copia) | La mitad ≤ 16 ms, el 99 % < 50 ms | 0.06 ms (3.3 ms) | 0.07 ms (1.9 ms) | 0.06 ms (1.7 ms) |
  | Escribir → resultados (contra la copia) | < 100 ms | 0.97 ms (26 ms) | 0.95 ms (33 ms) | 1.5 ms (28 ms) |
  | Cobrar → listo, en línea | < 300 ms | 31 ms (404 ms¹) | 25 ms (412 ms¹) | 24 ms (268 ms¹) |
  | Cobrar → listo, sin conexión | < 300 ms | No se dio | 16 ms (2 cobros) | 82 ms¹ (2 cobros) |
  | El latido | < 100 ms | 1.3 ms (20 ms) | 2.6 ms (29 ms) | 1.3 ms (20 ms) |
  | Un precio → en la copia de cada caja | < 2 min | 0.2 s | 0.25 s | 0.63 s |
  | Volver la red → la bandeja arriba, en línea | < 2 min | No se dio | 3.5 s | 2.6 s |
  | Abrir la caja (bajar la copia completa y armar su foto) | < 5 s | 0.85 s | 1.1 s | 2.0 s |
  | Memoria del servicio: antes del día → al terminar | < 300 MB | 261 → 370 MB | 262 → 383 MB | 282 → 422 MB |

  ¹ La primera del día: lo que se usa por primera vez se compila (en el servicio recién arrancado o en la caja). El 99 %
  del cobro en línea: 84, 33 y 90 ms.
- **Cuadró en todas** (también en las de abajo): las 240 ventas, ninguna perdida ni duplicada; los folios del 1 al 120 en
  cada caja; lo cobrado sin conexión, arriba una sola vez; cada corte sin diferencia entre lo esperado y lo contado; el
  corte del día con sus 236 tickets; las existencias de los ~300 productos que se movieron bajaron lo vendido menos lo
  devuelto, y en toda la base cada existencia es la suma de su kardex. El servicio usó el 2 % de un núcleo.
- **Con la caída de 2 minutos a mediodía** (`--caida 2m@25%`, cuando hay más gente): la caja 2 trabajó sin conexión
  0.8 s después de caerse y cobró 30 tickets con su copia (19 de cada 20 en 21 ms). 6.9 s después de volver la red ya
  había subido sus 33 cosas (30 ventas, un movimiento de efectivo y dos de la bitácora), cada una una vez, y trabajaba en
  línea; las devoluciones que esperaban a la caja principal se hicieron al volver. El precio que el dueño cambió durante
  la caída le llegó en cuanto volvió (33 s después del cambio). Cuadró igual.
- **Lo que no cumple: la memoria del servicio.** La meta es menos de 300 MB y sin crecer de un día a otro (antes de la
  entrega 5 usaba ~190–230 MB en esta PC). Ahora empieza el día con 250–280 MB y lo termina con 370–420 MB (lo más, con
  el catálogo ×4). No es una fuga: con 1,200 tickets sin esperas llega a ~410 MB y de ahí no pasa, sube y baja con cada
  recolección. Lo que ocupa, según Linux: 120–226 MB la memoria propia del servicio (sobre todo los objetos de .NET),
  90–107 MB lo que .NET arma para el código que compila al vuelo y 67–79 MB sus bibliotecas. Con el recolector de
  memoria «de estación» en vez del «de servidor», el mismo día con la caída queda en 230 → 307 MB, con el cobro igual 19
  de cada 20 veces (27 ms) y más lento el 99 % (97 ms contra 33); compilado de antemano (ReadyToRun) no baja, y con 2
  núcleos tampoco. No se cambió: ni así llega a la meta, y tiene su costo. Primero se mide en la caja principal con el
  simulador, que lo dice; si allá pasa de 300 MB, lo primero es probar el recolector de estación, o revisar la meta, que
  se midió antes del catálogo en memoria y la búsqueda de la entrega 5.
- **Otras cosas que se vieron, sin meta propia:** lo que cambió pesa en promedio 0.6 KB por pedido (unos 470 en el día,
  entre las dos cajas); la copia completa, 1.1 MB (4.1 MB con el catálogo ×4), y bajarla con el día en marcha, 0.5–1.1
  s; de lo demás, lo más lento es el conteo cíclico del día al entrar a vender, ~200 ms. Con el catálogo ×4, un precio
  tarda más en llegar a la copia (0.6 s) porque la caja vuelve a armar el índice de su búsqueda, de fondo. La medición
  de la copia (`MedicionCopiaTests`) pedía lo que cambió sin decir qué versión de lo más vendido traía la caja, y cada
  «un precio» llegaba con lo más vendido completo (62 KB con el catálogo ×4); ya lo pide como la caja: 1.9 KB.
- **Falta:** correrlo en la caja principal real un sábado o domingo
  ([guía](pruebas-en-tienda.md#el-día-pico-en-la-caja-principal-real)), y si da tiempo con la caja 2 por su Wi-Fi; y
  para la prueba de las sucursales, la carga sostenida (varios días seguidos, para ver la memoria de un día a otro) y
  apagar el servicio a media venta, que el simulador no hace.

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

**Respuestas del dueño (2026-10-21):** la semana del 12 estuvo en una caída: desconectó el cable de red que va del módem
al Tenda, lo volvió a conectar después de un rato, con el internet todavía caído (volvió hasta el día siguiente), y la
red local no volvió a caerse; no es concluyente y es poco probable poder hacer más pruebas
([notas](notas.md#red-de-la-tienda)). Para seguir trabajando, la caja 2 tomó internet del Wi-Fi de un celular y la caja
principal de otro celular por cable USB, sin dejar su cable de red: por eso es indispensable que la caja 2 tenga su
propia copia (ya la tiene, entrega 5). Pidió analizar un solo celular para las dos; la caja principal no tiene Wi-Fi
([análisis](arquitectura.md#la-red-de-la-matriz), por probar en la tienda). La sucursal 2 empezará con una sola PC, así
que no tendrá la sincronización entre cajas de la matriz.

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

## Decidido mientras el dueño no estaba (2026-10-17)

El dueño (2026-10-17): «te quedarás un buen rato trabajando solo; si tienes preguntas, haz una sugerencia recomendada y
apruébala; cuando vuelva reviso todo». Esto se decidió así, con la opción recomendada, y queda **por revisar**:

| Pregunta | Decisión | Dónde |
|---|---|---|
| Si un ticket no salió y el cliente ya se fue, ¿solo «Reintentar»? | También «No imprimirlo»: la cola sigue y el ticket se reimprime después desde las ventas del día. Además evita un ticket doble cuando la impresora imprimió pero no lo confirmó | #108 |
| Después de una restauración de eleventa, lo que se perdió de la caja 2, ¿a mano o se vuelve a mandar solo? | A mano en eleventa: queda listado como por revisar. Mandarlo solo le daría folios nuevos, distintos a los impresos, quizá en turnos que allá ya no existen | Puente, parte 3 |
| Si en la caja 2 no hay `gbak.exe`, ¿se copia o se escribe sin el respaldo propio? | Se copia el de Firebird 2.0 de la caja principal, con su `fbclient.dll` (se ve en la tienda; [guía](pruebas-en-tienda.md)). El respaldo sigue siendo obligatorio para escribir | Puente, parte 3 |
| La entrega 9 (reportes) | Justo después de aprobar la prueba con una persona real, empezando por lo que hace el reporte de ventas de eleventa y la comparación contra el año anterior ([paridad](paridad-eleventa.md#recomendaciones)) | Entrega 9 |
| Dejar eleventa | En dos pasos: primero el modo multicaja con el puente escribiendo; al final, apagar el puente cuando el POS tenga la entrega 9 y un mes que cuadre con eleventa | Piloto |
| La ganancia del día, con Telegram en pausa | En el corte del día, para quien tiene «Ver costos, utilidad y margen», como el de eleventa | El corte del día como el de eleventa |
| Las tres impresoras de inyección | Van con la entrega 4, después de alcanzar a eleventa (meta del 2026-10-15); mientras, Control Print sigue cobrando aparte. Se alinearon la fase 5 y [impresión](impresion.md) | Plan |
| ¿Quién hace el corte de un turno? | Solo quien tiene el permiso, como ya funciona | Propuesta |
| ¿Quién cierra el corte del día y cuándo se recuerda? | Quien tiene «ver y cerrar el corte del día» (por omisión, la primera cuenta). Se recuerda a la hora de cierre de Configuración › Tienda | Propuesta |
| ¿Pantalla táctil? | No por ahora: todo con teclado y ratón, con botones de buen tamaño | Propuesta |
| ¿Pedir el PIN otra vez tras unos minutos sin usar la caja? | No por ahora, como eleventa: estorba para cobrar rápido. Ctrl+U cambia de usuario | Propuesta |
| Las tres partes del puente (turno y corte, devoluciones de ida y vuelta, respaldos) | En un solo PR: los arreglos de la revisión tocan las tres | Puente (#109) |
| Empezar y terminar la temporada, la hoja y las tarjetas (entrega 13, parte 3) | Todo en Productos (no en Ctrl+M); el precio de entonces es el del primer día de la temporada pasada; el sugerido nunca baja un precio; una tarjeta cuenta como impresa al oprimir «Imprimir»; las consultas cuentan solo en línea (90 días, las 30 más); lo contado se guarda en la principal al escribirlo (pide «Conteo completo de un departamento») y al terminar entra contra lo que decía el sistema al contarlo; el precio de entonces no toma el de mayoreo; en el piloto, sin «Contado» | #123 |
| Las fotos de los productos (entrega 13, parte 2) | En la base (van en los respaldos), JPEG de 240 px sin recortar, una por variante; las sube quien tiene «Productos, precios y etiquetas» desde el celular (/conteo) o la PC (ficha F3, también Ctrl+V); «Quitar» sin confirmación; se aceptan fotos JPG de cámara de hasta 100 megapíxeles y lo demás (PNG, WebP, JPG progresivo) hasta 16, preparadas de una en una | #122 |
| El mostrador y las temporadas (entrega 13, parte 1) | Se acomoda en Ctrl+M › «Acomodar» (no en F3) con «Productos, precios y etiquetas»; un solo acomodo para todas las cajas; lugares de inicio «Repisa de arriba/abajo» y «Mesa de la entrada»; con el panel abierto, un número de hasta 4 cifras y Enter es la cantidad, salvo que sea el código de un producto (entonces entra ese producto); «45*» siempre es cantidad | #121 |
| La importación final al dejar eleventa | A la base de la tienda que ya existe, solo el historial y sin los tickets que mandó el puente (por su número y su marca, nunca por el folio); lo dudoso se lista, no se resuelve; con la caja cerrada | #120 |
| Cambiar precios de varios a la vez | Con «Poner un precio» además de porcentaje y cantidad; mayoreo en proporción y redondeo a $0.50 por omisión; el redondeo nunca va en contra de lo pedido ni deja $0; el mayoreo siempre más barato que el menudeo (si el redondeo lo rompe, la escala baja al paso de abajo o se queda; si nada cabe, el producto no cambia); en el piloto, las escalas del POS de un producto de eleventa solo suben hasta abajo de los precios de eleventa; hasta 1,000 a la vez; aplicar con Ctrl+Enter o el botón; el cobro cobra siempre la escala más barata que alcanza | #119 |
| Qué hacer con el ticket al cobrar | «Imprimir» (por omisión), «Ofrecerlo» (nunca pregunta antes de cobrar: se ofrece con Ctrl+I hasta el siguiente escaneo) o «No imprimirlo»; para toda la tienda; lo fiado se imprime siempre; imprimirlo después con «Reimprimir» sigue pidiendo motivo; sin conexión, «No imprimirlo» lo ofrece como «Ofrecerlo» (reimprimir no se puede sin la principal) | #118 |
| La memoria del servicio (370–420 MB al final de un día pico en la PC de desarrollo; meta: 300 MB) | Medirla primero en la caja principal real con el simulador; si allá pasa de 300 MB, probar el otro modo de memoria de .NET (baja a ~307 MB, pero el cobro más lento sube de 33 a 97 ms) o ajustar la meta | #117 |
| La red de la tienda en el POS | La caja se cambia a la IP comprobada de la principal si la guardada (también por nombre) deja de contestar; en la principal sin internet ni router, la franja no dice «no desconectes nada»; en el piloto, ping a la PC de eleventa; «Volver a buscarla» solo con el ratón; el detalle del Wi-Fi apagado por omisión | #116 |
| La llave propia de la caja principal | Para que una copia de la base en otra PC no se haga pasar por ella. **Consecuencia:** si se reinstala o se cambia la PC principal (o se restaura en la de repuesto), hay que dar de alta otra vez cada caja con el PIN del dueño | #116 |
| El corte del día como el de eleventa | Las devoluciones del día se restan de la utilidad y de su departamento; recordatorio a la hora de cierre (21:00 si no se configura), sin recordatorio en el piloto; las ventas por departamento las ve quien ve el corte, la utilidad solo con «Ver costos»; un día que no se cerró antes de medianoche se queda «sin cerrar» (solo se cierra el de hoy) | #115 |
| Inventario como eleventa: lo que vale, movimientos y bajos | Sin exportar a Excel por ahora; mínimo y máximo por variante para todas las sucursales; costos solo con «Ver costos»; «Pedir» en piezas; cinco detalles más en el PR | #114 |
| Ventas de otros días e historial de cortes: quién ve qué, topes y detalles | Otro día u otra caja con «Ver reportes de ventas»; buscar un folio con su serie, cualquiera (solo el número, sin el permiso, busca en esta caja); el historial, quien hace cortes; ocho detalles más en el PR | #113 |
| En el piloto, ¿qué devoluciones hace la caja 2? | Solo las de tickets del turno de eleventa en curso de la caja 2, que el puente sabe escribir. Los de otro turno, otra caja o la principal se devuelven en el eleventa que está abierto en esa misma caja, y la devolución llega sola con la lectura. Así no hay que adivinar qué devolución anotada a mano en eleventa corresponde a cuál (tres revisiones encontraron casos que se perdían o se duplicaban). Cuando se vea en la tienda cómo anota eleventa una devolución de otro turno, el puente puede aprender a escribirla y la caja 2 tomarlas también | Puente (#109) |
| Una anotación a mano en eleventa que no cuadra exacto | «Resolverlo a mano» con «Configuración de la tienda» y nota en la bitácora; mientras se decide, la existencia sigue a eleventa (sin congelarla) | Puente (#109) |
| Al devolver en la caja 2 un ticket cobrado en eleventa, ¿el dinero al peso o exacto? | Exacto al centavo, como lo devuelve eleventa ($9.50, no $10): así el ticket de eleventa queda en cero y el cajón cuadra con lo que anota eleventa. Lo que cobró la propia caja se sigue devolviendo al peso, porque se cobró redondeado | Puente (#109) |

## Recomendaciones

Lo que se propuso y el dueño dejó para después. Se retoma cuando toque, sin volver a preguntar lo que ya se contestó.

| Recomendación | Por qué | Estado |
|---|---|---|
| **Pedir el PIN para administrar usuarios:** el de quien entró, una vez al abrir «Usuarios y permisos», válido mientras esa ventana siga abierta. Lo mismo para quitar una PC y aplicar la calibración de la impresora, que con la Configuración nueva (entrega 7) ya no piden PIN | El servicio confía en la caja para saber quién es el usuario: con la llave de una caja y el id de un usuario (las fichas lo dan) se le podría cambiar el PIN o los permisos a otro, por ejemplo al dueño, sin saber el PIN de nadie. La solución de fondo es que el servicio dé una sesión al entrar y la pida en cada llamada | No por ahora (dueño, 2026-10-15) |
| **En el piloto, las devoluciones en eleventa** (salvo las ventas de la caja 2 del turno en curso, que el puente escribe como eleventa): las demás se hacen en el eleventa abierto en la misma caja y la lectura las trae sin efectivo | Cuatro revisiones del puente siguieron encontrando fallas justo en devolver en la caja 2 tickets de otro turno y en anotarlas a mano en eleventa; lo simple ya está sólido. Contradice la decisión del 2026-10-12 (ida y vuelta), por eso no se cambió | Por decidir (2026-10-17) |
| **El encabezado del ticket con los datos de la tienda:** si el encabezado está vacío, armarlo con el nombre, la dirección y el teléfono de Configuración → Tienda | La dirección y el teléfono se guardan (entrega 7) pero no salen en ningún lado; el encabezado se escribe a mano y normalmente ya los trae | No por ahora (dueño, 2026-10-15) |

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
- [x] Instalarlo y dárselo a quien prueba: la primera prueba fue el 2026-10-10, con la pantalla de hoy
- [ ] La siguiente prueba, con la pantalla nueva lista, el fin de semana del 17 y 18 de octubre (decisión del dueño,
  2026-10-12)

Fase 3:

- [x] Revisar el [plan de la fase 3](fase-3-precios-clientes.md) (precios y clientes) y contestar sus preguntas
- [x] Mayoreo: el F11 de eleventa vende el paquete a su precio; pasa a escalas por cantidad que se aplican solas
  ([decisión](fase-3-precios-clientes.md#decisiones-de-la-revisión))
- [x] Entrega 1: mayoreo y redondeo al peso
- [x] Entrega 2: clientes registrados y crédito
- [ ] Entrega 3: consumibles del mes
- [ ] Promociones y kits: antes del próximo regreso a clases

Piloto en la caja 2 ([ver arriba](#la-versión-unicaja-y-el-piloto-en-la-caja-2)):

- [x] Paso 1: leer eleventa cada pocos minutos (PR #64 y #65; probado en la tienda, solo leyendo, el 2026-10-11)
- [x] Paso 2: cómo escribe eleventa una venta (PR #66; visto en vivo en la tienda, solo leyendo)
- [x] Paso 3: mandar cada venta con la bandeja de salida (PR #67 y #69), en código; todavía no se escribe en el eleventa
  real
- [x] Paso 4: cancelaciones, devoluciones, turno, corte y entradas y salidas (PR #70, #72 y #73), en código
- [x] Los riesgos verificados el 2026-10-12: el cobro espera el último escaneo y «Pagó con» no toma escaneos como
  dinero; solo efectivo en la caja 2; la lectura de eleventa respeta lo que sigue en la bandeja (PR #82, #83 y #85)
- [x] Paso 6, la entrega 10: la base contra apagones (PR #84); el servicio de la caja 2 la respalda cada día
- [ ] Paso 6, lo que falta: cómo se actualiza la caja 2 del piloto con un arreglo, sin perder su base ni la
  configuración del puente
- [x] Eleventa abierto en la caja 2 en el mismo turno (adoptar su turno y tomar su corte) y los folios repetidos
  (2026-10-16, en código; falta probarlo contra la copia en el paso 5)
  ([diseño](migracion-eleventa.md#lo-que-falta-para-el-piloto-diseño-2026-10-12-por-aprobar))
- [x] Devoluciones y cancelaciones de ida y vuelta con eleventa (2026-10-16, en código; falta ver en vivo la de otra
  caja o de otro turno, que mientras se devuelve en eleventa, y probarlo contra la copia en el paso 5)
- [x] Los respaldos de eleventa: detectar una restauración y el respaldo propio con `gbak` (2026-10-17, en código; falta
  ver en la tienda dónde está `gbak.exe` en la caja 2 y probarlo contra la copia en el paso 5)
- [ ] Antes de vender en la caja 2: revisar en F3 el «Mayoreo por revisar». El F11 de eleventa (el segundo precio que
  el cajero pone a mano) no existe en la caja nueva: lo que no tenga escala se cobra a menudeo
  ([paridad](paridad-eleventa.md#venta-y-cobro))
- [ ] En la tienda, mirar lo que la [paridad con eleventa](paridad-eleventa.md#qué-mirar-en-la-tienda) marca «por
  confirmar»
- [ ] Paso 5: semanas de prueba contra una copia servida por un Firebird 2.0.1 como el de la tienda
- [ ] Paso 7: el fin de semana del 17 y 18 de octubre
  ([guía](pruebas-en-tienda.md#fin-de-semana-del-17-y-18-de-octubre))
- [ ] La primera escritura en el eleventa real, con el visto bueno del dueño
- [ ] Paso 8: enseñar a quien atiende la caja 2
- [ ] Al aprobar el piloto: el modo multicaja (decisión del dueño, 2026-10-12)

Piloto con dos cajas ([ver arriba](#para-el-piloto-con-dos-cajas)):

- [ ] Primer fin de semana en la tienda: conocer la red, las impresoras, los grabadores y el equipo; solo mirar y
  anotar, salvo los grabadores y, si el dueño lo aprueba, el script y el registro Dhcp-Client
  ([guía](pruebas-en-tienda.md#primer-fin-de-semana-conocer-la-tienda))
- [ ] Script de diagnóstico de red, probado antes en una PC con Windows, corriendo en las dos cajas si el dueño lo
  aprueba, con su ícono para el personal en la caja 2 y la instrucción ([qué registra](notas.md#red-de-la-tienda))
- [ ] Segundo fin de semana, si el dueño lo aprueba: arreglar la red
  ([guía](pruebas-en-tienda.md#segundo-fin-de-semana-arreglar-la-red))
- [x] En el POS, por la red: la caja 2 vuelve a buscar a la principal, avisos de nombres o WSD, la franja de «sin
  internet» y la bitácora de red (en código, 2026-10-17; falta verlo en Windows y en la tienda)
- [ ] Las tres impresoras de inyección en el módulo de impresión
- [x] Impresora de tickets compartida: reintento y botón «Reintentar» (la cola de la caja, entrega 5, parte 3), en
  código; falta probarla en la tienda con las dos cajas
- [ ] Corregir Telegram para que cumpla «solo el dueño»: rechazar el vínculo desde un grupo y revisar quién aprieta el
  botón (hoy se autoriza por chat; [notas](notas.md#reportes))
- [ ] Después de la prueba con una persona real y su aprobación: reportes de ventas por periodo contra el año anterior
- [ ] Después de la prueba con una persona real y su aprobación: el reporte de la ganancia real del mes y el punto de
  equilibrio
- [ ] Capturar cada gasto cuando se paga (propuesta, por aprobar: con la entrega 3 de la fase 3)
- [ ] Probar en Windows con las dos cajas reales: servicio, alta de la caja 2, contingencia, respaldos, ventana de la
  caja y ticket desde las dos (el cajón, cuando lo haya)
- [ ] Medir un día pico (~120 tickets) con las dos cajas, en la caja principal real, con el simulador de cajas (ya está,
  2026-10-17: `scripts\simular-dia-pico.cmd`, [guía](pruebas-en-tienda.md#el-día-pico-en-la-caja-principal-real))
- [ ] Departamentos en el POS: darlos de alta, cambiarles el nombre y desactivarlos. Hoy solo llegan de eleventa, y
  Compras pide darlos de alta «en F3 Productos», donde no se puede ([paridad](paridad-eleventa.md#productos-y-precios))
- [x] La importación final sin las ventas que ya mandó el puente (por `VENTATICKETS.ID` y su marca), para no contarlas
  dos veces en los reportes (2026-10-17, en código: `importar-eleventa --base`, se puede repetir y dice qué hizo;
  [cómo](migracion-eleventa.md#la-importación-final)). Lo que no se decide solo (una venta de la caja 2 anotada a mano en
  eleventa, una devolución que no llegó a uno de los dos lados) queda en una lista para revisarlo a mano
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
- [ ] Revisar los productos con el mismo nombre: pueden ser el mismo dado de alta dos veces o productos distintos, y
  varios tienen precio distinto (la lista, con los datos reales, está fuera del repositorio, junto a la copia de
  eleventa, en `depuracion-pendientes.md`)
- [ ] Para que las familias de la búsqueda salgan completas (análisis de los nombres, 2026-10-11):
  - corregir los errores de dedo (la lista está en el mismo archivo);
  - escribir el surtido de una sola forma (hoy hay más de diez) y las medidas igual («1.0 mm», «250 ml», «#9», «C/12»);
  - llenar la marca de cada producto, con una sola escritura;
  - pasar «pieza o paquete» y «por metro» del nombre a presentaciones y mayoreo.

Por definir:

- [x] Corte Z de la caja 2 durante el piloto: se hace en eleventa y la caja nueva toma ese corte; antes de que eleventa lo
  haga, al intentar el corte o al salir, la caja nueva avisa que el corte se hace en eleventa (2026-10-12; la caja ya
  toma ese corte desde el 2026-10-16,
  [diseño](migracion-eleventa.md#lo-que-falta-para-el-piloto-diseño-2026-10-12-por-aprobar))
- [x] Revisar la [propuesta de rediseño de la interfaz](propuesta-interfaz.md) con su prototipo: aprobada el 2026-10-10
- [x] Las preguntas de la venta para la entrega 0 (2026-10-10): rápidos Copia B/N ($2) y CURP ($10, 10 impresiones);
  sin billetes rápidos (se escribe lo que dio); sin sonidos por ahora
- [x] Para el piloto en la caja 2 (2026-10-10): las altas y los cambios de precio se siguen haciendo en eleventa, y el
  ticket de la caja 2 lleva el folio de eleventa ([piloto](#la-versión-unicaja-y-el-piloto-en-la-caja-2))
- [ ] Contestar las demás [preguntas de la propuesta](propuesta-interfaz.md#preguntas-para-el-dueño) antes de cada
  entrega: antes de la 1, las de los permisos (el corte del turno, el PIN tras unos minutos); antes de la 4, bandeja o
  tarjeta para las impresiones
- [ ] Probar el lector en la tienda: qué manda con los códigos de 11 cifras y su ritmo al teclear
- [ ] Instalador de la tienda para el administrador: servicio, cajas, firewall, carpeta de la caja con permisos y las
  colas de las impresoras de inyección ([detalle](#instalación-y-usuarios-de-windows)). Hoy son los scripts de
  `instalacion/`
- [ ] Si eleventa se restaura a un respaldo durante el piloto: lo que la caja 2 le había mandado y se perdió allá, ¿se
  anota a mano en eleventa (así quedó, por revisar en la pantalla de la caja 2) o la caja 2 lo vuelve a mandar sola, con
  folios nuevos? ([respaldos](migracion-eleventa.md#lo-que-falta-para-el-piloto-diseño-2026-10-12-por-aprobar))
- [ ] La importación final no quita ni aplica nada por adivinar: una venta de la caja 2 que se anotó a mano en eleventa
  (allá no lleva la marca), o una devolución que no llegó a uno de los dos lados, queda en una lista para revisarla a mano
  (2026-10-17). ¿Hace falta poder quitar a mano del historial el ticket de eleventa de una venta así?
  ([importación final](migracion-eleventa.md#la-importación-final))

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
