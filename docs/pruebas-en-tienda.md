# Pruebas en la tienda

> Las pruebas reales en la matriz solo se pueden hacer **sábado y domingo** ([forma de
> trabajar](notas.md#forma-de-trabajar)). Aquí se junta lo que necesita la tienda, en orden, para prepararlo entre
> semana y no perder tiempo ahí. Sin tocar los datos de eleventa; su configuración de red solo se cambia en el segundo
> fin de semana, con respaldo y la forma de regresar.

## Entre semana, antes de cada fin de semana

- **La lista del fin de semana,** en orden de importancia, con el tiempo de cada cosa y quién ayuda.
- **Lo que se lleva listo:** el instalador probado en una PC limpia, los scripts (el de diagnóstico de red y el que
  junta la configuración de cada PC), cables de red, cinta de color y una memoria USB con todo.
- **Una herramienta de prueba,** probada antes en la PC de desarrollo: lee por SNMP las tres impresoras de inyección,
  manda dos tickets a la vez al puerto 9100 de la impresora de tickets y manda el pulso del segundo cajón (`ESC p 1`).
- **Lo que se compra con días de anticipación:** el medidor de enchufe para el primer fin de semana (por cotizar); un
  cajón de prueba o un cable en Y, si se quiere probar el segundo cajón; el switch TL-SG108 (433 + 138 de envío en
  Cyberpuerta) y los cables para el segundo.

## Primer fin de semana: conocer la tienda

Solo mirar y anotar; no se cambia nada, salvo los grabadores, por seguridad.

**Al llegar, antes de tocar nada:** respaldar la base de eleventa y la configuración de Control Print, y correr en cada
PC el script que junta su configuración.

1. **Red** (30 min):
   - fotos del cableado y de las etiquetas del módem y del Tenda (modelo y firmware), y en qué puerto del Tenda entra el
     cable de Megacable;
   - en el Tenda: su dirección de internet, su red local, el rango del DHCP y las reservas, si está en modo router o AP,
     y la hora del reinicio programado;
   - en cada PC, guardado en un archivo: `ipconfig /all`, las impresoras y sus puertos (`Get-Printer`,
     `Get-PrinterPort`) y el perfil de red;
   - la hoja de estado de red de cada impresora;
   - si algo está conectado directo al módem o a su Wi-Fi.
2. **Impresoras** (1 a 2 h):
   - lectura SNMP de las dos WF-6590 y de la L5590 (serie, contador y niveles), la de la L5590 por primera vez;
   - dos tickets al mismo tiempo desde las dos cajas a la impresora de tickets, para ver si espera o rechaza;
   - si hoy hay cajón (y cuántos) y si la impresora de tickets abre un segundo cajón;
   - qué colas tiene cada PC y en qué PC corre Control Print.
3. **Grabadores** (30 min): configurarlos por seguridad ([lista](camaras.md#configurar-el-grabador)), y anotar qué
   cámara ve cada caja y la puerta.
4. **Las PC de caja** (30 min, con un solo reinicio de cada una, en un rato sin venta):
   - que el procesador sea x86-64-v2, desde la USB de Ubuntu ([por qué](notas.md#hardware-de-la-tienda));
   - si en el BIOS encienden solas al volver la luz;
   - el horario de Windows Update;
   - el desfase del reloj de cada caja y del grabador, y de dónde toman la hora ([NTP](notas.md#código-y-pruebas)).
5. **Equipo y luz:** la lista del equipo con precio y fecha de compra, una foto del recibo de CFE y un medidor de
   enchufe en la enmicadora, que se queda puesto toda la semana.
6. **Para la próxima falla de red:** el script de diagnóstico en el escritorio de la caja 2, el cable de Megacable y el
   puerto de internet del Tenda marcados con cinta de color, y la instrucción al personal
   ([abajo](#durante-una-falla-de-red)).

## Segundo fin de semana: arreglar la red

Con lo que se anotó en el primero, aplicar el [arreglo propuesto](arquitectura.md#la-red-de-la-matriz).

- **Preparado entre semana:** la lista de todos los equipos con su dirección vieja y la nueva, y dónde tienen
  configurada la red eleventa (la caja principal como servidor y las impresoras) y Control Print (su impresora): se
  cambia ahí también.
- **Al llegar, antes de tocar nada:** respaldar eleventa y Control Print y correr el script de configuración.
- **Hacer:** el módem solo al puerto de internet del Tenda, el firmware y el reinicio programado del Tenda, la red local
  nueva, las IP fijas, el switch, y en cada PC las impresoras por IP sin WSD. En las cajas, si hacía falta: encender
  solas al volver la luz y Windows Update fuera del horario de la tienda.
- **Probar:** vender en eleventa en las dos cajas, imprimir en las cuatro impresoras y ver el grabador. Después, quitar
  la señal al módem (el cable coaxial) dejando conectado el cable del módem al Tenda, unos 10 minutos: las cajas y las
  impresoras deben seguir. Luego repetir desconectando el cable del Tenda.
- **Si algo sale mal, regresar todo:** cada equipo a su dirección anotada (o a DHCP), las colas de Windows a sus puertos
  de antes, eleventa y Control Print a sus direcciones de antes, el Tenda a su red anotada, y quitar el switch.

## Después: las dos cajas con el POS

- **Al llegar, antes de tocar nada:** respaldar eleventa y Control Print y correr el script de configuración.
- **Las dos cajas reales,** sin tocar eleventa (el POS usa su propio puerto y su propia carpeta): instalar el servicio
  en la caja principal y la caja en las dos, dar de alta la caja 2, vender, imprimir el ticket y abrir el cajón, probar
  la contingencia (quitar el cable de la caja principal, un cable flojo y la red lenta), volver y ver que todo sube,
  revisar los respaldos y usarlas desde una cuenta común de Windows.
- **Respaldo y vigilancia:** el respaldo cifrado en Backblaze B2, con una restauración de ensayo, la vigilancia de la
  caja principal en Healthchecks.io y NetBird para el soporte a distancia
  ([notas](notas.md#respaldo-fuera-de-la-tienda-y-aviso-de-apertura)).
- **El bot de Telegram de verdad.**
- **La impresión** con la cola de laboratorio y después con la real, sin quitar Control Print hasta el piloto.
- **Un día pico** (~120 tickets) con el simulador de cajas, contra la caja principal real.

## Durante una falla de red

Cualquier día. **Instrucción al personal:** «Si solo se va el internet, la venta sigue. Si la caja 2 no ve a la caja
principal o no sale el ticket, abre el diagnóstico del escritorio y sigue lo que dice: a la mitad te pide desconectar
el cable marcado, espera un minuto y vuelve a revisar. Reconéctalo cuando el módem muestre la luz de en línea». Si se
puede, una foto de las luces del módem y de la pantalla de estado del Tenda. El diagnóstico guarda solo lo que hace
falta para encontrar la causa ([notas](notas.md#red-de-la-tienda)).
