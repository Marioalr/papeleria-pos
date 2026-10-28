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
