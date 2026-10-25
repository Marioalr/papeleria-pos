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
| Unidades | `MEDIDA_ID` → `MEDIDAS`: 1 pieza, 3 metro, 4 kilo, 5 litro. Muchos productos a granel tienen KG aunque se venden por metro o por pieza suelta (la medida KG está desactivada y la tienda no tiene báscula). Las impresiones son a granel con la medida pieza (`TVENTA` 'D', `MEDIDA_ID` 1) |
| Departamentos | `DEPARTAMENTOS` (`DEPTS` es una tabla vieja) |
| Existencias | `INVENTARIO_BALANCES.CANTIDAD_ACTUAL`; `PRODUCTOS.DINVENTARIO` está desactualizado |
| Mínimo y máximo | `PRODUCTOS.DINVMINIMO` y `DINVMAXIMO` (DOUBLE PRECISION), los de «Productos bajos en inventario», en la unidad del producto; 0 o vacío, sin. La tienda los usa: en cerca de 4 de cada 10 productos activos, siempre iguales (1, 2, 5, 10, 50, 100…), y la mitad de esos en su mínimo o debajo (base del 2026-10-24). `PRODUCTOS.CHECADO_EN` es una fecha sin hora: no dice cuándo cambió un producto |
| Tickets | `VENTATICKETS`: `VENDIDO_EN` (hora local; vacío si el ticket quedó abierto), `ESTA_CANCELADO` 't'/'f'. Solo efectivo |
| Líneas | `VENTATICKETS_ARTICULOS`: `GANANCIA` es la utilidad **por pieza** (costo de la línea = importe − cantidad × ganancia; hasta el 2026-10-11 se importaba como si fuera de toda la línea). **Las líneas viejas** (en la tienda, todas las de antes del 2024-10-05) **no tienen `TOTAL_ARTICULO` ni `PRECIO_FINAL`:** el precio está en `PRECIO_USADO` y el importe es `PRECIO_USADO × CANTIDAD`, y así todos los tickets cuadran (hasta el 2026-10-24 se leía `PRECIO_FINAL` y entraban en $0, con el costo en negativo) |
| Mayoreo | `PMAYOREOFINAL`: un segundo precio por pieza que el cajero **activa a mano** en la venta (`USA_MAYOREO`): se escriben las piezas del paquete y F11. Un producto «sin mayoreo» lo tiene igual a `PFINAL` (casi dos de cada tres en la tienda) o en cero, y eleventa no lo mueve al cambiar el precio: al subirlo, se queda en el de antes. Las escalas por cantidad están en `PROMOCIONES_POR_CANTIDAD` |
| Descuento | En el renglón, `PORCENTAJE_DESCUENTO`: cuánto bajó contra su precio de antes del descuento (el de menudeo o, con F11, el de mayoreo), en por ciento con cuatro decimales, y el precio ya rebajado en `PRECIO_USADO` y `PRECIO_FINAL`. El ticket guarda en `TOTAL_AHORRADO` todo lo ahorrado contra el precio de lista (el mayoreo y el descuento), y una devolución no lo cambia (base del 2026-10-24: así los 59 tickets con descuento) |
| Texto | Charset NONE con texto en **Windows-1252** (la ñ es el byte F1): se lee con la conexión en WIN1252 |
| Proveedores | `PROVEEDORES` está vacía; `PROVEEDORES_BASE` y `PRODUCTOS_BASE` son catálogos que trae eleventa, no de la tienda |
| Servicios | Impresiones, copias, engargolado y escaneo son productos a granel con fracciones ("0.8 a $10" = 8 impresiones de $1) |

## Qué se importa

- **Catálogo:** cada producto de eleventa queda como un producto con una sola variante, con su código, precio y
  departamento. Los borrados entran inactivos. Lo vendido con un código que ya no existe se crea como producto
  inactivo, para no perder esas ventas.
- **La unidad** sale de `TVENTA` y `MEDIDA_ID`: por pieza o, a granel, metro, litro o **pieza a granel**: con fracciones y sin
  «kg» en el ticket («0.2», 2026-10-24). Así entran las impresiones (la medida pieza) y, desde la revisión del PR #139
  (decidido, por revisar), lo que tiene la medida KG: en la tienda está desactivada y no hay báscula, y casi todo eso se vende
  por pieza suelta o por metro; su unidad se decide en la depuración (lo dice el aviso). Lo a granel cuyo nombre dice metro,
  metro. Antes todo eso entraba como kilo; en una base importada antes, la lectura del piloto corrige ese kilo.
- **Último costo** (`PCOSTO`), bajo un proveedor genérico, porque eleventa no tiene proveedores capturados.
- **Existencias** como inventario inicial al costo de eleventa (solo como referencia: el inventario de arranque sale
  del conteo físico). Las negativas no entran. Se redondean a la milésima como en la lectura: lo que el FLOAT de eleventa
  deja casi en cero (−0.0000012) es cero, no una existencia negativa (2026-10-24).
- **El mínimo y el máximo de inventario** (2026-10-24), los de «Productos bajos en inventario», en la variante de cada
  producto: en su unidad (a granel, metros o piezas sueltas) y con la misma conversión que la existencia; 0 o vacío, sin mínimo (o
  sin máximo). El mínimo igual al máximo, como los pone la tienda, se acepta: con eso o menos sale en «Bajos en
  inventario» y pide lo que falta para el máximo. Lo que la ficha no aceptaría no se toma y se avisa (media pieza en un
  producto por pieza, más de un millón); un máximo menor que el mínimo se deja fuera y queda el mínimo.
