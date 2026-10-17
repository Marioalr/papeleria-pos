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
  están (2026-10-16): `probar-impresora-tickets` y `probar-lector`. Falta la que lee por SNMP las tres impresoras de
  inyección, con el módulo de impresión.
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
   - en la caja principal, con `netstat -ano`, qué programa escucha en los puertos 3050 y 3309;
   - la hora de cada caja contra la del celular;
   - si OneDrive sincroniza Documentos en la caja 2 (las notas de `observar-en-vivo` son datos reales);
   - con `observar-en-vivo` en la caja 2: entradas y salidas de dinero, un cierre con diferencia y, si se da, algo
     después de medianoche;
   - lo que la [paridad con eleventa](paridad-eleventa.md#qué-mirar-en-la-tienda) marca «por confirmar»: si los cajeros
     usan Ins para la cantidad y si el ticket sale siempre, cómo se hace y se imprime el corte, si se consultan ventas o
     cortes de otros días y qué se mira del inventario.
4. **Lo que siga pendiente de conocer la tienda** ([abajo](#primer-fin-de-semana-conocer-la-tienda)), si da tiempo y
   el dueño lo aprueba.

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
   - dos tickets al mismo tiempo desde las dos cajas a la impresora de tickets, para ver si espera o rechaza;
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
   ([abajo](#durante-una-falla-de-red)).

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
- **El bot de Telegram de verdad.**
- **La impresión** con la cola de laboratorio y después con la real, sin quitar Control Print hasta el piloto.
- **Un día pico** (~120 tickets) con el simulador de cajas, contra la caja principal real.

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

Lo que se junta después lo hace el dueño ([notas](notas.md#red-de-la-tienda)).
