# Módulo de impresión

[Control Print](https://github.com/Marioalr/control-print) fue la prueba de concepto: demostró que se puede medir el
costo real de cada impresión en la WF-6590 y llevar la cola de lo que falta cobrar. Para el POS se **reescribe como
módulo propio.** Se conserva lo aprendido (formato del spool EMF, PrintTicket, lecturas SNMP de niveles y contadores,
método de calibración, atascos y cancelaciones) y se reusa código donde convenga.

## Por qué reescribirlo en lugar de conectarlo por API

- Un solo inicio de sesión, con los mismos permisos, base, instalador y actualizaciones.
- Las impresiones son líneas de venta y de pedido desde el principio, no algo que se sincroniza entre dos sistemas.
- El POS ya tiene un servidor por sucursal: el módulo solo necesita un agente en cada PC que imprime.

## Cómo encaja

- **Agente en cada PC que imprime:** vigila la cola, mide cada trabajo y manda el resultado al servicio de la sucursal.
- **Caja principal:** lee niveles y contadores de la impresora, calibra y verifica las hojas que salieron.
- **Cobro:** las impresiones y copias por cobrar aparecen en la caja y se agregan al ticket.
- **Margen real:** el costo de cada impresión entra a la utilidad del día y a la receta de los
  [servicios](servicios.md).
- **Papel:** las hojas impresas se descuentan del inventario de papel (consumo interno).
- **Copias en el panel de la impresora:** las detectadas por contador tienen que cobrarse o justificarse. Con el dueño
  ausente, esto cierra la fuga más común de una papelería con servicio de impresión.
- **Pedidos:** lo que se imprime desde un [pedido](pedidos.md) lleva el folio en el nombre del trabajo y se liga solo.
- **Repetidos:** la huella visual de cada página sale del mismo dibujo a 300 dpi que se hace para medir la tinta.
- **Cotizar:** los archivos que llegan a un pedido se cotizan antes de imprimir.

## Lo que hay que lograr

Pendientes que vienen de Control Print:

- Que los costos solo los vea quien tiene permiso, aplicado en el servidor y no solo en la pantalla. **Hecho en el editor de
  costos** (Configuración › Impresoras e impresiones, 2026-10-31): el servicio solo los da a quien tiene «Ver costos, utilidad
  y margen» y solo los cambia quien además tiene «Configurar la impresora». Ya lo hacían el costo de cada impresión y el de lo
  que se cotiza; falta revisar lo demás que el módulo enseñe de los costos cuando llegue.
- Cotizar PDF y detector de documentos comunes (CURP, recibo de luz).
- Impresiones desde el celular y copias hechas en el panel de la impresora.
- Varias impresoras: la matriz tiene **dos WF-6590 y una EcoTank L5590**, todas por red. Antes iban con la entrega 4; el
  2026-11-03 el dueño lanzó el trabajo del módulo y van ya ([varias impresoras](#varias-impresoras)).

Todo esto, en orden y con lo que hace Control Print cada día: [Reemplazar a Control Print en la caja
principal](#reemplazar-a-control-print-en-la-caja-principal).

## Los costos en la Configuración

Desde el 2026-10-31 (la instalación desde cero) los costos de cada sucursal se ven y se cambian en Configuración › Impresoras e
impresiones, en español: cada cartucho (su precio y lo que rinde), el papel de cada tamaño y tipo (lo que cuesta el paquete y sus
hojas, con el precio por hoja que sale), la caja de mantenimiento, el margen (uno para todo, por tramos del costo del trabajo o de
cada hoja, como el mayoreo de Control Print, o [por curvas](#las-curvas-del-margen) desde el 2026-11-08), el margen del papel (vacío: el mismo que lo demás), el cobro mínimo (para todo o solo
para lo de una cara) y el redondeo. Lo demás (la calibración, la luz, la depreciación, los tipos de papel) se queda como está. Antes
nada en el POS los cambiaba: eran los de Control Print (la caja 2) o los de fábrica, con el papel en cero.

- **Quién:** los ve quien tiene «Ver costos, utilidad y margen» y los cambia quien además tiene «Configurar la impresora»; lo revisa
  el servicio. Lo que se escribe se guarda solo, un renglón a la vez (un cartucho con su precio y lo que rinde, un papel con su paquete
  y sus hojas, un tramo): al salir del renglón, con Enter o al cerrar la Configuración, nunca a medias. Un renglón que no se pudo
  guardar se queda escrito, marcado, con su porqué. La bitácora dice qué cambió, sin los montos («el precio del
  cartucho negro»), porque la lee también quien no ve los costos; los de antes, con sus montos, quedan en `HistorialCostosImpresion`,
  como cuando llegan de Control Print.
- **Con Control Print** (la caja 2), los costos vienen de allá: se ven con la hora en que se leyeron y aquí no se cambian (el
  servicio lo rechaza); se cambian en Control Print, en Ajustes → Costos.
- **Lo que falta:** los precios en cero (de fábrica, el papel) salen arriba, y también en «Terminar la instalación».

## Las curvas del margen

El dueño (2026-11-08): «el precio de venta se parece más bien a una curva logarítmica, tanto por hoja individual como por trabajo total:
cuanta más tinta lleva una hoja menor es el margen, y cuantas más hojas se imprimen menor es el margen; me gustaría poder configurar la
curva a mano e ingresando las cantidades exactas». Es un modo más del margen, **«Por curvas»**, junto a los de siempre (uno para todo y
los tramos de Control Print, que se quedan para comparar). **Al actualizar no cambia ningún precio:** las curvas llegan con unos puntos
sugeridos, pero el modo sigue siendo el que estaba hasta que el dueño elija «Por curvas».

- **Dos curvas, con sus puntos exactos:**
  - **por la tinta de cada cara:** la tinta de la cara (%) → su margen (%). Por ejemplo 5 % → 300 %, 20 % → 180 %, 50 % → 110 %,
    100 % → 70 %;
  - **por las caras del trabajo:** las caras impresas → un factor (%) que multiplica el margen de cada cara. Por ejemplo 1 → 100 %,
    10 → 85 %, 100 → 65 %, 500 → 55 %.
- **La tinta de una cara** es la suma de sus colores sobre la hoja, como la mide el módulo (cian + magenta + amarillo + negro, cada uno
  como la fracción de la página que cubriría al 100 %); en B/N, el negro (el gris de la página). Con varias páginas por hoja, cada página
  se reduce: cuatro páginas al 10 % en una cara dan 10 %. No lleva los factores de calidad, papel ni limpiezas (esos ya suben el costo).
  Como guía: **una carta de texto ≈ 5 %, con fotos ≈ 40 %, toda llena ≈ 100 % o más**; a color puede llegar a 400 % (los cuatro colores
  llenos). Va en porcentaje de tinta, no en pesos: si suben los cartuchos, el margen no baja.
- **Las caras impresas** son páginas × copias entre las páginas por hoja; una hoja a doble cara cuenta 2 (las mismas `Caras` del trabajo).
- **Entre dos puntos** la curva va en línea recta sobre un eje logarítmico (así, con 4 o 5 puntos sale la forma de un logaritmo): a la
  mitad entre 5 % y 20 % en esa escala (10 %), la mitad entre 300 % y 180 % (240 %). **Antes del primer punto y después del último**
  se queda en el valor del extremo: una cara en blanco (0 %) lleva el margen del primer punto.
- **El precio:** cada cara cuesta su tinta (la de sus páginas) más su parte, pareja, de lo demás por cara: el papel (una hoja a doble
  cara se reparte entre sus dos caras), la caja de mantenimiento, la luz, la depreciación y los extras por página o por hoja. Cada cara
  lleva el margen de su tinta × el factor de las caras del trabajo. Los extras por trabajo llevan el margen promedio de las caras. Luego,
  como siempre: el papel con margen propio lo sigue llevando, el redondeo hacia arriba y el cobro mínimo. El precio especial sigue
  igual: su propio margen sobre el costo, **nunca más alto que el precio calculado**.
- **Qué se ve del margen:** con el costo de cada impresión (al cotizar y en «Impresiones del día», para quien ve los costos) sale de
  dónde salió: «curva: 12 % de tinta → 210 % × 85 % (40 caras)», o con caras distintas «curva: de 5 % a 100 % de tinta → de 300 % a
  70 % × 85 % (40 caras); en promedio 180 %». Con los tramos, «mayoreo: costo del trabajo 0–100 → 100 %». El margen que se guarda con
  el trabajo es ese promedio (con él se cobra «solo lo que salió»).
- **En la Configuración** (Impresoras e impresiones › Costos, debajo del margen): las dos tablas con sus puntos, cada uno con sus
  cantidades exactas, que se guarda como los demás renglones (al salir de él o con Enter); «Agregar un punto» (al final, al doble de x)
  y «Quitar». Cada una con su gráfica (eje x logarítmico), que se mueve mientras se escribe. Y el **probador**: la tinta de cada cara,
  las caras, color o B/N, el tamaño y el papel → el margen por la tinta, el factor, el margen de cada cara, el costo y el precio de cada
  cara, el precio del trabajo con su desglose y el precio con el margen de hoy. Con **«Comparar con los últimos 30 días»**, lo impreso de
  verdad (impresiones y copias del panel, sin las no vendidas ni las de un trámite) con los costos de hoy: cuánto se cobró, cuánto daría
  el margen de hoy y cuánto las curvas, con las 10 diferencias más grandes. El probador y la comparación usan las curvas como se están
  escribiendo, sin guardarlas ni elegirlas. Las curvas se ven aunque el modo sea otro, para afinarlas antes de elegirlas.
- **Lo que no se deja:** menos de 2 puntos o más de 12 en una curva, dos puntos en la misma x, una tinta de 0 o de más de 400 %, un
  margen de más de 1,000 % (el mismo tope de siempre), caras que no sean enteras de 1 a 1,000,000 y un factor de más de 200 %. El
  porqué se queda en el renglón, como en los demás costos.
- **Cada impresora con sus márgenes** ([decisión 2](#decisiones-para-el-dueño-control-print)): una impresora con sus propios
  consumibles (la L5590) puede tener sus propios márgenes, con sus curvas («Márgenes de» arriba del margen: «Darle sus propios
  márgenes» empieza con una copia de los de la tienda; «Usar los de la tienda» los quita). Valen junto con sus consumibles: mientras le
  falte un precio de sus botellas cobra como una WF-6590, con todo lo de la tienda. Las WF-6590 usan los márgenes de la tienda. El cobro
  mínimo y el redondeo son de la tienda.
- **Quién:** como los demás costos, los ve (y usa el probador y la comparación) quien tiene «Ver costos, utilidad y margen», revisado
  en el servicio; los cambia quien además tiene «Configurar la impresora». La bitácora dice qué cambió, sin los números («la curva del
  margen por la tinta de cada cara»).
- **Con Control Print** (la caja 2), los costos y el margen de la tienda vienen de allá y no se cambian aquí: las curvas se ven y se
  prueban, pero «Por curvas» se elige hasta que Control Print se quite. Los márgenes de la L5590 sí se cambian aquí. Lo que se importa
  de Control Print llega sin curvas y toma las sugeridas.

**Los puntos sugeridos** (decidido, por revisar) son para que lo típico salga como hoy con los márgenes de fábrica (tramos por costo del
trabajo: 100 % hasta $100 de costo, 80 % hasta $500, 60 % hasta $1,000 y 50 % después):

| Curva | Puntos |
|---|---|
| Por la tinta de cada cara | 5 % → 100 %, 25 % → 100 %, 60 % → 90 %, 120 % → 80 %, 250 % → 70 % |
| Por las caras del trabajo | 1 → 100 %, 100 → 100 %, 250 → 85 %, 1,000 → 65 %, 2,500 → 55 % |

Medido con los costos de fábrica de la WF-6590 y el papel a $0.20 la hoja (una cara de texto cuesta unos $0.47; una con fotos al 40 %,
$1.67; una toda llena al 120 %, $4.33):

- **Texto, en B/N o a color, de 1 a 100 caras: el mismo precio que hoy** (100 %). De 200 a 2,000 caras, hasta unos 10 % arriba o abajo
  de hoy: los tramos bajan en escalón por el costo y la curva baja poco a poco por las caras.
- **Con fotos (40 %), de 1 a 50 caras:** igual o hasta 3 % menos.
- **Toda llena (120 %) o foto completa (240 %), de 1 a 20 caras:** entre 10 % y 15 % menos que hoy: es lo que pidió el dueño (más tinta,
  menos margen), pero suave para no mover mucho los precios de inicio.

Son solo el inicio: el dueño los afina con el probador y la comparación antes de elegir «Por curvas». Sus ejemplos (300 % para el
texto) subirían el texto al triple del margen de hoy.

## Varias impresoras

La matriz tiene dos WF-6590 y una EcoTank L5590, todas por red. Desde el 2026-11-03 el módulo lleva cada una por separado; antes
llevaba una por sucursal.

- **Cada impresora** tiene su número en la sucursal (1, 2, 3…), su nombre («WF-6590 de la entrada»), su modelo (WF-6590 o L5590), su
  IP, su aviso de tinta y cada cuánto se lee. Se agregan, se les cambia el nombre y se dan de baja en Configuración › Impresoras e
  impresiones. No se borran: una dada de baja guarda su historia y su número no se vuelve a usar; la última activa no se da de baja.
  Dos activas no pueden tener la misma IP: se rechaza al guardar y dice de cuál es.
- **La que ya había es la número 1**, una WF-6590 llamada «WF-6590», con su IP, sus lecturas y lo que salió de ella. Lo hace la
  migración `VariasImpresoras` al actualizar; sus costos no cambian. Una tienda instalada desde cero no trae ninguna: la Configuración
  enseña el campo de la IP de siempre y, al escribirlo, da de alta la 1.
- **Cada una se lee y se verifica con su contador:** la caja principal lee cada impresora por su IP, y lo que salió de la L5590 se
  verifica con el contador de la L5590. El contador contra lo registrado es de cada una; el del día de la sucursal es la suma. Si en su
  IP contesta otra serie (el módem le cambió la dirección), su contador es el de otra impresora: esas lecturas no se guardan, sus hojas
  no se verifican (nunca se inventa un faltante) y su bloque de la Configuración lo dice en rojo hasta que se corrija la IP; con
  Telegram en pausa, ese es el aviso. Si se cambió la impresora por otra, se da de baja y se agrega la nueva. Por eso conviene que
  cada una tenga su IP fija.
- **Los costos:**
  - lo comercial es de la sucursal, uno para todas: el cobro mínimo, el redondeo y el papel; los márgenes (con sus tramos y sus
    curvas) también, salvo los de una impresora con sus propios consumibles, que puede tener los suyos (2026-11-08, [decisión
    2](#decisiones-para-el-dueño-control-print));
  - la tinta, la caja de mantenimiento, la luz y la depreciación son del modelo: las dos WF-6590 usan los cartuchos de los costos de
    la sucursal (se cambian en un solo lugar), y la L5590 trae los suyos;
  - la calibración es de cada impresora: la 1 la sigue guardando en los costos de la sucursal (así llega la de Control Print en la
    caja 2) y las demás en lo suyo; una WF-6590 nueva usa la de la 1 hasta que se le aplique la suya.

  Lo que sale de cada impresora se cobra con sus costos y sus márgenes: el mismo documento puede costar distinto en la L5590 ([decisión
  2](#decisiones-para-el-dueño-control-print), del dueño).
- **La L5590** llega con sus botellas T544 y lo que rinden según la ficha de Epson (la negra hasta 4,300 páginas, las de color hasta
  7,300), sin precios, sin caja de mantenimiento, con 12 W y sin depreciación. **Mientras le falte el precio de una botella cobra como
  una WF-6590, nunca gratis, y no se calibra.** Sus precios se escriben en los costos de la Configuración, debajo de los cartuchos;
  con Control Print también se cambian aquí, porque allá no están. **Por verificar en la tienda:** si Windows guarda sus trabajos en
  EMF, qué contesta por SNMP y si tiene caja de mantenimiento (Control Print dice que no; la ficha de Epson lista una que se cambia,
  la C12C934461). Si la tiene, se le pone en sus costos de la Configuración, con su precio y las caras que dura.
- **Cada PC dice qué impresora es cada cola** en su `caja.json`, con el número de la impresora:

  ```json
  "ColasImpresion": [
    { "Cola": "EPSON WF-6590 Series", "Impresora": 1 },
    { "Cola": "EPSON L5590 Series", "Impresora": 3 }
  ]
  ```

  El agente vigila todas a la vez y manda cada trabajo con su número. `"ColaImpresion": "<cola>"` sigue igual: una cola, de la impresora
  1 (la primera activa). **La caja 2 del piloto no cambia nada.** Un número que no existe en la sucursal, o el de una dada de baja, no
  pierde el trabajo: se anota en la primera, con una nota que se ve en su tarjeta de F2, y sale un aviso (una vez por caja y número) para
  corregir su `caja.json`. Hoy esto se escribe a mano en cada PC; que lo haga el instalador o la Configuración está en las
  recomendaciones del plan. Cada cola necesita lo de siempre: «Conservar documentos impresos», EMF y su puerto TCP/IP.
- **F2 y las tarjetas** dicen de qué impresora salió cada trabajo cuando la sucursal tiene más de una activa; con una sola (aunque
  haya otra de baja), como antes. Un lote nunca junta lo de dos impresoras ni lo de dos PC. Las de
  Control Print dicen su cola cuando lo que se ve viene de más de una. Las copias del panel se registran en la impresora donde se
  hicieron, y se cotiza con la impresora que se elija.
- **Los trámites** (la CURP y los demás) ofrecen primero todas las colas que vigila el agente de esa caja, en su orden: en cualquiera de
  ellas sus copias de más salen en F2 ([trámites](tramites.md)).
- **Los avisos** (la tinta, la impresora que no contesta, las hojas que faltaron) dicen de qué impresora cuando hay más de una; los de la
  L5590 hablan de su tinta («tinta negra baja»).
- **Los permisos, como antes:** las impresoras las ve quien abre «Impresoras e impresiones» y las cambia quien tiene «Configurar la
  impresora»; los costos, quien tiene «Ver costos, utilidad y margen» (cambiarlos pide además «Configurar la impresora»). Agregar,
  cambiar el nombre y dar de baja quedan en la bitácora.

**Lo que necesitaría el instalador** (#150; en esta vuelta no se cambia):

- preguntar, por cada cola de inyección de la PC, qué impresora es (su número) y escribir `ColasImpresion` (hoy escribe solo
  `ColaImpresion`);
- activar «Conservar documentos impresos» en cada cola que se mide (hoy solo en la de `ColaImpresion`);
- en la caja principal, dar de alta las impresoras con su IP y su modelo (o dejarlo a la Configuración);
- al actualizar no estorba: `CajaJson` solo escribe sus claves y deja las demás, así que `ColasImpresion` se queda.

## Dónde vive el código

El dueño pidió (2026-10-14) separar el núcleo de Control Print, sin retirarlo, para seguir trabajando su lógica sin
afectar el resto del POS. Desde el 2026-10-15 vive aparte, en `src/Impresiones`, con sus pruebas en `tests/Impresiones`:
el POS lo usa por un puerto (lo que el módulo le pide: la hora, las cajas, el horario y a quién avisar) y por una fachada
(lo que la venta hace con las impresiones), y sus tablas siguen en la base del POS. Los proyectos, sus límites, sus
pruebas y cómo cambiar su esquema: su [README](../src/Impresiones/README.md).

## Transición

Control Print sigue en uso hasta que el POS lo reemplace en la matriz: se desinstala por completo en la segunda fase del piloto, cuando
el POS se instale en la caja principal (el dueño, 2026-11-08).

## Reemplazar a Control Print en la caja principal

El dueño (2026-11-01): «en cuanto el apartado de Control Print del POS funcione, se desinstalará de la caja principal». Hasta
entonces **el POS solo lee a Control Print** (2026-10-25, confirmado el 2026-10-29): nunca le escribe ni marca nada como cobrado,
y en la caja principal no se instala nada sin el visto bueno del dueño. Aquí está el camino con lo que se sabe hoy
(2026-11-03), sacado del código y de los documentos de Control Print 1.12; los datos reales del sábado lo afinan.

### Lo que hace Control Print cada día, y lo que ya hace el módulo

| En el día | Control Print 1.12 (en PRINCIPAL, modo Servidor) | El módulo del POS hoy |
|---|---|---|
| Medir cada impresión | Vigila las colas de la WF-6590 de cada PC, mide la tinta de cada página a 300 dpi y guarda una miniatura | Igual: el agente de cada caja. Hasta hoy, una cola y una impresora; desde esta vuelta, [varias](#varias-impresoras) |
| Ponerle precio | Tinta, papel, caja de mantenimiento, luz, depreciación y extras, con margen por tramos, mínimo de $2 y pesos cerrados | El mismo cálculo; los costos se cambian en Configuración › Impresoras e impresiones |
| Avisar que se imprimió | Una notificación de Windows con el precio | El chip «Impresiones por cobrar» arriba y F2; sin notificación |
| La cola por cobrar | Tarjetas con miniatura, número, título, resumen y chips, en lotes («Cobrar 3 · $25») | F2, con las mismas tarjetas y lotes |
| Cobrar | «Cobrado» en Control Print y, aparte, IMP a mano en eleventa | En la misma venta: en el piloto llega a eleventa como IMP o COP, sin escribirlo |
| Precio especial | Con el nombre del cliente (Ctrl+E) y su propio margen | No hay |
| Otro precio | El administrador lo escribe | No hay (las copias del panel sí aceptan otro monto) |
| No vendida | Con motivo | Igual |
| Atasco o cancelación | El contador dice cuántas caras salieron; «cobrar lo que salió» | Igual |
| Nombrar y buscar | Etiqueta («Juan, tarea»), búsqueda, otro día, «No vendidas», «Especiales» | F2 solo enseña lo que falta cobrar; lo de un día, en la Configuración |
| Copias del panel | «Cobrar copias» (Ctrl+K), con lo que contó la impresora contra lo registrado | Igual (F2 › Copias del panel; el contador, en la Configuración) |
| Cotizar | Un PDF arrastrado o con «Elegir PDF…» (Ctrl+Q), guardar la cotización 15 días y «Cobrar lo cotizado» | Un PDF elegido (Ctrl+Q, en la caja de Windows), con el precio de lo medido mientras mide; se guarda sola 15 días y su impresión se liga sola en F2, con lo cotizado ([abajo](#cotizar-por-qué-daba-menos-que-imprimir)) |
| La impresora | Niveles, contador, caja de mantenimiento, calibración y avisos de consumibles | Igual; desde esta vuelta, de cada impresora |
| Reportes | Ventas, costo y ganancia por periodo, por usuario y PC, especiales y no vendidas, en Excel | Los reportes de ventas traen la línea «Impresiones» con su costo; lo demás, no |
| Usuarios, respaldos y red | Los suyos (administrador, empleado, invitado), respaldo diario, PC conectadas | Los del POS |

### Lo que falta o no funciona

- **Cotizar** («no funciona en lo absoluto», el dueño). Cómo está cada falla de Control Print en el módulo:
  - acepta un PDF arrastrado o con «Elegir PDF…» → el módulo lo elige con su botón; arrastrar no está probado en la caja;
  - rechaza los PDF con `IsPasswordProtected` aunque abran sin contraseña → **el módulo ya no** (revisión de #155): lo que abre sin
    clave se mide; un PDF muy grande se dibuja a menos dpi y una página dañada lo dice (falta probarlo en Windows);
  - en una PC Conectada la ventana truena por un 404 → no aplica (la caja dice el error del servicio); se revisa en Windows;
  - se cierra al cambiar la sesión → por revisar en la caja (Ctrl+U con Cotizar abierto);
  - no da precio hasta medir todas las páginas → **el módulo ya sí** (2026-11-08): «Precio de lo medido (N de M páginas)» mientras
    sigue, y el final al terminar;
  - «Cobrar lo cotizado» casi nunca aparece → **el módulo guarda cada cotización** (sola, 15 días) y liga su impresión por el nombre y
    las páginas con tolerancia; F2 dice «Cotizado $X» y, si salió igual, cobra lo cotizado
    ([abajo](#cotizar-por-qué-daba-menos-que-imprimir));
  - **cotizaba menos que lo que se cobraba al imprimirlo** (el dueño, 2026-11-08) → las causas y lo que se corrigió, [abajo](#cotizar-por-qué-daba-menos-que-imprimir);
    falta medirlo en la tienda ([la guía](pruebas-en-tienda.md#cotizar-contra-imprimir)).
  - Falta en la caja: arrastrar el PDF, y «Ctrl+U con Cotizar abierto».
- **Los costos, solo con permiso:** hecho en el servicio para F2, las tarjetas de Control Print, cotizar, el desglose del día y el
  editor (#151). Falta cuidarlo en lo nuevo: el aviso al imprimir y los reportes de impresiones.
- **Lo de cada día que el módulo no tiene:** precio especial con nombre, otro precio con permiso, etiqueta y búsqueda, otros días
  en F2, el aviso al imprimir y reportes de impresiones (no vendidas por motivo, especiales, por usuario, PC e impresora). Guardar
  cotizaciones ya está (2026-11-08).
- **El detector de documentos comunes, el celular y las copias del panel sin registrar:** Control Print nunca los tuvo; van
  [abajo](#lo-que-control-print-nunca-tuvo-y-cómo-entraría).
- **Varias impresoras:** las dos WF-6590 y la L5590, en esta vuelta. De la L5590 falta revisar en la tienda su spool, su SNMP y si
  tiene caja de mantenimiento (Control Print dice que no; la ficha de Epson lista la C12C934461).
- **Los avisos de hojas faltantes en la PC que imprimió:** hoy van a los avisos del dueño (en pausa con Telegram); la caja que
  imprimió no se entera.
- **Datos personales en las miniaturas:** las de los trámites ya no se guardan (#152); faltan las de los pedidos («P-…») y la base
  de Control Print, que tiene la miniatura de todo lo que imprimió (CURP incluidas) y se queda al desinstalarlo, con sus respaldos
  de 30 días.
- **Cobrar mientras la caja principal venda con eleventa:** ya no hace falta «Cobrada en eleventa». Control Print se desinstala en la
  segunda fase del piloto, cuando el POS se instale en la caja principal (que sigue mandando a eleventa), y desde ahí las impresiones se
  cobran en el ticket del POS ([decisión 1](#decisiones-para-el-dueño-control-print), del dueño).
- **El mosaico no se detecta** (el dueño, 2026-11-08): si una hoja se manda imprimir en 4 hojas (el póster o mosaico del driver), se
  cuenta 1 y se cobra 1. Pasa en Control Print y en el módulo: el spool trae una sola página lógica y el driver la parte al imprimir.
  Pistas: el contador de la impresora sí cuenta las caras que salieron (la verificación de hojas puede marcar el trabajo con caras de
  más, como hoy marca las que faltan), y el DEVMODE del driver de Epson quizá dice que está en modo póster (hay que ver en Windows qué
  campo cambia). Por ahora no se detecta.
- **Las dos no pueden vigilar la misma cola:** cada una quita de la cola lo que ya midió, así que la otra lo pierde. Y Control Print
  no vigila solo su cola: vigila **todas** las colas de la PC con el driver de la WF-6590, menos las que empiezan con «ControlPrint Lab»
  (`PrinterDiscovery`). Una cola aparte con el mismo driver no basta: Control Print la mediría y borraría lo medido. Un ensayo en la
  caja principal va en una cola que se llame **«ControlPrint Lab POS»** (con el driver de la WF-6590 y su puerto TCP/IP), que Control
  Print se salta, con hojas de prueba.

### Cotizar: por qué daba menos que imprimir

El dueño (2026-11-08): «el cotizar que tenía Control Print no era preciso: probé con varios documentos cotizándolos y luego
imprimiéndolos, y cotizar daba un precio inferior al calculado en la impresión». El módulo heredó ese diseño. Se compararon los dos
caminos paso a paso (`MedidorPdfWindows` y `QuoteCalculator` contra el agente: `JobAnalyzer`, `PageRasterizer.Measure` y
`CostCalculator.Calculate`), en el módulo y en el código de Control Print 1.12:

| | Al cotizar | Al imprimir | Efecto en lo cotizado | Estado |
|---|---|---|---|---|
| **Calidad** | Empezaba en «Normal» (`Standard`, factor 1.0) | La del PrintTicket: «Normal (colores vivos)» lleva 1.15 y «Alta» 1.3 | **Menos**: 13 % a 23 % menos tinta si la tienda imprime en vivos o alta; en el precio, según cuánto pese la tinta contra el papel (una hoja de texto casi nada, una foto casi todo) | **Corregido:** empieza con la calidad más usada en esa impresora (sus últimos 100 trabajos de 60 días). Qué calidad usa la tienda lo dicen los datos del sábado |
| **Papel** | Papel normal | El del PrintTicket (fotográfico: 1.3 a 1.5 de tinta y el papel más caro) | Menos, si se imprime en otro papel sin cambiarlo al cotizar | **Corregido** igual que la calidad |
| **Impresora** | La primera de la sucursal | La de su cola, con su calibración (otra WF-6590 calibrada aparte, la L5590 con sus botellas) | Cualquiera: lo que se aleje la calibración de una de la otra | **Corregido:** empieza en la impresora donde imprime la caja (la de su último trabajo) |
| **Costos en la caja 2** | Los guardados en la base | Los que leyó de Control Print en ese momento | Casi siempre iguales (se guardan al leerlos); distintos justo después de un cambio | **Corregido:** cotizar usa los mismos que la impresión |
| **El tamaño de la página** | Suponía que la página llenaba el área imprimible de la hoja (88.9 in² en carta) | El programa la ajusta: «Ajustar» (Acrobat, Edge y Chrome) la reduce al área imprimible (88.4 in² una carta, 81.9 una A4 en carta); «Tamaño real» pone la página completa (93.5 in²) y recorta los márgenes | Carta con «Ajustar»: +0.6 %; carta con «Tamaño real»: **5 % menos**; A4 u oficio en carta: 9 % y 24 % **más** | **Corregido** el ajuste (Cotizar ya manda el tamaño de cada página y se ajusta como al imprimir, girándola si va acostada). Si la tienda imprime en «Tamaño real», lo absorbe la corrección |
| **Cómo se dibuja** | El motor de PDF de Windows (Direct2D: suavizado gris, sin perfiles de color) | El EMF que manda el programa, dibujado con GDI: sin suavizado, letras ajustadas a la rejilla; Acrobat convierte el CMYK con perfiles; un navegador puede mandar la página como imagen | Se espera **menos** en el texto (GDI engruesa los trazos delgados); en fotos, cualquiera. Medido en Linux con poppler a 300 dpi: con y sin suavizado, ±4 %. Con GDI en Windows, por medir. La investigación de Control Print (2026-09-26) comparó el EMF con lo que manda el driver: < 1 %; Cotizar nunca se comparó | **Corrección por impresora** (abajo), aprendida con lo que se imprime |
| **Lo que agrega el programa** | Solo la página | Encabezado y pie del navegador (fecha, dirección, número de página); los campos de un formulario llenado y las anotaciones (Windows.Data.Pdf podría no dibujarlos: por revisar) | Menos: poco con el encabezado (< 0.5 % de la página), mucho en un formulario llenado | Corrección y la tienda (un formulario en la lista de PDF de prueba) |
| **CMYK y negro rico** | Sin perfil: el negro de un PDF en CMYK sale negro puro (solo K) | Con perfil (Acrobat): casi negro con algo de color | Mezclado: lo cotizado lleva más K y menos color; el precio casi igual (las caras a color no cambian el precio) | La tienda (un PDF en CMYK) |
| **Copias, doble cara, páginas por hoja, tamaño** | Lo que elige el cajero | Lo que se manda a imprimir | Cualquiera, y mucho, si no coincide | Ahora F2 lo reconoce: lo impreso distinto lo dice y no cobra lo cotizado sin preguntar |
| Mínimo, redondeo, márgenes por tramos | `CostCalculator.Calculate` | El mismo | Ninguno | Igual (lo prueban `CotizarTests`) |
| Más de 500 páginas | El promedio de las medidas | El promedio | Ninguno en el total (el promedio por las que faltan da la misma suma) | Igual |
| Un documento sin color, en B/N | Cotizar lo pone en B/N | La cola lo imprime en color | Ninguno: en color, el gris va todo a K, igual que en B/N | Igual |
| La calibración cambia | La de ese momento | La del día que se imprime | Cualquiera, poco en días | — |

Lo que se puede arreglar sin la tienda está arreglado: **cotizar usa la misma medición por página y el mismo cálculo que la impresión, con
los ajustes y la impresora con que se va a imprimir.** Lo que solo se mide en la tienda (el dibujo, lo que agrega el programa, «Tamaño
real») va en **la corrección de cotizar de cada impresora** (`Impresora.FactorCotizacion`): multiplica la tinta medida al cotizar (no
el papel ni lo demás, que ya salen exactos). Empieza en 1 (sin corregir), va de 0.5 a 2 y se cambia en Configuración › Impresoras e
impresiones, en el bloque de cada impresora, «Corrección al cotizar» (quien tiene «Configurar la impresora»; queda en la bitácora).

**Cómo se mide desde ahora:** cada cotización guarda su tinta sin corregir; al ligarse a su impresión guarda la tinta y el precio de lo
impreso. Con las que se imprimieron igual (mismas páginas, copias, doble cara, páginas por hoja, tamaño, papel, calidad y color, en la
impresora en que se cotizó), la Configuración dice de cada impresora cuántas son, cuánto salió lo impreso contra lo cotizado en promedio
(últimos 90 días) y, con 5 o más, la corrección que las igualaría (la tinta impresa entre la cotizada), con un botón «Usar». No se
aplica sola: la pone quien configura la impresora. Para lo de antes, `analizar-control-print` saca lo mismo de los datos de Control
Print ([analizarlos](#analizarlos)).

### Cotizar, desde el 2026-11-08

- **Empieza como se imprime:** en la impresora donde imprime la caja y con lo más usado en ella (calidad, papel, tamaño, color, doble
  cara y páginas por hoja); lo dice arriba del precio («Con lo que más se imprime en la WF-6590 2…»). Al cambiar de impresora, toma lo
  de esa. Las copias y el rango, no.
- **Precio mientras mide:** «Precio de lo medido (N de M páginas): sigue midiendo», como mucho cada 0.7 s, sin el rango; el final, al
  terminar.
- **Se guarda sola** al terminar de medir y otra vez con cada cambio de opciones (la misma cotización: vale lo último que se vio), por
  15 días, en `CotizacionesImpresion`: el nombre del archivo, sus páginas, los ajustes, la impresora, el precio, su costo y su tinta.
  **Ni el PDF, ni su miniatura, ni su texto** (la CURP de un cliente no se queda en la base). Abajo, «Cotizaciones de los últimos 15
  días», de todas las cajas, con si ya se imprimió.
- **Se reconoce al imprimir:** el mismo documento (el nombre del trabajo contiene el del archivo: Edge le agrega el suyo, otros la
  carpeta), con una página de más o de menos (o el 10 % en uno largo), impreso desde 5 minutos antes de cotizar hasta que vence; una
  cotización se usa una vez, y si hay varias, la que salió igual y la más nueva.
- **Qué se cobra:** si salió igual que lo cotizado y lo impreso está a ±20 % (o ±$2 en uno chico), **lo cotizado**, sin preguntar: es lo
  que se le dijo al cliente. F2 lo dice («Cotizado $X» y «Se cobra lo cotizado; al imprimir salió $Y») con «Cobrar lo impreso». Si salió
  distinto o se aleja más, se cobra lo impreso y F2 enseña los dos con «Cobrar lo cotizado»: elige el cajero, sin pedir permiso (como
  «cobrar lo que salió»). Queda en la impresión qué se cobró y los dos precios.

### Lo que Control Print nunca tuvo, y cómo entraría

- **Celular y copias sin registrar:** el contador ya dice cuántas caras nadie registró. Con varias impresoras, cada una con su
  contador: leerlo cada ~10 s y, si sube sin un trabajo de una PC, una tarjeta «Copias o celular · N caras» por cobrar (con IPP se
  sabe si fue el celular). Control Print lo dejó planeado.
- **Documentos comunes (CURP, recibo de luz):** «Servicios» ya cobra la CURP con su trámite (#152). El detector serviría para lo que
  el cliente trae hecho: reconocerlo por el nombre o el texto y sugerir el cargo, sin guardar sus datos ni su miniatura.
- **Pedidos:** lo impreso desde un pedido lleva su folio («P-0123 · …»), se liga solo y no guarda miniatura.
- **El precio de cada impresora:** la L5590 gasta menos tinta; el mismo documento sale al precio que dan sus costos y sus márgenes
  ([decisión 2](#decisiones-para-el-dueño-control-print), del dueño).
- **El aviso en la caja que imprimió:** el precio al momento, las hojas que faltaron y la tinta que se acaba.
- **El papel en el inventario:** las hojas impresas se descuentan del papel (consumo interno). El costo de cada impresión ya entra a
  la utilidad de los reportes.
- **Repetidos:** la huella de cada página para avisar «esto ya se imprimió hoy» (una reimpresión o un doble cobro).

### Lo que solo dicen los datos, la tienda o Windows

- **Los datos de Control Print** (el sábado, con `analizar-control-print`): cuánto se imprime por PC, cola y mes; cuántas copias del
  panel; cuántas cotizaciones se guardaron, se usaron y se cobraron; cuántas veces se cambió el precio (a mano, especial, lo que
  salió) y por cuánto; las no vendidas por motivo; y si el cálculo del módulo da el mismo precio que Control Print. Con eso se ordena
  lo que falta: si casi no se usa el precio especial, va después.
- **La tienda** (sábado y domingo): las colas de cada PC (nombre, driver, puerto y a qué impresora van), la IP, la serie y el
  contador de las tres impresoras, qué contesta la L5590 por SNMP, si guarda los trabajos en EMF y si tiene caja de mantenimiento,
  y cómo se usa Control Print en el mostrador: lo que no puede faltar el día que se quite. La lista está en [la guía del fin de
  semana](pruebas-en-tienda.md#varias-impresoras-y-control-print-en-la-caja-principal).
- **Windows:** el agente con varias colas a la vez, medir el spool de la L5590, cotizar un PDF con contraseña de propietario y
  todo lo que se instale en la caja principal.

### Los hitos, en orden

1. Varias impresoras en el módulo (esta vuelta) y la L5590 revisada en la tienda.
2. Los datos de Control Print del sábado, leídos con el dueño (`analizar-control-print`).
3. Cotizar que funcione: precio mientras mide, guardar la cotización y «Cobrar lo cotizado» (los PDF con permisos ya se miden).
   **Hecho el 2026-11-08**, con por qué cotizaba de menos ([abajo](#cotizar-por-qué-daba-menos-que-imprimir)); falta compararlo en la
   tienda con cada impresora y poner su corrección.
4. Lo de cada día que falta en F2, en el orden que digan los datos: precio especial, etiqueta y búsqueda, otros días, otro precio.
5. El aviso en la caja que imprimió: el precio al imprimir y las hojas que faltaron.
6. Reportes de impresiones: por periodo, usuario, PC e impresora, especiales y no vendidas.
7. Ensayo en la caja principal, con el visto bueno del dueño: la caja y su agente en la cola «ControlPrint Lab POS» (Control Print se
   salta las que empiezan así), con hojas de prueba.
8. **Segunda fase del piloto: el POS en la caja principal** (que sigue mandando a eleventa para tener el inventario al día) y **Control
   Print se desinstala por completo** (el dueño, 2026-11-08): juntar sus datos por última vez, desinstalarlo y decidir qué pasa con su
   base y sus respaldos. Desde ahí las impresiones se cobran en el ticket del POS.
9. Después: celular y copias por el contador, el detector de documentos, los pedidos y el papel en el inventario.

### Decisiones para el dueño (Control Print)

1. **¿Cuándo se quita Control Print?** **Decidido por el dueño (2026-11-08): Control Print se desinstala por completo en la segunda
   fase del piloto**, cuando el POS se instale en la caja principal (que seguirá mandando a eleventa para mantener el inventario al
   día); desde ahí las impresiones se cobran en el ticket del POS. «Cobrada en eleventa» ya no hace falta.
2. **¿El mismo documento cuesta igual en todas las impresoras?** **Decidido por el dueño (2026-11-08): el mismo documento se vende al
   precio que calcula cada impresora con sus costos y su configuración de márgenes.** Por eso una impresora con sus propios consumibles
   (la L5590) puede tener sus propios márgenes ([las curvas del margen](#las-curvas-del-margen)).
3. **La base de Control Print al desinstalarlo** tiene la miniatura de cada impresión (CURP incluidas). Recomendado: juntar sus
   datos una última vez y borrarla con sus respaldos.
4. **El precio especial:** ¿con un nombre escrito, como en Control Print, o con los clientes registrados del POS? Recomendado: los
   clientes registrados (sin nombres a mano, como pedía el plan de Control Print).

Las que se tomaron para poder seguir quedan en el [plan](plan.md#2026-11-03-el-módulo-de-impresión-reemplazar-a-control-print-y-varias-impresoras)
como **decidido, por revisar**.

## Control Print en la caja 2: sus tarjetas y la cola de la caja 2

El dueño (2026-10-25): «agrega ya las tarjetas de la cola de Control Print (el sistema sigue instalado en la caja principal),
de esta forma podemos empezar a integrarlo al uso diario». Control Print sigue en la caja principal y no se puede instalar en
la caja 2 (pide Windows 11). Así que, en la caja 2:

- **F2 enseña la cola «por cobrar» de Control Print**, como sus tarjetas, y **el POS solo la lee** (el dueño, 2026-10-25):
  nunca marca nada como cobrado allá ni le escribe nada. Lo que se cobra en la caja 2 va a la venta del POS y, en el piloto,
  a eleventa, como cualquier venta.
- **Lo que se imprime en la caja 2 lo mide el agente de impresión del POS** (la cola de la WF-6590 de esa PC) y le pone
  precio con los costos de Control Print, leídos de su servidor. Eso tampoco va a Control Print: allá no aparece.

**Lo que hay que saber en la caja principal** (decisión del dueño, 2026-10-29: «Solo los lee»): lo que se cobra en la caja 2
**sigue «por cobrar» en Control Print**, y ahí se puede volver a cobrar, por ejemplo en un lote («Cobrar 3 · $25») que junte
lo de otro cliente. **Antes de cobrar en Control Print, mira si la caja 2 dice que ya lo cobró:** su aviso de «Cobradas dos
veces» (arriba y en F2) o su venta, que en eleventa lleva el renglón IMP o COP con el número del trabajo («Impresión #1234»).
Si de todos modos se cobra dos veces, la caja 2 lo nota en cuanto Control Print deja de tenerlo por cobrar y lo avisa (ver
«Cobrarlas»).

### Las tarjetas en F2

- **Como en Control Print:** la miniatura, el número (#1234), el título (la etiqueta o el documento), «3 págs × 2 copias ·
  Color · Carta · Papel normal · Dúplex», los chips (la hora, la PC y quién atendía, Especial, Cotizado, Copias en impresora,
  Revisar hojas) y el precio (el puesto allá o el calculado). Las de la caja 2, junto a ellas, con su chip «Impresa en CAJA2»
  y sus botones de siempre (No vendida, Cobrar lo que salió). El costo, solo para quien tiene «Ver costos, utilidad y
  margen».
- **En lotes, como en Control Print:** lo mandado a 2 minutos o menos uno del otro va junto, con «lote de 3 trabajos · $25» y
  «Elegir 3 · $25», que elige todo el lote. Del más nuevo al más viejo. A diferencia de Control Print, un lote es de una sola PC y una
  sola impresora: lo de la caja principal y lo de la caja 2 a la misma hora son de dos clientes (auditoría, 2026-11-04).
- **Con el teclado** (revisión de #155): al abrir F2 el cursor está en la tarjeta más nueva; ↑ ↓ pasan a otra, Espacio la elige y
  Enter agrega lo elegido (sin nada elegido, la que tiene el cursor). Un escaneo con el cursor en una tarjeta entra al ticket, como
  con el cursor en el cuadro.
- **Las de la caja 2** dicen «Terminó con error» si la impresora las detuvo (aunque no se lea el contador), «Precio estimado» si no
  se pudieron medir, y su nota. Su resumen dice B/N si no llevan tinta de color, aunque el driver estuviera en color. La lista trae
  hasta las 200 más nuevas, sin miniaturas: cada una se pide al mostrarse.
- **Cuáles:** las por cobrar de hoy y de ayer (`ControlPrint:Dias`). Se leen todas las de esos días (hasta 2,000), se quita
  lo que ya se cobró aquí (allá sigue por cobrar) y de lo que queda salen hasta 200: así lo cobrado en la caja 2 no ocupa el
  lugar de lo que de verdad falta. Lo de antes que siga por cobrar allá ya no sale aquí. Si Control Print está en «cada quien
  ve sus trabajos», sin sesión solo se ven los de «Sin sesión», y F2 lo dice.
- **Con «Revisar hojas» no se cobra aquí** (la impresora contó menos de las que se mandaron): aquí solo se cobraría completa
  y el cliente pagaría hojas que no salieron. La tarjeta sale apagada y dice que se resuelve en la caja principal («cobrar lo
  que salió» o «no vendida»); después sale para cobrarse con su precio.
- **Ligero para la caja 2:** el servicio tiene la cola siempre al día (espera los avisos de Control Print, `api/changes`; si
  no los da, lee cada 10 s). Con F2 abierto, la caja pregunta cada 5 s cuántas hay y solo vuelve a leer y a pintar lo que
  cambió. Las miniaturas se piden cuando se van a ver, de una en una, y se quedan guardadas en el servicio y en la caja. Si
  Control Print deja de contestar, «Agregar al ticket» no saca las suyas que ya estaban en el ticket: se quedan con lo último que se
  leyó, y al cobrar se revisan como siempre.
  Arriba, el chip de «Impresiones por cobrar» cuenta las dos, sin leer las listas.

### Cobrarlas

- **Tocar una tarjeta la elige** (o «Elegir» un lote) y «Agregar al ticket» las pone en el ticket, con las de la caja 2, en
  **dos líneas**: las impresiones y, aparte, las copias del panel de la impresora (las de Control Print de ese tipo y las
  registradas en F2), como las separa la tienda. Con trabajos de Control Print cada línea dice cuáles: «Impresión #1234»,
  «Impresiones #1234, #1235 y 1 de CAJA2», «Copias #1240»; los números seguidos van juntos («#1234–#1240») y, si no caben en
  las 120 letras de la línea, termina en «y 12 más» (la caja guarda la lista completa).
- **Justo antes de cobrar, el servicio vuelve a leer en Control Print** (solo leer) los trabajos del ticket: todos a la vez,
  sin sus miniaturas y con **medio segundo** como lo más que espera. Si alguno ya no está por cobrar allá (se cobró en la
  caja principal, se marcó no vendido, se canceló), ya se cobró en esta caja, le cambiaron el precio o tiene hojas por
  revisar, **no se cobra nada**: la caja lo quita del ticket (o pone el precio de ahora), dice por qué y el cajero cobra otra
  vez. **Nunca se cobra dos veces.** La caja espera el cobro 2.5 s (2 del folio de eleventa y medio para la red y la base):
  lo que tarde Control Print se le quita a la espera de eleventa, así que por él el cobro nunca pasa de ese plazo. En el piloto,
  el renglón de cada cobro en el registro del servicio dice las dos: «Cobro con eleventa · Venta B-000123 · el cajero esperó
  1.62 s (antes, 0.31 s la revisión en Control Print) · salió con el folio 4521 de eleventa».
- **Si Control Print no contesta en ese medio segundo** (2026-10-29, decidido, por revisar): lo que sí contestó a tiempo
  cuenta tal cual (lo cobrado allá, lo no vendido o con otro precio no se cobra, ni siquiera «sin revisar»). Solo una tarjeta
  sin respuesta se cobra con lo último que se leyó de ella, si es de hace 10 minutos o menos (a ese precio), y queda «sin
  revisar»; cuando Control Print vuelve a contestar, se revisa. Si es de antes, no se cobra: «Control Print no contesta:
  cóbrala cuando conteste». **Nunca a mano con IMP:** eso no la ligaba a su trabajo, su tarjeta volvía a salir y se cobraba
  otra vez.
- **La caja 2 recuerda lo que cobró** (la tabla `TrabajosControlPrintCobrados`: el número, cuándo lo vio Control Print, la
  venta, el precio, el costo, si eran copias y si se cobró sin revisar), así sus tarjetas ya no salen aunque allá sigan por
  cobrar. Un Control Print que se reinstala vuelve a empezar sus números: por eso se compara también la hora.
- **Lo cobrado aquí que después se cobra también en la caja principal se nota** (2026-10-29): mientras está por cobrar allá,
  la caja 2 lo sigue viendo en cada lectura; cuando deja de estarlo, lo lee, y si allá también se cobró, lo avisa con qué
  trabajo, en qué venta de aquí y quién lo cobró allá: arriba («Cobradas dos veces», un chip que abre F2), en F2, en `GET
  /api/controlprint`, en los avisos del dueño (con su propio tipo de aviso, «Impresión cobrada dos veces»: apagar los de la
  impresora no lo apaga) y en el registro del servicio. Se queda guardado (sigue después de reiniciar) y se ve mientras la
  venta de aquí siga cobrada, en los días que se ven. Se revisa con el cliente y se le devuelve uno. Es la única defensa: el
  POS nunca le escribe a Control Print (el dueño, 2026-10-29: «Solo los lee»).
  - **Cuándo se nota:** Control Print 1.12 avisa a sus PC un lote («Cobrar 3 · $25»), pero no el «Cobrar» de una sola
    tarjeta: por eso la cola se vuelve a leer cada 20 s aunque no avise nada (una búsqueda de lo que sigue por cobrar, sin
    miniaturas; casi siempre una página). Un lote se nota en unos segundos; el «Cobrar» de una sola tarjeta, en unos 20.
  - **Días después:** lo cobrado aquí sigue «por cobrar» allá para siempre; lo de antes de los días que se ven se revisa cada
    15 minutos, hasta 30 días atrás. Lo que allá se cancela, se marca no vendido o se borra queda revisado (sin cobro doble)
    y ya no se busca.
- **En el piloto, a eleventa como las cobra la tienda:** la línea de impresiones va como el producto **IMP «Impresiones»** y
  la de copias como **COP «Copias»** (los dos a granel a $10; COP en el departamento de papelería, con otro costo): la
  cantidad es el monto entre su precio (0.2 = $2, 1.5 = $15) y el renglón lleva los números («Impresión #1234»). Nunca quedan
  por revisar por ser de aquí: si el catálogo no sabe cuál es IMP o COP en eleventa, el puente lo busca allá por su código
  (`Eleventa:ProductoImpresiones`, `Eleventa:ProductoCopias`). Si en el mismo ticket se vendió IMP o COP a mano, eleventa lo
  junta en un solo renglón (como lo hace ella). Fuera del piloto, cada línea es una pieza por el monto.
- **Cancelar o devolver:** cada línea se devuelve completa (la ventana de devolver solo ofrece «Completa») y solo suelta lo
  suyo (con una sola línea de impresiones, como en las ventas de antes de separar las copias, suelta todo lo que se cobró con
  ella): sus trabajos vuelven a estar por cobrar (los de la caja 2 en el módulo y los de Control Print vuelven a salir en F2
  si allá siguen por cobrar). Si el cliente se queda con alguno, se vuelve a cobrar desde F2. Una parte devuelta en eleventa
  (eleventa sí la deja) llega a la línea con la lectura, como dinero que se le regresó, y las impresiones siguen cobradas; si
  con eso se devuelve completa, se sueltan.

### Si Control Print no contesta

- Arriba sale «Control Print no contesta» (un aviso: cede como los demás, «Control Print no…») y F2 lo dice, sin sus tarjetas
  (no se podrían revisar al cobrar): se cobran cuando conteste, nunca a mano. Lo impreso en la caja 2 sigue igual: nada
  detiene la venta.
- Lo nota al revisar un cobro (medio segundo) o, si la caja principal se apaga sin avisar, en unos 13 s (la espera de cambios
  es de 10 s); antes tardaba hasta 40 s.
- Una tarjeta que ya estaba en el ticket se cobra con lo último que se leyó si es de hace 10 minutos o menos; si no, sale del
  ticket (ver «Cobrarlas»).
- El servicio lo vuelve a intentar cada 10 s y, cuando contesta, vuelve a leer todo solo y revisa lo que se cobró sin
  revisar.

### La cola de la caja 2

- **El agente del POS** (`AgenteImpresion`, lo arranca la caja con `ColaImpresion` en su `caja.json`) vigila la cola de la
  WF-6590 de la caja 2, mide la tinta de cada trabajo cuando Windows ya lo imprimió y lo manda al servicio, que le pone
  precio con **los costos de Control Print** (`GET /api/settings/cost`, solo leer): de memoria mientras estén al día (5
  minutos, o hasta que Control Print avise que cambiaron). Cuando cambian, quedan también como los de la sucursal: con ellos
  se cobran las copias del panel y se cotiza, y si al arrancar Control Print no contesta, se usan esos. Los que se reemplazan
  se quedan en su historial (`HistorialCostosImpresion`, con su fecha): nada se pierde, ni la calibración de la tinta. **Los
  de fábrica de Control Print no se usan** (un Control Print reinstalado arranca con ellos): se sigue con los de antes y `GET
  /api/controlprint` y el registro lo avisan.
- **Lo que necesita la cola** (como Control Print en sus PC): una cola de esta PC con su puerto TCP/IP a la impresora (no la
  compartida desde la caja principal), el driver «EPSON WF-6590 Series», **«Habilitar características de impresión
  avanzadas»** (así Windows guarda cada trabajo en EMF, que es lo que se mide; sin eso queda en RAW y solo se estima por
  páginas) y **«Conservar documentos impresos»** (para leerlo ya impreso; el agente lo quita de la cola después). El agente
  anota en el registro de la caja si algo de eso falta. Cómo dejarla: [la guía del fin de
  semana](pruebas-en-tienda.md#control-print-en-la-caja-2-sus-tarjetas-y-la-cola-de-la-caja-2).

### Cómo se configura (el servicio de la caja 2)

- En `servicio.json`, la sección `ControlPrint`: `Servidor` (la IP de la caja principal, «192.168.1.106»), `Puerto` (47815),
  `Huella` (opcional) y `Dias` (2). **Sin la sección no hay tarjetas**, como antes.
- **El código de conexión va solo en la variable de entorno del servicio, `ControlPrint__Codigo`** (como `Eleventa__Clave`):
  lo pone `instalacion\poner-codigo-controlprint.ps1`, que lo pide sin que se vea, reinicia el servicio y dice si Control
  Print contestó. Nunca va en un archivo ni se escribe en pantalla: **uno en `servicio.json` (o en otro archivo) se ignora**
  y el registro del servicio y `GET /api/controlprint` dicen dónde está y dónde va, sin repetirlo. Si por error se pone en
  `Servidor` (o ahí va algo que no es una IP ni el nombre de una PC), no se busca en la red ni se repite: F2 y `GET
  /api/controlprint` dicen que se corrija. Y si Control Print lo mandara en algún texto (el nombre de un documento, una nota,
  sus costos), ese texto llega vacío: no sale en F2 ni se guarda.
- **El certificado de Control Print queda fijado** en el primer saludo con el código correcto (o es el de `Huella`): su
  huella se guarda junto a la base (`pos.controlprint`) y desde ahí otro certificado en esa dirección se rechaza sin mandarle
  nada. Si Control Print se reinstala (certificado nuevo), se borra ese archivo (o se pone la huella nueva en `Huella`) y se
  reinicia el servicio.
- `GET /api/controlprint` (en la caja 2, `http://localhost:5080/api/controlprint`) dice cómo lo ve el servicio: si contesta,
  su nombre y versión, la huella fijada, cuántas por cobrar leyó, si no contesta por qué, y sus avisos (el código en un
  archivo, los costos de fábrica, lo cobrado dos veces). Nunca el código.

## Cómo lee el POS a Control Print

Mientras tanto, el POS lee a Control Print por su red (para juntar sus datos y, después, para mostrar su cola en la caja 2),
y **nunca le escribe** (el dueño, 2026-10-25).

Lo que se vio el 2026-10-25 desde la PC de desarrollo, en la red de la matriz: la caja principal es **PRINCIPAL
(192.168.1.106)** y tiene Control Print **1.12.0 en modo Servidor**: HTTPS en el puerto **47815**, protocolo 1, con su
certificado propio «Control Print PRINCIPAL» (SHA-256
`7B:D8:39:26:F8:86:B2:2E:B0:35:99:E8:C4:BA:08:85:51:85:E8:39:A9:64:AC:4B:00:C8:E6:34:56:D9:A5:62`). La búsqueda por UDP no
contesta (el firewall); el puerto 47815, sí. Sin el código de conexión solo contesta el saludo. En la caja principal no se
instala nada.

`ClienteControlPrint` (en el módulo, [README](../src/Impresiones/README.md#control-print-por-la-red)) habla el mismo protocolo
que las PC «Conectadas» de Control Print (ControlPrint.Net):

- **El saludo, sin el código:** `api/hello` con un reto de esta PC. Control Print contesta su nombre, su versión y una prueba
  (un HMAC del código, del reto y de la huella de su certificado). El cliente la compara con el código que se escribió sin
  mandarlo: si no coincide, no se le manda nada (un código mal escrito, o una PC que se hace pasar por Control Print). Si
  coincide, fija ese certificado: si después otro contesta en la misma dirección con otro certificado, se rechaza. Con la
  huella de arriba (`--huella`), se rechaza desde el saludo.
- **Después, cada lectura** lleva el código (`X-CP-Key`), el nombre de la PC con «(POS)» (`X-CP-Device`) y la versión del
  POS (`X-CP-Version`): así sale en Control Print, Ajustes → Red, entre las PC conectadas de las últimas 12 horas.
- **Solo lee:** no inicia sesión, no cobra, no hace acciones de un trabajo, no cambia la configuración ni le pide una
  lectura a la impresora. Solo GET y la búsqueda de trabajos (un POST que solo lee); el cliente rechaza cualquier otra cosa
  antes de mandarla, y las pruebas lo vigilan con un Control Print falso que falla si recibe una escritura. Por lo mismo no
  lee el reporte de Control Print, que pide la sesión del administrador: la búsqueda trae los mismos campos.
- **Llamadas cortas,** como en Control Print: 3 s para conectar, 8 s cada una (30 s todo el historial de lecturas o de
  cotizaciones) y, sin conexión, las siguientes fallan al instante durante 5 s.
- **Lo que lee:** los trabajos por fecha y de página en página, un trabajo con su miniatura, el resumen de un día, las caras
  registradas (para el contador), las lecturas de la impresora, la calibración, cómo está el servidor, las cotizaciones, los
  usuarios (solo número, nombre, perfil y si está activo: aunque el servidor mandara más, no se guarda), la configuración y
  la espera de cambios (sus contadores).
- **Los costos** (Ajustes → Costos) llegan como `CostSettings`, el mismo tipo con que el módulo calcula: para ponerle precio,
  como Control Print, a lo que imprima la caja 2 con el agente del POS. Se guardan en memoria y se vuelven a leer cuando
  Control Print avisa que cambió su configuración, cada 5 minutos o cuando se pide; si no contesta, se dan los de antes y se
  dice que son de antes.
- **«Cada quien ve sus trabajos»:** si Control Print está así (Ajustes), sin sesión solo deja ver los del invitado («Sin
  sesión»). El POS no inicia sesión, así que solo vería esos; `recopilar-control-print` lo avisa.

## Recopilar los datos de Control Print

Para seguir con el módulo con datos de verdad, `recopilar-control-print` (PapeleriaPos.Herramientas) junta lo que tiene
Control Print, solo leyendo.

### Cómo correrlo

Desde una PC con el repositorio, en la red de la tienda (la de desarrollo), con la huella del certificado de Control Print
(la de [arriba](#cómo-lee-el-pos-a-control-print)):

```
dotnet run --project src/PapeleriaPos.Herramientas -- recopilar-control-print --servidor 192.168.1.106 --huella 7B:D8:39:26:F8:86:B2:2E:B0:35:99:E8:C4:BA:08:85:51:85:E8:39:A9:64:AC:4B:00:C8:E6:34:56:D9:A5:62
```

- **Con `--huella`,** desde el saludo solo contesta el Control Print de verdad: el nombre y la versión que dice antes de pedir
  el código ya son los suyos. Sin ella, esos no se comprueban hasta escribir el código (si no coincide, no se le manda nada,
  pero lo que se vio pudo decirlo otro). Si Control Print se reinstaló (otro certificado), lo dice y no manda nada: entonces se
  corre sin `--huella`, como la primera vez (el código comprueba que es él antes de mandarle algo), y la huella nueva que
  muestra se anota aquí y en `scripts/recopilar-control-print.cmd`.
- **El código de conexión:** espera a que salga «Código de conexión:» y escríbelo entonces. Lo que se teclee antes (mientras
  compila `dotnet run` o mientras saluda) queda a la vista: la terminal de Linux lo muestra tal cual. Está en la caja
  principal, Control Print → Ajustes → Red, «Código de conexión» (16 letras y números; los guiones y las mayúsculas dan
  igual). No se ve al escribirlo (salen asteriscos); si no coincide, lo vuelve a pedir, hasta tres veces. Nunca va en la línea
  de comandos ni se escribe en pantalla o en archivos. La variable `CONTROLPRINT_CODIGO` es para las pruebas y la
  automatización, no para correrlo a mano: escrita en la terminal, el código se queda en su historial.
- **Opciones:** `--puerto` (47815), `--salida <carpeta>`, `--desde AAAA-MM-DD` (solo los trabajos, cotizaciones y lecturas
  desde ese día), `--miniaturas` (también la de cada trabajo, en PNG: una petición por trabajo, tarda más) y `--huella <SHA-256>`
  (el certificado que tiene que presentar). En `--servidor` va la IP o el nombre de la PC: si lo que se pone parece el código
  de conexión (escrito como sea), no se acepta, no se busca en la red y no se repite.
- **Lo que deja** en `--salida` (por omisión `~/.cache/control-print-datos` en Linux y `Documentos\TPV\control-print-datos` en
  Windows): una carpeta con la fecha con `control-print.db` (SQLite: los trabajos con todos los campos que manda Control Print
  —estado, páginas, color o B/N, costos, precio, cobrado o no vendido con su motivo, la PC, quién atendió, las fechas, su
  desglose—, las copias del panel, el resumen y las caras de cada día, las cotizaciones, las lecturas de la impresora, la
  configuración y los usuarios), `miniaturas/` y un `README.md` con cuántos hay de cada cosa y de qué fechas. Todo llega hasta
  la hora en que empezó (la de corte): Control Print da su resumen solo de días enteros, así que el del día que no había
  terminado a esa hora se calcula de los trabajos, como lo calcula él, y queda marcado. Son datos reales: nunca van al
  repositorio.
- **Al final compara cada mes** con lo que cuenta Control Print: una PC que estuvo sin red sube sus trabajos al volver, con la
  fecha en que se imprimieron, y pueden caer en un mes ya leído. El mes que cambió se vuelve a leer, una vez. Si aun así no
  cuadra, su README no dice «Completa»: dice cuántos faltan o sobran, en qué mes y que hay que volver a correrlo.
- **Se puede detener** con Ctrl+C (la segunda vez sale ya): lee mes por mes, de 50 en 50 trabajos, y guarda cada página en una
  transacción. Al volver a correrlo el mismo día con las mismas opciones, sigue en la misma carpeta donde se quedó y llega
  hasta la misma hora que la primera vez. Si Control Print deja de contestar, reintenta tres veces y se detiene igual. Una
  recopilación terminada no se toca: otra del mismo día va en «fecha-2».

### En la caja 2

La caja 2 no tiene el SDK de .NET: las herramientas van en un solo `.exe`, con .NET adentro (unos 55 MB), aparte del paquete
de la caja y del servicio.

1. **El `.exe`:** el flujo «Herramientas para la tienda» de GitHub Actions (a mano, en la pestaña Actions, o con una etiqueta
   `herramientas-*`) deja el artefacto `PapeleriaPos-Herramientas-1.0.<corrida>` con `PapeleriaPos.Herramientas.exe` y
   `recopilar-control-print.cmd`. O en cualquier PC con el SDK, también en la de desarrollo (Linux):

   ```
   dotnet publish src/PapeleriaPos.Herramientas -c Release -r win-x64 --self-contained -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:EnableCompressionInSingleFile=true -p:DebugType=none -o publicar/herramientas
   ```

   y `scripts\recopilar-control-print.cmd` se copia junto al `.exe`.
2. **En la caja 2,** los dos en una carpeta (por ejemplo `Documentos\TPV\herramientas`), sin instalar nada, y se abre
   `recopilar-control-print.cmd`. Busca Control Print en `PRINCIPAL` (o en la variable `CONTROLPRINT_SERVIDOR`) con la huella
   de arriba (o la de la variable `CONTROLPRINT_HUELLA`); con la IP: `recopilar-control-print.cmd --servidor 192.168.1.106`. Lo
   demás se pasa igual: `recopilar-control-print.cmd --miniaturas`. El código, cuando salga «Código de conexión:». Lo
   recopilado queda en `Documentos\TPV\control-print-datos`.
3. La primera vez, .NET deja unos archivos suyos en `%TEMP%\.net`. Si el `.exe` llegó descargado del navegador, Windows puede
   preguntar antes de abrirlo (SmartScreen: no está firmado): «Más información» → «Ejecutar de todas formas».

### Analizarlos

`analizar-control-print` (2026-11-03) lee una recopilación, **solo leyendo**, y deja un informe corto para leerlo con el dueño
(`analisis.md`, junto a la recopilación: no va al repositorio). Contesta las preguntas de [lo que solo dicen los
datos](#lo-que-solo-dicen-los-datos-la-tienda-o-windows):

```
dotnet run --project src/PapeleriaPos.Herramientas -- analizar-control-print [--carpeta <recopilación>] [--base pos.db [--sucursal M]]
```

- **Los trabajos por mes, PC y cola:** cuántos, sus caras, lo cobrado, lo que sigue por cobrar, las no vendidas y los cancelados; y en
  cuántas colas se imprimió (cada una sería de una impresora en el módulo).
- **Las copias del panel** por mes, y qué parte son de lo registrado.
- **Las cotizaciones:** cuántas se guardaron, se usaron, se cobraron con su precio y vencieron sin usarse.
- **Los precios puestos** (a mano, especial, solo lo que salió, cotizado): cuántos, cuánto contra el calculado y quién los puso; de los
  clientes, solo cuántos son.
- **Las no vendidas** por motivo, con su costo; **las hojas que faltaron** y cómo se resolvieron; **lo medido** contra lo estimado, por
  PC y cola.
- **Lo cotizado contra lo impreso** (2026-11-08): cada cotización con su impresión (la que ligó Control Print o, si no, el mismo
  documento con casi las mismas páginas mientras valía), el precio de una y otra, cuántas se imprimieron igual y, con su tinta, la
  corrección inicial de cotizar; las que se imprimieron distinto, en qué (calidad, papel, caras). Y **la calidad y el papel con que se
  imprime** en cada cola, contra los de las cotizaciones.
- **El precio de Control Print contra el del módulo:** vuelve a calcular cada trabajo con su desglose (la tinta de cada página) y los
  costos que tiene Control Print, y dice cuántos dan el mismo precio y cuáles se alejan más; con `--base`, también con los costos de
  esa base del POS.

Sin documentos, notas ni nombres de clientes: los trabajos van por su número y su día. Por omisión toma la recopilación más nueva de
`~/.cache/control-print-datos` (en Windows, `Documentos\TPV\control-print-datos`).
