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

> **Aclarado por el dueño (2026-10-11):** la base nueva se concentra en sus propias metas (el máximo rendimiento, etc.) y
> no se adapta a eleventa. La caja 2 guarda todo en su base y a la principal le manda solo lo que su eleventa necesita
> para registrarlo como una venta más; todo lo de eleventa vive en el puente (`PapeleriaPos.Eleventa` y la bandeja). Más
> adelante, una opción aparte exportará la base a una versión de eleventa.

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
- **Lo que eleventa todavía no tiene de la caja 2** (solo del piloto, vive en el puente): la existencia queda en la de
  eleventa más lo que movieron, según su kardex, las ventas, cancelaciones y devoluciones de la bandeja que siguen
  pendientes, quedaron por revisar o se descartaron. Así la lectura no regresa una venta que todavía no llega ni la
  vuelve a quitar cuando llega. Lo enviado ya está allá. Lo que alguien dejó en eleventa a mano se anota con
  `POST /api/eleventa/envios/{id}/a-mano` (si está pendiente o por revisar): ya no se manda ni se descuenta. En
  cambio, `…/revisar` solo deja pasar lo demás: lo que queda por revisar no está allá y se sigue descontando. Lo que se
  mandó o se anotó a mano mientras se leía, o lo pendiente cuyo último intento falló por la red (se pudo cortar justo
  cuando eleventa ya lo había guardado), puede estar allá o no: esa vez sus productos no se ajustan («Existencia sin
  ajustar…» en los avisos) y se ajustan en otra lectura. Lo que eleventa rechazó no está allá y se descuenta, aunque la
  bandeja siga detenida en eso.
- **Lo que no se cambia en la caja 2 mientras lee** (solo del piloto, 2026-10-16): lo que pone la lectura, si se
  cambiara aquí, lo regresaría la siguiente sin decir nada, y en ese rato la caja vendería con lo que se escribió. Por
  eso las páginas no lo dejan cambiar, con una nota, y el servicio lo rechaza con el mismo aviso (también a una caja de
  antes); quien no tiene el permiso oye lo del permiso, como siempre:
  - en Productos (F3), de un producto de eleventa (con su ID): el nombre, el departamento y si se vende, si es de una
    sola variante; si cada variante está activa; los códigos de su pieza (se pueden agregar otros); el precio de menudeo
    y las promociones de eleventa. Tampoco se da de baja, se junta ni se le cambia la unidad. Sí se cambian las escalas
    del F11 o de F3 (también el mayoreo por revisar), las otras presentaciones, la marca, las variantes nuevas y lo dado
    de alta aquí, que va a eleventa como artículo común;
  - en Inventario (F4), lo que mueve existencias: recibir y cerrar compras, el conteo del día (la venta ya no enseña su
    chip), el conteo completo (también en el celular), ajustes, mermas y abrir un paquete. Tampoco con lo dado de alta
    aquí: una compra o un conteo junta los dos, y eleventa no lleva su existencia. Las órdenes, el sugerido, las
    etiquetas y la hoja de códigos siguen; lo que quedó abierto se consulta y se cancela;
  - los departamentos se dan de alta y se cambian en eleventa (Productos › Departamentos solo los enseña);
  - en Productos › Temporadas (2026-10-17): al empezarla, el precio de lo de eleventa se ve sin cuadro y se confirma como
    está (lo dado de alta aquí sí cambia); al terminarla, lo que sobró no se cuenta aquí, y lo dice. El resumen sí sale: lo
    que bajó con la lectura cuenta como vendido en la caja principal, y lo que subió, como lo que llegó. La hoja, las
    tarjetas de precio y las consultas del verificador son solo del POS y siguen igual.

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
- cancelar, devolver, entradas y salidas, cerrar el turno y el corte: lo que se sabe sale de la copia («Cancelar,
  devolver y cerrar el turno» y «Entradas y salidas de dinero», abajo); falta verlos en vivo cuando pasen de verdad,
  sin hacer operaciones de prueba en la tienda.

Para eso está `observar-eleventa`. Toma una foto de toda la base, solo leyendo; después de hacer algo en eleventa toma
otra y dice qué renglones aparecieron o cambiaron y qué contadores avanzaron.
- Cada comparación se guarda fuera del repositorio y fuera de OneDrive (`AppData\Local\PapeleriaPos\observaciones-eleventa`),
  porque son datos reales.