- **Historial de ventas** completo, con cancelados marcados: alimenta el análisis ABC, el sugerido de compra, la
  temporada y los reportes de ventas (el año pasado). Cada ticket trae lo que eleventa le bajó al devolver: lo devuelto
  cuenta el día de la venta. Su **artículo común** (código `COMUN-…` o, con un código que no es del catálogo, el
  departamento «Productos Comunes») queda marcado: los reportes lo cuentan como el de aquí, aparte y sin utilidad (eleventa
  le pone de ganancia todo el precio). Todos van a un solo producto inactivo, «Artículo común de eleventa», en el
  departamento de los comunes de eleventa (2026-10-24: antes, un producto por cada uno; 127 en la base de la tienda). Lo
  que llega sin departamento va al «- Sin Departamento -» de eleventa, sin crear otro junto a él. Las líneas viejas
  entran con su `PRECIO_USADO`. Un ticket cuyo total no es la suma de sus artículos se avisa, y en los reportes la
  diferencia sale «Sin desglose» (revisión del PR #131, 2026-10-22; lo ya importado se marca al migrar la base).
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
inexistentes, mayoreo deducido y por revisar, promociones repetidas, existencias negativas, mínimos que no se tomaron y
tickets abiertos.

### Qué columna de eleventa va a dónde

| Eleventa | En el POS | Importación (base nueva) | Importación final | Lectura del piloto |
|---|---|---|---|---|
| `DEPARTAMENTOS` | Departamentos | Sí | — | Sí: altas, nombre y si está activo |
| `PRODUCTOS.CODIGO` | Código de la pieza | Sí (repetido, del primero) | — | Sí, si aquí no lo tiene otro producto |
| `DESCRIPCION`, `DEPT`, `ELIMINADO_EN` | Nombre, departamento, inactivo | Sí | — | Sí (el nombre, si es de una sola variante) |
| `TVENTA`, `MEDIDA_ID` | Unidad: pieza, pieza a granel, metro, litro (el kilo, en la depuración) | Sí | — | No: si cambia allá, avisa. Solo corrige el kilo de una importación de antes (2026-10-24) |
| `PFINAL` | Precio de menudeo | Sí | — | Sí |
| `PCOSTO` | Último costo, con un proveedor genérico | Sí | — | Sí |
| `PMAYOREOFINAL` | Escala deducida de las ventas con F11, o mayoreo por revisar | Sí | — | El precio de su escala, o por revisar (2026-10-24); igual a `PFINAL`, sin mayoreo |
| `DINVMINIMO`, `DINVMAXIMO` | Mínimo y máximo de la variante | Sí (2026-10-24) | Una sola vez, a lo que no tenga ninguno, si la base no los trajo | Sí, cada vez (2026-10-24) |
| `INVENTARIO_BALANCES.CANTIDAD_ACTUAL` | Existencia | Inventario inicial | — | Ajuste en el kardex |
| `PROMOCIONES_POR_CANTIDAD` | Escalas por cantidad | Sí | — | Sí: se agregan, cambian y se quitan |
| `VENTATICKETS`, `VENTATICKETS_ARTICULOS` | Historial de ventas (en las líneas viejas, con `PRECIO_USADO`) | Sí | Sí, sin lo de la caja 2 | No |
| `DEVOLUCIONES`, `DEVOLUCIONES_ARTICULOS` | Lo devuelto de un ticket | Ya bajado en el ticket | Para decir de quién es cada una | Solo las de los tickets de la caja 2: la vuelta |

La lectura compara todo el catálogo cada vez: eleventa no anota cuándo cambió un producto, así que un cambio solo del
mínimo también llega.

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

- **Qué lee:** departamentos, productos (nombre, código, precio, costo, mayoreo, borrados, y desde el 2026-10-24 el
  mínimo y el máximo de inventario), existencias y promociones por cantidad. Las ventas no, porque pesan y no hacen falta
  cada pocos minutos. Lee todo el catálogo cada vez y lo compara con la base: lo que cambió, aunque sea una sola columna,
  llega ([qué columna va a dónde](#qué-columna-de-eleventa-va-a-dónde)).
- **Cómo:** en una transacción de solo lectura y con foto fija, así todo es del mismo momento. No escribe nada en
  eleventa. Con la base real tarda menos de un segundo.
- **Qué cambia aquí** (eleventa manda):
  - las altas, con su código, precio, costo y existencia;
  - el nombre, el departamento, el código y si está activo;
  - el precio de menudeo y las promociones por cantidad: se agregan, cambian y se quitan;
  - el costo;
  - la existencia, con un ajuste en el kardex al costo promedio y a nombre de `lectura-eleventa`;
  - el mínimo y el máximo de inventario (2026-10-24; decidido, por revisar): en el piloto el inventario se lleva en
    eleventa, así que eleventa manda también en los mínimos. Se ponen cada vez, también si allá se los quitan (0 o vacío);
    lo que la ficha no aceptaría (media pieza en un producto por pieza) se avisa y deja lo de antes. Un servicio no los
    lleva. Antes eran del POS y en el piloto se ponían aquí;
  - el mayoreo (F11) de lo que ya estaba (2026-10-24; decidido, por revisar): los precios se ponen en eleventa, así que
    su cambio también llega. Se compara con lo último que se supo de él (lo de cada producto queda anotado y no se repite;
    revisión del PR #139):
    - el mismo, nada, aunque el menudeo baje un rato abajo de él: la escala se queda (la caja nunca cobra una escala más cara
      que el menudeo) y vuelve a contar cuando el menudeo lo pasa;
    - si eleventa se lo quita (el precio de mayoreo igual al menudeo, como lo deja eleventa, o en cero), la escala se quita;
    - si cambia y aquí está su escala (la deducida al importar o la decidida en F3), la escala toma el precio nuevo, sin que el
      precio por pieza baje y luego suba con la cantidad (arriba del menudeo se queda sin cobrarse); si no (sin escala todavía,
      o uno que ya no cuadra), queda en «Mayoreo por revisar» (F3) con un aviso, como el de un producto nuevo;
    - uno nuevo, abajo del menudeo, queda por revisar con un aviso. El menudeo de antes que eleventa dejó al subir el precio de
      uno sin mayoreo no es un mayoreo nuevo: se anota sin aviso (en la tienda, casi dos de cada tres productos; antes, cada
      aumento así llenaba F3);
    - si la escala del F11 y una promoción por cantidad de eleventa son de la misma cantidad, comparten el renglón y manda la
      promoción: quitar el F11 no se lleva la promoción, y quitar la promoción regresa el renglón a la escala del F11, si
      eleventa la sigue teniendo.
- **Qué no toca:**
  - lo dado de alta aquí (con su mínimo y su máximo);
  - desde cuántas va una escala de mayoreo (la deducida del F11 o la capturada en F3): solo su precio, arriba;
  - la unidad (si cambia allá, da un aviso). Solo corrige lo que una importación de antes puso en kilo (a granel con la
    medida pieza, como las impresiones, o con la medida KG, desactivada en la tienda): pasa a pieza a granel, una vez y con
    un aviso, y el ticket deja de decir «kg» (decidido, por revisar: en el piloto la unidad no se cambia aquí). Lo que eleventa
    tiene en metros o litros no se toca;
  - el nombre de un producto que la depuración ya agrupó;
  - lo juntado en la depuración.
- **Lo que necesita revisarse** queda como aviso, por ejemplo un código que aquí ya tiene otro producto. El mayoreo de
  un producto nuevo queda en F3 por revisar, porque todavía no tiene ventas con F11, y también el nuevo o cambiado de uno
  que aquí no tiene su escala.
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
    y las promociones de eleventa; y su mínimo y su máximo de inventario (2026-10-24: la ficha los enseña sin dejarlos
    cambiar y no los manda; volverlo servicio sí se puede, y los deja). Tampoco se da de baja, se junta ni se le cambia
    la unidad. Sí se cambian las escalas
    del F11 o de F3 (también el mayoreo por revisar), las otras presentaciones, la marca, las variantes nuevas y lo dado
    de alta aquí, que va a eleventa como artículo común. Al cambiar precios de varios a la vez (Productos › «Cambiar
    precios», 2026-10-17), igual: de lo de eleventa solo cambia su mayoreo de aquí, sin pasar de su menudeo ni de sus
    promociones, y la vista previa lo dice;
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
`C:\ProgramData\PapeleriaPos\servicio.json`, nunca aquí ni en el `appsettings.json` del programa, que se reemplaza al
actualizar ([actualizaciones](arquitectura.md#actualizaciones)). La clave va solo en la variable de entorno
`Eleventa__Clave` del servicio, nunca en un archivo.

```json
"Eleventa": { "Activo": true, "Servidor": "<la caja principal>", "Base": "C:\\…\\PDVDATA.FDB", "Usuario": "SYSDBA", "Minutos": 5 }
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
- **Turnos:** uno abierto por caja (`UNIQUE (ID_CAJA, TERMINO_EN)`, `UNQ_TURNO_ABIERTO_CAJA`: con Firebird 2.x no
  deja dos turnos de la misma caja sin `TERMINO_EN`). En el piloto la caja 2 nueva adopta el que abre eleventa y toma
  su corte ([abajo](#lo-que-falta-para-el-piloto-diseño-2026-10-12-por-aprobar)).
- **Las otras operaciones del corte** van en `CORTE_MOVIMIENTOS`, de tipo `Entrada`, `Salida` y `Devolucion` (paso 4).
- **El día es el del cobro:** el acumulado de `CORTE_OPERACIONES` es lo vendido ese día en esa caja (cuadra en todos
  los días de la copia). Si un turno pasa de la medianoche, lo cobrado después suma al renglón del día siguiente.
- **Un producto va una sola vez por ticket:** si se escanea otra vez, sube la cantidad.
- **`NUMERO_ARTICULOS`** cuenta las piezas, y uno por cada producto a granel (`TVENTA = 'D'`).
- **El mayoreo (F11):** la línea lleva `USA_MAYOREO = 't'` y el ticket, en `TOTAL_AHORRADO`, lo ahorrado contra el
  menudeo: (menudeo − mayoreo) × cantidad. La caja 2 lo escribe igual en lo que cobró con una escala de mayoreo
  (2026-10-24; antes iba como menudeo, sin lo ahorrado).
- **El descuento:** la línea lleva su `PORCENTAJE_DESCUENTO` y el precio ya rebajado, y el descuento cuenta en el
  `TOTAL_AHORRADO` del ticket (arriba, «Descuento»). La caja 2 lo escribe igual con lo que rebajó (Ctrl+D, una promoción o el
  precio cambiado a la baja; revisión del PR #139): el porcentaje, contra el precio de antes del descuento, sin el redondeo del
  efectivo, que va en la línea más grande para que el total sea lo que entró a la caja y no es descuento.
- **«Ticket N»** cuenta los tickets cobrados del turno, sin el abierto (2026-10-24: la caja 2 contaba también el abierto).
- **El artículo común** lleva el código `COMUN-<número al azar>-<el contador de artículos antes de su renglón>`, el
  departamento «Productos Comunes», ganancia igual al precio y nada de inventario.
- **El código del producto** se guarda en las ventas y devoluciones con hasta 20 letras (`TCODIGOPRODUCTO`), aunque el
  producto admite 50; en la tienda el más largo tiene 20 (2026-10-24). La caja 2 no lo recorta (sería otro producto): una
  venta con un código más largo queda por revisar, para anotarla a mano, y la bandeja sigue. Un producto sin descripción
  o sin departamento allá va con el nombre de aquí y en el «- Sin Departamento -» de eleventa.
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
- **El folio** es el último más uno, tomado dentro de la transacción. Justo después revisa que otra caja no haya cobrado
  con el mismo; si pasó, el ticket de la caja 2 toma el siguiente, porque todavía no se entrega con él
  ([folios repetidos](#lo-que-falta-para-el-piloto-diseño-2026-10-12-por-aprobar)).
- **Lo de cada producto lo toma de eleventa:** su código, nombre, departamento y costo de ese momento. La caja nueva
  manda el ID de eleventa, la cantidad y el importe.
- **Lo que eleventa no tiene** (un artículo común, un producto dado de alta solo en la caja nueva o borrado en eleventa)
  va como artículo común. Así el total de eleventa es lo que entró a la caja.
- **Si eleventa no tiene un turno abierto para esa caja,** lo abre como lo abre eleventa: la hora y la IP en `CAJAS`, el
  renglón del día, el turno en ceros con el fondo y su ticket abierto. Cerrarlo es del paso 4.
- No pone los apartados que eleventa pone al escanear: la caja nueva escanea contra su propia base, y al cobrar ya no
  quedaría ninguno.
- **La nota del ticket (F4 al cobrar, 2026-10-22) no va:** `VENTATICKETS.NOTAS` sigue vacía. La columna existe (un BLOB
  binario, con `IMPRIMIR_NOTA` junto), pero no se sabe cómo la guarda eleventa (¿texto en Windows-1252, RTF?) ni si su
  reimpresión la saca, y escribir algo que eleventa no espera en su base real no vale el riesgo. La nota se queda en el
  POS (en su base, en el ticket de la caja 2 y en sus reimpresiones). Cuando pase una venta con nota en eleventa, se ve
  con `observar-eleventa` y el puente la puede mandar.

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
- **El ticket** imprime «Folio 4521» (el de eleventa) cuando lo hay. Desde el 2026-10-16 el ticket no lleva QR
  («primero igualar a eleventa»): para devolver se escribe el folio impreso, el de eleventa o el de la caja (B-000123).
  Si eleventa repitió ese folio en otra caja, la caja enseña los dos tickets y se elige por la hora y el total.
- **Lo que eleventa no entendería** queda por revisar y no se manda solo: lo que no fue en efectivo (la tienda solo
  cobra en efectivo). Lo que se canceló antes de intentar mandarlo se descarta: allá nunca existió. Pero si ya se
  intentó mandar, cualquier intento pudo dejarla en eleventa (se cortó por tiempo o por la red después de que eleventa
  la guardó), aunque el último ni se haya conectado: antes de descartarla se busca allá por su marca, y si está, queda
  como enviada y se manda su cancelación (revisiones del 2026-10-17). Si se descartara, eleventa la seguiría teniendo
  como vendida y la lectura no lo sabría: la existencia se iría corriendo. Mientras eleventa no conteste, no se sabe y
  no se descarta.
- **Al restaurar un respaldo de la base de la caja 2** (2026-10-23, [cómo](arquitectura.md#respaldos)): eleventa no se toca, y
  la bandeja regresa como estaba en el respaldo; lo que se mandó después vuelve a estar pendiente, a veces sin ningún
  intento. Mandarlo otra vez no lo duplica (la marca de arriba). Pero «nunca se intentó» ya no quiere decir «no está en
  eleventa»: al restaurar, todo lo pendiente queda con al menos un intento, así cancelarlo aquí, dejarlo por revisar, la
  lectura de las existencias y la búsqueda de un folio también lo buscan allá antes.
- **El redondeo del efectivo** va en la línea más grande, para que el total de eleventa sea lo que entró a la caja.
- **Cómo va:** `GET /api/eleventa/envios` dice cuántas ventas están pendientes y por revisar, el último error («No
  contesta el eleventa de…») y los folios repetidos. `POST /api/eleventa/enviar` hace la vuelta ya.

Se configura en el servicio de la caja 2, junto con la lectura, en `servicio.json` (la clave, solo en la variable de entorno
`Eleventa__Clave`). La caja y el cajero son los `ID` de eleventa (`CAJAS.ID` de la caja 2, y el cajero a cuyo nombre van
las ventas):

```json
"Eleventa": { "Activo": true, "Escribir": true, "Servidor": "<la caja principal>", "Base": "C:\\…\\PDVDATA.FDB", "Usuario": "SYSDBA", "Caja": 0, "Cajero": 0 }
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
  - la devolución en la caja 2 de un ticket que se cobró en eleventa (la ida,
    [abajo](#lo-que-falta-para-el-piloto-diseño-2026-10-12-por-aprobar)), también en la misma transacción;
  - el corte Z de un turno que mandó algo, como el cierre del turno de eleventa;
  - lo que dependía de una venta descartada también se descarta; lo de una venta por revisar, también queda por revisar.
- **Dónde se aplica:** lo devuelto va al turno y al día en que se devuelve, que es cuando sale el dinero de la caja.
  Eleventa no lo ha hecho entre turnos, pero así cuadran sus cortes.
- **Devolver todo lo que le queda al ticket es cancelarlo** (2026-10-24; decidido, por revisar): eleventa nunca deja un
  ticket vendido en cero y sin artículos, y la tienda, para eso, lo cancela. Así lo escribe la caja 2: `C`, sin
  `DEVOLUCIONES_ARTICULOS` y con `NUMERO_VENTAS` uno menos; con una parcial antes, es la cancelación de lo que quedó, como
  la de eleventa. Sale lo que salió de la caja 2. Aquí sigue siendo una devolución (cancelar es de otro permiso y solo del
  mismo día), y la ventana lo dice antes de confirmar.
- **Lo que no se puede mandar solo:**
  - si algo no entra, lo que sigue espera en la bandeja;
  - si ya se arregló a mano en eleventa, `POST /api/eleventa/envios/{id}/a-mano` lo anota y lo demás pasa; si todavía
    falta revisarlo, `POST …/revisar` lo deja por revisar, lo demás pasa y la lectura lo sigue descontando hasta
    anotarlo a mano. Los dos los hace quien entró con «Configuración de la tienda», y quedan en la bitácora (desde el
    2026-10-17);
  - lo que volvió dañado no regresa al inventario de eleventa.
- **El cierre** usa la diferencia del corte Z de la caja 2 (contado menos esperado). Con las entradas y salidas ya en
  eleventa (abajo), su efectivo al cierre queda en lo contado.

**Mientras dure el piloto el corte de la caja 2 se hace en eleventa** (decisión del dueño, 2026-10-12; `CorteEnElPiloto`,
solo para convivir con eleventa):
- el servicio rechaza el corte Z de esa caja, y la caja lo avisa al intentar el corte y al salir;
- el turno de eleventa lo abre eleventa con su fondo y lo cierra su corte: el puente no lo abre ni lo cierra. Si allá no
  hay turno abierto, lo que se manda espera en la bandeja («Abre la caja en eleventa…») y entra en cuanto lo abran;
- un cierre que ya estaba en la bandeja, o el de un corte Z que sube de la contingencia, queda por revisar;
- la caja nueva toma el corte de eleventa: cierra su turno cuando eleventa cierra el que adoptó (2026-10-16,
  [abajo](#lo-que-falta-para-el-piloto-diseño-2026-10-12-por-aprobar)). Mientras eleventa no lo cierre, el turno de la
  caja nueva sigue abierto (puede durar días), así que solo se cancela un ticket del mismo día (después es devolución) y
  no sale el aviso de retiro (el cajón es de las dos).

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
  - **Avance (2026-10-16), en código** (`TurnosDelPiloto`, tabla del puente `TurnosEnEleventa`):
    - **Adoptar.** El turno de la caja nueva guarda el `TURNOS.ID` (y su `INICIO_EN`) del turno que eleventa tiene
      abierto en la caja 2: lo toma lo primero que manda, o la lectura de cada pocos minutos si allá hay uno solo. Sin
      turno en eleventa no se abre ninguno: lo que se manda espera («Abre la caja en eleventa…»), como desde el #86. Lo
      del diseño, «si no hay, lo abre… como hoy», se quedó como hoy: lo abre eleventa con su fondo. Con dos abiertos
      (la base no lo deja: `UNQ_TURNO_ABIERTO_CAJA`) no adopta ninguno y avisa.
    - **A qué turno va cada cosa: por su hora.** Lo que pasó en la caja nueva va al turno de eleventa que estaba abierto
      a esa hora o, si no había ninguno, al siguiente que se abrió (el primero de la caja que terminó después, o sigue
      abierto). Si ese turno ya se cortó, lo que no alcanzó a llegar ya se contó allá: queda por revisar, con el porqué,
      y no detiene la bandeja. Lo que se cobró después del corte de eleventa (antes de que la caja nueva lo viera, por
      ejemplo con la caja 2 apagada en la noche) entra al turno siguiente: así funciona «después del corte, la siguiente
      venta va al turno siguiente». Por eso no se dejan por revisar todos los pendientes del turno al tomar el corte:
      solo los de antes de la hora del corte.
    - **Tomar el corte.** Cuando la lectura ve cerrado el turno adoptado, cierra el de la caja nueva a la hora del corte
      de eleventa, sin corte Z ni conteo aquí, y guarda lo que se contó allá (`EFECTIVO_AL_CIERRE`) y lo que faltó o
      sobró (el movimiento «Efectivo faltante/sobrante al cerrar turno»). Si en ese turno se cobró después del corte
      (eleventa cortó con la caja 2 apagada y en la mañana se vendió antes de que la lectura lo viera), se cierra a la
      hora de lo último que pasó en él, para que siga en el corte del día; la hora del corte de eleventa se guarda aparte
      (revisión del 2026-10-17). Queda en la bitácora («Corte del turno de Ana
      hecho en eleventa…»). El corte del día enseña ese turno como «En eleventa», con lo contado allá en una nota: es el
      cajón de las dos cajas, así que no entra en lo contado ni en las diferencias del día. Después, cualquier cajero
      abre su turno en la caja nueva, y adopta el siguiente de eleventa.
    - **La lectura y la bandeja usan el mismo candado:** mientras la lectura lee y aplica, la bandeja no manda; el cobro
      no la espera (el ticket sale con el folio de la caja). Con límites (revisión del 2026-10-17): Firebird por sí solo
      espera sin fin a un eleventa que se quedó callado a media llamada, así que cada llamada de la lectura, la bandeja y
      las revisiones espera lo más 2 minutos (`Eleventa:SegundosMaximos`) y después se deja de esperar y se toma como
      falla de red (se reintenta); y la lectura espera el candado lo más el doble: si no, se deja para la siguiente
      vuelta. No se cancela (revisión del 2026-10-17, segunda vuelta): con el Firebird 2.0.1 de la tienda, FirebirdClient
      no sabe cancelar una llamada y, si se le pide, truena el servicio de la caja 2 (hay una prueba que habla con el
      Firebird de prueba como 2.0 y deja callada una escritura). La llamada que se dejó de esperar sigue sola por detrás,
      pero ya no confirma: justo antes de confirmar en eleventa pregunta si todavía se la espera y, si no, se deshace
      (cuarta revisión): si no, una escritura lenta podía entrar cuando aquí ya se había decidido sin ella (por ejemplo,
      descartar una venta que se canceló). Lo que ya había confirmado cuando se dejó de esperar lo encuentra el reintento
      por su marca; la revisión del folio solo lo mueve si todavía tiene el que se revisó (si corre dos veces, la segunda
      no lo mueve otra vez); y un choque de candados (otra escritura tenía los mismos renglones, quizá la que se dejó de
      esperar) no se da por rechazado: se reintenta. Lo
      mismo las lecturas en vivo de la ventana de devoluciones (3 segundos): lo que no contestó se deja con su conexión,
      porque cerrarla también esperaba a eleventa sin fin, con el candado de la bandeja tomado (la bandeja y la lectura
      se quedaban paradas).
    - Antes de que eleventa cierre su turno sigue el aviso de #86: al intentar el corte o al salir, la caja dice que el
      corte se hace en eleventa. Si el corte de eleventa cierra el turno con la caja nueva abierta, lo siguiente que se
      cobre dice que el turno ya está cerrado: se entra otra vez (Ctrl+U) y se abre el nuevo.
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
  - Falta ver en la copia cómo anota eleventa una devolución de otra caja o de otro turno. Mientras, la caja 2 no las
    hace (decidido el 2026-10-17, por revisar con el dueño): se hacen en eleventa y llegan con la vuelta (abajo, «Solo
    lo del turno de ahora»).
  - **Avance (2026-10-16), en código** (`DevolucionesConEleventa`, `TicketsDeEleventa`; tablas del puente
    `DevolucionesDeEleventa`, la ida, y `DevolucionesDesdeEleventa`, la vuelta):
    - **La ida.** El folio que se escribe en la ventana de devoluciones se busca también en vivo en eleventa (hasta 3
      segundos). Si es un ticket de otra caja, o uno que se cobró en el eleventa de la caja 2, se abre ahí como uno de la
      caja, con «(eleventa)» y su caja: lo vendido, lo ya devuelto allá y lo que queda. Se devuelve aquí solo si es del
      turno que eleventa tiene abierto ahora en la caja 2 (se lee en vivo con el ticket; abajo, «Solo lo del turno de
      ahora»), por artículo (el ticket completo no se cancela aquí), si se cobró en efectivo, no está cancelado y tiene 7
      días o menos, con el motivo, el permiso de devolución y quien autoriza, como en la caja nueva. Sin eleventa no se
      devuelve: dice que se espere a la red.
    - Al devolver se vuelve a leer en vivo (otra caja pudo devolver algo mientras) y se guarda todo junto: el ticket como
      se leyó y lo devuelto (en el puente, no como venta del núcleo); la salida de efectivo del turno de la caja nueva
      («Devolución del ticket #4521 de eleventa (Principal): …»; no va a eleventa como salida); la apertura del cajón; la
      entrada al kardex de lo que regresa al inventario, y el envío a la bandeja, que la escribe en eleventa como eleventa
      escribe una parcial, renglón por renglón del ticket. Mientras no llega, la lectura la sigue sumando a las
      existencias.
    - **Lo que sale es exacto, al centavo** (revisión del 2026-10-17): eleventa cobró con centavos y dio el cambio con
      centavos, así que se regresa lo que dice eleventa, como lo regresaría eleventa. De cada artículo, si se devuelve todo
      lo que le queda, sale exacto lo que le queda en eleventa; si es una parte, con la regla de la caja (2026-10-24): lo que
      le toca a lo devuelto hasta ahora con esto, en proporción a lo vendido, menos lo que de verdad ya salió (allá, con el
      redondeo de eleventa, y aquí), sin pasarse de lo que le queda. Antes cada parte se redondeaba por su lado: de tres
      plumas por $20.00, devolver dos de una en una daba $13.34 y juntas $13.33. La ventana pide autorizar justo eso.
      Así el ticket de eleventa queda en cero al devolverlo todo (nunca abajo) y lo que sale del cajón es lo que eleventa
      anota como devuelto. Al peso, como en las ventas de la caja nueva (que se cobraron ya redondeadas), un ticket de
      $9.50 devuelto completo salía en $10 y dejaba el de eleventa en −$0.50. Al escribirla, si eleventa cambió desde que
      se devolvió aquí y ya no le queda tanto a ese renglón, queda por revisar.
    - **Solo lo del turno de ahora** (revisión del 2026-10-17; decidido, por revisar con el dueño). El puente solo sabe
      escribir la devolución de un ticket del turno que eleventa tiene abierto en la caja 2: así la hace eleventa en la
      copia y en vivo, y la de otra caja o de otro turno todavía no se ha visto. Por eso, mientras dure el piloto, la caja
      2 solo devuelve o cancela lo que el puente sabe escribir: sus ventas y los tickets cobrados en el eleventa de la
      caja 2 cuyo ticket es del turno que eleventa tiene abierto ahora en la caja 2. Se ve en vivo, con el ticket (cuarta
      revisión: antes, para sus ventas, se veía su turno aquí, que se cierra hasta que la lectura toma el corte de
      eleventa: se aceptaba lo que ya era de otro turno y se rechazaba lo que se cobró después del corte); sin eleventa,
      para sus ventas, mientras su turno aquí siga abierto. De cualquier otro la ventana de devoluciones enseña el ticket,
      sin ofrecer devolverlo ni cancelarlo (el servicio también lo rechaza), y dice por qué: de una venta de la caja 2,
      «Este ticket es de otro turno: devuélvelo en eleventa (está abierto en esta caja). La devolución llega aquí sola con
      la lectura.»; de un ticket cobrado en eleventa, «Este ticket es de otro turno de eleventa…» o «Este ticket es de otra
      caja de eleventa (Principal)…», sin decir que llega aquí (no es una venta de aquí). Se devuelve en eleventa y, si es
      un ticket de la caja 2, entra a su venta con la vuelta como cualquier otra devolución de eleventa: sin efectivo de
      la caja nueva y sin nada que ligar. Antes la caja 2 los devolvía, quedaban por revisar para anotarlos a mano en
      eleventa, y había que adivinar cuál devolución de eleventa era esa anotación.

      Queda un caso raro: si al devolverla eleventa no contesta (o la bandeja está mandando) y eleventa ya cortó su turno
      sin que la lectura lo viera, o si eleventa corta justo entre que se acepta y se manda, al mandarla ya es de otro
      turno y queda por revisar, para anotarla a mano (abajo). Cuando se vea en la tienda cómo anota eleventa una
      devolución de otro turno o de otra caja
      ([guía](pruebas-en-tienda.md#fin-de-semana-del-17-y-18-de-octubre)), el puente puede aprender a escribirla y la caja 2
      a recibir esas devoluciones también.
    - **La vuelta.** La lectura de cada 5 minutos lee, en la misma foto que las existencias, las devoluciones y
      cancelaciones en eleventa de los tickets que mandó la caja 2 en los últimos 31 días (listas `IN` en grupos de
      1000). Antes de las existencias, cada una entra a su venta de aquí como una devolución del núcleo **sin efectivo**:
      el dinero salió de la caja de eleventa, así que el corte de la caja nueva no la cuenta. Va al turno de la venta, a
      nombre de su cajero, con la hora de eleventa y el motivo «Hecha en eleventa (Caja 2, cajera2, 16/10 12:30): no
      salió de esta caja». La cancelación devuelve lo que queda; la parcial, cada artículo en las líneas de su producto (o,
      si fue común, de su nombre), con lo que le toca por la regla de la caja: lo acumulado menos lo ya devuelto, así varias
      parciales de una línea suman su importe (2026-10-24; antes cada una se redondeaba por su lado y tres de tres podían
      sumar un centavo de más). Si el ticket ya tenía devoluciones, eleventa cancela lo que quedó: aquí es la
      devolución de lo que quedaba y la venta sigue cobrada, como cuando se devuelve todo en la caja (aquí no se cancela un
      ticket con devoluciones; cancelarla restaría dos veces lo ya devuelto en el corte del día y en los reportes; revisión
      del PR #131, 2026-10-22). En la bitácora queda como la cancelación que fue. Lo que regresa entra al kardex al costo
      con que salió, así que las existencias ya
      cuadran con eleventa y no salen como ajuste; los reportes de ventas la descuentan, y queda en la bitácora.
    - La vuelta nunca entra a la bandeja. Lo ya pasado se reconoce por `DEVOLUCIONES.ID` (con su `DEVUELTO_EN`) y lo que
      escribió la caja 2 por la marca `TICKET_ID` + `DEVUELTO_EN`. Si aquí ya no queda tanto (¿se devolvió en las dos
      cajas?), queda por revisar.
    - **La anotación a mano se elige, no se adivina** (revisión del 2026-10-17; decidido, por revisar con el dueño). Con
      «Solo lo del turno de ahora», que una devolución de aquí no pase sola a eleventa es raro: su turno de eleventa se
      cortó antes de mandarla (la lectura todavía no lo veía), se perdió al restaurar un respaldo, o alguien la dejó por
      revisar a mano. Queda por revisar, «anótala a mano», y su anotación en eleventa llega con la vuelta sin la marca de la
      caja 2, con la hora de eleventa: parecería otra devolución. Antes la vuelta adivinaba si era la misma (por sus
      artículos) o preguntaba «¿Es la misma?»; con dos devoluciones del mismo ticket, o una anotada en dos partes, se
      perdía o se repetía algo. Ahora:
      - cada envío que queda por revisar guarda por qué y desde cuándo: «se anota a mano» (de otro turno, de un turno ya
        cortado, perdido al restaurar, dejado por revisar a mano), «eleventa ya lo tenía» (el ticket ya está cancelado, ya
        no le queda eso por devolver; son dos reembolsos y los dos quedan a la vista) u otra cosa. Lo que ya estaba por
        revisar antes de este cambio lo saca de su texto, y desde cuándo, de su último intento. Y lo de eleventa que
        esperaba la pregunta «¿Es la misma?» de una de aquí ya marcada «Ya lo anoté en eleventa» (por ejemplo, anotada en
        dos partes) queda ligado con ella al actualizar: no se vuelve a pasar a la venta (cuarta revisión);
      - mientras una devolución de aquí espera su anotación a mano, la vuelta no pasa a la venta las devoluciones de
        eleventa de ese ticket hechas después (con 10 minutos de holgura por la hora de cada PC) que pueden ser su
        anotación: con algún artículo en común (por su producto de aquí, así dos productos de eleventa con el mismo código
        que aquí son uno cuentan como el mismo), o la cancelación del ticket. Esas esperan; lo que no tiene nada en común
        se pasa como siempre. Y la existencia sigue a eleventa mientras tanto: de lo que devolvió la de aquí (que todavía
        no llega allá y se suma) se descuenta lo que ya devolvió eso que espera, porque si es su anotación se contaría dos
        veces (cuarta revisión: antes se dejaba de ajustar esa existencia, y si se tardaba en decidir se quedaba atrás de
        eleventa). Esos tickets se siguen leyendo aunque sean de hace más de 31 días;
      - al marcarla «Ya lo anoté en eleventa», la pantalla de la caja 2 lee en vivo las devoluciones de eleventa de ese
        ticket de después de la de aquí (que no escribió la caja 2 ni se han pasado o ligado) y quien la anotó elige cuál
        es su anotación, o cuáles, si la anotó en partes. Tienen que devolver juntas lo mismo, artículo por artículo (o las
        dos ser la cancelación del ticket). La de aquí que devolvió todo lo que le quedaba a la venta también puede ser la
        cancelación del ticket, sola: así la escribe el puente y así la anota la tienda (revisión del PR #139). Si no, no se
        puede marcar todavía: lo que sobre o falte no se esconde en la liga. Sin eleventa no se puede marcar. Se ligan por `DEVOLUCIONES.ID`, no se pasan a la venta, y la de aquí queda
        anotada a mano (con lo elegido en la bitácora). Lo que esperaba y no se eligió se pasa en ese momento a la venta,
        leído en vivo, como cualquier otra de eleventa (si eleventa no contesta, en la siguiente lectura);
      - si lo anotado allá no cuadra con nada que se pueda elegir (dos de aquí anotadas en una sola allá, más o menos
        artículos, una cancelación anotada como devolución…), quien tiene «Configuración de la tienda» la marca «Resolverlo a
        mano» (queda en la bitácora, con su nota): la de aquí queda anotada a mano sin ligarse con nada, y lo que eleventa
        devolvió de ese ticket deja de esperar y se pasa en ese momento a la venta como cualquier otra devolución de allá
        (o queda por revisar si aquí ya no alcanza). Desde ahí la existencia sigue a eleventa;
      - si todavía se iba a mandar sola, primero se deja por revisar: desde ahí la lectura espera su anotación;
      - lo que sí llegó solo aunque aquí no se supo (se cortó al contestar, o se dejó por revisar mientras la bandeja lo
        mandaba) no se anota a mano: quedaría dos veces (cuarta revisión). «Dejar por revisar» toma el candado de la
        bandeja y, si ya se intentó mandar, lo busca antes en eleventa por su marca: si está, queda como enviado (sin
        eleventa no se sabe, y no se deja). Y una devolución de aquí que aparece en eleventa con su marca (en la lectura,
        o al ir a elegir su anotación) queda como enviada.

      Ligada, la de aquí no impide devolver aquí lo que queda: su dinero salió de esta caja.
    - **Antes de devolver aquí** un ticket de la caja 2 que ya está en eleventa, se leen en vivo sus devoluciones allá
      (con el candado de la bandeja, hasta 3 segundos) y entran primero. Si eleventa ya le devolvió algo, lo que queda se
      devuelve en eleventa: el reembolso de aquí se redondea con lo que salió de esta caja, y lo de eleventa no salió de
      ella. Si eleventa no contesta, se deja pasar; si al mandarla choca, queda por revisar.
    - **Por revisar, en la pantalla de la caja 2:** arriba, junto al estado de la caja, sale «Eleventa · 2 por revisar»
      cuando hay algo. Ahí está lo que la bandeja dejó por revisar, con el porqué, y las devoluciones de eleventa que no
      se pudieron pasar. Quien tiene «Configuración de la tienda» marca «Ya lo anoté en eleventa» (de una devolución de
      aquí, eligiendo cuál es su anotación allá) o «Ya lo revisé» (queda en la bitácora). También dice lo que espera para
      irse a eleventa y por qué, los folios repetidos y los avisos de la lectura.
- **Folios repetidos.** Justo después de guardar, el puente revisa si el folio ya lo tiene otro ticket; si todavía no
  se entregó, toma el siguiente y corrige solo su propio ticket y su kardex. Si ya salió impreso, no se cambia: sus
  reimpresiones salen con el folio de la caja 2, como cuando eleventa no contesta, y como el ticket ya no lleva QR
  (dueño, 2026-10-16: «primero igualar a eleventa»), al escribir ese folio para devolver la caja enseña los dos tickets
  (el de la caja 2 y el de la otra caja) para elegir por la hora y el total. La revisión tardía va en su propio paso,
  aunque la bandeja esté atorada.
  - **Avance (2026-10-16), en código** (`FoliosRepetidos`, tabla del puente `FoliosRepetidosEnEleventa`): la revisión al
    guardar toma el siguiente folio y cambia «Venta #folio» en su kardex (hasta tres veces; si sigue chocando, queda
    marcado). La venta que se mandó en la vuelta también: su folio de eleventa no se imprimió. La revisión tardía, en
    cada vuelta de la bandeja antes de mandar, vuelve a ver las ventas de la última hora; si otra caja tiene el mismo
    folio, marca la venta (`FolioRepetido`) y anota el otro ticket con su caja, su hora y su total. Al escribir el folio
    en la ventana de devoluciones (`GET /api/eleventa/folio/{folio}`), si son dos, se elige con «Es este»: el de la caja
    2 se abre ahí, y el de otra caja también, leído en vivo (la ida, arriba). Si eleventa no contesta, el de otra caja se
    devuelve en eleventa.
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
  - **Avance (2026-10-17), en código** (`RestauracionDeEleventa`, `RespaldoDeEleventa`; tabla del puente
    `DetencionesDelPuente`):
    - **Ver la restauración, solo con lo que escribió el puente.** Antes de cada escritura, en la misma transacción, y en
      la lectura de cada 5 minutos, en la misma foto, se revisa en eleventa:
      - que sigan ahí los últimos tickets que mandó la caja 2, con su folio y su marca (`CREADO_EN`): si ya no están, o
        ese número ya es de otro ticket (eleventa volvió a cobrar con los números del respaldo), se perdieron;
      - que siga el turno que adoptó, con su inicio;
      - y que los contadores de eleventa (tickets, turnos, devoluciones, entradas y salidas) no estén abajo de lo que ya
        le dieron a la caja 2.

      Lo que eleventa hace siempre no toca nada de eso: cancelar o devolver (también todo lo de un renglón, que lo
      borra), cobrar en otra caja y cortar el turno (que borra su ticket abierto) no dan falsas alarmas.
    - **Si eleventa se restauró, el puente se detiene:** lo que iba a escribir no se escribe y se queda en la bandeja,
      no escribe nada más, la lectura no aplica nada (las existencias, los precios, los turnos y la vuelta de eleventa
      ya no cuadran con lo que mandó la caja 2) y no se revisan folios. La caja sigue vendiendo: los tickets salen con
      el folio de la caja. En la pantalla de la caja 2 sale «Eleventa · detenido», con lo que se vio («ya no está el
      ticket #4522 que mandó el 16/10 10:05…»).
    - **Se reanuda a mano:** quien tiene «Configuración de la tienda» revisa eleventa y toca «Ya revisé eleventa:
      reanudar» (`POST /api/eleventa/reanudar`); queda en la bitácora, con lo que se vio y su nota. Al reanudar, con
      eleventa contestando (si no, no se puede: no se sabría qué se perdió):
      - lo que la caja 2 mandó en los últimos 31 días y ya no está allá como lo dejó (la venta por su ticket, su folio
        y su marca; la devolución y la entrada o salida por su renglón y su marca) queda por revisar, «Eleventa ya no lo
        tiene: se perdió al restaurar un respaldo… Anótalo a mano en eleventa». La venta conserva el folio que salió
        impreso, para devolverla; su número de ticket en eleventa se olvida, porque ya puede ser de otro;
      - si el turno adoptado ya no está, se suelta y la caja nueva toma el que eleventa tenga abierto;
      - el puente vuelve a escribir (primero lo que esperaba en la bandeja) y a leer.

      Decidido aquí, porque el diseño no lo decía: lo perdido no se vuelve a mandar solo, queda por revisar. Volver a
      mandarlo le daría folios nuevos que no son los impresos, a turnos que quizá allá ya no están; lo que queda por
      revisar sigue contando en las existencias de la caja nueva y se ve en la pantalla de la caja 2 hasta anotarlo a
      mano (queda la pregunta para el dueño, abajo).
    - **El respaldo propio del día, con gbak.** Antes de lo primero que escribe cada día, y en cuanto la lectura ve que
      eleventa contesta (así el primer cobro del día no espera a gbak), el puente corre el `gbak.exe` de la caja 2 con
      la base de la principal por la red (`gbak -b -g servidor:ruta archivo`; con Firebird 2.0, gbak escribe el
      respaldo en la PC que lo corre). El usuario y la clave van en el entorno (`ISC_USER`, `ISC_PASSWORD`), no en la
      línea de comandos. Queda `eleventa-AAAA-MM-DD.fbk` en `ProgramData\PapeleriaPos\respaldos-eleventa` (junto a la
      base de la caja 2, fuera de OneDrive), primero como `.tmp` y renombrado solo si gbak terminó bien; se guardan 5
      días. No acepta una carpeta en OneDrive. Se espera a gbak hasta 10 minutos.
    - **Sin el respaldo del día no se escribe en eleventa:** lo que va allá espera en la bandeja (la caja sigue
      vendiendo y el primer ticket del día sale con el folio de la caja mientras gbak termina) y la pantalla de la caja 2
      dice por qué («No se manda nada a eleventa hasta respaldarla: …»); se vuelve a intentar cada 5 minutos. Con
      `RespaldoObligatorio` en falso se escribe aunque no se haya podido, con aviso. Sin la ruta de gbak y con el
      respaldo obligatorio (así viene), no se escribe nada: escribir en el eleventa real pide un respaldo.

    ```json
    "Eleventa": { …, "Gbak": "C:\\…\\bin\\gbak.exe", "CarpetaRespaldos": "C:\\ProgramData\\PapeleriaPos\\respaldos-eleventa", "DiasDeRespaldos": 5, "RespaldoObligatorio": true }
    ```

    Probado con un gbak de mentiras (nunca contra nada real) y contra el Firebird de prueba, haciendo a mano lo que deja
    una restauración (borrar los últimos tickets, regresar el contador y volver a cobrar con esos números). Falta ver en
    la tienda dónde está `gbak.exe` en la caja 2 y probar el respaldo contra la copia en el paso 5.

    Para el dueño: al reanudar después de una restauración, ¿lo que se perdió en eleventa se anota a mano allá (como
    quedó), o la caja 2 lo vuelve a mandar sola, con folios nuevos?
- **El modo multicaja, al aprobar el piloto (propuesta):** un solo servicio en la caja principal, como quedará al final;
  la base de la caja 2 se muda ahí y la caja 2 se conecta a él. El puente se muda con él y escribe en el Firebird de la
  misma PC, una venta a la vez, con el `CAJAS.ID` y el cajero de cada caja. Eleventa sigue abierto en las dos cajas.
  Al final del día: el corte de la caja 2 y salir de su eleventa, luego el de la principal; el puente cierra el turno de
  la principal cuando ya no ve eleventa ni su respaldo corriendo. Falta medir que la principal aguante eleventa,
  Firebird y el servicio juntos.
- **Para la importación final:** las ventas que mandó el puente ya están en la base nueva; la importación de eleventa
  no las trae (por `VENTATICKETS.ID` y su marca), para no contarlas dos veces. Ya está en código (2026-10-17,
  [abajo](#la-importación-final)).

## La importación final

Al dejar eleventa, su historial de ventas entra a la base de la tienda, la que se armó con `importar-eleventa` y donde
corrió el piloto, para los reportes contra el año anterior (entrega 9). Solo el historial: el catálogo ya está (lo siguió
la lectura de cada pocos minutos) y las existencias salen del conteo físico. Primero con `--simular`, que dice qué haría
sin guardar nada:

```
dotnet run --project src/PapeleriaPos.Herramientas -- importar-eleventa --fdb C:\ruta\PDVDATA.FDB --base C:\…\pos.db --simular
dotnet run --project src/PapeleriaPos.Herramientas -- importar-eleventa --fdb C:\ruta\PDVDATA.FDB --base C:\…\pos.db
```

- **Sin lo que mandó el puente** (2026-10-17, `ImportacionFinal` y `LoQueMandoElPuente`). Las ventas de la caja 2 ya están
  en la base, con sus cancelaciones y devoluciones: las de la caja 2 y las que se hicieron en eleventa (la vuelta las pasó
  sin efectivo). Sus tickets no se traen, o contarían dos veces. Se reconocen como el puente reconoce lo suyo, nunca por el
  folio (eleventa lo repite entre cajas):
  - por el número que le dio eleventa (`VENTATICKETS.ID`) y su marca (`CREADO_EN`: cuándo se empezó la venta en la caja 2,
    en diezmilésimas de segundo);
  - si la caja 2 no supo su número (se cortó la red justo cuando eleventa lo guardó), por su marca y la hora del cobro
    (`VENDIDO_EN`).

  Lo que eleventa les devolvió o canceló ya se lo bajó a esos tickets, así que tampoco entra. Las devoluciones
  (`DEVOLUCIONES`) solo se leen para decir de quién fue cada una: de la caja 2 (su marca, `TICKET_ID` + `DEVUELTO_EN`, o la
  anotación a mano que se eligió para una de aquí) o de eleventa (la vuelta, por su `DEVOLUCIONES.ID`).
- **Lo de eleventa entra como en una base nueva:** cada ticket con lo que eleventa le bajó al devolver (también lo que
  devolvió la caja 2 de un ticket de eleventa, la ida) y los cancelados marcados.
- **Se puede correr más de una vez.** Lo que ya estaba igual no se toca; lo que cambió en eleventa desde la importación
  anterior (una devolución, una cancelación) se vuelve a escribir; lo nuevo entra una vez; lo que resultó ser de la caja 2
  se quita, y lo que ya no está en eleventa se deja. Los mapeos dicen qué ticket es cada venta del historial y, también,
  qué ticket de eleventa es cada venta de la caja 2 (`Venta`): si después se quita el puente, otra importación sigue sin
  traerlas.
- **Todo o nada,** en una transacción y con la base apartada para escribir, así que se corre con la caja cerrada. Antes
  deja una copia de la base junto a ella (`pos.antes-de-importar-AAAA-MM-DD-HHmmss.db`). No acepta una base que no se armó
  con `importar-eleventa`, ni una copia de eleventa más vieja que lo que ya se importó; si la copia es de antes de lo último
  que mandó la caja 2, lo dice primero (conviene sacar otra y repetirla).
- **Lo vendido con un producto que la base no tiene** (se dio de alta en eleventa después de la última lectura) se da de
  alta inactivo, con su Id de eleventa; lo vendido con un código que ya no es de ningún producto, como al importar una base
  nueva, y los artículos comunes, al producto de los comunes (uno solo, 2026-10-24). Una vez: al repetirla se usan los
  mismos.
- **Las líneas viejas que leyó mal una importación de antes** (en $0, sin `PRECIO_FINAL`) se corrigen con su
  `PRECIO_USADO` y se cuentan aparte, como corregidos: eleventa no las cambió (2026-10-24).
- **Del catálogo, solo el mínimo y el máximo de inventario** (2026-10-24; decidido, por revisar): los de eleventa, a sus
  productos que aquí no tienen ninguno, una sola vez: si la base no los trajo ya (se armó antes de que la importación los
  trajera y no siguió la lectura del piloto). La base nueva, la lectura del piloto y la primera importación final que los trae
  dejan la marca de que ya se trajeron; desde ahí son del POS: lo que tienen no se pisa y repetirla, también después de
  arrancar, no regresa los de eleventa, tampoco el que se quitó en F3 (revisión del PR #139: antes «sin ninguno» no
  distinguía «nunca tuvo» de «se lo quitaron aquí»). Dice cuántos puso, o que ya se habían traído.
- **Dice qué hizo:** cuántos tickets entraron, cambiaron, ya estaban o se quitaron; cuántos de la caja 2 no se trajeron,
  cuánto suman, cuántos estaban cancelados o tenían devoluciones (de la caja 2 o de eleventa); cuántas ventas de la caja 2
  no llegaron a eleventa (allá no hay nada que quitar), y cómo cuadra: lo que suma eleventa es el historial más lo de la
  caja 2.
- **Lo que no se decide solo queda en una lista, para revisarlo a mano** (decidido el 2026-10-17, por revisar: no se quita
  ni se aplica nada por adivinar):
  - una venta de la caja 2 que se anotó a mano en eleventa, o que quedó para anotarse: allá no lleva la marca y se trae
    como de eleventa; si se anotó, cuenta dos veces. Dice qué tickets de eleventa pueden ser (el mismo total, al peso,
    cobrados después);
  - una que se mandó y eleventa ya no tiene (¿se restauró un respaldo?), o que eleventa tiene dos veces;
  - una que aquí se canceló antes de llegar, o que sigue pendiente, y que eleventa sí tiene (no se trae). Desde el
    2026-10-17 la bandeja busca en eleventa la cancelada antes de descartarla, así que sería de antes;
  - una devolución que se hizo en eleventa de un ticket de la caja 2 y no está en su venta (la lectura no la trajo, o quedó
    por revisar): como ese ticket no se trae, no cuenta en ningún lado hasta pasarla. Si puede ser la anotación a mano de
    una devolución de aquí que todavía nadie elige («Ya lo anoté en eleventa»; mientras, la vuelta la retiene), lo dice:
    si es esa, aquí ya está. La que ya se eligió cuenta como de la caja 2;
  - una devolución de la caja 2 de un ticket de eleventa (la ida) que no llegó a eleventa: el ticket se trae sin ella y
    cuenta de más, salvo que se haya anotado a mano allá. Desde que la caja 2 solo devuelve lo del turno de ahora es raro:
    eleventa cortó ese turno antes de que llegara.

Se probó sin Firebird (las filas de eleventa con las marcas del puente: lo reconocido por su número o solo por su marca,
correrla dos veces, lo que cambió en eleventa, lo que ya estaba y era de la caja 2, sin la bandeja y la lista para
revisar, también la anotación a mano de una devolución de aquí, ya elegida o por elegir) y contra el Firebird de prueba: la
caja 2 vendió, canceló y devolvió por el puente, en eleventa se le devolvió algo (la vuelta), eleventa vendió, canceló y
devolvió lo suyo, y la caja 2 devolvió un ticket de su eleventa del turno de ahora (la ida; el de la principal no lo deja
devolver aquí y se devolvió en eleventa). El historial quedó exactamente con los tickets de eleventa, suma lo mismo que
eleventa sin los de la caja 2, y la segunda vez no cambió nada.

## Arranque (fase 5)

1. Depuración del catálogo sobre la base importada ([plan](plan.md#depuración-del-inventario)): bajas, duplicados,
   equipo de la tienda, unidades y servicios.
2. Conteo físico completo, por departamentos, en las semanas previas. Reemplaza todas las existencias de eleventa.
3. La importación final del historial de eleventa, sin lo que mandó el puente ([arriba](#la-importación-final)).
4. Arranque en un día tranquilo.
5. Eleventa queda instalado solo para consulta unas semanas, como respaldo, con un plan para regresar si algo falla.
6. Capacitación a cajeros y encargado antes del arranque, con datos de prueba.
