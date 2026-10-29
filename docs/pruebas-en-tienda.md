# Pruebas en la tienda

> Las pruebas reales en la matriz solo se pueden hacer **sábado y domingo** ([forma de
> trabajar](notas.md#forma-de-trabajar)). Aquí se junta lo que necesita la tienda, en orden, para prepararlo entre
> semana y no perder tiempo ahí. Sin tocar los datos de eleventa: en la tienda solo se lee, y escribir en el eleventa
> real va después de las semanas de prueba contra una copia y con el visto bueno del dueño
> ([piloto](migracion-eleventa.md#lo-que-falta-para-el-piloto-diseño-2026-10-12-por-aprobar)). Su configuración de red
> solo se cambia en el fin de semana de la red, si el dueño lo aprueba, con respaldo y la forma de regresar.

## Entre semana, antes de cada fin de semana

- **La lista del fin de semana,** en orden de importancia, con el tiempo de cada cosa y quién ayuda.
- **Lo que se lleva listo:** el instalador probado en una PC limpia, los scripts (el de diagnóstico de red, probado en
  una PC con Windows, y el que junta la configuración de cada PC), cables de red, cinta de color y una memoria USB con
  todo.
- **Las herramientas de prueba,** probadas antes en la PC de desarrollo. Las de la impresora de tickets y el lector ya
  están (2026-10-16): `probar-impresora-tickets` y `probar-lector`; el simulador de cajas para el día pico, desde el
  2026-10-17 (`simular-dia-pico`, [abajo](#el-día-pico-en-la-caja-principal-real)). Falta la que lee por SNMP las tres
  impresoras de inyección, con el módulo de impresión.
- **Sin compras** (2026-10-09): todo se hace con lo que ya hay en la tienda.

## Fin de semana del 17 y 18 de octubre

Para la siguiente prueba con una persona real y el piloto en la caja 2
([plan](plan.md#la-versión-unicaja-y-el-piloto-en-la-caja-2)). En la tienda solo se mira y se prueba sin tocar los
datos de eleventa.

1. **La prueba con una persona real, con la pantalla nueva:** el instalador de prueba armado entre semana con lo último
   de `main` (su propio puerto y su base de prueba, aparte de eleventa) y la guía al día, en una PC con Windows.
2. **El paso 7 del piloto:** la pantalla nueva en la ventana de Windows a 1366×768, y en la caja 2, con el repositorio
   al día (como `observar-en-vivo`):
   - `scripts\probar-impresora-tickets.cmd`: dos tickets de prueba a la impresora de tickets, primero a la vez y después
     casi juntos; si la caja principal también tiene el repositorio, abierto en las dos, con Enter al mismo tiempo. Pide
     la IP de la impresora. Se anota lo que dijo de cada ticket y si salieron completos, hasta «Fin del ticket». Solo
     gasta papel: no toca eleventa.
   - `scripts\probar-lector.cmd`: escanear varios códigos, uno de 11 cifras (comparado con el de ese producto en
     eleventa). Dice qué llegó y si en el cobro la caja lo reconoce como el lector por su ritmo; si no, cuánto subir
     `RAFAGA_MS` en `caja.js`. Sin QR: el ticket no lo lleva por ahora (2026-10-16).
3. **Solo mirar, por eleventa**
   ([por qué](migracion-eleventa.md#lo-que-falta-para-el-piloto-diseño-2026-10-12-por-aprobar)):
   - la versión de eleventa en las dos cajas (Configuración ➜ Mantenimiento ➜ Licencia);
   - su respaldo automático: si está activo, si respalda al salir, en qué carpeta, y la hora, el tamaño y el tipo de los
     últimos respaldos;
   - si existe `gbak.exe` en la carpeta de Firebird (`FirebirdSQL20\bin`) y si Firebird corre como servicio de Windows;
   - en la caja 2, la ruta completa de un `gbak.exe` de Firebird 2.0 (con `fbclient.dll` o `gds32.dll` junto a él) y el
     espacio libre del disco: la caja nueva lo usa para respaldar eleventa antes de escribirle cada día (va en
     `Eleventa:Gbak`; los respaldos, unos 5 días, en `ProgramData\PapeleriaPos\respaldos-eleventa`). Solo mirar: no correr
     gbak en la tienda;
   - en la caja principal, con `netstat -ano`, qué programa escucha en los puertos 3050 y 3309;
   - la hora de cada caja contra la del celular (en el piloto, la caja nueva compara la hora de lo que manda con la del
     corte de eleventa para saber si ya se contó allá);
   - cómo se hace el corte de la caja 2 en eleventa al final del día: quién lo hace, a qué hora, si después se cierra
     eleventa o se deja abierto, y si en la mañana se abre eleventa en la caja 2 antes de vender (la caja nueva adopta el
     turno que abre eleventa y toma su corte);
   - si OneDrive sincroniza Documentos en la caja 2 (las notas de `observar-en-vivo` son datos reales);
   - con `observar-en-vivo` en la caja 2: entradas y salidas de dinero, un cierre con diferencia y, si se dan de
     verdad, algo después de medianoche y una devolución o cancelación de un ticket de otra caja o de otro turno (por
     ejemplo, en la caja 2 algo que se vendió en la principal, o un ticket de ayer). Esto último es lo que más falta: el
     script dice en qué turno y con qué caja la anota eleventa. Mientras no se vea, la caja 2 no hace esas devoluciones
     (dice que se hagan en eleventa, que llegan solas con la lectura); cuando se vea, el puente puede aprender a
     escribirlas y la caja 2 a recibirlas también;
   - lo que la [paridad con eleventa](paridad-eleventa.md#qué-mirar-en-la-tienda) marca «por confirmar»: si los cajeros
     usan Ins para la cantidad y si el ticket sale siempre, cómo se hace y se imprime el corte, si se consultan ventas o
     cortes de otros días y qué se mira del inventario.
4. **Lo que siga pendiente de conocer la tienda** ([abajo](#primer-fin-de-semana-conocer-la-tienda)), si da tiempo y
   el dueño lo aprueba.

## El día pico en la caja principal real

Cualquier fin de semana, en un rato sin venta (antes de abrir o al cerrar): unos 15 minutos con la caja principal libre. Mide
el servicio del POS en el procesador y el disco de la caja principal de verdad, con un día pico de dos cajas
([plan](plan.md#para-el-piloto-con-dos-cajas)). **Solo lectura para eleventa:** no lo abre ni lo lee; usa una copia de la
base de prueba del POS, nunca la de eleventa ni la de la prueba con una persona.

- **Qué hace** `scripts\simular-dia-pico.cmd`: compila el servicio y el simulador del repositorio, arranca un servicio del
  POS solo para él (en esta PC, en un puerto propio, sin red hacia afuera) con una copia de la base de prueba que dejó el
  instalador (si no está, arma una igual), y dos cajas simuladas venden 120 tickets cada una en 10 minutos, como la caja de
  verdad: con su copia, cobros, búsquedas, devoluciones, cancelaciones, entradas y salidas, un precio que cambia, un producto
  que llega, y la red de la segunda caída 30 segundos a la mitad del día. Al final hace el corte de cada turno, cuadra todo,
  detiene el servicio y borra su carpeta.
- **Antes:** el repositorio al día y .NET en la caja principal (como `observar-en-vivo` en la caja 2), cerrada la caja de la
  prueba del POS y lo demás pesado. Eleventa puede quedarse abierto, sin vender: el simulador le quita procesador.
- **Se corre** con doble clic en `scripts\simular-dia-pico.cmd`. Dice cómo va; al final, «Cuadra» o qué no cuadró.
- **Se anota:** el informe que deja en `Documentos\TPV\dia-pico` (los tiempos contra las metas, la memoria del servicio, el
  cuadre, lo que pasó y la PC). Se trae a la PC de desarrollo: no tiene datos de la tienda.
- **Sale bien si** cuadra (ninguna venta perdida ni duplicada, los cortes al centavo, las existencias con su kardex) y los
  tiempos de 19 de cada 20 quedan en sus metas; si no, el informe dice qué y cuánto, y se revisa entre semana.
- **Con la red de verdad (si da tiempo):** en la caja 2, con la caja de la prueba del POS abierta en la caja principal (su
  servicio, en el puerto 5180), `dotnet run -c Release --project src\PapeleriaPos.Herramientas -- simular-cajas --servicio
  http://<IP de la caja principal>:5180 --cajas 2 --tickets 120 --minutos 10 --caida 30s@50% --salida
  "%USERPROFILE%\Documents\TPV\dia-pico\por-wifi.md"`: las cajas simuladas van por el Wi-Fi de la caja 2. Así no mide la
  memoria del servicio (es de la otra PC: se ve en su Administrador de tareas). Deja las cajas simuladas y sus ventas en
  la base de la prueba con una persona; «Empezar de cero», en la franja de la prueba, la deja como estaba.

## Con el POS en la caja 2: las fotos de lo que se señala

Las fotos de los productos ([entrega 13, parte 2](plan.md#primero-la-versión-unicaja-entrega-0)) se toman en la tienda, con
los productos enfrente, contra el catálogo de verdad: el fin de semana del piloto en la caja 2 (el paso 7), cuando ya lee el
catálogo de eleventa. Las fotos son solo del POS: no tocan los datos de eleventa.

- **Entre semana:** acomodar en Ctrl+M el mostrador y la temporada que corra (Todos Santos, hasta el 5 de noviembre;
  Navidad, desde el 20 de noviembre): así se sabe qué productos fotografiar. Quién las toma (con «Productos, precios y
  etiquetas»: el dueño o el encargado) y con qué celular.
- **En la tienda:**
  1. El celular, en el Wi-Fi de la tienda. En la caja 2, en Productos (F3), la ficha de un producto, «Con el celular…»
     enseña el código QR de la página (y su dirección): entrar con el usuario y el PIN y tocar «Fotos».
  2. Por cada producto: escanear su código (o escribir su nombre), «Tomar la foto», revisar que se vea el producto y
     «Guardar». Con buena luz, de frente, sobre algo liso y llenando la foto.
  3. En la caja 2: Ctrl+M (cada pieza con su foto), buscar uno por su nombre (la foto chica a la izquierda) y F9 (grande).
- **Qué anotar:** si la cámara se abre directo desde la página y en qué celulares; cuánto tarda cada foto, desde «Tomar la
  foto» hasta «Guardada»; cuánto tarda en salir en la caja 2 y si se reconoce bien el producto en su pantalla; cuántas se
  tomaron y en cuánto tiempo.

## Con el POS en la caja 2: la hoja de la temporada y las tarjetas de precio

La hoja de la temporada y las tarjetas de precio ([entrega 13, parte 3](plan.md#primero-la-versión-unicaja-entrega-0)) se
imprimen en las impresoras de inyección y en la láser, que solo están en la tienda. El mismo fin de semana de las fotos, ya
con la temporada acomodada y sus fotos tomadas:

- **En la tienda,** en la caja 2, Productos (F3) › «Temporadas», la temporada que corra:
  1. «2 · Hoja» › «Vista previa» y luego «Imprimir la hoja», en la WF-6590 y en la L5590, al **100 % (tamaño real)**, sin
     márgenes que la achiquen.
  2. «3 · Tarjetas» › «Imprimir…» una hoja de tarjetas; recortar dos por su línea.
  3. En la venta, escanear con el lector de la caja 2 el código de una tarjeta recortada y uno de la hoja: cada uno entra al
     ticket como el producto.
- **Qué anotar:** si la hoja sale completa y en su tamaño (medir una tarjeta: 6.5 × 4.2 cm), si las fotos se reconocen
  impresas (en blanco y negro y a color), si el lector lee los códigos a la primera (también a través de un portatarjetas o
  una mica), y cuánto tarda en salir cada hoja.

## Instalar el piloto en la caja 2: los usuarios

La base de la caja 2 se arma con una copia de la base de eleventa (`importar-eleventa`, como en el [README](../README.md)).
Desde el piloto alfa (2026-10-25) trae también a los usuarios de eleventa, solo su número y su nombre (nunca su clave), y el
PIN de cada quien vive en el POS ([cómo funciona](migracion-eleventa.md#los-usuarios-de-eleventa-y-a-nombre-de-quién-va-cada-cosa-el-piloto-alfa)):

1. **Importar con el dueño, desde la caja 2** (así se hace en la tienda: la caja 2 no tiene el Firebird embebido ni el SDK, y la
   PC de desarrollo no llega al Firebird de la tienda). Primero, la copia del ensayo en la caja principal,
   `PDVDATA-ENSAYO.FDB`, hecha desde la caja 2 con el `gbak` de Firebird 2.0 como dice el instructivo del ensayo (en la caja
   principal no se instala nada). Después, en la caja 2, con las herramientas de un solo archivo:

   ```
   PapeleriaPos.Herramientas.exe importar-eleventa --servidor <nombre o IP de la caja principal> --ruta "C:\Program Files (x86)\AbarrotesMultiCaja\db\PDVDATA-ENSAYO.FDB" --usuario SYSDBA --salida pos.db --duenio <su usuario o su número en eleventa>
   ```

   Pide la clave de Firebird y luego el PIN del dueño, de 4 cifras, dos veces; nada de eso se ve al escribirlo. Lee la copia
   por la red, solo leyendo, y nunca la base en uso: una ruta que termina en `PDVDATA.FDB` no la acepta. El dueño queda con
   todos los permisos; los demás usuarios activos de eleventa llegan sin PIN. Al final dice el usuario del dueño, el que se
   escribe para dar de alta la PC. Sin `--duenio` no importa; si se escribió mal, dice cuáles hay (su número, su usuario y su
   nombre). La base va después en `C:\ProgramData\PapeleriaPos\pos.db`. Con una copia en archivo en la misma PC (y el Firebird
   embebido junto a ella) es lo mismo con `--fdb <la copia>` en lugar de `--servidor`, `--ruta` y `--usuario`.
2. **Dar de alta la PC como caja** con ese usuario y el PIN del dueño, como siempre. En `servicio.json` sigue haciendo falta
   `Eleventa:Cajero`: a su nombre va en eleventa lo de quien no viene de eleventa (alguien que se dé de alta aquí).
3. **Los PIN de los demás:** el dueño entra con su ficha, abre Configuración › Usuarios y permisos y a cada uno le pone su
   PIN («Ponerle PIN»). Mientras, su ficha dice «sin PIN» y al tocarla dice que se lo pida al dueño.
4. **Si a alguien se le olvida el PIN (también al dueño),** en la caja 2, con el programa del servicio que ya está instalado
   ahí (el paquete de la tienda no trae las herramientas) y su misma base. En Inicio › «PowerShell» › «Ejecutar como
   administrador»:

   ```
   & "C:\Program Files\PapeleriaPos\Servicio\PapeleriaPos.Servicio.exe" pin-usuario --usuario <su nombre, su usuario o su número de eleventa>
   ```

   Lo pide dos veces y sin que se vea, y queda en la bitácora (sin el PIN); sirve al momento, aunque el servicio esté
   corriendo. Si dice que la base no está al día, arrancar el servicio (la pone al día) y volver a correrlo. Con las
   herramientas es lo mismo con `pin-usuario --base C:\ProgramData\PapeleriaPos\pos.db --usuario <…>`.
5. **En eleventa, en la caja 2, entrar con el usuario de quien vende** (eleventa sigue abierto ahí): el turno de eleventa es
   de quien entra, y eleventa nunca pone una venta a nombre de otro en un turno. Si eleventa está abierto con otro usuario, lo
   de la caja nueva va de todos modos a nombre de quien lo vendió, y lo dicen el registro del servicio y la caja 2, en el chip
   «Eleventa» de arriba. **Mirar:** que en eleventa una venta de la caja 2 diga el cajero de quien la cobró (el ticket y el
   reporte de ventas por cajero).

## Actualizar la caja 2 del piloto

Un arreglo para la caja 2 se pone con el paquete nuevo y `actualizar.ps1`, sin perder su base ni la configuración del puente
([cómo funciona](arquitectura.md#actualizaciones)). La caja 2 deja de vender unos minutos: se hace en un rato tranquilo y,
mientras, se cobra en el eleventa que sigue abierto en esa caja.

- **Antes, el paquete:** en GitHub, Actions › «Instalador de prueba» › «Run workflow» con `main` (unos 20 minutos: antes
  corre todas las pruebas). La corrida deja el artefacto `PapeleriaPos-Actualizacion-1.0.<número>`: un zip con
  `actualizar.ps1`, `servicio\` y `caja\`. Se lleva a la caja 2 en una memoria USB (o se baja ahí) y se descomprime en una
  carpeta, por ejemplo `Descargas\PapeleriaPos-1.0.57`.
- **En la caja 2:**
  1. En la caja: cobrar o dejar en espera la venta, salir y cerrar la ventana; los tickets en espera se guardan. Si se
     olvida, el script lo pide y espera a que se cierre. Si la caja no responde, con C la cierra a la fuerza: lo último que
     se escaneó en la venta que se atendía puede no regresar.
  2. Inicio › escribir «PowerShell» › «Ejecutar como administrador», y ahí:

     ```
     cd "$env:USERPROFILE\Downloads\PapeleriaPos-1.0.57"
     powershell -ExecutionPolicy Bypass -File .\actualizar.ps1
     ```

  3. Dice la versión que hay y la que pone, respalda, cambia y espera a que el servicio conteste: unos minutos. No cerrar la
     ventana mientras tanto.
- **Sale bien si** termina con «Actualizado de 1.0.x a 1.0.y» y «Ya se puede abrir la caja». Después, en la caja:
  - entrar con la ficha y el PIN: Configuración › Acerca de dice la versión nueva, y regresan los tickets en espera;
  - la primera venta sale con el folio de eleventa (si la caja principal contesta) y arriba no hay avisos nuevos del puente:
    su configuración sigue.
- **Si dice que se regresó a la versión de antes:** la caja 2 quedó como estaba, con su base, y se sigue vendiendo. El arreglo
  no quedó: se trae `actualizar.log` (al final dice dónde está) para revisarlo entre semana.
- **Si dice que no pudo regresar solo:** no vender en la caja 2 (sí en eleventa) y avisar. Lo de antes está completo en la
  carpeta del respaldo que dice (`C:\ProgramData\PapeleriaPos\actualizaciones\<fecha>-<versión>`). Para regresarlo a mano,
  en PowerShell como administrador: `Stop-Service PapeleriaPos`; copiar lo de `base\` a `C:\ProgramData\PapeleriaPos`
  (antes, borrar ahí `pos.db-wal` y `pos.db-shm` si `base\` no los trae), `servicio\` a `C:\Program Files\PapeleriaPos\Servicio`
  y `caja\` a `C:\Program Files\PapeleriaPos\Caja`; y al final `Set-Service PapeleriaPos -StartupType Automatic` y
  `Start-Service PapeleriaPos`.
- **Lo que no cambia:** la base (`C:\ProgramData\PapeleriaPos\pos.db`), la configuración de esa PC con el puente
  (`servicio.json`, en la misma carpeta), la clave de eleventa y el código de Control Print (van solo en variables de entorno
  del servicio) y lo de la caja (su alta, su copia y los tickets en espera, en `C:\ProgramData\PapeleriaPos\Caja`).
- **Otras veces:** con la misma versión no hace nada, y una anterior solo la pone con `-Forzar`. La primera vez, sin el
  servicio en esa PC, se instala con `servicio\instalar-servicio.ps1` y `caja\instalar-caja.ps1` del mismo paquete.

## Control Print en la caja 2: sus tarjetas y la cola de la caja 2

Desde el 2026-10-25 la caja 2 enseña en F2 la cola «por cobrar» de Control Print de la caja principal y mide lo que se
imprime en ella misma ([cómo funciona](impresion.md#control-print-en-la-caja-2-sus-tarjetas-y-la-cola-de-la-caja-2)). El POS
**solo lee** Control Print (el dueño, 2026-10-29: «Solo los lee»): lo que se cobra en la caja 2 va a eleventa como IMP (las
copias del panel, como COP) y **en Control Print sigue «por cobrar»**. **En la caja principal, antes de cobrar en Control
Print, hay que mirar si la caja 2 ya lo cobró** (su aviso «Cobradas dos veces» o su venta): si se cobra dos veces, la caja 2
lo avisa después. En la caja principal no se instala ni se cambia nada; todo lo de aquí es en la caja 2.

- **Entre semana:** el paquete con esta versión, como en [Actualizar la caja 2 del piloto](#actualizar-la-caja-2-del-piloto).
- **En la caja 2, una vez:**
  1. En `C:\ProgramData\PapeleriaPos\servicio.json` (con el Bloc de notas como administrador), la sección de Control Print
     junto a la de eleventa:

     ```json
     "ControlPrint": { "Servidor": "192.168.1.106" }
     ```

     El puerto es 47815 (no hace falta escribirlo). Si se quiere fijar desde ya su certificado: `"Huella":
     "7B:D8:39:26:F8:86:B2:2E:B0:35:99:E8:C4:BA:08:85:51:85:E8:39:A9:64:AC:4B:00:C8:E6:34:56:D9:A5:62"` (la que se vio el
     2026-10-25); sin ella, se fija en el primer saludo. Sin la sección no hay tarjetas, como antes.
  2. El código de conexión: en la caja principal, Control Print → Ajustes → Red, «Código de conexión» (lo ve el administrador
     de Control Print). En la caja 2, Inicio › «PowerShell» › «Ejecutar como administrador»:

     ```
     powershell -ExecutionPolicy Bypass -File "C:\Program Files\PapeleriaPos\Servicio\poner-codigo-controlprint.ps1"
     ```

     Lo pide sin que se vea, lo pone en la variable del servicio (`ControlPrint__Codigo`, junto a la clave de eleventa),
     reinicia el servicio y dice cómo salió. **Sale bien si** termina con «Listo: Control Print 1.12.0 en PRINCIPAL contesta
     con el código; hay N por cobrar de hoy y ayer». Si dice que el código no coincide, se corre otra vez con el de Ajustes →
     Red. Nunca escribe el código. Actualizar no lo toca.
  3. La cola de la WF-6590 en la caja 2 (la que se usa al imprimir desde esa PC). En Configuración › Dispositivos ›
     Impresoras y escáneres, la WF-6590 › Administrar › Propiedades de impresora:
     - **Puertos:** el de TCP/IP de la impresora (su IP, la misma que tiene Control Print en Ajustes), no la impresora
       compartida desde la caja principal (`\\PRINCIPAL\…`): con la compartida la tinta no se puede medir.
     - **Opciones avanzadas:** el controlador «EPSON WF-6590 Series», **«Habilitar características de impresión avanzadas»
       activado** (así Windows guarda cada trabajo en EMF; sin eso queda en RAW y solo se estima por páginas) y **«Conservar
       documentos impresos» activado** (también lo pone `instalar-caja.ps1 -ColaImpresion "<nombre de la cola>"`). La cola no
       se llena: el agente quita cada trabajo ya medido.
     - Para revisarlo, en PowerShell: `Get-Printer | Format-Table Name,DriverName,PortName,Shared,KeepPrintedJobs` y
       `Get-CimInstance Win32_Printer | Format-Table Name,RawOnly` (la de la WF-6590 tiene que decir `KeepPrintedJobs True` y
       `RawOnly False`).
  4. Cerrar la caja y, en `C:\ProgramData\PapeleriaPos\Caja\caja.json`, poner `"ColaImpresion": "<nombre de la cola>"`
     (exacto como sale en `Get-Printer`). Al abrir la caja, en `registros\caja-AAAA-MM-DD.log` sale «Agente de impresión:
     Vigilando «…» (driver EPSON WF-6590 Series, puerto …, conserva impresos: sí, EMF: sí)»; si falta algo, ahí lo dice.
- **Qué revisar:**
  1. F2 en la caja 2: las tarjetas de Control Print con su número, título, resumen, hora, PC, precio y miniatura, en sus
     lotes; arriba de ellas, la línea que dice que solo se leen.
  2. Imprimir algo en la caja principal: sale en F2 de la caja 2 en unos segundos, sin cerrarlo ni abrirlo.
  3. Cobrar en la caja 2 una tarjeta de Control Print: el ticket dice «Impresión #…»; en eleventa, la venta de la caja 2
     lleva IMP con la cantidad (el monto entre 10) y el número; en Control Print sigue «por cobrar» y en F2 de la caja 2 ya
     no sale. Con unas copias del panel (las de Control Print o las registradas en F2), van en su propia línea «Copias» y en
     eleventa como COP.
  4. Imprimir algo desde la caja 2: sale en F2 con «Impresa en …» y su precio, puesto con los costos de Control Print
     (comparar con lo que daría Control Print para el mismo documento).
  5. Poner en un ticket de la caja 2 una tarjeta, cobrarla en la caja principal (Control Print) y luego cobrar en la caja 2:
     dice «#… ya se cobró en Control Print», la quita y se cobra lo demás.
  6. Cancelar en la caja 2 una venta con impresiones: sus tarjetas vuelven a salir en F2.
  7. Cerrar Control Print en la caja principal (o desconectarle la red un momento): en unos 13 s arriba sale «Control Print
     no contesta» y F2 lo dice, sin sus tarjetas; lo impreso en la caja 2 sigue. Una tarjeta que ya estaba en el ticket se
     cobra (se leyó hace un momento) y nada dice que se cobre a mano. Al volver, las tarjetas regresan solas.
  8. Cobrado dos veces, con una hoja de prueba: cobrarla en la caja 2 y después, en la caja principal, cobrarla también en
     Control Print. La caja 2 lo avisa: arriba «Cobradas dos veces» y en F2 qué trabajo, en qué venta y quién lo cobró allá.
     Con el «Cobrar» de esa sola tarjeta (Control Print no lo avisa a sus PC) tarda unos 20 s; en un lote («Cobrar N · $X»),
     unos segundos. Anotar cuánto tardó. Devolver la venta de la caja 2 lo quita.
  9. Una impresión con «Revisar hojas» (cancelarla en el panel de la impresora a medias): en F2 de la caja 2 sale apagada y
     no se puede elegir; después de «cobrar lo que salió» en la caja principal, sale con ese precio y se cobra.
- **Qué anotar:** cuánto tarda en salir en la caja 2 una impresión de la caja principal; si las miniaturas se ven; si el
  precio de lo impreso en la caja 2 coincide con Control Print; si la caja 2 se siente lenta con F2 abierto (y cuántas
  tarjetas había); si en la caja principal se entendió que lo cobrado en la caja 2 sigue «por cobrar» allá y que antes de
  cobrar en Control Print se mira la caja 2. **La memoria:** al final de un día de mucho trabajo, en el Administrador de
  tareas de la caja 2, cuánto ocupan «PapeleriaPos.Servicio» y la caja (las miniaturas se guardan por cuántas son, hasta 300
  y 400, no por cuánto pesan: está en las recomendaciones del plan).
- **Para quitarlo:** `poner-codigo-controlprint.ps1 -Quitar`, borrar la sección `ControlPrint` de `servicio.json` y poner
  `"ColaImpresion": null` en `caja.json`. Si Control Print se reinstala (su certificado cambia), el servicio ya no se le
  conecta (dice «otra huella»): borrar `C:\ProgramData\PapeleriaPos\pos.controlprint` (o poner la huella nueva en `Huella`) y
  reiniciar el servicio.

## Restaurar un respaldo

Regresa la base de la caja principal a como estaba en uno de sus respaldos, como «Restaurar» en eleventa: después de un
apagón que la dañó ([abajo](#si-la-base-se-daña)) o si algo salió muy mal. Lo hace el dueño (o quien tenga «Configuración de
la tienda»), con su PIN otra vez ([cómo funciona](arquitectura.md#respaldos)). En el piloto, la caja 2 tiene su propio
servicio y su propia base: se restaura en ella y eleventa no se toca.

- **Qué se pierde:** todo lo que se vendió o se cambió después de ese respaldo: ventas, cortes, entradas y salidas, precios,
  inventario, clientes y usuarios. Los tickets que ya salieron siguen en papel, y en el corte de los turnos que estaban
  abiertos ese efectivo sale como sobrante. La base de ahora no se borra: queda completa en
  `C:\ProgramData\PapeleriaPos\antes-de-restaurar-<fecha>`, por si hay que rescatar algo (cuando ya no haga falta, se borra a
  mano).
- **Los respaldos que hay:** uno al prender la caja principal cada día (diario), uno con cada corte Z, los de «Respaldar
  ahora» y el que hace la caja principal al volver con un respaldo restaurado («Al restaurar»), de los últimos 30 días, en
  `C:\ProgramData\PapeleriaPos\respaldos`.
- **Pasos:**
  1. En cualquier caja, entrar con la ficha y el PIN y abrir Configuración (`/`) › Respaldos › «Restaurar un respaldo» ›
     «Elegir respaldo…».
  2. La lista dice cuándo es cada uno, de qué es (diario, corte Z o «Respaldar ahora») y cuánto mide; el más reciente
     arriba. Si la base se dañó, uno dice «el de antes del apagón».
  3. «Restaurar este» (Tab y Enter, o con el ratón), leer lo que dice, escribir tu PIN y Enter. Primero dice «Revisando el
     respaldo…»: la caja principal lo revisa completo (con una base grande, unos segundos). Si no cabe en el disco, no pasa la
     revisión o lo hizo una versión más nueva del programa, lo dice y no se hace nada.
  4. La caja principal se reinicia sola: tarda unos segundos, a veces un par de minutos. Mientras, todas las cajas venden
     sin conexión, con la franja ámbar. La pantalla espera y al final dice «Se restauró el respaldo del …» y dónde quedó la
     base de antes (o, si no se pudo, por qué; la base se queda como estaba). Si dice «La caja principal ya volvió, pero no
     contestó cómo quedó la restauración», quien la pidió (o esta caja) se dio de alta después del respaldo: salir y entrar
     con otro usuario (o dar de alta la caja otra vez).
- **Después, en las cajas, solo:** cada caja nota la base nueva y baja su copia completa en su siguiente latido. Lo que vendió
  sin conexión sube a la base restaurada, una sola vez, en su turno; si su turno se abrió después del respaldo, se vuelve a dar
  de alta con el mismo número. Sus folios siguen después del último que imprimió: no se repiten (lo dice antes de volver a
  cobrar en línea).
- **Si una caja dice «Se restauró un respaldo de antes del turno de las …»:** en el respaldo sigue abierto el turno de antes de
  esa caja (su corte Z fue después del respaldo). Hacer otra vez el corte Z de ese turno, con lo que se contó en su corte (en el
  piloto no hace falta: lo cierra solo la lectura del corte de eleventa, en unos minutos). Después, el turno de la caja se da
  de alta solo y sube lo que se cobró en él; hasta entonces, no cobrar en el turno de antes.
- **Lo que queda «con error» en una caja:** lo que usa algo que se dio de alta después del respaldo (un producto, un cliente,
  un usuario o una caja nueva) no puede subir; la franja lo dice y se revisa con el encargado.
- **Revisar:** en «Ventas del día» ya no está lo de después del respaldo, y la bitácora dice «Restauró el respaldo del …» con
  quién lo pidió.
- **En el piloto (la caja 2):** lo que la caja 2 ya había mandado a eleventa no se manda otra vez: antes de mandarlo, se busca
  allá. Lo que se anotó a mano en eleventa después del respaldo vuelve a salir «por revisar»: revisarlo en eleventa antes de
  anotarlo otra vez.
- **Si no vuelve en unos minutos:** en la caja principal, Inicio › «Servicios» › «Papeleria POS» tiene que decir «En
  ejecución»; si no, «Iniciar». Si no arranca, el script de [abajo](#si-la-base-se-daña).
- **En la caja del navegador** (desarrollo, sin servicio de Windows) no se reinicia sola: la pantalla lo dice, y se restaura al
  volver a abrirla. Mientras, «Cancelar la restauración».

## Si la base se daña

En la tienda no hay no-break: un apagón a media escritura puede dañar la base de la caja principal. Al volver la luz, el
servicio la revisa (la revisión rápida de SQLite) y, si no pasa:
- la franja de las cajas dice «La base no pasó la revisión después de apagarse de golpe: avisa al dueño, hay que restaurar el
  respaldo de antes del apagón (pos-….db) en Configuración › Respaldos»;
- se sigue vendiendo, y la caja principal deja de hacer respaldos: saldrían dañados, y con los días la limpieza borraría los
  buenos.

**Qué hace el dueño:**
1. Restaurar el respaldo que dice la franja, como en [Restaurar un respaldo](#restaurar-un-respaldo): en la lista es el que
   dice «el de antes del apagón». Lo que se vendió desde ese respaldo hasta ahora se pierde de la base (los tickets impresos
   siguen): si se quiere, anotarlo aparte antes.
2. Al volver la caja principal, la franja ya no dice nada: la base restaurada pasó la revisión y los respaldos siguen solos.

**Si ni siquiera arranca el servicio** (todas las cajas siguen sin conexión, también la de la caja principal, y en
Inicio › «Servicios» «Papeleria POS» no se queda «En ejecución»): la base está tan dañada que no se puede abrir. Las cajas siguen
vendiendo con su copia. En la caja principal:
1. Inicio › escribir «PowerShell» › «Ejecutar como administrador», y ahí:

   ```
   powershell -ExecutionPolicy Bypass -File "C:\Program Files\PapeleriaPos\Servicio\restaurar.ps1"
   ```

2. Enseña los respaldos, el más reciente primero (si la base tenía el aviso, dice cuál es el de antes del apagón): escribir su
   número y Enter, leer lo que se pierde y escribir SI.
3. Revisa que quepa en el disco (si no, lo dice y no hace nada: liberar espacio y correrlo otra vez). Detiene el servicio,
   aparta la base dañada (con su -wal, su -shm y sus marcas) en `C:\ProgramData\PapeleriaPos\antes-de-restaurar-<fecha>` sin
   borrarla, pone el respaldo, arranca el servicio y espera a que conteste. Al arrancar, el servicio revisa la base restaurada
   (y que el respaldo no sea de una versión más nueva del programa).
- **Sale bien si** termina con «Se restauro el respaldo …». Las cajas se ponen al día solas, como al restaurar desde la
  pantalla.
- **Si dice que el respaldo tampoco pasó la revisión,** correrlo otra vez y elegir el de antes. **Si dice «La base de antes
  regreso a su lugar»,** no pudo apartarla o poner el respaldo (un archivo ocupado, el disco lleno): todo quedó como estaba y el
  servicio también; correrlo otra vez. **Si dice que no pudo** y que la base de antes está en su carpeta, lo que pasó está en
  `restaurar.log`, junto a ella; para regresarla a mano, moverla (no copiarla).
- **Si los respaldos están en otra carpeta** (una USB): `restaurar.ps1 -Carpeta E:\respaldos`.

## Primer fin de semana: conocer la tienda

Solo mirar y anotar. Lo único que se cambia es la configuración de los grabadores, por seguridad. Si el dueño lo
aprueba, también se deja el script de diagnóstico de red en las dos cajas (solo lee, arranca solo al iniciar sesión con
un acceso directo y no cambia la configuración de Windows) y se activa el registro Dhcp-Client de Windows, para que la
próxima caída quede registrada. La falla de red no se provoca a propósito ([por qué](notas.md#red-de-la-tienda)).

**Al llegar, antes de tocar nada:** respaldar la base de eleventa con su propia función de respaldo (nunca copiando el
archivo con eleventa abierto) y la configuración de Control Print, correr en cada PC el script que junta su
configuración y exportar el registro del Tenda (un reinicio lo borra).

1. **Red** (30 min):
   - fotos del cableado y de las etiquetas del módem y del Tenda (modelo, versión de hardware y firmware), en qué
     puerto del Tenda entra el cable de red que viene del módem, y si el servicio es de cable (coaxial) o de fibra;
   - el módem: si está en modo router o puente, y si su página de configuración abre desde la caja principal (y en
     qué dirección);
   - qué está conectado a dónde: el switch que ya hay (marca, modelo y puertos libres), qué equipos van al switch y
     cuáles directo al Tenda, y si hay algo en el módem o en su Wi-Fi;
   - en el Tenda: su dirección de internet (si es privada, el módem está en modo router), su red local, el rango del
     DHCP y sus reservas, cuánto dura cada concesión (el tiempo que le presta una dirección a cada equipo), si está en
     modo router o AP, la hora del reinicio programado y si el ahorro del Wi-Fi está activo; fotos de todas sus
     páginas de configuración y, si lo permite, un respaldo de su configuración en un archivo, fuera del repositorio;
   - si los clientes u otras personas se conectan al Wi-Fi del Tenda (la caja 2 va por ahí:
     [seguridad](notas.md#seguridad));
   - en cada PC, guardado en un archivo: `ipconfig /all`, las impresoras, sus puertos y sus drivers (`Get-Printer`,
     `Get-PrinterPort`, `Get-PrinterDriver`) y el perfil de red;
   - la hoja de estado de red de cada impresora, si cada una tiene IP fija, y cómo se le cambia la IP a la impresora
     de tickets y cuál es la suya de fábrica;
   - si hay un cable que pueda llegar hasta la caja 2, y si la caja 2 tiene Bluetooth
     ([para qué](arquitectura.md#la-red-de-la-matriz));
   - de cada caja, los registros del Visor de eventos para buscar las caídas pasadas
     ([cuáles](notas.md#red-de-la-tienda)), y si el registro Dhcp-Client está activado.
2. **Impresoras** (1 a 2 h):
   - lectura SNMP de las dos WF-6590 y de la L5590 (serie, contador y niveles), la de la L5590 por primera vez;
   - dos tickets al mismo tiempo desde las dos cajas a la impresora de tickets, para ver si espera o rechaza; con la
     caja nueva, que el segundo diga «espera a la impresora» y salga solo después, una sola vez; si alguno dice «no
     salió» pero sí salió (no confirmó), «No imprimirlo» lo deja y sigue lo demás, sin que salga dos veces;
   - qué colas tiene cada PC y en qué PC corre Control Print;
   - para qué se usa cada impresora y cuánto se cobra en cada una, con el personal y en Control Print.
3. **Grabadores** (30 min): configurarlos por seguridad ([lista](camaras.md#configurar-el-grabador)), y anotar qué
   cámara ve cada caja y la puerta.
4. **Las PC de caja** (30 min, con un solo reinicio de cada una, en un rato sin venta):
   - que el procesador sea x86-64-v2, desde la USB de Ubuntu ([por qué](notas.md#hardware-de-la-tienda));
   - si en el BIOS encienden solas al volver la luz;
   - el horario de Windows Update;
   - el desfase del reloj de cada caja y del grabador, y de dónde toman la hora ([NTP](notas.md#código-y-pruebas)).
5. **Equipo y luz:** la lista del equipo con marca y modelo (no hay fecha de compra; propuesta, por aprobar: el dueño
   estima lo que vale hoy y los años que le quedan), una foto del recibo de CFE, y la potencia que dice la etiqueta de
   la enmicadora y cuántas horas pasa encendida al día (lo dice el personal).
6. **Para la próxima falla de red:** el script de diagnóstico corriendo en las dos cajas (si el dueño lo aprueba) y
   su ícono «Diagnóstico de red» en el escritorio de la caja 2; el cable de red que va del módem al puerto de internet
   del Tenda, marcado con cinta de color en las dos puntas; y la instrucción al personal
   ([abajo](#durante-una-falla-de-red)). Donde ya esté el POS, su bitácora de red se anota sola
   ([cómo se lee](#después-de-una-caída-la-bitácora-de-red)).

## Segundo fin de semana: arreglar la red

Solo con el visto bueno del dueño. Con lo que se anotó en el primero, aplicar el
[arreglo propuesto](arquitectura.md#la-red-de-la-matriz). Antes de empezar se fija una hora límite el domingo: si a esa
hora algo no funciona, se regresa todo.

- **Preparado entre semana:** la lista de todos los equipos con su dirección de hoy y la fija que tendrá (dentro de la
  misma red local, fuera del rango que reparte el Tenda), y dónde tienen configurada la red eleventa (la caja
  principal como servidor y las impresoras) y Control Print (su impresora): se cambia ahí también.
- **Al llegar, antes de tocar nada:** cerrar eleventa en las dos cajas, sin venta abierta; respaldar su base con su
  propia función de respaldo (o con el gbak de su Firebird) y revisar el tamaño y la fecha del respaldo; respaldar
  Control Print y las colas de impresión de cada PC (con `printbrm` si está; si no, el archivo del primer fin de
  semana y capturas); correr el script de configuración; tener a la mano las fotos y el respaldo de la configuración
  del Tenda, y la clave del Wi-Fi.
- **Hacer:**
  - el módem solo al puerto de internet del Tenda y nada más conectado a él;
  - todos los equipos de la tienda en el switch;
  - las IP fijas, dentro de la red local de hoy;
  - en cada cola de impresión, cambiar solo su puerto a «TCP/IP estándar» con la IP de la impresora, sin cambiarle el
    nombre; imprimir una página de prueba y, solo entonces, apagar el WSD en la impresora. Nunca borrar una cola:
    eleventa y Control Print la buscan por su nombre;
  - en el firewall de Windows de cada caja, permitir el ping y los puertos 3050 y 3309 (los que pide
    [eleventa](https://eleventa.com/aprender/conexion-multicaja) para que se comuniquen sus cajas) en todos los perfiles
    de red, solo desde la red local;
  - en el Tenda: UPnP e IPv6 apagados, concesiones largas y el reinicio diario de madrugada (anotar su hora: borra el
    registro). El firmware, al final y solo si sus notas para esa versión de hardware traen un arreglo de seguridad o
    de estabilidad, después del respaldo de su configuración;
  - la caja 2 por cable con IP fija, si hay un cable que llegue;
  - en las cajas, si hacía falta: encender solas al volver la luz y Windows Update fuera del horario de la tienda.

  Al terminar, abrir eleventa en las dos cajas y revisar que se conecten.
- **Probar:** vender en eleventa en las dos cajas, imprimir en las cuatro impresoras, revisar que Control Print cuente
  y ver el grabador. Después, con la tienda cerrada y eleventa cerrado, apagar el Tenda unos 20 minutos y, mientras
  tanto, reiniciar una caja y apagar y prender una Epson. Con eleventa abierto otra vez, la caja principal debe vender
  e imprimir, y la caja 2 también si va por cable: con eleventa, la caja 2 por Wi-Fi no puede vender mientras el router
  esté apagado (la contingencia es del POS nuevo). Al volver a prender el Tenda, ver las cámaras en DMSS desde un
  celular con datos, porque se apagó el UPnP. Esto comprueba el arreglo, no la causa: la prueba de verdad es la
  próxima caída real, con el script corriendo.
- **Con el POS en la caja 2 (el piloto):** con el puerto de la cola de la impresora de tickets ya en TCP/IP, el chip de
  arriba dice solo «Tickets» (antes, «Tickets · configúrala por IP»); mientras el Tenda está apagado, la franja de la
  caja 2 dice «Sin red local» o «Sin internet» según lo que vea, y su bitácora de red anota cuándo se perdió y cuándo
  volvió el router ([cómo se lee](#después-de-una-caída-la-bitácora-de-red)).
- **Si algo sale mal o se llega a la hora límite, regresar todo:** cada equipo a su dirección anotada (o a DHCP), la
  caja 2 de vuelta a su Wi-Fi (con la clave a la mano), cada cola de Windows a su puerto de antes, el WSD de las
  impresoras otra vez prendido, eleventa y Control Print a sus direcciones de antes, el Tenda a su configuración
  anotada, y los equipos a los puertos donde estaban. Después, revisar que las dos cajas vendan en eleventa, que las
  cuatro impresoras impriman y que Control Print cuente. Si el Tenda queda inservible, se vuelve a como estaba antes de
  él: el módem como router y Wi-Fi.

## Después, al dejar eleventa: las dos cajas con el POS

- **Al llegar, antes de tocar nada:** respaldar eleventa con su propia función y Control Print, y correr el script de
  configuración.
- **Las dos cajas reales,** sin tocar eleventa (el POS usa su propio puerto y su propia carpeta): instalar el servicio
  en la caja principal y la caja en las dos, dar de alta la caja 2, vender, imprimir el ticket (el cajón, cuando lo
  haya), probar la contingencia (quitar el cable de la caja principal, un cable flojo y la red lenta), volver y ver
  que todo sube, revisar los respaldos y usarlas desde una cuenta común de Windows.
- **Respaldo y vigilancia:** el respaldo cifrado en Backblaze B2, con una restauración de ensayo, la vigilancia de la
  caja principal en Healthchecks.io y NetBird para el soporte a distancia
  ([notas](notas.md#respaldo-fuera-de-la-tienda-y-aviso-de-apertura)).
- **Si se reinstala o se cambia la PC de la caja principal** (también al restaurar una copia en la de repuesto): su
  llave propia (`pos.llave`, junto a la base) no va en los respaldos, así que la nueva tiene otra. Cada caja deja de
  mandarle su llave, sigue vendiendo con su copia y su franja dice «La caja principal no probó ser la misma de
  siempre» con «Dar de alta otra vez» (sin nadie en la venta): el dueño la da de alta con su PIN, eligiendo la misma
  caja. Si se oprime por error, «Volver sin dar de alta» regresa.
- **El bot de Telegram de verdad.**
- **La impresión** con la cola de laboratorio y después con la real, sin quitar Control Print hasta el piloto.
- **Un día pico** (~120 tickets) con el simulador de cajas, contra la caja principal real
  ([cómo](#el-día-pico-en-la-caja-principal-real); se puede antes, con la base de prueba).

## El internet de respaldo con un celular

Cualquier fin de semana, unos 20 minutos, con la tienda tranquila y el visto bueno del dueño
([el análisis](arquitectura.md#la-red-de-la-matriz)). No se provoca la falla: desconectar el cable no la reproduce, solo
deja a la tienda sin internet, como en una caída.

1. Desconectar el cable marcado (el de red que va del módem al Tenda).
2. En el celular, compartir sus datos por cable USB y por su Wi-Fi a la vez; anotar si lo deja y si el plan lo permite.
3. El cable USB a la caja principal y el Wi-Fi del celular a la caja 2.
4. En cada caja: abrir una página de internet e imprimir una página de prueba en una Epson y un ticket de prueba. En
   la caja 2, ya en el Wi-Fi del celular: cobrar algo (sale con la copia de la caja), volver al Wi-Fi de la tienda y
   ver que lo cobrado suba solo y que el ticket salga con «Reintentar».
5. Si la caja 2 tiene Bluetooth: en el celular, compartir también por Bluetooth; en la caja 2, emparejarlo y, en
   Dispositivos e impresoras, clic derecho en el celular › «Conectar mediante» › «Punto de acceso», sin dejar el Wi-Fi
   de la tienda. Abrir una página, bajar un archivo de unos MB (anotar cuánto tarda) y ver que el POS siga conectado
   con la caja principal.
6. Si una caja no abre la página con el celular conectado: la métrica (en el análisis), que es un ajuste de Windows:
   solo con el visto bueno del dueño, y anotar cómo estaba.
7. Volver a conectar el cable marcado, desconectar el celular y ver que todo quede como antes.

Con lo que salga, se agrega a la instrucción de [abajo](#durante-una-falla-de-red) cómo conectar el celular.

## Durante una falla de red

Cualquier día. **Instrucción al personal:**

> Si solo se va el internet, la venta sigue: no desconectes nada. Si además la caja 2 no se conecta con la caja
> principal o no sale el ticket, primero junta esto, en no más de 3 minutos y sin apagar ni reiniciar el módem, el
> Tenda, el switch ni las computadoras:
>
> 1. Anota la hora y graba un video de 60 segundos de las luces del módem, del Tenda y del switch.
> 2. En un celular, abre la lista de redes Wi-Fi, toca la de la tienda y toma captura de lo que salga (si la red ni
>    aparece, toma captura de la lista).
> 3. En la caja 2, abre «Diagnóstico de red» y presiona «Se cayó la red».
>
> Después desconecta el cable marcado con cinta (el de red que va del módem al Tenda), presiona «Ya desconecté» y
> avísale al dueño. Nunca toques el cable redondo de Megacable. Cada hora, cuando la caja 2 no tenga una venta abierta,
> vuelve a conectar el cable marcado y presiona «Reconecté». Si en 2 minutos la caja 2 sigue conectada con la caja
> principal y un celular abre una página con el Wi-Fi de la tienda, ya regresó el internet; si no, desconéctalo otra
> vez. Al cerrar la tienda,
> déjalo conectado y anota la hora.

Lo que se junta después lo hace el dueño ([notas](notas.md#red-de-la-tienda)), y con eso la bitácora de red del POS
([abajo](#después-de-una-caída-la-bitácora-de-red)).

## Después de una caída: la bitácora de red

Donde está instalado el POS, la bitácora de red se anota sola, sin que nadie haga nada
([cómo funciona](arquitectura.md#la-red-de-la-matriz)). Para un vistazo, Configuración › Acerca de › Red enseña las
últimas caídas con desde, hasta y cuánto duraron (las de esa caja y las de la caja principal).

- **Dónde está** (se guardan 10 días: copiarla a la USB junto con lo demás antes de que pasen):
  - **cada caja:** `red-caja-AAAA-MM-DD.log`, en la carpeta del registro de la caja
    (`%LocalAppData%\PapeleriaPos\registros` de la cuenta de Windows con que se usa; en la caja de prueba,
    `C:\ProgramData\PapeleriaPos-Prueba\registros`);
  - **el servicio:** `red-servicio-AAAA-MM-DD.log`, en `C:\ProgramData\PapeleriaPos\registros` de la PC donde corre (la
    caja principal; durante el piloto corre en la caja 2, y la caja principal de eleventa no tiene POS: ahí sigue
    haciendo falta el script de diagnóstico).
- **Cómo se lee:** se abre con el Bloc de notas; cada línea empieza con la fecha y la hora de esa PC, y solo hay líneas
  cuando algo cambia (más un resumen cada 15 minutos):
  - «Empieza la bitácora de red … (la PC arrancó a las …)» es cuando se abrió la caja o arrancó el servicio; «Termina
    …», cuando se cerró bien. Un hueco sin líneas antes de un «Empieza» sin su «Termina» dice que la PC se apagó de
    golpe o se fue la luz;
  - «Se perdió «internet»» y «Volvió «internet» tras 2 h 5 min (se perdió a las 14:03:12)»: lo mismo para «la caja
    principal», «el router», «la impresora de tickets» y los equipos de `Red:Equipos` (en el piloto, también «la PC de
    eleventa», solo con ping). Se da por perdido con dos revisiones seguidas sin respuesta (unos 30 a 60 s después de
    que pasa). Un equipo que nunca contestó el ping (su firewall no lo deja) no cuenta como caído: lo dice una vez.
    Estas líneas, y las de «Empieza» y «Termina», se anotan siempre, aunque el archivo del día llegue a su tope;
  - «Se perdió «la caja principal»» dice también cómo estaba la red antes y lo que tardaron los últimos latidos. Que la
    red con la caja principal se ponga lenta y vuelva a estar buena (pasa seguido en un Wi-Fi regular) no lleva línea:
    el resumen dice cuántas veces;
  - «Esta PC: …», al empezar, y «Esta PC cambió de IP / de conexión / de Wi-Fi / de router»: cómo está conectada (cable
    o Wi-Fi; con el detalle del Wi-Fi prendido, también el nombre de su perfil de red y su señal: abajo), su IP y si se
    la dio el router (DHCP) o es fija, y el router con su MAC. Una IP 169.254 dice que el router no le dio dirección;
    «El router … cambió de MAC», que otro aparato contesta con la IP del router;
  - «El DNS del router no contesta»: el router está trabado (no contesta ni su propio nombre, `tendawifi.com`). Se
    anota la primera vez en cada 15 minutos; el resumen dice cuántas veces no contestó;
  - «Llega mucha difusión a esta PC: … paquetes por segundo»: posible tormenta de tráfico;
  - ««Caja 2» dejó de latir» / «volvió a latir» (en la del servicio): cuándo la caja principal dejó de saber de cada
    caja. También la primera vez en cada 15 minutos; si se repite, el resumen dice cuántas veces más;
  - «Se busca la caja principal en la red», «Contestó …, pero no probó …» y «La caja principal cambió de dirección»: la
    caja buscándola por si le cambió la IP;
  - «Esta caja recuerda la llave de su caja principal (…)»: una vez, al estrenar esta versión en una caja que ya estaba
    dada de alta (las que se dan de alta después ya la traen). Desde ahí solo se cambia a una caja principal que firme
    con esa llave;
  - «… no firma con la llave de su caja principal …» o «Contestaron …, y prueban ser cajas principales distintas …» (la
    franja dice «La caja principal no probó ser la misma de siempre» o «Hay dos cajas principales en la red: avisa al
    dueño»): en la red hay otra PC con el servicio y una copia de la base (la caja principal de antes, un respaldo
    abierto en una laptop), o se reinstaló o se cambió la PC de la caja principal. La caja no se cambia sola y sigue
    vendiendo con su copia: hay que apagar el servicio de la PC que sobra o, si se cambió la caja principal, dar de alta
    la caja otra vez ([abajo](#después-al-dejar-eleventa-las-dos-cajas-con-el-pos));
  - «Resumen de 15 min»: cuánto tardaron los latidos y el router (lo menos, lo más y la mitad), si hubo internet, la
    señal del Wi-Fi (con el detalle prendido), cuánta difusión y cuántos descartados llegaron, y lo que se repitió: «la
    red con la caja principal se puso lenta 12 veces», «el DNS del router no contestó 10 de 31 veces», ««Caja 2» dejó
    de latir 4 veces más».
- **El detalle del Wi-Fi** (el nombre del perfil de red y la señal) viene apagado: en Windows 11 24H2, leerlo podría
  sacar la pregunta del permiso de ubicación encima de la venta. Para diagnosticar las caídas se prende en la caja 2, un
  sábado o domingo fuera del horario de venta: cerrar la caja y abrir con el Bloc de notas
  `C:\ProgramData\PapeleriaPos\Caja\caja.json`. Si tiene el renglón `"Red": null`, cambiar `null` por
  `{ "DetalleWifi": true }`; si no lo tiene, agregar justo después de la primera llave, `{`, el renglón
  `"Red": { "DetalleWifi": true },` (con la coma). Guardar y abrir la caja: si no abre, el renglón quedó mal (revisar
  las comillas y la coma, o quitarlo). Esperar un par de minutos: si sale cualquier pregunta de Windows (la de la
  ubicación), contestar que no y apagarlo. Si no sale nada, la línea «Esta PC: …» de la bitácora de ese día ya trae el
  Wi-Fi y su señal. Para apagarlo, cambiar `true` por `false`.
- **Qué mirar después de la caída**, con las horas de la instrucción al personal (el video, «Se cayó la red», «Ya
  desconecté», «Reconecté») y la hora de Megacable:
  1. en la del servicio, si «el router» se perdió junto con «internet» o después (las causas 1 y 2 de
     [notas](notas.md#red-de-la-tienda)), y si volvió en cuanto se desconectó el cable del módem;
  2. en la de la caja 2, si se perdió «la caja principal» y «el router» con la señal del Wi-Fi buena (el Tenda dejó de
     dar Wi-Fi) o con la señal mala (con el detalle del Wi-Fi prendido), si la caja se quedó con una IP 169.254 (sin
     DHCP) y si el DNS del router se trabó;
  3. si las dos cajas lo anotaron a la misma hora (««Caja 2» dejó de latir» en la del servicio): con los equipos en el
     switch e IP fija, la caja principal no debería perder a la impresora de tickets (si la pierde, el arreglo no
     basta);
  4. si hubo mucha difusión o descartados en el resumen (la causa 3: una tormenta de tráfico);
  5. si cambió la IP o la MAC del router en medio, o algo seguía configurado por nombre o con WSD (lo dice al empezar).