- La primera vez, por el Wi-Fi de la caja 2, cada foto tardó unos 20 s: casi todo eran idas y vueltas. Ahora los
  contadores, los conteos y las llaves van juntos, y los renglones se piden de a 2,000.

```
dotnet run --project src/PapeleriaPos.Herramientas -- observar-eleventa --servidor <la caja principal> --ruta C:\…\PDVDATA.FDB --usuario SYSDBA
```

#### Verlo en vivo

Lo que la caja 2 escribe al cancelar, devolver y cerrar el turno salió de la copia (paso 4). Para confirmarlo cuando pase
de verdad, en la caja 2 se abre `scripts\observar-en-vivo.cmd`:
- **Se conecta solo:** toma la caja principal del eleventa de esa PC (`pdventa.ini`, `[EnRed] IPServidor`) y la ruta de una
  instalación normal. Pide la clave sin que se vea y abre `observar-eleventa`, solo leyendo.
- **Las notas** van en `Documents\TPV\observaciones-eleventa`, con los demás datos de la tienda.
- **Cuando pase una cancelación, una devolución o un cierre de turno** (en cualquier caja), se escribe qué fue. Además de lo
  que cambió, dice si eleventa hizo lo mismo que la caja 2 (`RevisionEnVivo`):
  - **✓** coincide, por ejemplo «el ticket quedó cancelado» o «el faltante quedó como Salida y en ACUMULADO_SALIDAS del turno»;
  - **✗** eleventa lo hizo distinto, o tocó una tabla que la caja 2 no escribe;
  - **·** no se alcanza a ver en la foto, como un ticket de hace mucho o ventas en el mismo rato.
- **No se hacen operaciones de prueba en la tienda:** se espera a que pasen.
- **Las entradas y salidas de dinero** no se revisan aquí.
- **Para probarlo contra una copia** en un Firebird de prueba están `ELEVENTA_SERVIDOR`, `ELEVENTA_PUERTO`, `ELEVENTA_RUTA` y
  `ELEVENTA_CLAVE`.

Lo que la caja 2 escribe pasa esa revisión sin una sola ✗: hay una prueba que lo fotografía antes y después de cancelar,
devolver y cerrar.

**Lo primero que se vio en vivo (2026-10-11):** en eleventa, una devolución parcial de algo a granel, la cancelación de
lo que quedó de ese ticket y un cierre de turno que cuadró, con una venta en el mismo rato.
- La devolución salió igual en todo.
- La cancelación tuvo dos ✗ que la copia ya tenía y no se habían visto: el subtotal también baja lo devuelto (queda en
  cero) y `NUMERO_ARTICULOS` baja las piezas, no lo de granel («Cancelar, devolver y cerrar el turno», abajo). La caja 2
  ya lo escribe así.
- En el cierre, la ✗ del ticket abierto era de la revisión: eleventa cobró con el ticket abierto y abrió otro, que el
  cierre borró. La revisión ya lo distingue.

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

**La bandeja de salida** (`EnvioVentasEleventa`, tabla `EnviosAEleventa`). Así viaja cada venta de la caja 2:
- **Al cobrar** entra a la bandeja en la misma transacción que la venta: aunque la caja se apague justo después, no queda
  una venta cobrada sin mandar. También las que suben de la bandeja de contingencia.
- **Enseguida se intenta mandar**, hasta 2 s (`SegundosAlCobrar`). Si eleventa contesta, el ticket se imprime con su
  folio (decidido por el dueño, 2026-10-10 y 2026-10-11), para devolver en cualquiera de las dos cajas.
- **Si no contesta a tiempo** (la principal apagada, el Wi-Fi caído o lento), el ticket sale con el folio de la caja 2
  (A-000123) y la venta se manda después. Su devolución se hace en la caja 2.
  - Tras una falla de red, o si va otro envío, el cobro no espera: solo el primer cobro sin red tarda los 2 s; los
    demás, lo normal.
- **Una vuelta de fondo** manda lo pendiente cada 30 s (`SegundosEntreEnvios`), de la más vieja a la más nueva. Si es la
  red, deja el resto para la siguiente vuelta. Al volver la red, cada venta toma el siguiente folio de eleventa, y su
  reimpresión ya lo lleva.
