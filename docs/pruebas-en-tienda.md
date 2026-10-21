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
   - si hay un cable que pueda llegar hasta la caja 2;
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
