# Arquitectura

> Punto de partida en lluvia de ideas: puede cambiar mientras se cierra el plan.

## Componentes

```
              ┌───────────────────────────────────────────┐
              │ Central: servidor virtual                 │
              │ servicio POS en modo central · SQLite     │
              │ sucesos y avisos · panel · un solo bot    │
              │ webhook de WhatsApp                       │
              └─────────▲────────────────────▲────────────┘
                        │ HTTPS, siempre lo inicia la sucursal
          ┌─────────────┴─────────┐   ┌──────┴────────────────┐
          │ Matriz                │   │ Sucursal 2            │
          │ caja principal:       │   │ caja principal:       │
          │ servicio POS + SQLite │   │ servicio POS + SQLite │
          │ puente: cámaras,      │   │ puente: cámaras,      │
          │ reloj y contador      │   │ reloj y contador      │
          └──────────┬────────────┘   └──────────┬────────────┘
                     │ LAN (router Tenda)        │ LAN
   Cajas · celulares · impresoras ·    Cajas · celulares · impresoras ·
   grabador Dahua y cámaras            grabador Dahua y cámaras

  Dueño: Telegram, alerta que suena aunque el celular esté en silencio y llamada; panel con llave de acceso;
  cámaras por red privada
  Fuera: Healthchecks.io vigila al central y a las tiendas; Backblaze B2 guarda los respaldos
```

> Es el destino con varias sucursales, **pospuesto** el 2026-10-08: hoy el foco es la matriz con caja principal y
> secundaria, sin central ni puente ([central](servidor-remoto.md), [cámaras](camaras.md), [notas](notas.md)).

| Componente | Tecnología | Dónde corre |
|---|---|---|
| Servicio POS | ASP.NET Core como servicio de Windows | Caja principal de cada sucursal |
| Central | El mismo servicio en modo central (pospuesto) | Servidor virtual ([por qué](servidor-remoto.md)) |
| Caja | Ventana de Windows (WPF) que muestra la interfaz compartida | Todas las cajas |
| App Android | Kotlin nativo, con la API del servicio (2026-10-10) | Celulares de la sucursal, por Wi-Fi |
| Panel del dueño | La interfaz compartida, servida por el central (pospuesto) | Navegador, en internet con llave de acceso (passkey) |
| Puente | Servicio .NET en Linux, siempre prendido: cámaras, reloj de la tienda y contador (pospuesto, [cámaras](camaras.md)) | Raspberry Pi en cada tienda |
| Receptor de WhatsApp | Un Cloudflare Worker o un proveedor (Kapso) que guarda los mensajes ([WhatsApp](whatsapp.md)) | Fuera de la tienda, mientras no haya central |
| Módulo de impresión | Agente en cada PC que imprime ([detalle](impresion.md)); su código va aparte, en `src/Impresiones` ([README](../src/Impresiones/README.md)) | PCs con impresora |
| Núcleo | Biblioteca .NET sin dependencias de Windows | Compartido por todo; sus pruebas corren en cualquier máquina |