- **El ticket** imprime «Folio 4521» (el de eleventa) cuando lo hay. El QR sigue con el de la caja 2, que abre la
  devolución al escanearlo, y en la ventana de devolución también se puede escribir el folio de eleventa.
- **Lo que eleventa no entendería** queda por revisar y no se manda solo: lo que no fue en efectivo (la tienda solo
  cobra en efectivo). Lo que se canceló antes de mandarse se descarta: allá nunca existió.
- **El redondeo del efectivo** va en la línea más grande, para que el total de eleventa sea lo que entró a la caja.
- **Cómo va:** `GET /api/eleventa/envios` dice cuántas ventas están pendientes y por revisar, el último error («No
  contesta el eleventa de…») y los folios repetidos. `POST /api/eleventa/enviar` hace la vuelta ya.

Se configura en el servicio de la caja 2, junto con la lectura. La caja y el cajero son los `ID` de eleventa
(`CAJAS.ID` de la caja 2, y el cajero a cuyo nombre van las ventas):

```json
"Eleventa": { "Activo": true, "Escribir": true, "Servidor": "<la caja principal>", "Base": "C:\\…\\PDVDATA.FDB", "Usuario": "SYSDBA", "Clave": "…", "Caja": 0, "Cajero": 0 }
```

Se probó de punta a punta en esta PC, con una base importada de la copia y otra copia servida por un Firebird de
prueba:
- se cobró una venta y el ticket salió con el siguiente folio de eleventa;
- se apagó ese Firebird: los tickets salieron con el folio de la caja, con una espera solo en el primer cobro;
- al prenderlo, las pendientes entraron en orden, sin repetirse, y el turno de eleventa cuadró con sus tickets.

### Cancelar, devolver y cerrar el turno (paso 4)

**Cómo lo hace eleventa**, sacado de una copia (sus cancelaciones, devoluciones y turnos cerrados). Los ejemplos son
inventados.

- **Cada devolución** deja un renglón en `DEVOLUCIONES`:
  - `TIPO_DEVOLUCION` es `C` (cancelación del ticket) o `P` (parcial);
  - lleva el turno, el ticket, `PAGADO_EN` del ticket y `DEVUELTO_EN`;
  - `CAJERO` y `CAJA` van como texto: `USUARIOS.USUARIO` y `CAJAS.NOMBRE`;
  - `TOTAL_DEVUELTO` es lo que salió de la caja.
- **La cancelación** deja el ticket con `ESTA_CANCELADO = 't'` y su total. A `SUBTOTAL` y a la ganancia les resta lo
  devuelto, así que quedan en cero, y sus renglones de artículos no se tocan. No lleva `DEVOLUCIONES_ARTICULOS`.
  - Si antes hubo una parcial, se cancela lo que quedó en el ticket.
  - Las cancelaciones de 2024 conservan subtotal, ganancia y artículos: era otra versión de eleventa.
- **La parcial** cambia el ticket original:
  - el artículo baja su `CANTIDAD` y su `TOTAL_ARTICULO`;
  - si se devolvió completo, el renglón se borra (eleventa no deja artículos en cero);
  - el ticket baja `TOTAL`, `SUBTOTAL`, `TOTAL_FACTURABLE` y `GANANCIA`;
  - lleva un renglón de `DEVOLUCIONES_ARTICULOS` por artículo;
  - `CANTIDAD_DEVUELTA` y `FUE_DEVUELTO` del artículo, y `TOTAL_DEVUELTO` del ticket, no se usan.
- **En las dos:**
  - `NUMERO_ARTICULOS` baja las piezas devueltas. Lo de granel contó uno al vender y no baja, aunque se devuelva todo y
    su renglón se borre; cuenta el tipo de venta que el producto tiene hoy. Así salen en la copia casi todas las
    cancelaciones y parciales desde 2025; las pocas que no, ya no cuadraban desde la venta;
  - un movimiento del corte por artículo (`CORTE_MOVIMIENTOS`, tipo `Devolucion`, `FORMA_PAGO = 'E'`,
    `RAZON_DEVOLUCION = 'V'`), por ejemplo «Devolución "Cuaderno profesional", Ticket #4521»;
  - una entrada al inventario por producto: «Devolución de venta #4521», con la cantidad en negativo y el costo del producto.
