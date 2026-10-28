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

- Que los costos solo los vea quien tiene permiso, aplicado en el servidor y no solo en la pantalla.
- Cotizar PDF y detector de documentos comunes (CURP, recibo de luz).
- Impresiones desde el celular y copias hechas en el panel de la impresora.
- Varias impresoras: la matriz tiene **dos WF-6590 y una EcoTank L5590**, todas por red. Hoy el POS mide y cobra una
  sola. Las tres van con la entrega 4, después de alcanzar a eleventa (meta del dueño, 2026-10-15); mientras, Control
  Print sigue cobrando aparte ([plan](plan.md#decidido-mientras-el-dueño-no-estaba-2026-10-17)).

## Dónde vive el código

El dueño pidió (2026-10-14) separar el núcleo de Control Print, sin retirarlo, para seguir trabajando su lógica sin
afectar el resto del POS. Desde el 2026-10-15 vive aparte, en `src/Impresiones`, con sus pruebas en `tests/Impresiones`:
el POS lo usa por un puerto (lo que el módulo le pide: la hora, las cajas, el horario y a quién avisar) y por una fachada
(lo que la venta hace con las impresiones), y sus tablas siguen en la base del POS. Los proyectos, sus límites, sus
pruebas y cómo cambiar su esquema: su [README](../src/Impresiones/README.md).

## Transición

Control Print sigue en uso hasta que el POS lo reemplace en la matriz, durante el piloto.

## Control Print en la caja 2: sus tarjetas y la cola de la caja 2

El dueño (2026-10-25): «agrega ya las tarjetas de la cola de Control Print (el sistema sigue instalado en la caja principal),
de esta forma podemos empezar a integrarlo al uso diario». Control Print sigue en la caja principal y no se puede instalar en
la caja 2 (pide Windows 11). Así que, en la caja 2:

- **F2 enseña la cola «por cobrar» de Control Print**, como sus tarjetas, y **el POS solo la lee** (el dueño, 2026-10-25):
  nunca marca nada como cobrado allá ni le escribe nada. Lo que se cobra en la caja 2 va a la venta del POS y, en el piloto,
  a eleventa, como cualquier venta.
- **Lo que se imprime en la caja 2 lo mide el agente de impresión del POS** (la cola de la WF-6590 de esa PC) y le pone
  precio con los costos de Control Print, leídos de su servidor. Eso tampoco va a Control Print: allá no aparece.

**Lo que hay que saber en la caja principal:** lo que se cobra en la caja 2 **sigue «por cobrar» en Control Print**. La caja
2 recuerda lo que cobró (la venta y el número de cada trabajo) y ya no lo muestra, pero en la caja principal sí se sigue
viendo: si alguien quiere cobrarlo ahí, primero hay que ver si ya se cobró en la caja 2 (en eleventa, la venta de la caja 2
lleva el renglón IMP con el número: «Impresión #1234»).

### Las tarjetas en F2

- **Como en Control Print:** la miniatura, el número (#1234), el título (la etiqueta o el documento), «3 págs × 2 copias ·
  Color · Carta · Papel normal · Dúplex», los chips (la hora, la PC y quién atendía, Especial, Cotizado, Copias en impresora,
  Revisar hojas) y el precio (el puesto allá o el calculado). Las de la caja 2, junto a ellas, con su chip «Impresa en CAJA2»
  y sus botones de siempre (No vendida, Cobrar lo que salió). El costo, solo para quien tiene «Ver costos, utilidad y
  margen».
- **En lotes, como en Control Print:** lo mandado a 2 minutos o menos uno del otro va junto, con «lote de 3 trabajos · $25» y
  «Elegir 3 · $25», que elige todo el lote. Del más nuevo al más viejo.
- **Cuáles:** las por cobrar de hoy y de ayer (`ControlPrint:Dias`), hasta 200; lo de antes que siga por cobrar allá ya no
  sale aquí. Si Control Print está en «cada quien ve sus trabajos», sin sesión solo se ven los de «Sin sesión», y F2 lo dice.
- **Ligero para la caja 2:** el servicio tiene la cola siempre al día (espera los avisos de Control Print, `api/changes`; si
  no los da, lee cada 10 s). Con F2 abierto, la caja pregunta cada 5 s cuántas hay y solo vuelve a leer y a pintar lo que
  cambió. Las miniaturas se piden cuando se van a ver, de una en una, y se quedan guardadas en el servicio y en la caja.
  Arriba, el chip de «Impresiones por cobrar» cuenta las dos, sin leer las listas.

### Cobrarlas

- **Tocar una tarjeta la elige** (o «Elegir» un lote) y «Agregar al ticket» las pone en la línea de impresiones, con las de
  la caja 2. Con trabajos de Control Print la línea dice cuáles: «Impresión #1234», «Impresiones #1234, #1235 y 1 de CAJA2».
- **Justo antes de cobrar, el servicio vuelve a leer en Control Print** (solo leer) cada trabajo del ticket. Si ya no está
  por cobrar allá (se cobró en la caja principal, se marcó no vendido, se canceló), si ya se cobró en esta caja o si le
  cambiaron el precio, **no se cobra nada**: la caja lo quita del ticket (o pone el precio de ahora), dice por qué y el
  cajero cobra otra vez. Si Control Print no contesta en ese momento, tampoco: no se sabe si sigue por cobrar. **Nunca se
  cobra dos veces.**
- **La caja 2 recuerda lo que cobró** (la tabla `TrabajosControlPrintCobrados`: el número, cuándo lo vio Control Print, la
  venta, el precio y el costo), así sus tarjetas ya no salen aunque allá sigan por cobrar. Un Control Print que se reinstala
  vuelve a empezar sus números: por eso se compara también la hora.
- **En el piloto, a eleventa como las cobra la tienda:** la línea de impresiones va como el producto **IMP «Impresiones»** (a
  granel a $10): la cantidad es el monto entre su precio (0.2 = $2, 1.5 = $15) y el renglón lleva los números («Impresión
  #1234»). Las dos fuentes y las copias del panel registradas en F2 van así, y nunca quedan por revisar por ser de aquí: si
  el catálogo no sabe cuál es IMP en eleventa, el puente lo busca allá por su código (`Eleventa:ProductoImpresiones`). Si en
  el mismo ticket se vendió IMP a mano, eleventa lo junta en un solo renglón (como lo hace ella). Fuera del piloto, como
  siempre: una pieza por el monto.
- **Cancelar o devolver:** la línea de impresiones se devuelve completa (la ventana de devolver solo ofrece «Completa»). Sus
  trabajos vuelven a estar por cobrar: los de la caja 2 en el módulo y los de Control Print vuelven a salir en F2 si allá
  siguen por cobrar. Si el cliente se queda con alguno, se vuelve a cobrar desde F2. Una parte de IMP devuelta en eleventa
  (eleventa sí la deja) llega a la línea con la lectura, como dinero que se le regresó, y las impresiones siguen cobradas; si
  con eso se devuelve completa, se sueltan.

### Si Control Print no contesta

- Arriba sale «Control Print no contesta» (un aviso: cede como los demás, «Control Print no…») y F2 lo dice, sin sus tarjetas
  (no se podrían revisar al cobrar). Lo impreso en la caja 2 y cobrar impresiones a mano con IMP siguen igual: nada detiene
  la venta.
- Una tarjeta que ya estaba en el ticket no se cobra: sale del ticket con el porqué («cóbralo a mano con IMP» si el cliente
  no ha pagado).
- El servicio lo vuelve a intentar cada 10 s y, cuando contesta, vuelve a leer todo solo.

### La cola de la caja 2

- **El agente del POS** (`AgenteImpresion`, lo arranca la caja con `ColaImpresion` en su `caja.json`) vigila la cola de la
  WF-6590 de la caja 2, mide la tinta de cada trabajo cuando Windows ya lo imprimió y lo manda al servicio, que le pone
  precio con **los costos de Control Print** (`GET /api/settings/cost`, solo leer): de memoria mientras estén al día (5
  minutos, o hasta que Control Print avise que cambiaron). Cuando cambian, quedan también como los de la sucursal: con ellos
  se cobran las copias del panel y se cotiza, y si al arrancar Control Print no contesta, se usan esos.
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
  Print contestó. Nunca va en un archivo ni se escribe en pantalla. Si por error se pone en `Servidor` (o ahí va algo que no
  es una IP ni el nombre de una PC), no se busca en la red ni se repite: F2 y `GET /api/controlprint` dicen que se corrija.
  Y si Control Print lo mandara en algún texto (el nombre de un documento, una nota, sus costos), ese texto llega vacío: no
  sale en F2 ni se guarda.
- **El certificado de Control Print queda fijado** en el primer saludo con el código correcto (o es el de `Huella`): su
  huella se guarda junto a la base (`pos.controlprint`) y desde ahí otro certificado en esa dirección se rechaza sin mandarle
  nada. Si Control Print se reinstala (certificado nuevo), se borra ese archivo (o se pone la huella nueva en `Huella`) y se
  reinicia el servicio.
- `GET /api/controlprint` (en la caja 2, `http://localhost:5080/api/controlprint`) dice cómo lo ve el servicio: si contesta,
  su nombre y versión, la huella fijada, cuántas por cobrar leyó y, si no contesta, por qué. Nunca el código.

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