- **La interfaz de la caja.** Componentes Blazor (HTML/CSS + C#) en una biblioteca: la caja los muestra en una
  ventana de Windows (con WebView2, que ya trae Windows 11) y el panel del dueño (pospuesto) los mostraría en el
  navegador. La app Android es nativa en Kotlin y usa la misma API del servicio (2026-10-10), así que la interfaz se
  diseña solo para la caja: el [rediseño](propuesta-interfaz.md) se aprobó el 2026-10-10. En la caja el código
  corre en la misma PC, sin WebAssembly.
- **Cada sucursal funciona sola.** Internet se usa para Telegram, el respaldo en Backblaze B2, Healthchecks.io y, con
  la API de WhatsApp, pedir los mensajes a su receptor; con el central (pospuesto), también para hablar con él. Si se
  cae, se vende igual.
- **Servicio de Windows, no app de bandeja:** corre aunque nadie haya iniciado sesión y Windows lo reinicia si falla.
- **Caja principal:** guarda la base; en la matriz no hay no-break (2026-10-09), así que si se va la luz se apaga de
  golpe ([notas](notas.md#hardware-de-la-tienda)). Windows Update con horas activas configuradas para que no reinicie en
  horario de tienda.

## Datos

- SQLite en modo WAL. En cada lugar, el servicio es el único que escribe; las cajas pasan por la API (SQLite en una
  carpeta compartida no es confiable con varios escritores).
- **Kardex:** cada venta, compra, ajuste, merma, traspaso o consumo de un servicio es un movimiento nuevo. La
  existencia es la suma. No hay que "pisar" cantidades, así que sincronizar no genera conflictos.
- **Crédito:** la cuenta de cada cliente es un libro de movimientos que solo se agregan, cada uno con el ID de la venta,
  el abono o la devolución que lo originó; el saldo es la suma, así que lo que llega dos veces o tarde no descuadra.
- **IDs UUIDv7** generados donde nace el registro: no chocan entre cajas ni sucursales.
- **Dinero en centavos enteros** (sin errores de redondeo). Fechas en UTC; se muestran en hora de México.
- **Folios por caja:** serie + consecutivo (A-000123), legibles para el ticket.
- Todo registro lleva su **sucursal** desde el primer día, aunque al principio solo exista una.

## Contingencia: caja sin servidor

- Cada caja guarda una copia local del catálogo y los precios, y una **bandeja de salida**.
- Si la caja principal no responde, la caja entra en modo contingencia: vende con sus propios folios, imprime ticket y
  guarda todo para enviarlo después.
- Al volver el servidor, se sube la bandeja; el inventario no choca porque son movimientos.
- Limitaciones en contingencia: no se ven los pedidos de otras cajas. El crédito sigue: se fía hasta el saldo que conoce
  la caja (los últimos saldos del servicio más lo que ella hizo y él todavía no le confirma) y se reciben abonos; al
  volver, el servicio acepta todo y lo que no cuadra queda por revisar, con aviso al dueño.

## Varias sucursales

Pospuesto (2026-10-08). Lo que sigue es la propuesta para cuando se retome. Mientras, con una sola sucursal, soporte
instala las actualizaciones a distancia ([plan](plan.md#para-el-piloto-con-dos-cajas)).

- **Dueño de cada dato:**
  - El central es dueño del catálogo, los precios base y por sucursal y los usuarios. Se editan en el panel (o desde
    cualquier caja con permiso) y bajan a las sucursales. Un cambio hecho sin internet sube como propuesta: si nadie
    tocó ese dato entra; si alguien lo tocó, gana el de mayor rango, nunca la hora del reloj.
  - Cada sucursal es dueña de sus ventas, cortes, movimientos, pedidos y crédito. Suben al central para verlos; el
    crédito no baja a la otra sucursal. Si un cliente registrado vale en las dos está por definir
    ([preguntas](servidor-remoto.md#preguntas)).
  - Cada dato tiene un solo dueño que lo escribe; el central manda **comandos** (bloquear, pedir un conteo), con
    vencimiento y confirmación.
- **Sincronización:** mientras está prendida, la caja principal de cada sucursal se conecta al central: sube cada
  cambio en segundos (una bandeja que se escribe junto con el dato) y recibe catálogo, precios, reglas y comandos por
  número de versión. Cada evento lleva su ID: reenviarlo no
  duplica nada. Ojo: hoy los códigos y presentaciones que se quitan se borran sin rastro (los precios quitados solo
  quedan en su historial) y ningún borrado sube la versión; para que la sucursal se entere hay que mandar el catálogo
  completo o registrar lo que se quita.
- **De noche** las cajas se apagan. Lo que llega mientras tanto (WhatsApp, cambios, comandos) espera en el central y
  se entrega al encender la caja principal. Las cámaras y el contador los atiende el puente de cada tienda.
- **Si el central falla,** las sucursales siguen vendiendo y se ven las cámaras por la red privada; Healthchecks.io
  avisa y el puente manda lo urgente directo. Mientras vuelve se pierden el panel, la sincronización, los avisos por
  Telegram y las acciones a distancia (en la caja queda el PIN). Se reconstruye desde el respaldo
  ([central](servidor-remoto.md#si-algo-falla)).
- **La matriz es una sucursal más.** Cualquier sucursal compra y recibe; un almacén futuro es una sucursal sin cajas.
- **Traspasos:** documento con estados (borrador → enviado → recibido a ciegas → cerrado). Cada lado genera sus
  movimientos y las diferencias se marcan. Una compra repartida queda «de paso» en quien la recibe y sale como
  traspaso al mismo costo.

## Red

- **Router Tenda** en cada sucursal: IP fija reservada para la caja principal. Las cajas la encuentran por búsqueda en
  la red al darlas de alta y, si después cambia la IP, la vuelven a encontrar solas, con su llave
  ([red de la matriz](#la-red-de-la-matriz)).
- **La red local no debe depender del internet** ([red de la matriz](#la-red-de-la-matriz)).
- **Red privada** (NetBird, gratis): soporte a distancia y, con el central, las cámaras en vivo, sin abrir puertos.
  Tailscale gratis es solo para uso no comercial; Tailscale de pago es el plan B ([propuesta](servidor-remoto.md)).
- **Nada de intermediarios en la caja principal** (Caddy, túneles): el servicio deja pasar sin llave lo que llega de
  la misma PC, y con un intermediario todo parecería venir de ahí.
- **Con el central (pospuesto), públicos en él** (HTTPS con dominio propio): la sincronización, la entrada de los
  puentes y dispositivos, el panel, la app de Telegram, la llamada y el webhook de WhatsApp. Todo pide llave, llave de
  acceso o firma. Mientras no haya central, el webhook de WhatsApp va en un receptor aparte ([WhatsApp](whatsapp.md)).
- **Telegram:** el bot vive en la caja principal (conexión de salida). Con el central (pospuesto), un solo bot en el
  central y las sucursales lo dejan de usar.
- **La hora:** con el puente (pospuesto), el puente es el reloj de la tienda; mientras, cada PC toma la hora de
  Windows, que fuera de dominio se corrige solo cada varias horas ([notas](notas.md#código-y-pruebas)).

### La red de la matriz

Todo va por la red local, sin USB: las dos cajas (la caja 2 por Wi-Fi), una impresora de tickets, dos Epson WF-6590, una
EcoTank L5590 y el grabador, con un switch y el Tenda. No hay no-break. El internet es de Megacable, y casi siempre que
se va, también se cae toda la red local: no contesta el ping y los celulares no se conectan al Wi-Fi. Se cae en cuanto
se va el internet y vuelve en cuanto regresa, y solo en una caída real (desconectar el módem no lo reproduce). Ya pasaba
con el módem solo y con otro módem, y siguió con el Tenda; hoy, para que la red vuelva antes, el personal desconecta el
cable de red que va del módem al Tenda. Las
causas posibles y qué se junta en la próxima caída están en [notas](notas.md#red-de-la-tienda).

- **El arreglo propuesto,** por aprobar, con lo que ya hay y sin automatizar nada: que la venta y la impresión no
  dependan del router.
  - IP fija puesta en cada equipo, dentro de la red local de hoy, fuera del rango del DHCP y reservada en el Tenda (por
    ejemplo: .10 caja principal, .11 caja 2, .20 impresora de tickets, .21 y .22 las WF-6590, .23 la L5590, .30 el
    grabador; DHCP de .100 a .199). Cambiar de red local solo después, si choca con la del módem;
  - todos los equipos de la tienda en el switch que ya hay, con un solo cable del switch al Tenda; la caja 2 también,
    si hay un cable que llegue (si no, con eleventa no puede vender mientras el router esté trabado);
  - en Windows, cada cola de impresión con puerto TCP/IP estándar, sin cambiarle el nombre, y sin WSD; en el firewall,
    el ping y los puertos 3050 y 3309, los que pide [eleventa](https://eleventa.com/aprender/conexion-multicaja) para
    que se comuniquen sus cajas, permitidos en todos los perfiles de red, solo desde la red local; en el POS, todo por
    IP;
  - el módem solo al puerto de internet del Tenda, y nada más conectado al módem ni a su Wi-Fi;
  - el Tenda en modo router, con UPnP e IPv6 apagados, concesiones largas y el reinicio programado de madrugada; el
    firmware, solo si trae un arreglo para su versión de hardware.

  Si en la próxima caída los equipos que ya están en el switch no se contestan por IP, el arreglo no basta.
- **Si aun así falla:** lo que se podría comprar queda solo como referencia ([notas](notas.md#red-de-la-tienda)); el
  dueño no quiere lista de compras (2026-10-09).
- **Internet de respaldo con un solo celular** (análisis del 2026-10-21, por probar en la tienda,
  [cómo](pruebas-en-tienda.md#el-internet-de-respaldo-con-un-celular)): hoy se usan dos, uno por el Wi-Fi a la caja 2 y
  otro por cable USB a la caja principal ([notas](notas.md#red-de-la-tienda)). Un celular puede compartir sus datos a la
  vez por cable USB, por su Wi-Fi y por Bluetooth (Android y iPhone lo permiten; probarlo con el que se use y que su plan
  deje compartir); las cajas, y la terminal de tarjetas si va por el Wi-Fi del celular, gastan del mismo plan.
  - **Cómo se conecta:** el cable USB a la caja principal, que no tiene Wi-Fi (2026-10-21) y sigue imprimiendo y
    atendiendo a la caja 2 por su cable de red; el Wi-Fi del celular, a la caja 2.
  - **La caja 2 se cambia de red** cada vez que baja algo de internet. Mientras está en el Wi-Fi del celular, el POS
    vende con su copia: lo cobrado espera en su bandeja y los tickets en la cola («Reintentar» o «No imprimirlo» al
    volver). En el piloto, eleventa no vende en la caja 2 mientras tanto, y lo que cobre el POS llega a eleventa al
    volver, con el folio de la caja. Que Windows solo se conecte solo al Wi-Fi de la tienda: el del celular, a mano.
  - **Para que la caja 2 no se cambie de red,** con lo que ya hay: si tiene Bluetooth, el celular le comparte el
    internet por Bluetooth y ella se queda en el Wi-Fi de la tienda. Es más lento (uno o dos megabits: un PDF de 5 MB
    tarda como medio minuto), pero alcanza para los archivos de los clientes. O, si llega un cable de red a la caja 2,
    el cable para lo de la tienda y el Wi-Fi del celular para el internet.
  - **Lo que hay que ver:** con la conexión de la tienda puesta y sin internet en el Tenda, Windows puede seguir
    mandando lo de internet por ella (al Tenda, sin salida) aunque el celular esté conectado: en la caja principal, y
    en la caja 2 si se queda en la red de la tienda. Si pasa, se le sube la métrica a esa conexión una sola vez
    (propiedades de IPv4 › Opciones avanzadas: quitar «Métrica automática» y poner 100). Lo de internet se va por el
    celular y lo de la tienda sigue por esa conexión, porque es su misma red; sin el celular, todo queda como hoy. Y
    que la red del celular no tenga el mismo rango que la de la tienda (`ipconfig`).
  - **El POS no cambia:** con internet por el celular, la franja «sin internet» se quita sola, y el servicio sigue
    atendiendo a la caja 2 por la red de la tienda.
- **En el POS** (en código desde el 2026-10-17; falta verlo en Windows y en la tienda,
  [avance](plan.md#la-versión-unicaja-y-el-piloto-en-la-caja-2)):
  - **La caja vuelve a encontrar sola a la caja principal** si le cambió la IP: si la dirección guardada no contesta
    30 s, pregunta en la red por UDP (puerto 5081, un paquete a la difusión de cada red, sin recorrerla equipo por
    equipo) y a cada servicio que contesta le pide que pruebe que es su caja principal: un reto al azar que el servicio
    firma (HMAC-SHA256) con la huella de la llave de esa caja, el número de la caja y la IP y el puerto en que recibió
    la pregunta, y además con su propia llave. La llave de la caja no viaja; un reto nuevo cada vez evita que se repita
    una respuesta vieja, y la dirección, que otro equipo le pase la pregunta a la caja principal de verdad y se quede en
    medio. Se revisa cada IP que contesta una vez (solo de la red de la caja, hasta 64, a la vez), así que respuestas
    falsas no dejan fuera a la de verdad; la dirección guardada cuenta como «la misma» solo si prueba quién es. Se
    cambia solo si una sola caja principal lo prueba, y guarda la dirección nueva en `caja.json`; si dos lo prueban, no
    se cambia sola a ninguna y la franja dice «Hay dos cajas principales en la red: avisa al dueño», con «Dar de alta
    otra vez» (abajo). Si no la encuentra, busca otra vez a los 30 s, al minuto, a los 2 y luego cada 5 minutos (con la
    marca de tiempo de la PC: un cambio de hora de Windows no lo mueve); «Volver a buscarla», en la franja, busca al
    momento. Mientras la guardada contesta, nada. El servicio contesta por UDP solo a su red local (su misma red, sin
    túneles como NetBird; el firewall ya solo abre el 5081 a la red local), a lo más 5 veces por segundo a cada equipo y
    200 entre todos, y solo el de la caja principal (`Descubrimiento:Activo`; apagado en la prueba y en desarrollo). En
    Windows, un «puerto inalcanzable» (ICMP) no corta la búsqueda. La prueba (`api/identidad`) va sin llave y solo a la
    red local; los clientes de la caja no usan proxy.
  - **La llave propia de la caja principal:** el servicio tiene una llave ECDSA en su propio archivo junto a la base
    (`pos.llave`), fuera de la base y de sus respaldos (en Windows, cifrada para esa PC con DPAPI de la máquina; en los
    demás sistemas, solo la lee su dueño). La caja recuerda la llave pública al darse de alta o, si es de antes, la
    primera vez que su dirección de siempre (la que puso el dueño) prueba quién es; queda en `caja.json`. Desde ahí, una
    copia de la base en otra PC (la caja principal de antes, un respaldo abierto en una laptop) sabe la huella de la
    llave de la caja pero no firma como su caja principal: nunca gana. Con un servicio o una caja de antes, todo sigue
    como antes. Si se reinstala o se cambia la PC de la caja principal (también al restaurar una copia en la de
    repuesto), su llave es otra: cada caja se da de alta otra vez.
  - **Después de una caída (y al abrir la caja),** la llave de la caja no sale hasta que la dirección guardada pruebe
    otra vez quién es: mientras, lo de la venta va con la copia (al abrir, se espera esa prueba unos milisegundos, a lo
    más 3 s). Si la que sabe de la caja (en la dirección guardada o en otra IP) firma con otra llave (se reinstaló o se
    cambió la PC de la caja principal, o es una copia de su base), no se le manda la llave y la franja ofrece «Dar de
    alta otra vez» (lo hace el dueño, con su PIN). El botón sale solo sin nadie en la venta (con alguien, se perdería el
    ticket que lleva), y desde ahí se puede volver sin dar de alta. Si ya no conoce a la caja (la quitaron), se da de
    alta otra vez, como antes, en cuanto sale quien atiende; mientras, vende con la copia.
  - **Avisos de lo que está por nombre o con WSD:** la impresora de tickets por nombre (`tcp://tickets:9100`, o una cola
    de Windows con su puerto TCP/IP a un nombre), compartida desde otra PC o con un puerto WSD (la caja de Windows lee
    el puerto de la cola en el registro) dice «Tickets · configúrala por IP» en el estado de arriba y en la entrada,
    donde ya se ven los problemas de la impresora; la caja principal por nombre lo dice en Configuración › Acerca de ›
    Red, y la PC de eleventa por nombre, en la bitácora del servicio.
  - **La franja y el chip:** el servicio revisa cada 30 s (15 s sin internet) si hay internet, con una conexión TCP a
    1.1.1.1:443 y 8.8.8.8:443 (2 s; sin DNS ni cuentas; `Red:Internet`), y si contesta su router (ping o su página,
    1 s), y lo dice en su estado (el latido de las cajas). Dos revisiones seguidas sin respuesta: no hay. La caja lo
    junta con lo suyo: «Sin internet: la venta sigue. No desconectes nada.» si llega a la caja principal y no hay
    internet; «Sin red local: esta caja trabaja con su copia» si no llega (con «Volver a buscarla»); en la caja
    principal, sin internet y sin saber que su router contesta (el router trabado, o la PC arrancó sin que el router le
    diera dirección: una 169.254, sin puerta de enlace), «Sin internet ni red local: esta caja sigue vendiendo», sin
    el «no desconectes nada», porque la [guía](pruebas-en-tienda.md#durante-una-falla-de-red) pide juntar los datos y
    desconectar el cable del módem; con todo bien, nada. Arriba, el chip «Sin internet», como en el prototipo. Sin modal
    y fuera del cobro.
  - **La bitácora de red:** cada caja (`red-caja-AAAA-MM-DD.log`, junto a su registro) y el servicio
    (`red-servicio-AAAA-MM-DD.log`, en `C:\ProgramData\PapeleriaPos\registros`) anotan solos lo que cambia de la red,
    con fecha y hora, y un resumen cada 15 minutos; se guardan 10 días (lo viejo se quita también al cambiar el día). Lo
    que va y viene (la red lenta, el DNS del router, una caja que deja de latir) se cuenta en el resumen; lo que se
    pierde y vuelve se anota siempre, aunque el archivo del día llegue a su tope. El Wi-Fi (nombre del perfil y señal)
    solo con `"Red": { "DetalleWifi": true }` en `caja.json`: en Windows 11 24H2 podría pedir el permiso de ubicación.
    Lo que anota y cómo leerla, en [pruebas en la tienda](pruebas-en-tienda.md#después-de-una-caída-la-bitácora-de-red);
    las últimas caídas, con desde, hasta y cuánto duraron, en Configuración › Acerca de › Red. Cubre casi todo lo que el
    script de diagnóstico iba a juntar ([notas](notas.md#red-de-la-tienda)); el script sigue haciendo falta en una PC
    sin el POS (la caja principal durante el piloto) y para lo que el POS no ve.
- **Cambiar la red de la tienda** afecta también a eleventa y a Control Print, que siguen en uso: se hace un sábado o
  domingo, con todo listado y la forma de regresar ([pruebas en la tienda](pruebas-en-tienda.md)).

## Respaldos

- **Local:** copia de la base en cada caja principal al empezar el día (la diaria, `pos-AAAA-MM-DD.db`), otra con cada corte
  Z (`…-z-HHmm.db`), las de «Respaldar ahora» (`…-ahora-HHmm.db`) y la que se hace al arrancar con un respaldo restaurado
  («Al restaurar», `…-restaurado-HHmm.db`), en `C:\ProgramData\PapeleriaPos\respaldos`, 30 días. Con
  la copia en línea de SQLite: la base completa, también lo que sigue en el -wal, sin detener la venta. Si la base no pasó la
  revisión al arrancar (la base contra apagones), no se hacen: saldrían dañadas.
- **Restaurar uno** (2026-10-23; en eleventa, «Restaurar»): Configuración › Respaldos › «Restaurar un respaldo», para quien
  tiene «Configuración de la tienda» y vuelve a escribir su PIN ([pasos](pruebas-en-tienda.md#restaurar-un-respaldo)). La base
  no se cambia con el servicio en marcha (`RestauracionDeRespaldos`):
  1. El servicio revisa el respaldo: que quepa en el disco (la copia y lugar para migrar y seguir escribiendo: la mitad del
     respaldo más, nunca menos de 64 MB), la revisión completa (`PRAGMA integrity_check`: también que cada índice cuadre con su
     tabla; con una base grande tarda unos segundos, y la pantalla dice «Revisando el respaldo…») y que no lo haya hecho una
     versión más nueva del programa (una migración que esta no conoce). Si algo no, lo dice y no pide nada. Si sí, deja el
     pedido junto a la base (`pos.db.restaurar`), lo último que puede fallar, y contesta (la bitácora va después y, si no se
     puede guardar, la anota la base restaurada). Ya salida la respuesta, se detiene con calma (las cajas que esperaban terminan, el puente
     deja de mandar, la base se cierra) y al final sale con el código 1 antes de decirle a Windows que se detuvo: para Windows es
     una falla y lo vuelve a arrancar con las acciones de recuperación que pone `instalar-servicio.ps1` (`sc failure`: a los
     5 s, otra vez a los 5 s y luego al minuto; el contador vuelve a cero cada día). Detenido como siempre, el servicio de
     Windows de .NET le avisaría «detenido» con el código 0 y Windows no haría nada.
  2. Al arrancar, antes de revisar la base y de migrarla: otra vez que quepa, copia el respaldo junto a la base
     (`pos.db.restaurando`) y le hace las mismas revisiones; si algo falla (no cabe, el disco se llena a media copia, no pasa),
     borra la copia, no toca nada y lo dice. Si pasa, mueve la base de ahora con su -wal, su -shm y sus marcas (también la de
     dañada) a una carpeta `antes-de-restaurar-<fecha>` junto a ella (un cambio de nombre en el mismo disco; nunca se borra) y
     la copia pasa a ser la base. La marca de dañada queda quitada solo porque la restaurada pasó.
  3. Migra como siempre y, ya migrada, lo anota en su bitácora (con quien lo pidió) y quita el pedido; si eso falla, el pedido
     se queda y se anota al volver a arrancar (una sola vez: va con el Id del pedido). Después hace el respaldo «Al restaurar»,
     así lo más reciente de la carpeta es de la base restaurada y no de la que se dejó. Todo va al registro.
  - Cada paso queda escrito en el pedido antes del siguiente (con unos intentos, por el antivirus): si se va la luz a la mitad,
    al volver sigue donde se quedó, en la misma carpeta (revisión del PR #136):
    - si se cortó justo después de poner la copia, ya es la base: sigue desde ahí y la revisa al arrancar (con la marca de
      dañada, que la revisión quita si pasa);
    - una copia que pudo quedar a medias se revisa antes de ponerla y, si no pasa, se copia otra vez; lo que ocupe el lugar de
      la base y no sea la de antes se aparta también;
    - si el respaldo ya no se puede poner (ya no está, no pasa, no cabe), la base de antes regresa a su lugar (primero su -wal y
      lo demás, la base al final) y queda dicho como no restaurado;
    - si no puede apartar la base (un archivo ocupado), el servicio no arranca y Windows lo vuelve a intentar: nunca abre una
      base a medias. Si el pedido no se puede leer (el antivirus), tampoco arranca; si no se entiende, se aparta. Y si a media
      restauración falta la base, no arranca con una vacía: el registro dice dónde está la de antes y la copia.
  - **Las cajas:** la base de su copia (`VersionesDeLaCopia.Base`) es otra en cada arranque, así que la caja nota la base
    restaurada y baja su copia completa, en lugar de pedirle cambios desde una versión que ya no quiere decir lo mismo; lo que
    tenía encima de la copia de antes (productos y promociones que dijo el cobro) no se queda. Antes de subir lo que esperaba,
    le dice a la caja principal su turno abierto (con su mismo Id, como uno abierto sin conexión: si se restauró un respaldo de
    antes de abrirlo, se vuelve a dar de alta) y su último folio (la serie sigue después de lo que ya imprimió), y eso antes de
    volver a cobrar en línea: un cobro en ese momento podría llevar un folio ya impreso. Lo de la bandeja sube después, una vez,
    por su Id. Si en el respaldo sigue abierto otro turno de la caja (el de antes; el suyo se abrió después del respaldo), la
    caja principal no deja darlo de alta: la caja lo guarda aparte (`turno-sin-alta`, que no tapa el turno que dice la caja
    principal), la franja dice qué hacer, y lo vuelve a intentar (en el latido, una vez por minuto, y al preguntar por el
    turno): en cuanto el otro se cierra (su corte Z otra vez; en el piloto, la lectura del corte de eleventa), queda dado de
    alta con su mismo Id y sube lo que se cobró en él.
  - **El piloto con eleventa:** restaurar no toca eleventa; la bandeja del puente (`EnviosAEleventa`) regresa como estaba en el
    respaldo, y lo que el puente mandó después vuelve a estar pendiente. No se manda dos veces: cada intento busca primero, en
    la misma transacción, la marca de lo suyo en eleventa (la venta por `CAJA_ID` y `CREADO_EN`, la devolución por su ticket y
    `DEVUELTO_EN`, la entrada o salida por su caja, su tipo y `CUANDO_FUE`) y, si está, lo deja como enviado sin escribir y sin
    cambiarle el folio, que pudo salir impreso: si otra caja tiene el mismo, queda como repetido, como en la revisión tardía.
    Cuatro lugares tomaban «nunca se intentó» como «no está en eleventa» (cancelar aquí una venta sin mandar, «dejar por
    revisar», la lectura de las existencias y la búsqueda de un folio de eleventa): al restaurar, lo pendiente queda con al
    menos un intento, y esos también la buscan allá antes. Lo anotado a mano en eleventa después del respaldo vuelve a salir
    «por revisar». Dos lugares se quedan del lado seguro mientras lo que regresó no se vuelva a mandar: una devolución de la ida
    que ya había llegado cuenta como que no (retiene de más), y una devolución cuya venta regresó pendiente no se busca allá
    («dejar por revisar» la pide anotar a mano): se revisa en eleventa antes de anotarla.
  - **Si el servicio ni arranca** (la base no se puede migrar): `restaurar.ps1`, como administrador en la caja principal
    ([pasos](pruebas-en-tienda.md#si-la-base-se-daña)). Antes de tocar nada revisa que quepa (lo mismo que el servicio).
    Detiene y deshabilita el servicio, aparta la base igual, copia el respaldo, deja la marca para que el servicio lo revise al
    arrancar y su pedido ya puesto (lo de después de migrar lo termina el servicio), y al final la copia pasa a ser la base; lo
    arranca y espera a `api/estado`. Si no puede apartar la base o poner el respaldo, borra la copia a medias, regresa la base
    a su lugar (moviéndola: con el disco lleno también se puede) y deja el servicio como estaba. La versión del respaldo no la
    puede revisar (PowerShell no abre la base): la revisa el servicio al arrancar y, si lo hizo una versión más nueva, no
    arranca con él; el script enseña lo que dijo (del Visor de eventos).
  - **Sin servicio de Windows** (la caja del navegador, en desarrollo): nadie lo vuelve a arrancar; el pedido espera al
    siguiente arranque y, mientras, se puede cancelar.
- **Central** (pospuesto): por la sincronización, tendría la réplica casi en vivo de todas las sucursales.
- **Fuera:** desde el piloto, cada caja principal sube a Backblaze B2 su copia diaria y la del corte Z, cifradas, con
  una llave que no puede borrar y un bloqueo de 14–30 días. El central, copia continua a B2 y el respaldo diario del
  proveedor.

## Actualizaciones

**Durante el piloto, a mano, con el script** ([cómo](pruebas-en-tienda.md#actualizar-la-caja-2-del-piloto)):

- **El paquete:** `actualizar.ps1`, `servicio\` y `caja\`, publicados para Windows de 64 bits con .NET adentro. Lo deja cada
  corrida del «Instalador de prueba» de GitHub Actions (`PapeleriaPos-Actualizacion-1.0.<número>`), con lo mismo que el
  instalador de prueba; también lo arma `instalacion/armar-paquete.ps1` en una PC con Windows. Cada .exe lleva su versión
  (1.0.<número de la corrida>; lo compilado a mano, 0.0.0-local) y `actualizar.ps1` la compara: no pone una anterior ni la
  misma sin `-Forzar`.
- **Lo que cambia y lo que no:**
  - el programa, en `C:\Program Files\PapeleriaPos` (`Servicio` y `Caja`), se reemplaza entero, con su `appsettings.json`.
    Ahí las cuentas comunes de Windows no pueden cambiarlo; un servicio instalado en otra carpeta, como se hacía antes, pasa
    ahí al actualizarlo;
  - lo de la PC, en `C:\ProgramData\PapeleriaPos`, no se toca: la base (`pos.db`) y sus respaldos, la configuración de esa PC
    (`servicio.json`, con el puente con eleventa), la llave del servicio, los registros y lo de la caja (`Caja\`: su alta, su
    copia y los tickets en espera);
  - `servicio.json` manda sobre el `appsettings.json` del programa, y las variables de entorno y la línea de órdenes mandan
    sobre ella. La clave de eleventa va solo en la variable de entorno `Eleventa__Clave` del servicio, nunca en un archivo:
    actualizar no borra el servicio (al de otra carpeta solo le cambia la ruta), así que su variable se queda. La prueba con
    una persona real no lee `servicio.json`: tiene su propia carpeta y puede estar en la misma PC.
- **Con respaldo y vuelta atrás:** con la caja cerrada y el servicio detenido, respalda la base (con su `-wal` y su `-shm`) y
  los dos programas en `ProgramData\PapeleriaPos\actualizaciones\<fecha>-<versión de antes>` (las últimas 3). Arranca el
  servicio y espera a que `api/estado` conteste con la versión nueva. Si no contesta, se detiene solo o la revisión de la base
  al arrancar la encuentra dañada, regresa los programas y también la base (las migraciones pudieron cambiarla) y arranca la
  versión de antes.
- **Detener el servicio no pierde nada del puente.** La bandeja de salida (`EnviosAEleventa`) vive en `pos.db`: cada venta
  entra a la bandeja en la misma transacción que la venta, y lo que va a eleventa sigue pendiente hasta que eleventa lo
  confirma. Al detenerse, el servicio deja de esperar lo que iba en camino sin cancelarlo en Firebird (Firebird 2.0 no lo
  aguanta): lo que eleventa alcanzó a guardar lo encuentra su marca en el siguiente intento y no se manda dos veces, y lo que
  no, se vuelve a mandar. Al arrancar, la vuelta de fondo sigue con lo pendiente, en su orden. Mientras, la caja 2 está
  cerrada y se cobra en eleventa.
- **Un límite, por revisar:** el servicio migra la base antes de avisarle a Windows que ya arrancó, y Windows espera eso unos
  30 s; al restaurar un respaldo, antes también lo copia, le hace la revisión completa y, ya migrada, su respaldo. Con las bases de hoy son segundos. Si un día una migración tardara más (una base muy grande), Windows daría por
  fallido el arranque y `actualizar.ps1` regresaría a la versión de antes: para entonces, migrar después de avisarle a
  Windows, o desde el script antes de arrancar el servicio.

**Después, con el central** (pospuesto): el central guarda el instalador vigente; cada caja principal lo descarga y verifica
(SHA-256) y las cajas lo toman de su caja principal. Dentro de una sucursal todas las cajas deben tener la misma versión:
primero se actualiza la caja principal y después las demás cajas. Primero se actualiza una sucursal y después las demás.

## Cámaras y dispositivos

Pospuesto con la multisucursal: el grabador Dahua de cada sucursal detecta personas de noche y cuenta cruces en la
puerta; un puente recibiría sus eventos, sacaría la foto y el clip de cada suceso y los subiría al central
([cámaras](camaras.md)). Con una sola sucursal, la caja principal podría escribir el texto de cada suceso sobre el video
y adjuntar la foto del momento ([idea](ideas.md#cámaras-con-una-sola-sucursal)). La presencia en la zona
del efectivo y otros dispositivos propios quedan en [ideas](ideas.md).

## Hardware

| Equipo | Notas |
|---|---|
| Cajas | i5-12400F, 16 GB, NVMe: de sobra. Para cajas nuevas basta un i3 o mini PC N100 con gráficos integrados |
| Monitor | Económico: diseño para 1366×768, alto contraste y letra grande |
| Impresora de tickets | POS-8360, térmica de 80 mm, ESC/POS, en la red (puerto 9100). Impresión directa con comandos: logo, QR y códigos en el ticket ([ticket](fase-1-caja.md#ticket)). En la matriz hay una sola para las dos cajas y no hay cajón ([notas](notas.md#impresoras)) |
| Impresoras de inyección | En la matriz, dos Epson WF-6590 y una EcoTank L5590, por red ([impresión](impresion.md)) |
| Lector | Genérico, lee QR, funciona como teclado |
| Cajón de dinero | En la matriz no hay. Se conecta a la impresora de tickets; cada apertura queda registrada |
| Router | Tenda en cada sucursal ([red de la matriz](#la-red-de-la-matriz)) |
| Switch | Ya hay uno en la matriz (2026-10-09); el primer fin de semana se anotan su marca, modelo, puertos libres y qué va conectado a él. Conecta por cable las cajas, las impresoras y el grabador (el Tenda trae pocos puertos): con la IP puesta en cada equipo se siguen viendo si el Tenda se reinicia o se traba; el Wi-Fi y el DHCP siguen en el Tenda |
| Puente | Pospuesto: Raspberry Pi 5 de 2 GB en cada tienda, siempre prendida (kit de 2,788 MXN con IVA) |
| Cámaras | Grabador Dahua en cada sucursal: en la sucursal 2, XVR5104HS-4KL-I3 con 4 cámaras HAC-HFW1500CM-A de 5 MP; en la matriz, modelo por confirmar ([cámaras](camaras.md)) |
| No-break | En la matriz no hay ninguno, y por ahora no se compra (2026-10-09): si se va la luz, se apagan de golpe la caja principal y la red. Referencia, por si un día se compra: [notas](notas.md#hardware-de-la-tienda) |
| Etiquetas | Hojas adhesivas carta en la WF-6590 |