- **Los acumulados:** en el turno, el día y el departamento, `ACUMULADO_VENTAS` y la ganancia bajan, y
  `DEVOLUCIONES_VENTAS_EFECTIVO` sube. `VENTAS_EFECTIVO` no se toca. La cancelación también baja `NUMERO_VENTAS`.
- **Al cerrar el turno:**
  - se anotan `TERMINO_EN` y `EFECTIVO_AL_CIERRE`, que es el fondo más el efectivo, menos lo devuelto, más entradas y
    menos salidas;
  - se borra su ticket abierto;
  - si faltó o sobró al contar, queda un movimiento `Salida` («Efectivo faltante al cerrar turno de <cajero> del
    <día que abrió>») o `Entrada` («…sobrante…»), que entra a las salidas o entradas del turno y, como cualquier
    entrada o salida, a las del día solo si ese día ya tiene renglón («Entradas y salidas de dinero», abajo). Así
    `EFECTIVO_AL_CIERRE` es lo que se contó;
  - `ID_OPERACION_FIN` no se usa.
- En la copia todas las devoluciones son del mismo turno que su venta.

**La caja 2 hace lo mismo**, con la misma bandeja y en orden: una devolución nunca llega antes que su venta, ni un turno
se cierra antes que sus ventas.
- **Qué entra a la bandeja:**
  - la cancelación o devolución de una venta que fue a eleventa, en la misma transacción;
  - el corte Z de un turno que mandó algo, como el cierre del turno de eleventa;
  - lo que dependía de una venta descartada también se descarta; lo de una venta por revisar, también queda por revisar.
- **Dónde se aplica:** lo devuelto va al turno y al día en que se devuelve, que es cuando sale el dinero de la caja.
  Eleventa no lo ha hecho entre turnos, pero así cuadran sus cortes.
- **Lo que no se puede mandar solo:**
  - si algo no entra, lo que sigue espera en la bandeja;
  - si ya se arregló a mano en eleventa, `POST /api/eleventa/envios/{id}/a-mano` lo anota y lo demás pasa; si todavía
    falta revisarlo, `POST …/revisar` lo deja por revisar, lo demás pasa y la lectura lo sigue descontando hasta
    anotarlo a mano;
  - lo que volvió dañado no regresa al inventario de eleventa.
- **El cierre** usa la diferencia del corte Z de la caja 2 (contado menos esperado). Con las entradas y salidas ya en
  eleventa (abajo), su efectivo al cierre queda en lo contado.

**Mientras dure el piloto el corte de la caja 2 se hace en eleventa** (decisión del dueño, 2026-10-12; `CorteEnElPiloto`,
solo para convivir con eleventa):
- el servicio rechaza el corte Z de esa caja, y la caja lo avisa al intentar el corte y al salir;
- el turno de eleventa lo abre eleventa con su fondo y lo cierra su corte: el puente no lo abre ni lo cierra. Si allá no
  hay turno abierto, lo que se manda espera en la bandeja («Abre la caja en eleventa…») y entra en cuanto lo abran;
- un cierre que ya estaba en la bandeja, o el de un corte Z que sube de la contingencia, queda por revisar;
- el turno de la caja nueva no se cierra, así que solo se cancela un ticket del mismo día (después es devolución) y no
  sale el aviso de retiro (el cajón es de las dos).

Se probó como el paso 3, con pruebas contra el Firebird de prueba y de punta a punta contra copias: se cobraron dos
ventas, se canceló una y se devolvió parte de la otra (un artículo completo), y se hizo el corte Z con un faltante. En la
copia quedaron:
- la cancelación y la devolución como las deja eleventa;
- el inventario de vuelta;
- el turno cerrado con lo contado, el faltante como salida y sin ticket abierto.

### Entradas y salidas de dinero (paso 4)

**Cómo lo hace eleventa**, sacado de una copia. Ahí casi todas las `Salida` son faltantes al cerrar el turno y casi todas
las `Entrada`, sobrantes; a mano hay muy pocas (unas tres entradas y ninguna salida). Se distinguen por la descripción:
la del cierre dice «Efectivo faltante al cerrar turno de…», la de una a mano es el comentario que se escribió. Los
ejemplos son inventados.

