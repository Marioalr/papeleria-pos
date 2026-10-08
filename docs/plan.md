# Papelería POS — Plan

> **Lluvia de ideas.** Las decisiones de abajo son el punto de partida y pueden cambiar mientras se cierra el plan.

## Contexto

- Papelería con servicios: impresiones, copias, engargolado, enmicado, recargas, pago de servicios y trámites
  (CURP, actas…).
- Hoy: **1 sucursal con 2 cajas** (se planea una 3.ª). **2.ª sucursal en camino** y una 3.ª en unos dos años:
  **solo serán 2 sucursales durante al menos un año.**
- Unos **60 tickets al día**, con picos de ~120. La temporada de regreso a clases pesa mucho.
- Inventario de ~500 mil MXN a costo, que creció 10 veces en 4 años; margen bruto anual de ~65 %.
- Se usa **eleventa multicaja** (sin el módulo de clientes).
- Un número de WhatsApp Business por sucursal, en un celular (también para llamadas); el grueso del trabajo se hace
  desde la PC.
- Las cajas principales se apagan de noche; las sucursales probablemente tendrán horarios distintos.
- **Cámaras Dahua con grabador en las dos sucursales,** que hoy se ven con la app DMSS ([cámaras](camaras.md)).
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
| Central | **En revisión.** Propuesta: **servidor virtual** siempre prendido (Vultr Ciudad de México, 1 o 2 GB según la prueba, 107.87 o 215.74 MXN al mes) antes de que la sucursal 2 pase al POS, para administrar ante un suceso, precios por sucursal y cualquier flujo de mercancía ([propuesta](servidor-remoto.md)). Antes se ensaya en una máquina propia con Linux ([prueba](#prueba-de-varias-sucursales-con-un-servidor-linux)) |
| Base de datos | SQLite en todas partes; en cada lugar el servicio es el único que escribe |
| Núcleo | Sin dependencias de Windows: lo comparten la caja, los servidores y la app Android |
| Inventario | Kardex: solo se agregan movimientos y la existencia es la suma |
| IDs, dinero y fechas | UUIDv7, centavos enteros, UTC; folios legibles por caja (A-000123) |
| Contingencia | Cada caja guarda copia del catálogo y una bandeja de salida: si la caja principal se apaga, las demás siguen vendiendo |
| Roles | Dueño, encargado y cajero; lo que puede hacer cada uno es **configurable** |
| Clientes | El celular solo identifica un **pedido** al recogerlo. **Clientes registrados**: pocos, los de confianza |
| Impresión | Lo que hace Control Print se reescribe como módulo del POS ([detalle](impresion.md)) |
| WhatsApp | API oficial en modo coexistencia; nunca librerías no oficiales. Antes de eso, captura manual desde la PC |
| Alertas y autorización remota | Bot de Telegram, desde el piloto. Propuesta con el central: un solo bot en el central, con foto del suceso y botones; si el dueño no lo ve, alerta que suena en silencio (Pushover) y llamada (Twilio); acciones a distancia con PIN o huella ([propuesta](servidor-remoto.md)) |
| Panel del dueño | Servido por el central, en internet con llave de acceso (passkey) y dominio propio; las cámaras en vivo, por una red privada (NetBird) ([propuesta](servidor-remoto.md)) |
| Cámaras | Las de cada sucursal se cruzan con el sistema: texto de cada suceso sobre el video, foto y clip del momento, y vigilancia de noche, con un puente siempre prendido por tienda (Raspberry Pi) ([cámaras](camaras.md)) |
| App Android | .NET MAUI con la misma interfaz, sin publicar en la tienda |
| Fiscal | Fuera de alcance, pero se guardan clave SAT, unidad y desglose de impuestos para poder facturar después |
| Nube | El central, en un servidor virtual siempre prendido ([central](servidor-remoto.md)); antes de contratarlo, unas horas en uno que se cobra por hora ([prueba](prueba-sucursales-linux.md)). Azure for Students no sirve: es solo para estudiantes y para uso educativo no comercial |
| Código | La fase 0 ya se programa; las demás fases, hasta cerrar su parte del plan |
| Idioma del código | Dominio en español (`Producto`, `Existencia`, `Corte`); lo técnico en inglés |
| Acceso a datos | EF Core con migraciones; SQL directo donde convenga (kardex, reportes) |
| Pruebas | xUnit |
| Datos reales | Nunca entran al repositorio. Viven fuera de él, en `Documents\TPV` de la máquina de desarrollo |
| Migración | El inventario **se depura** al pasar al POS nuevo: el catálogo y las existencias de eleventa son punto de partida, no se conservan tal cual. Las existencias de arranque salen del conteo físico |
| Diagnóstico | Se entrega en Excel, una hoja por análisis |
| Precios | Precio al público con IVA incluido; el desglose de impuestos se guarda aparte. Precio por unidad base: el paquete cuesta las piezas que trae, salvo excepción. Por sucursal: iguales el primer día, después se ajustan |
| Impuestos | IVA 16 %, 0 % o exento; sin IEPS |
| Cantidades | Se vende por metro y a granel: enteros en milésimas de la unidad base |
| Marca | Catálogo propio de marcas: distingue productos parecidos y sirve para buscar |
| Variantes | Texto libre ("Azul") en la fase 0; con atributos (Color, Tamaño) más adelante |
| Costo promedio | Por sucursal |
| Formas de pago | Efectivo, tarjeta (terminal externa) y transferencia (por verificar). Hoy casi todo es efectivo |
| Impresora de tickets | POS-8360, térmica de 80 mm, ESC/POS; el ticket conserva el formato y el logo del de eleventa |
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
| Central en la nube con PostgreSQL, luego en la caja principal de la matriz | Servidor virtual con SQLite | Las cajas se apagan de noche, los horarios serán distintos y podría haber vigilancia nocturna |
| Integrar Control Print por API | Reescribirlo como módulo del POS | Fue una prueba de concepto; así hay un solo inicio de sesión, base e instalador |
| Niveles de confianza automáticos por celular | Celular solo para el pedido; clientes registrados a mano | No guardar números de más |

## Fases

Sin tiempos: se avanza cuando la fase anterior funciona en la tienda.

| Fase | Contenido |
|---|---|
| 0. Base e importación | Modelo de datos probado con datos y productos **ficticios**; al final, importación de prueba desde eleventa y diagnóstico con los datos reales ([detalle](#fase-0)) |
| 1. Caja | Venta, cobro mixto, ticket, turnos y corte ciego, roles y autorizaciones, bitácora, contingencia, Telegram, módulo de impresión |
| 2. Inventario | Compras, recepción ciega, presentaciones, conteos cíclicos, etiquetas, sugerido de compra |
| 3. Precios y clientes | Mayoreo, promociones, kits, clientes registrados, crédito |
| 4. Servicios y pedidos | Recetas y costeo, pedidos, cola compartida, charolas, recogida por celular, duplicados, apartados, listas escolares, captura manual desde WhatsApp |
| 5. Piloto en la matriz | Depuración del inventario, conteo físico inicial, arranque, eleventa como respaldo. Propuesta: desde el arranque, respaldo cifrado en Backblaze B2, aviso de apertura en Healthchecks.io y el puente de la matriz (el dueño ya ve sus cámaras sin abrir puertos) |
| 6. Central y WhatsApp | Propuesta, antes de la 7 ([entregas](servidor-remoto.md#entregas)): 6.1 central y subida; 6.2 bajada y precios por sucursal; 6.3 sucesos, avisos y acciones a distancia; 6.4 mercancía (traspasos, compras en cualquier sucursal y repartidas); 6.5 cámaras y contador; 6.6 ensayo largo. La API de WhatsApp → pedidos va cuando el dueño la quiera, después de 6.2 |
| 7. Sucursal 2 | Su puente, sus avisos y su horario; precios iguales el día 1; inversión de apertura y punto de equilibrio. Mínimo para pasarla al POS: de 6.1 a 6.5 y la etapa 2 de la [prueba](prueba-sucursales-linux.md). Si abre antes, abre con eleventa; su puente solo sirve para ver en vivo |
| 8. App Android | Preventa y pedidos desde el celular |
| 9. A la medida | Listas escolares avanzadas, dispositivos propios, [piloto automático](ideas.md#asistente-inteligente-piloto-automático) (se apoya en lo de la fase 6) y lo que se vaya describiendo |

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
- **Gastos fijos del mes** por sucursal: renta, sueldos, luz, internet. Son los mismos gastos que se reparten en la
  [utilidad neta de los servicios](servicios.md#dos-utilidades).
- **De las ventas:** la utilidad bruta de cada mes (venta − costo de lo vendido), que ya sale del POS.
- **Insumos del negocio:** lo que la tienda gasta pero no vende, comprado cuando se necesita: un rollo de papel
  térmico a medio mes, cinta, bolsas, artículos de limpieza, tóner de la impresora de tickets. Se registran con
  fecha, sucursal, concepto, categoría, monto y comprobante, y entran como gasto del mes en la ganancia mensual.
  - Si se pagan con el efectivo de la caja, la salida F8 lleva la categoría "Insumo" y queda registrado solo, sin
    capturarlo dos veces.
  - Los materiales de los servicios (espirales, micas, papel para copias) no son insumos: están en el inventario y se
    descuentan con la [receta](servicios.md#receta-de-un-servicio). Así no se cuentan dos veces.

Con eso, un reporte por sucursal:

| Indicador | Cómo se calcula |
|---|---|
| Punto de equilibrio mensual | Venta necesaria para cubrir los gastos: (gastos fijos + insumos) ÷ margen bruto |
| Ganancia del mes | Utilidad bruta − gastos fijos − insumos |
| Recuperación de la inversión | Mes en que la suma de las ganancias alcanza la inversión de apertura |
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
  Actions (Windows), porque la PC de desarrollo es Linux. Como en la tienda, lo instala el administrador para todos
  los usuarios de la PC y quien prueba lo usa con una cuenta común de Windows ([instalación](#instalación-y-usuarios-de-windows)).
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
  - En la PC de la WF-6590, su cola con "Conservar documentos impresos" (cambiarlo pide administrador).
- **Lo que se hace desde la cuenta común de Windows:** vender y todo lo de la caja, dar de alta la caja (pide el PIN
  del dueño, no administrador), trabajar sin conexión, cotizar e imprimir.
- **Dónde se escribe:** la base de la sucursal la escribe solo el servicio. Lo de cada caja (su configuración y la
  copia para trabajar sin conexión) va en `C:\ProgramData\PapeleriaPos\Caja`, que el instalador deja con escritura para
  las cuentas de Windows: sirve igual si la dio de alta el administrador o si la PC tiene otra cuenta común.
- **La WF-6590:** el agente lee los trabajos del usuario que tiene abierta la caja. Si alguien imprime desde otra
  cuenta de Windows, esa cuenta necesita "Administrar documentos" en la cola (lo da el administrador).
- **La prueba con una persona real** se instala igual: el administrador la instala para todas las cuentas de Windows
  y quien prueba la usa con una cuenta común.

### Varias sucursales: el central

**Propuesta en revisión** ([propuesta](servidor-remoto.md), revisada el 2026-10-08 con las respuestas del dueño y
precios vigentes). El dueño rechazó empezar sin central: necesita administrar ante un suceso a cualquier hora, con las
cámaras y el contador, precios por sucursal y que el POS resista cualquier flujo de mercancía, con la mejor
calidad-precio.

- **Cada sucursal vende sola,** con su servidor en la caja principal; ninguna venta pasa por el central.
- **El central,** en un servidor virtual (Vultr Ciudad de México, 215.74 MXN al mes el de 2 GB, o 107.87 el de 1 GB si
  la prueba muestra que alcanza), antes de que la sucursal 2 pase al POS: sincronización en los dos sentidos, precios
  por sucursal, traspasos y compras en cualquier sucursal, sucesos, avisos y acciones a distancia, panel y un solo bot.
- **Un puente en cada tienda** (Raspberry Pi) para las [cámaras](camaras.md): la foto y el clip del momento, la
  vigilancia de noche, el reloj de la tienda y el contador. La caja escribe el texto de cada suceso sobre el video.
- **Avisos que escalan:** Telegram con foto y botones, alerta que suena en silencio y llamada.
- **Costo:** ≈287–309 MXN al mes ya instalado; ≈9,400–9,700 el primer año (más los no-break si hacen falta).

### Prueba de varias sucursales con un servidor Linux

El central correrá en Linux. Antes de contratar el servidor virtual se ensaya en una máquina que ya hay: la laptop con
Intel Celeron o la PC con AMD A6, las dos con 8 GB. Que sean modestas sirve: si el procesador no es muy viejo (se
confirma con su modelo), por núcleo rinden algo menos que el servidor virtual (un Celeron, unos dos tercios; un A6,
parecido), así que si el central responde bien ahí, allá irá igual o mejor. La máquina con Linux se limita a 1 núcleo y
2 GB, como el plan elegido; la otra se queda con Windows y hace de sucursal 2 ([detalle](prueba-sucursales-linux.md)):

| Etapa | Cuándo | Qué prueba |
|---|---|---|
| 0. Conocer las máquinas | Ya | Procesador, disco y Windows de cada una: si sirven para .NET y cuál queda con Windows |
| 1. Arreglos para Linux | Ya, en paralelo a la fase 3 | Lo que hoy solo funciona en Windows y el simulador de cajas. Si el dueño quiere, medir ya cómo responde la máquina con Linux (con una sola sucursal) |
| 2. Dos sucursales con central | Entregas 6.1 a 6.4, después del piloto | El central en Linux con la matriz y la sucursal 2: subida y bajada, precios por sucursal, mercancía, acciones y avisos. Al cerrar 6.1, unas horas en un servidor virtual de verdad y se contrata el central |
| 3. El puente con el grabador real | Fase 5 y entrega 6.5 | La Raspberry Pi contra el grabador de la matriz: foto, eventos de personas, reloj y red privada |
| 4. Ensayo largo | Entrega 6.6 | Dos a cuatro semanas con fallas provocadas y simulacro de noche |

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
- [x] Entrega 3: varias cajas (falta instalarla en la caja principal y probarla con las tres cajas)
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

Prueba con una persona real (al cerrar la entrega 2 de la fase 2, [ver arriba](#prueba-con-una-persona-real)):

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

Varias sucursales y la prueba en Linux ([ver arriba](#varias-sucursales-el-central)):

- [ ] Revisar la [propuesta del central](servidor-remoto.md), la de [cámaras](camaras.md) y el
  [plan de la prueba](prueba-sucursales-linux.md), y contestar sus preguntas
- [ ] Ya: configurar los grabadores de las dos sucursales ([lista](camaras.md#configurar-el-grabador)); abrir las
  cuentas a nombre del negocio y registrar el dominio antes del 31 de octubre
- [ ] Arreglos previos al central y para Linux (fase 3), y el simulador de cajas
- [ ] (Si el dueño quiere) Medir ya cómo responde la máquina con Linux: día pico, techo, cortes de luz y 72 horas, con
  1 núcleo y 2 GB
- [ ] Antes del piloto: respaldo cifrado de la matriz en Backblaze B2, con una restauración de ensayo, su aviso en
  Healthchecks.io y el puente de la matriz
- [ ] Entregas 6.1 a 6.6 del [central](servidor-remoto.md#entregas), con las etapas 2 a 4 de la prueba
- [ ] Al cerrar 6.1: unas horas del script del central en un servidor por hora, decidir entre 1 y 2 GB y contratar el
  central (al principio, solo con la matriz)

Depuración del inventario (antes del piloto, [ver abajo](#depuración-del-inventario)):

- [ ] Dar de baja los productos que ya no existen
- [ ] Sacar del inventario el equipo de la tienda (impresoras, engargoladoras, enmicadoras, mobiliario) y registrarlo como [inversión](#administración-y-punto-de-equilibrio)
- [ ] Juntar los productos capturados dos veces (los que marca el diagnóstico)
- [ ] Corregir unidades: los productos a granel que eleventa tiene en KG (listón, micas, hojas sueltas…)
- [ ] Pasar a servicios lo que eleventa vende como producto a granel (impresiones, copias, engargolado, escaneo)
- [ ] Conteo físico completo: reemplaza todas las existencias de eleventa

Por definir:

- [ ] Instalador de la tienda para el administrador: servicio, cajas, firewall, carpeta de la caja con permisos y la
  cola de la WF-6590 ([detalle](#instalación-y-usuarios-de-windows)). Hoy son los scripts de `instalacion/`

- [ ] Si la sucursal 2 abre antes de la fase 7: guardar la lista de gastos de apertura con comprobantes
- [ ] Administración: insumos del mes (papel térmico, cinta, limpieza…) para la ganancia mensual
  ([detalle](#administración-y-punto-de-equilibrio)); el dueño lo pasó a la fase 3 al revisar la fase 2 (es la
  [entrega 3](fase-3-precios-clientes.md#3-consumibles-del-mes), «Consumibles del mes»)
- [ ] Módulo de convivencia: el POS nuevo como una caja más de eleventa durante la adaptación
  ([idea](migracion-eleventa.md#convivencia-con-eleventa-idea-a-evaluar))
- [ ] Demás funciones a la medida (las describe el dueño)
- [x] Privilegios de los clientes registrados: descuento, mayoreo, crédito, pedido sin anticipo y apartar sin pagar;
  nada más ([decisión](fase-3-precios-clientes.md#decisiones-de-la-revisión))
- [ ] Regla para pedidos de no registrados: monto máximo que se prepara sin anticipo, y días para borrar el celular
- [ ] WhatsApp: ver [pendientes](whatsapp.md#pendientes)
- [ ] Dispositivos propios: ver [ideas](ideas.md)
