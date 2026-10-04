# Papelería POS — Plan

> **Lluvia de ideas.** Las decisiones de abajo son el punto de partida y pueden cambiar mientras se cierra el plan.

## Contexto

- Papelería con servicios: impresiones, copias, engargolado, enmicado, recargas, pago de servicios y trámites
  (CURP, actas…).
- Hoy: **1 sucursal con 2 cajas** (se planea una 3.ª). **2.ª sucursal en camino** y una 3.ª en unos dos años.
- Unos **60 tickets al día**, con picos de ~120. La temporada de regreso a clases pesa mucho.
- Inventario de ~500 mil MXN a costo, que creció 10 veces en 4 años; margen bruto anual de ~65 %.
- Se usa **eleventa multicaja** (sin el módulo de clientes).
- Un número de WhatsApp Business por sucursal, en un celular (también para llamadas); el grueso del trabajo se hace
  desde la PC.
- Las cajas principales se apagan de noche; las sucursales probablemente tendrán horarios distintos.
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
| Central | **Servidor virtual** siempre prendido (Vultr CDMX, 5 USD/mes) desde que abra la sucursal 2; en el piloto no hace falta ([propuesta](servidor-remoto.md)) |
| Base de datos | SQLite en todas partes; en cada lugar el servicio es el único que escribe |
| Núcleo | Sin dependencias de Windows: lo comparten la caja, los servidores y la app Android |
| Inventario | Kardex: solo se agregan movimientos y la existencia es la suma |
| IDs, dinero y fechas | UUIDv7, centavos enteros, UTC; folios legibles por caja (A-000123) |
| Contingencia | Cada caja guarda copia del catálogo y una bandeja de salida: si la caja principal se apaga, las demás siguen vendiendo |
| Roles | Dueño, encargado y cajero; lo que puede hacer cada uno es **configurable** |
| Clientes | El celular solo identifica un **pedido** al recogerlo. **Clientes registrados**: pocos, los de confianza |
| Impresión | Lo que hace Control Print se reescribe como módulo del POS ([detalle](impresion.md)) |
| WhatsApp | API oficial en modo coexistencia; nunca librerías no oficiales. Antes de eso, captura manual desde la PC |
| Alertas y autorización remota | Bot de Telegram, desde el piloto |
| Panel del dueño | Servido por el central; acceso por Tailscale |
| App Android | .NET MAUI con la misma interfaz, sin publicar en la tienda |
| Fiscal | Fuera de alcance, pero se guardan clave SAT, unidad y desglose de impuestos para poder facturar después |
| Nube | Azure for Students solo para pruebas |
| Código | La fase 0 ya se programa; las demás fases, hasta cerrar su parte del plan |
| Idioma del código | Dominio en español (`Producto`, `Existencia`, `Corte`); lo técnico en inglés |
| Acceso a datos | EF Core con migraciones; SQL directo donde convenga (kardex, reportes) |
| Pruebas | xUnit |
| Datos reales | Nunca entran al repositorio. Viven fuera de él, en `Documents\TPV` de la máquina de desarrollo |
| Migración | El inventario **se depura** al pasar al POS nuevo: el catálogo y las existencias de eleventa son punto de partida, no se conservan tal cual. Las existencias de arranque salen del conteo físico |
| Diagnóstico | Se entrega en Excel, una hoja por análisis |
| Precios | Precio al público con IVA incluido; el desglose de impuestos se guarda aparte. Precio por unidad base: el paquete cuesta las piezas que trae, salvo excepción |
| Impuestos | IVA 16 %, 0 % o exento; sin IEPS |
| Cantidades | Se vende por metro y a granel: enteros en milésimas de la unidad base |
| Marca | Catálogo propio de marcas: distingue productos parecidos y sirve para buscar |
| Variantes | Texto libre ("Azul") en la fase 0; con atributos (Color, Tamaño) más adelante |
| Costo promedio | Por sucursal |
| Formas de pago | Efectivo, tarjeta (terminal externa) y transferencia (por verificar). Hoy casi todo es efectivo |
| Impresora de tickets | POS-8360, térmica de 80 mm, ESC/POS; el ticket conserva el formato y el logo del de eleventa |
| Inversión por sucursal | Se registra lo que cuesta abrir cada sucursal para saber cuándo alcanza el punto de equilibrio ([detalle](#inversión-y-punto-de-equilibrio)) |
| Git | Ramas cortas por tema, fusionadas a `main` |

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
| 5. Piloto en la matriz | Depuración del inventario, conteo físico inicial, arranque, eleventa como respaldo |
| 6. Central y WhatsApp | Servidor virtual, sincronización, panel del dueño, API de WhatsApp → pedidos |
| 7. Sucursal 2 | Precios por sucursal, traspasos, compras que se reparten, inversión de apertura y punto de equilibrio |
| 8. App Android | Preventa y pedidos desde el celular |
| 9. A la medida | Listas escolares avanzadas, dispositivos propios y lo que se vaya describiendo |

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

### Inversión y punto de equilibrio

Para decidir la tercera sucursal con números reales, no con intuición: cuánto costó abrir la segunda y cuándo se
pagó sola.

- **Inversión de apertura** por sucursal, una sola vez: mobiliario, equipo (impresoras, computadoras, engargoladoras),
  adecuaciones del local, depósito de renta, trámites e inventario inicial. Con fecha, monto y comprobante.
- **Gastos fijos del mes** por sucursal: renta, sueldos, luz, internet. Son los mismos gastos que se reparten en la
  [utilidad neta de los servicios](servicios.md#dos-utilidades).
- **De las ventas:** la utilidad bruta de cada mes (venta − costo de lo vendido), que ya sale del POS.

Con eso, un reporte por sucursal:

| Indicador | Cómo se calcula |
|---|---|
| Punto de equilibrio mensual | Venta necesaria para cubrir los gastos fijos: gastos fijos ÷ margen bruto |
| Resultado del mes | Utilidad bruta − gastos fijos |
| Recuperación de la inversión | Mes en que la suma de los resultados alcanza la inversión de apertura |
| Rendimiento | Resultado anual ÷ inversión |

- El equipo de la matriz que hoy está en el inventario de eleventa (impresoras, engargoladoras) entra aquí como
  inversión, no como mercancía.
- Si la sucursal 2 abre antes de que el POS llegue a la fase 7, hay que **guardar desde ya la lista de gastos de
  apertura con sus comprobantes** para capturarla después; si no, el cálculo empieza incompleto.

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
- [ ] Descuentos: definirlos con el módulo de impresión (entrega 5)

Fase 3:

- [ ] Mayoreo: en eleventa es un precio que el cajero activa a mano; decidir si pasa a escalas por cantidad (¿desde
  cuántas piezas?) o se deja fuera

Depuración del inventario (antes del piloto, [ver abajo](#depuración-del-inventario)):

- [ ] Dar de baja los productos que ya no existen
- [ ] Sacar del inventario el equipo de la tienda (impresoras, engargoladoras, enmicadoras, mobiliario) y registrarlo como [inversión](#inversión-y-punto-de-equilibrio)
- [ ] Juntar los productos capturados dos veces (los que marca el diagnóstico)
- [ ] Corregir unidades: los productos a granel que eleventa tiene en KG (listón, micas, hojas sueltas…)
- [ ] Pasar a servicios lo que eleventa vende como producto a granel (impresiones, copias, engargolado, escaneo)
- [ ] Conteo físico completo: reemplaza todas las existencias de eleventa

Por definir:

- [ ] Si la sucursal 2 abre antes de la fase 7: guardar la lista de gastos de apertura con comprobantes
- [ ] Módulo de convivencia: el POS nuevo como una caja más de eleventa durante la adaptación
  ([idea](migracion-eleventa.md#convivencia-con-eleventa-idea-a-evaluar))
- [ ] Demás funciones a la medida (las describe el dueño)
- [ ] Privilegios de los clientes registrados: pedido sin anticipo, crédito, apartar sin pagar… ¿qué más?
- [ ] Regla para pedidos de no registrados: monto máximo que se prepara sin anticipo, y días para borrar el celular
- [ ] Central: ver [preguntas](servidor-remoto.md#preguntas)
- [ ] WhatsApp: ver [pendientes](whatsapp.md#pendientes)
- [ ] Dispositivos propios: ver [ideas](ideas.md)
