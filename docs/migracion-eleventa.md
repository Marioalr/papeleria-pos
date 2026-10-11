# Migración desde eleventa

## Fuente

- La carpeta de datos de eleventa multicaja. Guarda todo en una base Firebird, `PDVDATA.FDB` (≈ 90 MB, páginas de
  16 KB). En la tienda corre con Firebird 2.0.1 de 32 bits (carpeta `FirebirdSQL20`).
- La base es **ODS 10.1, creada con Firebird 1.5 de 32 bits.** El Firebird de 64 bits no la abre ("not a valid
  database"): las bases de antes de Firebird 3 dependen de la plataforma.
- Se lee **siempre sobre una copia,** nunca sobre la base en uso.

## Leer la base

El importador copia la base a una carpeta temporal, la respalda con el `gbak` de Firebird 2.5 **de 32 bits** (el
respaldo no depende de la plataforma), lo restaura como base de **64 bits** y lee esa. Al terminar borra la carpeta
temporal. Hace falta, fuera del repositorio, junto a la carpeta de la base:

| Carpeta | Qué lleva | De dónde |
|---|---|---|
| `firebird25` | Firebird 2.5 embebido de 64 bits (`fbembed.dll` y compañía) | `Firebird-2.5.9.27139-0_x64_embed.zip` |
| `firebird25-32\embed` | Firebird 2.5 embebido de 32 bits, más `gbak.exe` y `fbembed.dll` copiado como `fbclient.dll` | `Firebird-2.5.9.27139-0_Win32_embed.zip` y `gbak.exe` de `Firebird-2.5.9.27139-0_Win32.zip` |

Los tres ZIP están en los [releases oficiales de Firebird](https://github.com/FirebirdSQL/firebird/releases/tag/R2_5_9).
Nada se instala: son DLL que el importador carga.

```
dotnet run --project src/PapeleriaPos.Herramientas -- importar-eleventa --fdb C:\ruta\PDVDATA.FDB --salida eleventa.db
```

## Cómo guarda eleventa los datos (esquema 216)

| Dato | Dónde y cómo |
|---|---|
| Productos | `PRODUCTOS`: el **código es la llave** (las ventas lo guardan por código, no por ID). `TVENTA` 'U' por pieza, 'D' a granel. `PFINAL` es el precio con impuestos (aquí igual a `PVENTA`: no se desglosa IVA). `ELIMINADO_EN` marca los borrados |
| Unidades | `MEDIDA_ID` → `MEDIDAS`: 1 pieza, 3 metro, 4 kilo, 5 litro. Muchos productos a granel tienen KG aunque se venden por metro o por pieza suelta (y la medida KG está desactivada) |
| Departamentos | `DEPARTAMENTOS` (`DEPTS` es una tabla vieja) |
| Existencias | `INVENTARIO_BALANCES.CANTIDAD_ACTUAL`; `PRODUCTOS.DINVENTARIO` está desactualizado |
| Tickets | `VENTATICKETS`: `VENDIDO_EN` (hora local; vacío si el ticket quedó abierto), `ESTA_CANCELADO` 't'/'f'. Solo efectivo |
| Líneas | `VENTATICKETS_ARTICULOS`: `GANANCIA` es la utilidad **por pieza** (costo de la línea = importe − cantidad × ganancia; hasta el 2026-10-11 se importaba como si fuera de toda la línea). `TOTAL_ARTICULO` **está vacío en las líneas viejas**: ahí el importe es `PRECIO_FINAL × CANTIDAD`, y así todos los tickets cuadran |
| Mayoreo | `PMAYOREOFINAL`: un segundo precio por pieza que el cajero **activa a mano** en la venta (`USA_MAYOREO`): se escriben las piezas del paquete y F11. Las escalas por cantidad están en `PROMOCIONES_POR_CANTIDAD` |
| Texto | Charset NONE con texto en **Windows-1252** (la ñ es el byte F1): se lee con la conexión en WIN1252 |
| Proveedores | `PROVEEDORES` está vacía; `PROVEEDORES_BASE` y `PRODUCTOS_BASE` son catálogos que trae eleventa, no de la tienda |
| Servicios | Impresiones, copias, engargolado y escaneo son productos a granel con fracciones ("0.8 a $10" = 8 impresiones de $1) |

## Qué se importa

- **Catálogo:** cada producto de eleventa queda como un producto con una sola variante, con su código, precio y
  departamento. Los borrados entran inactivos. Lo vendido con un código que ya no existe se crea como producto
  inactivo, para no perder esas ventas.
- **Último costo** (`PCOSTO`), bajo un proveedor genérico, porque eleventa no tiene proveedores capturados.
- **Existencias** como inventario inicial al costo de eleventa (solo como referencia: el inventario de arranque sale
  del conteo físico). Las negativas no entran.
- **Historial de ventas** completo, con cancelados marcados: alimenta el análisis ABC, el sugerido de compra y la
  temporada.
- **El mayoreo de F11 pasa a escala por cantidad** (fase 3): en eleventa, al vender un paquete se escribían sus piezas
  y se oprimía F11. De esas ventas sale el paquete: la cantidad que, junto con sus múltiplos (dos o tres paquetes),
  explica más ventas con F11, siempre que ella sola se haya vendido al menos 3 veces y explique la mitad. Con 12 ×3 y
  24 ×4 el paquete es 12, no 24. La escala queda desde ahí al precio de mayoreo. No cuentan las ventas del regreso a
  clases (F11 dejaba al costo los productos estratégicos) ni las vendidas sin ganancia. Si el paquete es el de una
  promoción por cantidad, se queda la promoción. Lo deducido también queda anotado, ya resuelto.
- **Lo que no se puede deducir queda en «Mayoreo por revisar» (F3):** pocas ventas, ninguna cantidad que domine, a
  granel, al costo, que choque con una promoción o con el código repetido en eleventa (sus ventas con F11 no se pueden
  separar). Ahí se ve el precio de mayoreo de eleventa (por pieza, metro o kilo) y lo más vendido con F11; el
  encargado captura desde cuántas o lo deja sin mayoreo. No se acepta una escala que haga subir el precio por pieza al
  subir la cantidad, ni una que tape una promoción con otro precio. Lo que se junta, se da de baja o cambia de unidad
  sale de la lista (con otra unidad, el mayoreo se captura en la ficha).
- **Al repetir el mayoreo en la importación final** vale la última decisión de cada producto, y cambia o quita la
  escala que esa importación haya deducido en vez de sumar otra: «sin mayoreo» la quita y «desde 10» la mueve a 10.

El importador termina con una lista de lo que hubo que interpretar ("para revisar"): unidades dudosas, códigos
inexistentes, mayoreo deducido y por revisar, promociones repetidas, existencias negativas y tickets abiertos.

## Diagnóstico: primer entregable

Antes de que exista la caja, con los datos reales. Ya funciona sobre los datos ficticios
(`dotnet run --project src/PapeleriaPos.Herramientas -- diagnostico --base ficticios.db`) y deja un Excel con una
hoja por análisis:

| Hoja | Qué muestra |
|---|---|
| Resumen | Venta propia, costo, margen, ticket promedio, inventario, rotación y los hallazgos de las demás hojas |
| Dinero parado | Mercancía con existencia sin venta en los últimos 90 días, de mayor a menor valor |
| 80-20 | Variantes de la que más vende a la que menos, con clase A (80 % de la venta), B (15 %) y C (5 %) |
| Margen por departamento | Venta, costo, utilidad y margen; las recargas aparte porque solo la comisión es ingreso |
| Revisar catálogo | Sin código, con algún precio debajo del costo (menudeo, mayoreo o paquete) o posibles duplicados |
| Temporada | Venta por mes y lo que mueve el regreso a clases (15 de julio a 15 de septiembre) |

- **Duplicados:** mismo nombre con las palabras en otro orden, otras mayúsculas o acentos, o abreviadas ("p." por
  "punto"). Es estricto a propósito: "Lapicero Bic azul" y "Lapicero Bic negro" no se marcan.
- **Costo para comparar precios:** el promedio de la existencia; si no hay existencia, el último costo de compra.

## Limpieza

- **Variantes:** en eleventa probablemente son productos sueltos ("Lapicero azul", "Lapicero rojo"). Se agrupan de forma
  semiautomática por parecido del nombre y una persona revisa.
- **El nombre de eleventa queda como sinónimo** (2026-10-10). Si la depuración le cambia el nombre a un producto
  (abreviaturas expandidas, variantes juntas, otro orden de las palabras), el de eleventa se guarda como sinónimo suyo.
  Así, el nombre que cada cajero recuerda lo sigue encontrando mientras se acostumbra al nuevo, y la búsqueda le enseña
  el nombre nuevo ([propuesta](propuesta-interfaz.md#el-camino-de-siempre)). Los códigos cortos de eleventa se quedan
  como códigos.
- **Servicios:** se convierten en servicios con receta (ver [servicios](servicios.md)).

## El piloto: convivencia con eleventa

> **Decidido por el dueño (2026-10-10):** es el piloto, en la caja 2. Los pasos están en el
> [plan](plan.md#la-versión-unicaja-y-el-piloto-en-la-caja-2).

Durante un tiempo, la caja 2 vende con el POS nuevo y guarda en su propia base, pero **se hace pasar por una caja más de
eleventa multicaja** y manda cada venta a la caja principal, igual que hoy: la venta queda en los dos lados. Así
eleventa sigue siendo el sistema oficial (existencias, cortes, reportes de siempre) mientras el POS nuevo se prueba en
la tienda, y después se deja eleventa con las dos cajas.

Lo que hay que resolver antes:

- **Entender cómo escribe eleventa** una venta: qué tablas toca (venta, líneas, existencias, folios, turno o corte),
  en qué orden y con qué reglas. Se averigua sobre la copia, comparando la base antes y después de ventas de prueba
  hechas en un eleventa de pruebas, nunca en la tienda.
- **Riesgo de dañar la base de eleventa.** Escribir en una base ajena sin conocer todas sus reglas puede descuadrar
  existencias o cortes. Hace falta respaldo diario y probarlo semanas en una copia.
- **Firebird 2.0.1 de 32 bits con una base ODS 10.1:** para leer ya sabemos cómo (ver «Leer la base»), pero
  escribir en la base viva requiere hablarle al servidor de la tienda por red, como hace una caja de eleventa.
- **Qué pasa con lo que eleventa no tiene:** servicios con receta, pedidos, fracciones o códigos nuevos. Se manda
  solo lo que eleventa entiende (venta, cobro, existencia) y lo demás vive solo en el POS nuevo.
- **Si la red o la caja principal no contestan:** la caja 2 sigue vendiendo con su base y manda lo pendiente al volver,
  sin duplicar (una bandeja de salida, como la de la contingencia). En la tienda la red se cae cuando se va el internet
  ([red de la matriz](arquitectura.md#la-red-de-la-matriz)).

**El primer paso es solo leer** de eleventa (catálogo, precios y existencias cada pocos minutos), para que la caja 2
trabaje con datos al día sin escribir nada en eleventa. Cuando eso funcione, se agrega la escritura.

### La lectura de cada pocos minutos (paso 1)

La base de la caja 2 se arma una vez con `importar-eleventa`, desde una copia. Desde ahí, su servicio lee el eleventa de
la caja principal por la red, como lo abre una caja de eleventa, y se pone al día:

- **Qué lee:** departamentos, productos (nombre, código, precio, costo, mayoreo, borrados), existencias y promociones
  por cantidad. Las ventas no, porque pesan y no hacen falta cada pocos minutos.
- **Cómo:** en una transacción de solo lectura y con foto fija, así todo es del mismo momento. No escribe nada en
  eleventa. Con la base real tarda menos de un segundo.
- **Qué cambia aquí** (eleventa manda):
  - las altas, con su código, precio, costo y existencia;
  - el nombre, el departamento, el código y si está activo;
  - el precio de menudeo y las promociones por cantidad: se agregan, cambian y se quitan;
  - el costo;
  - la existencia, con un ajuste en el kardex al costo promedio y a nombre de `lectura-eleventa`.
- **Qué no toca:**
  - lo dado de alta aquí;
  - las escalas de mayoreo deducidas del F11 o capturadas en F3;
  - la unidad (si cambia allá, da un aviso);
  - el nombre de un producto que la depuración ya agrupó;
  - lo juntado en la depuración.
- **Lo que necesita revisarse** queda como aviso, por ejemplo un código que aquí ya tiene otro producto. El mayoreo de
  un producto nuevo queda en F3 por revisar, porque todavía no tiene ventas con F11.
- **Si eleventa no contesta** (la PC apagada, la red caída), la caja sigue vendiendo con lo último que se leyó y vuelve
  a intentar en la siguiente vuelta. El error se ve en `GET /api/eleventa`; `POST /api/eleventa/leer` lee ya, sin
  esperar la vuelta.
- **La existencia y una venta a la vez:** el ajuste se guarda con la base apartada para escribir, así una venta que se
  cobre en ese momento espera unos milisegundos y no se pierde. Entre dos ventas a la vez el riesgo sigue como antes, y
  `VerificarExistenciasAsync` lo detecta.

Se configura en el servicio de la caja 2, sección `Eleventa`. Los datos reales van en la configuración de esa PC,
nunca aquí; la clave puede ir en la variable de entorno `Eleventa__Clave`.

```json
"Eleventa": { "Activo": true, "Servidor": "<la caja principal>", "Base": "C:\\…\\PDVDATA.FDB", "Usuario": "SYSDBA", "Clave": "…", "Minutos": 5 }
```

Para probarla una vez a mano, sobre una base ya importada (con `--simular` solo dice qué cambiaría). Por la red, la
clave se pide al correrlo, sin que se vea (o va en la variable `ELEVENTA_CLAVE`):

```
dotnet run --project src/PapeleriaPos.Herramientas -- sincronizar-eleventa --base eleventa.db --fdb C:\ruta\PDVDATA.FDB --simular
dotnet run --project src/PapeleriaPos.Herramientas -- sincronizar-eleventa --base eleventa.db --servidor <la caja principal> --ruta C:\…\PDVDATA.FDB --usuario SYSDBA --simular
```

Se probó primero con la base real, toda local:
1. Se importó la copia y se leyó la misma copia: sin cambios ni avisos.
2. Se leyó por la red desde un Firebird 2.5 local con otra copia: la misma respuesta en 0.2 s.
3. Se cambiaron un precio y una existencia en esa copia: llegaron, y la lectura siguiente salió sin cambios.
4. Se apagó ese Firebird: la caja siguió contestando y el error quedó en el estado.

Después, desde la caja 2 y contra el Firebird 2.0 de la caja principal (2026-10-11, con `--simular`): leyó todo el
catálogo por Wi-Fi en menos de un segundo, sin errores ni avisos, y encontró lo que cambió en eleventa desde la copia
(precios, costos y existencias).

### Cómo escribe eleventa una venta (paso 2)

Sacado de una copia de la base (esquema, triggers y ventas ya guardadas) y confirmado mirando a eleventa trabajar en la
caja 2 con `observar-eleventa` (2026-10-11: abrir la caja, escanear, borrar y cobrar). Los ejemplos son inventados.

**La base no tiene lógica.** Sus triggers solo ponen el ID desde su contador (`GEN_<TABLA>_ID`) y sus procedimientos solo
consultan. El folio, la existencia, el turno y el corte los escribe el programa de eleventa: la caja 2 nueva tiene
que escribir lo mismo.

**Una venta en efectivo** (la tienda solo cobra en efectivo) deja:

1. **El ticket** (`VENTATICKETS`).
   - Ya existía: cada caja con turno abierto tiene siempre un ticket abierto, con `ESTA_ABIERTO = 't'`, sin folio,
     `NOMBRE` «Ticket N», `CREADO_EN`, `CAJA_ID`, `CAJERO_ID`, `TURNO_ID` y `OPERACION_ID = -1`.
   - Al cobrar se llenan `FOLIO`, `VENDIDO_EN` y `PAGADO_EN` (la misma hora), `ESTA_ABIERTO = 'f'`, `SUBTOTAL`,
     `IMPUESTOS`, `TOTAL`, `GANANCIA`, `PAGO_CON`, `FORMA_PAGO = 'e'`, `NUMERO_ARTICULOS` y `TOTAL_FACTURABLE`.
   - Enseguida se crea el ticket abierto siguiente.
2. **Un renglón por artículo** (`VENTATICKETS_ARTICULOS`), **que se escribe al escanear**, no al cobrar.
   - Al escanear, eleventa agrega en la base de la caja principal el renglón y un apartado de inventario
     (`INVENTARIO_APARTADO`: producto, cantidad, ticket y caja). Al borrar el artículo, quita los dos.
   - O sea, cada escaneo es un viaje de ida y vuelta por la red a la caja principal: eso explica las pausas de eleventa
     por Wi-Fi. La caja 2 nueva no lo hace: escanea contra su copia y escribe en eleventa al cobrar (paso 3).
   - Lleva el código, el nombre, la cantidad, el departamento, `PRECIO_USADO` y `PRECIO_FINAL`, `TOTAL_ARTICULO`,
     `GANANCIA` y `USA_MAYOREO`. `AGREGADO_EN` es cuando se escaneó.
   - Al cobrar se llenan `PAGADO_EN` y `PORCENTAJE_PAGADO` (de 0 a 1000000). Siempre `IMPUESTOS_USADOS = ','`.
3. **El inventario.**
   - Un renglón por producto en `INVENTARIO_HISTORIAL`: «Venta #<folio>», `CANTIDAD_ANTERIOR`, `CANTIDAD`,
     `COSTO_UNITARIO`, `COSTO_DESPUES`, `VENTA_ID`, `CAJA_ID`, `USUARIO_ID` y `ALMACEN_ID = 1`.
   - La existencia baja en `INVENTARIO_BALANCES`. Su historial forma una cadena exacta, por ejemplo 12 → 10 → 9.
4. **Los acumulados**, en cada venta y no al cerrar (un turno abierto ya cuadra con sus ventas):
   - en `TURNOS`: `ACUMULADO_VENTAS`, `ACUMULADO_GANANCIA`, `VENTAS_EFECTIVO` y `NUMERO_VENTAS`;
   - en `CORTE_OPERACIONES`, el renglón del día y la caja (el turno apunta a él con `ID_OPERACION_INICIO`);
   - en `CORTE_VENTAS_POR_DEPTO`, el renglón del turno y el departamento, y en `CORTE_VENTAS_DEPTO_OPERACIONES`, el del
     día y el departamento (`ID_OPERACION`). Si no hay renglón para ese departamento, se crea con la primera venta.

**Al abrir la caja** (antes de la primera venta), eleventa:
1. anota la IP y la hora en `CAJAS` (`ULTIMA_IP`, `ULTIMO_INGRESO`);
2. crea el renglón del día de esa caja en `CORTE_OPERACIONES`, en ceros, si no existe;
3. abre el turno en `TURNOS`, con la caja, el cajero, `INICIO_EN`, `DINERO_INICIAL` y el renglón del día;
4. abre el primer ticket.

Lo demás:
- Sin impuestos desglosados no hay `VENTATICKETS_IMPUESTOS`, y sin pago mixto no hay `PAGOS_MIXTOS`. Las tablas viejas
  `VENTAS` y `OPERACIONES` ya no se usan.
- **La ganancia:**
  - la `GANANCIA` de cada artículo es por pieza: precio menos costo;
  - la del ticket es la suma de la cantidad por esa ganancia. Por ejemplo, 0.5 a $8 con costo $3: el artículo dice 5 y
    el ticket, 2.5;
  - la del turno, la del día y la del departamento suman la de los tickets.
- **El folio** es uno solo para todas las cajas y va en el orden en que se cobra. No sale de un contador de Firebird: el
  programa toma el último y le suma uno.
  - Ya pasó que dos cajas cobraran con dos segundos de diferencia y se repitiera un folio.
  - La caja 2 nueva tendrá el mismo riesgo mientras conviva con eleventa: hay que tomarlo justo antes de guardar, en la
    misma transacción, y revisar después que no se repitió.
- **Turnos:** uno abierto por caja (`UNIQUE (ID_CAJA, TERMINO_EN)`). La caja 2 nueva va a necesitar abrir y cerrar el
  suyo en eleventa (paso 4).
- **Las otras operaciones del corte** van en `CORTE_MOVIMIENTOS`, de tipo `Entrada`, `Salida` y `Devolucion` (paso 4).
- **El día es el del cobro:** el acumulado de `CORTE_OPERACIONES` es lo vendido ese día en esa caja (cuadra en todos
  los días de la copia). Si un turno pasa de la medianoche, lo cobrado después suma al renglón del día siguiente.
- **Un producto va una sola vez por ticket:** si se escanea otra vez, sube la cantidad.
- **`NUMERO_ARTICULOS`** cuenta las piezas, y uno por cada producto a granel (`TVENTA = 'D'`).
- **El artículo común** lleva el código `COMUN-<número al azar>-<el contador de artículos antes de su renglón>`, el
  departamento «Productos Comunes», ganancia igual al precio y nada de inventario.
- Todos los productos de la tienda llevan inventario (`USA_INVENTARIO`) y no hay kits.
- Las cantidades y los montos son `FLOAT` (unas 7 cifras); los acumulados del corte, `NUMERIC(18,4)`.
- Al cobrar no queda ningún apartado: se quitan los de ese ticket.

**Lo que falta confirmar mirando cómo trabaja:**
- en qué orden escribe al cobrar y si todo va en una sola transacción (la caja nueva lo escribe todo en una);
- cancelar, devolver, entradas y salidas, cerrar el turno y el corte. Se miran cuando pasen de verdad, sin hacer
  operaciones de prueba en la tienda.

Para eso está `observar-eleventa`. Toma una foto de toda la base, solo leyendo; después de hacer algo en eleventa toma
otra y dice qué renglones aparecieron o cambiaron y qué contadores avanzaron.
- Cada comparación se guarda fuera del repositorio y fuera de OneDrive (`AppData\Local\PapeleriaPos\observaciones-eleventa`),
  porque son datos reales.
- La primera vez, por el Wi-Fi de la caja 2, cada foto tardó unos 20 s: casi todo eran idas y vueltas. Ahora los
  contadores, los conteos y las llaves van juntos, y los renglones se piden de a 2,000.

```
dotnet run --project src/PapeleriaPos.Herramientas -- observar-eleventa --servidor <la caja principal> --ruta C:\…\PDVDATA.FDB --usuario SYSDBA
```

### Mandar cada venta (paso 3)

**Ya está la escritura** (`EnvioEleventa.MandarAsync`): una venta en efectivo de la caja nueva entra a eleventa como la
escribe una caja de eleventa al cobrar. Deja el ticket con su folio, sus artículos, la salida de inventario y los
acumulados del turno, del día y del departamento.
- **Todo o nada:** va en una sola transacción. Si choca con otra caja que escribe lo mismo en ese instante (la misma
  existencia), lo vuelve a intentar unas veces.
- **Sin duplicar:** la marca de cada venta es la hora en que se empezó en la caja nueva (`CREADO_EN`, en diezmilésimas de
  segundo, de esa caja). Si se manda otra vez, por ejemplo porque se cortó la red justo al confirmar, regresa el folio
  que ya tenía y no escribe nada.
- **El folio** es el último más uno, tomado dentro de la transacción. Después revisa que otra caja no haya cobrado con
  el mismo; si pasó, lo dice, para revisarlo.
- **Lo de cada producto lo toma de eleventa:** su código, nombre, departamento y costo de ese momento. La caja nueva
  manda el ID de eleventa, la cantidad y el importe.
- **Lo que eleventa no tiene** (un artículo común, un producto dado de alta solo en la caja nueva o borrado en eleventa)
  va como artículo común. Así el total de eleventa es lo que entró a la caja.
- **Si eleventa no tiene un turno abierto para esa caja,** lo abre como lo abre eleventa: la hora y la IP en `CAJAS`, el
  renglón del día, el turno en ceros con el fondo y su ticket abierto. Cerrarlo es del paso 4.
- No pone los apartados que eleventa pone al escanear: la caja nueva escanea contra su propia base, y al cobrar ya no
  quedaría ninguno.

Se probó así:
- Con pruebas contra un Firebird de prueba en la misma PC: una base vacía con las mismas tablas, tipos, contadores y
  triggers de eleventa. Corren donde está la variable `PRUEBAS_FIREBIRD` («servidor:puerto»); en el CI no hay Firebird y
  se saltan.
- Contra una copia de la base real, con `probar-envio-eleventa`: lo que deja coincide campo por campo con lo que dejó
  eleventa en la caja 2 al cobrar, y tarda menos de 0.1 s en la misma PC. Esa herramienta escribe, así que solo acepta
  un Firebird en esta misma PC:

```
dotnet run --project src/PapeleriaPos.Herramientas -- probar-envio-eleventa --servidor 127.0.0.1 --ruta C:\…\copia.fdb --usuario SYSDBA --caja <ID> --cajero <ID> --articulo <ID>:<cantidad> --comun Recuerdo:15
```

**Falta:**
- la bandeja de salida: al cobrar, la venta queda en la bandeja en la misma transacción, y un trabajo de fondo la manda,
  reintenta y avisa;
- el folio de eleventa en el ticket de la caja 2, y qué folio lleva cuando la caja principal no contesta;
- la configuración de la caja 2 (su caja y su cajero en eleventa) y lo que no es efectivo;
- el redondeo del efectivo, si se usa: el total de eleventa debe ser lo que entró a la caja.

## Arranque (fase 5)

1. Depuración del catálogo sobre la base importada ([plan](plan.md#depuración-del-inventario)): bajas, duplicados,
   equipo de la tienda, unidades y servicios.
2. Conteo físico completo, por departamentos, en las semanas previas. Reemplaza todas las existencias de eleventa.
3. Arranque en un día tranquilo.
4. Eleventa queda instalado solo para consulta unas semanas, como respaldo, con un plan para regresar si algo falla.
5. Capacitación a cajeros y encargado antes del arranque, con datos de prueba.