- **Cada una deja un renglón en `CORTE_MOVIMIENTOS`:**
  - `TIPO` es `Entrada` o `Salida` y `MONTO`, lo que entró o salió, en positivo;
  - `DESCRIPCION` es el comentario tal cual, por ejemplo «Cambio del banco»;
  - `CUANDO_FUE` va con sus diezmilésimas de segundo, y lleva `ID_TURNO` e `ID_CAJA`;
  - sin `FORMA_PAGO` ni `RAZON_DEVOLUCION`, que son de las devoluciones.
- **Los acumulados**, en el momento y no al cerrar: `ACUMULADO_ENTRADAS` o `ACUMULADO_SALIDAS` del turno y del día
  (`CORTE_OPERACIONES`). Al cerrar, `EFECTIVO_AL_CIERRE` las cuenta (fondo + efectivo − devuelto + entradas − salidas).
- **Nada más:** ni ticket ni inventario. Las tablas viejas `MOVIMIENTOS` y `OPERACIONES` ya no se usan (sus últimos
  renglones son de 2024) y `HISTORIAL_USUARIOS` está vacía.
- **Un turno se puede abrir solo para meter dinero:** en la copia hay uno que se abrió, tuvo una entrada y se cerró, sin
  ventas.
- **El día solo si ya tiene renglón.** En la copia:
  - los turnos que abrieron y cerraron el mismo día llevan todo también en el día: las entradas a mano, los faltantes y
    los sobrantes;
  - los que cerraron al día siguiente (o días después) llevan su faltante solo en el turno, y en ninguno de esos casos
    ese día tenía todavía renglón: lo creó el turno siguiente al abrir;
  - o sea, eleventa suma al renglón de ese día y esa caja si ya existe, y no lo crea (una venta, en cambio, sí lo crea).
  - Esto corrige lo que se había dicho del cierre en el paso 4 («al turno, no al día»): los faltantes recientes de la
    copia son todos de turnos que cerraron otro día.

**Lo que no se puede saber de la copia** y queda por ver en vivo:
- una entrada o salida, o un cierre con diferencia, en un turno que ya pasó de la medianoche y ya vendió ese día (así
  ese día ya tiene renglón): si va al día, como lo hace la caja 2, o a ninguno. En la copia no hay ninguno así;
- que el eleventa de hoy siga sumando al día: los turnos de la copia que cerraron el mismo día con entradas, salidas o
  diferencia son todos de un mismo mes de 2025, y no se puede descartar que una versión posterior ya no lo haga;
- si eleventa pone un texto cuando no se escribe comentario (la caja 2 siempre lleva motivo);
- en qué orden escribe y si va en una sola transacción (la caja 2 lo escribe todo en una).

**La caja 2 hace lo mismo**, por la misma bandeja y en orden (`Tipo = MovimientoDeCaja`, con `MovimientoId`):
- **Qué entra a la bandeja:** cada F7 y F8, en la misma transacción que el movimiento, si esta caja manda a eleventa
  (`Eleventa:Escribir`) o si su turno ya mandó algo. También los que suben con la bandeja de contingencia.
- **Antes de la primera venta del turno** también va: si en eleventa no hay turno abierto, lo abre con el fondo, como lo
  abre eleventa (y como el turno de la copia que solo metió dinero). Así el corte de eleventa tiene todo lo que pasó por
  el cajón.
- **Se manda ya:** al guardarla se pide la vuelta, sin esperar los 30 s.
- **Sin duplicar:** su marca es la hora en que se hizo en la caja 2 (`CUANDO_FUE`, en diezmilésimas de segundo), con la
  caja y el tipo. Si se manda otra vez, no se escribe de nuevo.
- **La descripción** es el motivo que se escribió en la caja 2.
- **El día:** suma al de la hora en que se hizo, solo si ya tiene renglón, como eleventa. Lo mismo el faltante o
  sobrante del cierre.
- **Lo que sube sin conexión cuando su turno ya tenía corte Z** queda por revisar: en eleventa ese turno ya se cerró, y
  mandarla abriría otro. Se anota a mano.
- **El corte Z** cierra el turno de eleventa también si el turno solo tuvo entradas o salidas.
- **El cierre cuadra:** lo esperado del corte de la caja 2 ya cuenta entradas y salidas, así que con su diferencia
  (contado menos esperado) `EFECTIVO_AL_CIERRE` de eleventa queda en lo contado. No cuadra con lo que eleventa no tiene:
  los abonos de clientes, el saldo a favor que se devuelve y las ventas que quedaron por revisar (lo que no fue en
  efectivo, o fue mixto).

Se probó con pruebas contra el Firebird de prueba (una entrada antes de vender, una salida, sin duplicar, el día y la
medianoche), sin Firebird (la bandeja, también la de contingencia) y de punta a punta contra copias:
- una entrada antes de la primera venta abrió el turno de eleventa; después, una venta, una salida y el corte Z con
  sobrante. Eleventa cerró con lo contado, con las entradas y salidas en el turno y en el día;
- una salida que subió con la bandeja de contingencia también llegó, y el turno cerró con lo contado;
- una entrada que subió después del corte Z quedó por revisar.

### Lo que falta para el piloto: diseño (2026-10-12, por aprobar)

Con las decisiones del dueño del 2026-10-12 ([plan](plan.md#la-versión-unicaja-y-el-piloto-en-la-caja-2)): solo
efectivo en la caja 2, eleventa abierto en la caja 2, devoluciones de ida y vuelta, la regla de adaptación y, al aprobar
el piloto, el modo multicaja. Va después de la pantalla nueva para la prueba del 17 y 18 de octubre; lo que se puede ver
antes en la tienda está en la [guía](pruebas-en-tienda.md#fin-de-semana-del-17-y-18-de-octubre). Todo lo de abajo es
puente: vive en `PapeleriaPos.Eleventa` y en tablas del puente, y se quita al dejar eleventa.

- **Primero, los riesgos verificados** (en curso, un PR por cada uno): el cobro que no espera el último escaneo y el
  escaneo que se toma como dinero en «Pagó con»; solo efectivo mientras se escribe en eleventa; la lectura de cada 5
  minutos que deshace lo que sigue en la bandeja; y la entrega 10 (la base contra apagones).
- **Eleventa abierto en la caja 2, en el mismo turno.** Hay una sola persona en la caja 2: vende en uno o en el otro.
  - La caja nueva adopta el turno abierto de esa caja en eleventa y guarda su `TURNOS.ID`; si no hay, lo abre al
    mandar lo primero, como hoy. Nunca deja de vender por esperar a eleventa.
  - El puente no abre un segundo turno: si el turno adoptado ya no está abierto o hay dos, lo que llegue queda por
    revisar, con aviso.
  - **El corte se hace en eleventa** (decisión del dueño, 2026-10-12): mientras dure el piloto, el puente no cierra el
    turno de eleventa (hoy el corte Z de la caja nueva lo cierra y borra los tickets abiertos del turno,
    `EnvioEleventa.Devoluciones.cs`). Por ahora, al intentar el corte o al salir, la caja nueva avisa que el corte se
    hace en eleventa. Después, la caja nueva toma ese corte: al ver cerrado el turno adoptado, cierra el suyo como
    «corte hecho en eleventa», sin conteo y con lo contado allá; lo que siga en la bandeja de ese turno queda por
    revisar, porque allá ya se contó.
  - Si se cae la red, se sigue en la caja nueva (contingencia): el eleventa de la caja 2 escribe en el Firebird de la
    principal y no vende sin red. Si falla la caja nueva, se cambia a eleventa.
- **Devoluciones de ida y vuelta.**
  - Ida: si el folio no es de la caja 2, se lee el ticket en vivo en eleventa (el ticket, sus artículos y sus
    devoluciones). Solo efectivo, 7 días y las reglas de la caja nueva. Si eleventa no contesta, se espera a que
    vuelva la red. El ticket leído se guarda en tablas del puente, no como venta en el núcleo.
  - Vuelta: la lectura de cada 5 minutos trae, en la misma foto que las existencias, las devoluciones y cancelaciones
    de los tickets que mandó la caja 2, y las aplica antes de las existencias. Lo que vino de eleventa no entra a la
    bandeja. Lo propio se reconoce por `DEVOLUCIONES.ID` y por la marca `TICKET_ID` + `DEVUELTO_EN`; la lectura y el
    envío usan el mismo candado. Las listas `IN` van en grupos de menos de 1500 (límite de Firebird 2.0).
  - Antes de devolver un ticket de la caja 2 que ya está en eleventa, se revisa en vivo que allá no esté devuelto; si
    eleventa no contesta, se deja pasar (decisión del 2026-10-11) y un choque queda por revisar.
  - Un choque con eleventa («ya está cancelado») queda por revisar y no detiene la bandeja; solo la falla de red se
    reintenta. Lo que quede por revisar se ve en la pantalla de la caja 2 (el bot de Telegram todavía no existe).
  - Falta ver en la copia cómo anota eleventa una devolución de otra caja o de otro turno: mientras, eso queda por
    revisar.
- **Folios repetidos.** Justo después de guardar, el puente revisa si el folio ya lo tiene otro ticket; si todavía no
  se entregó, toma el siguiente y corrige solo su propio ticket y su kardex. Si ya salió impreso, no se cambia: ese
  ticket se devuelve en la caja 2 por su QR y sus reimpresiones salen con el folio de la caja 2, como cuando eleventa no
  contesta. La revisión tardía va en su propio paso, aunque la bandeja esté atorada.
- **Los respaldos de eleventa.** Según sus páginas (consultadas el 2026-10-12): el [respaldo
  automático](https://eleventa.com/aprender/respaldo-automatico) se configura en Configuración ➜ Mantenimiento y puede
  respaldar al salir; en multicaja queda en `AbarrotesMultiCaja\respaldos` de la PC servidor;
  [restaurar](https://eleventa.com/aprender/restaurar-eleventa) regresa a la fecha del respaldo y pierde lo de después;
  la principal debe tener eleventa abierto para que las otras se conecten
  ([multicaja](https://eleventa.com/aprender/conexion-multicaja)). Su [historial de
  cambios](https://descargas.eleventa.com/HistorialCambios-Produccion.html) dice que, si el respaldo automático detecta
  la base dañada, «intenta restaurar de tu respaldo más reciente de forma automática», y que hubo avisos falsos de base
  dañada con el punto de venta o cajas secundarias abiertas. Si eso pasa con la caja 2 escribiendo, se pierde lo del día
  en eleventa. Por eso:
  - el puente detecta una restauración (tickets o turnos que mandó y ya no están, o números que regresan) y entonces
    deja de escribir y avisa; se reanuda a mano;
  - el puente hace su propio respaldo diario de eleventa con `gbak` (sirve en Firebird 2.0), antes de la primera
    escritura del día;
  - en la tienda se mira, sin cambiar nada: la versión de eleventa, su respaldo automático, la carpeta de respaldos y
    si existe `gbak.exe` ([guía](pruebas-en-tienda.md#fin-de-semana-del-17-y-18-de-octubre)).

  Sin verificar: si eleventa respalda con `gbak` o copiando el archivo, si saca a las otras cajas mientras respalda, y
  si la versión de la tienda sigue restaurando sola.
- **El modo multicaja, al aprobar el piloto (propuesta):** un solo servicio en la caja principal, como quedará al final;
  la base de la caja 2 se muda ahí y la caja 2 se conecta a él. El puente se muda con él y escribe en el Firebird de la
  misma PC, una venta a la vez, con el `CAJAS.ID` y el cajero de cada caja. Eleventa sigue abierto en las dos cajas.
  Al final del día: el corte de la caja 2 y salir de su eleventa, luego el de la principal; el puente cierra el turno de
  la principal cuando ya no ve eleventa ni su respaldo corriendo. Falta medir que la principal aguante eleventa,
  Firebird y el servicio juntos.
- **Para la importación final:** las ventas que mandó el puente ya están en la base nueva; la importación de eleventa
  las excluye por `VENTATICKETS.ID`, para no contarlas dos veces.

## Arranque (fase 5)

1. Depuración del catálogo sobre la base importada ([plan](plan.md#depuración-del-inventario)): bajas, duplicados,
   equipo de la tienda, unidades y servicios.
2. Conteo físico completo, por departamentos, en las semanas previas. Reemplaza todas las existencias de eleventa.
3. Arranque en un día tranquilo.
4. Eleventa queda instalado solo para consulta unas semanas, como respaldo, con un plan para regresar si algo falla.
5. Capacitación a cajeros y encargado antes del arranque, con datos de prueba.
