# Notas de investigación

> Lo que se descubrió el **2026-10-08** al planear la multisucursal, que el dueño **pospuso** ese mismo día para
> completar primero el POS de una sola sucursal con caja principal y secundaria, y lo que se revisó el **2026-10-09**
> con sus respuestas (la red de la tienda, gastos, reportes y quién ve la ganancia). Nada se retira: la propuesta
> completa queda en [central](servidor-remoto.md), [cámaras](camaras.md) y [prueba en
> Linux](prueba-sucursales-linux.md), y las ideas en [ideas](ideas.md#varias-sucursales-pospuesto). Cada dato lleva su
> fuente; los precios y planes cambian: hay que volver a revisarlos al retomar. Tipo de cambio usado: 17.98 MXN por
> dólar y 20.12 por euro (FIX de Banxico del 7 de octubre de 2026).

## Forma de trabajar

Decidido por el dueño el 2026-10-08:

- **Las pruebas reales, en la matriz, solo se pueden hacer sábado y domingo.** Ahí va todo lo que necesita la tienda:
  las dos cajas reales, la red local, las impresoras, el grabador y el cajón.
- **El resto de la semana:** desarrollo, simulaciones y pruebas con datos ficticios, dejando todo listo para esos días.

Propuesta para cumplirlo ([guía](pruebas-en-tienda.md)):

- **Llegar listos al fin de semana.** Cada prueba de la tienda se prepara entre semana para no perder tiempo ahí:
  - el instalador armado y probado antes en una PC limpia;
  - una guía paso a paso con lo que debe pasar, lo que hay que anotar o medir y quién lo hace;
  - el respaldo al llegar y cómo regresar todo a como estaba, sin tocar los datos de eleventa;
  - juntar en un mismo fin de semana todo lo que necesite la tienda, ordenado de lo más importante a lo menos.
- **Lo que se adelanta sin la tienda:** el código, las pruebas automáticas, el simulador de cajas, el instalador en
  GitHub Actions y en una PC limpia (si hace falta Windows, en la laptop o la PC del dueño), y la prueba con una persona
  real (en cualquier PC con Windows).

## Aplica ya, con una sola sucursal

### Seguridad

- **El servicio confía en lo que llega de la misma PC.** `SeguridadCajas.EsLaMismaPc` deja pasar todo `/api` sin llave
  si la dirección es local, y no hay encabezados reenviados ni autenticación aparte. Cualquier túnel o intermediario en
  la caja principal (cloudflared, Caddy, Tailscale Serve o Funnel, o Tailscale en modo userspace) dejaría entrar sin
  llave a quien llegue por él. **Regla:** nada de eso en la caja principal hasta cambiarlo por confiar en la llave y no
  en la dirección. (`SeguridadCajas.cs:42-54`) Además, la llave de cada caja viaja sin cifrar (HTTP) por la red de la
  tienda: las cajas y los celulares de conteo deben ir en una red sin el público (nada de Wi-Fi compartido con
  clientes).
- **Grabadores Dahua: ataque CameraSwarm** (17 de junio al 22 de julio de 2026). Más de 14,500 equipos Dahua
  comprometidos, sobre todo cámaras IP pero también grabadores: fuerza bruta al puerto 37777 con claves de fábrica y,
  por el P2P de DMSS, 283 equipos solo con el número de serie; 1,923 quedaron con una cuenta «p2pwn». El día más fuerte
  (18 de junio) cayó en proveedores de internet de México.
  ([Hunt.io](https://hunt.io/blog/operation-cameraswarm-dahua-cameras-compromised), 2026-08-18) **Revisar los grabadores
  ya**, aunque no se programe nada: [lista](camaras.md#configurar-el-grabador).
- **Firmware del grabador de la sucursal 2** (XVR5104HS-4KL-I3, hardware V3): el vigente es V4.000.0000004.2.R.250123.
  CVE-2021-33045 (explotada) afecta a grabadores con firmware anterior a diciembre de 2019; ningún aviso nombra a este
  modelo, pero hay que actualizar.
- **Grabador de la matriz** (modelo por confirmar): CVE-2021-33046 afecta firmwares de 2017 a 2021 y CVE-2022-30564 a
  los modelos «-I2» y «-X». Anotar la fecha de su firmware y actualizar por Maintenance Center o por USB en FAT32, sin
  cortar la luz. Los códigos de recuperación de clave ya emitidos tardan en dejar de servir: además de cambiar la
  clave, revisar las cuentas. Puertos del grabador: 37777, 37778, 80, 554, 443, 123 y 38800 (POS): no se abre
  ninguno. Viene de fábrica en 192.168.1.108.
- **Autorización por Telegram:** hoy basta con tener el chat vinculado para aprobar, sin PIN. Pedir un PIN no necesita
  el central: el bot lo pide como respuesta, lo borra y la caja principal lo verifica. Por decidir si se hace antes del
  piloto o si, mientras, se pone un monto máximo para aprobar por Telegram. (La huella sí necesitaría una página
  pública, una Mini App.)
- **Dar de baja una caja** (`Caja.Activa = false`) no la bloquea: sigue contestando y una caja desconectada no se
  entera.

### Código y pruebas

- **Las 8 pruebas que fallaban en Linux** eran por SkiaSharp sin su biblioteca para Linux (logo del ticket). Las
  pruebas llevan el paquete `SkiaSharp.NativeAssets.Linux.NoDependencies` 4.153.1; el servicio no, porque la tienda es
  Windows. Vender e imprimir no lo usan.
- **Las pruebas corren en cada PR** (`.github/workflows/pruebas.yml`): todas en Windows y las de
  `PapeleriaPos.Pruebas` en Linux, también en los PR apilados. Los que solo cambian documentación no las corren: el
  repositorio es privado y los minutos de Windows cuentan doble.
- **Fusionar una pila de PR:** el repositorio no borra la rama al fusionar, así que GitHub no reapunta el siguiente PR
  a `main`. Antes de fusionar cada uno hay que cambiarle la base a `main`. Si GitHub marca conflicto pero la fusión en
  local sale limpia, suele ser porque hay dos bases comunes (un PR que entró por separado y también dentro de la pila).
  Se arregla fusionando `main` en la rama del PR. Pasó con el #49 y el #44 el 2026-10-10.
- **No hay simulador de cajas** para medir el día pico ni la contingencia, y la única medición de búsquedas (18 ms, 19
  de cada 20) es de la PC de desarrollo (i9): falta medir en una PC como las de caja.
- **Una sola impresora de inyección por sucursal:** el servicio vigila una sola dirección (`ImpresoraHost`); la matriz
  tiene tres ([impresoras](#impresoras)).
- **Los borrados del catálogo no dejan rastro:** el catálogo y la depuración borran códigos, presentaciones y ligas con
  proveedores; solo los precios quedan en su historial. Un guardado que solo borra no sube la versión del catálogo, así
  que la copia de contingencia de la caja secundaria puede conservar un código borrado hasta 30 minutos.
  (`RegistroCatalogo.cs:301-305, 320-321, 353`; `PosDbContext.cs:117-118`)
- **Lo que no se puede en contingencia:** sin la caja principal, la secundaria vende, pero no cobra impresiones, no
  cancela, no devuelve, no recibe compras, no cuenta inventario, no edita el catálogo ni pide autorización por Telegram
  (queda el PIN). Pasa a contingencia en unos 8 s y sus existencias se renuevan cada 30 minutos. Mientras hay conexión
  no busca en su copia: le pregunta a la principal, con 3 s para conectar y hasta 15 s de espera, y si la red va y
  viene, alterna entre los dos modos. En la prueba con las dos cajas, probar también con el cable flojo o una red lenta,
  no solo desconectada.
- **Nadie vigila a la caja principal:** la presencia de las cajas vive en la memoria del servicio y el bot de Telegram
  vive en ella. Si se apaga en horario, el POS no puede avisar; los avisos de más de un día vencen sin enviarse. La
  respuesta es la vigilancia de Healthchecks.io ([abajo](#respaldo-fuera-de-la-tienda-y-aviso-de-apertura)).
- **Alertas de Telegram:** el código avisa de más casos que la lista de la entrega 4, pero el cajón abierto sin venta
  se guarda y no avisa, y no hay alertas por patrón.
- **Telegram:** un solo receptor por bot (dos servicios con el mismo token se pelean); mandar mensajes con el mismo bot
  desde otro equipo sí se puede. Guarda lo no leído solo 24 horas y no tiene alerta que suene en silencio. Una foto
  puede pesar hasta 10 MB y su texto, hasta 1,024 caracteres (Bot API 10.3). Para pruebas, un bot aparte.
- **La hora de Windows** fuera de dominio se corrige cada 1,024 a 32,768 s (unas 9 h) con time.windows.com, y no corrige
  desfases de más de 15 h. En contingencia la caja usa su propio reloj. La hora oficial de México por NTP:
  cronos2.cenam.mx (cronos.cenam.mx no respondió desde aquí; repetir la prueba desde la tienda) y mx.pool.ntp.org.
  Medir el desfase de cada caja y del grabador (el grabador lo da `global.cgi?action=getCurrentTime`; su manual dice
  que su NTP es «solo TCP»: probar que tome la hora). México no tiene horario de verano desde 2022, salvo la franja
  fronteriza norte.
- **Los importadores** de eleventa y de Control Print solo corren en Windows; generar-ficticios, preparar-caja y
  preparar-prueba también corren en Linux.

### Hardware de la tienda

- **No-break:** no hay ninguno, y por ahora no se compra (el dueño no quiere lista de compras, 2026-10-09). Riesgo: si
  se va la luz, se apagan de golpe la caja principal, que guarda la base, y toda la red. En el BIOS: encender sola al
  volver la luz; Windows Update fuera del horario de la tienda.
  - **Referencia, por si un día se compra** (precios del 2026-10-09): APC BE600M1-LM (600 VA / 330 W, con puerto USB
    de datos), 1,499 en Amazon México. Uno para la caja principal, su monitor y la impresora de tickets, con el cable
    USB para que Windows la apague sola; otro para el módem, el Tenda, el switch, el grabador y la fuente de las
    cámaras. Según la tabla de APC dura unos 30 minutos con 80 W y unos 10 con 180 W. Es de onda escalonada: con
    algunas fuentes de PC (las que tienen PFC activo) la PC se apaga de golpe al pasar a batería, así que hay que
    probarlo antes de confiar en él. Si Windows apaga la caja y la luz vuelve antes de que se acabe la batería, la
    caja no se prende sola: hay que prenderla con el botón.
- **No hay cajón** (ya se sabía: la entrega 1 quedó sin probarlo), y por ahora no se compra (2026-10-09). Las opciones
  quedan como referencia en [impresoras](#impresoras).
- **.NET 11 y siguientes** (la 11 sale en noviembre de 2026) piden procesador x86-64-v2 (SSE4.2): sin eso no arrancan.
  Vale para las PC de caja que ya hay en la matriz y para las nuevas: revisarlas con `/lib64/ld-linux-x86-64.so.2
  --help` desde una USB de Ubuntu. .NET 10 (el del proyecto) tiene soporte hasta noviembre de 2028 y corre en cualquier
  procesador de 64 bits, pero en uno que solo llega a v1 va sin aceleración y arranca más lento (hay que medirlo).
- **La laptop Celeron y la PC A6 del dueño:** la mejor de las dos puede quedar con Windows como caja principal de
  repuesto (si muere una, se instala el servicio y se restaura la última copia). Casi ninguna laptop enciende sola si
  se le agotó la batería: revisar en el BIOS «Power on AC attach» y, si queda de servidor con Linux,
  HandleLidSwitch=ignore para que no se suspenda con la tapa cerrada. La PC A6 gasta de 3 a 5 veces más luz. Modelos por
  confirmar ([cómo](prueba-sucursales-linux.md#las-máquinas)).
- **El cajón abierto con la llave** no lo ve el sistema; algunas impresoras de tickets reportan el sensor del cajón.

### Red de la tienda

Lo que reporta el dueño (2026-10-08 y 2026-10-09): casi siempre que se va el internet de Megacable (no siempre),
también se cae **toda** la red local: no hay comunicación entre las cajas ni con las impresoras. **El problema es
anterior al Tenda:** con el módem solo (como router y Wi-Fi) ya pasaba; entonces esperaban a que volviera el internet
y, para seguir trabajando, usaban los datos del celular y conectaban la impresora directo a la PC con un cable de red.
Se cambió el módem y siguió; el Tenda se compró para arreglarlo y tampoco. Hoy el personal desconecta el cable de
internet del Tenda y la red vuelve. **Solo pasa en una caída real del internet:** si se desconecta el módem o el
cable de internet, no se reproduce. **Cuando pasa, la red local se cae en cuanto se va el internet y vuelve sola en
cuanto regresa.** Durante la falla no contesta el ping y los celulares no se pueden conectar al
Wi-Fi. Todo va por cable salvo la caja 2, que va por Wi-Fi. El Tenda está bien configurado y conectado a su switch. En
el último medio año se fue el internet 3 veces (es variable), y suele tardar unas 12 horas en volver. No hay ningún
no-break. La causa **no se conoce** todavía.

- **Lo que dicen los datos** (revisión del 2026-10-09): lo más probable es que no sea un aparato dañado. Cambiar el
  módem por otro de Megacable descarta un módem descompuesto, pero no la forma en que se porta ese tipo de módem
  cuando pierde el servicio (no se sabe si el nuevo es del mismo modelo). Que los celulares no se puedan conectar
  probablemente apunta al propio router, que deja de dar Wi-Fi y direcciones, no solo internet (la captura del celular
  lo confirma). Pasó con dos routers distintos (antes el módem, ahora el Tenda), así que lo que lo dispara viene del
  lado del internet, y desconectar ese cable lo libera. Que la red se caiga y vuelva al mismo tiempo que el internet
  dice que, mientras dura ese tipo de caída, algo afecta al router en vivo; no es algo que se acumule con las horas. Y
  que no pase en todas las caídas ni al desconectar dice que depende de cómo se cae el servicio de Megacable: al
  desconectar, el corte es limpio.
- **Causas posibles** (las dos primeras, de más a menos probable; la 3 se suma a cualquiera de ellas; es inferencia: no
  hay reportes públicos de esto ni en Tenda ni en Megacable):
  1. **Algo que llega del lado de Megacable mientras dura la caída:** con la señal a medias, un módem de cable se
     resincroniza o se reinicia en ciclo (su conexión con el router sube y baja sin parar), o manda tráfico de más.
     El router no lo aguanta y deja de dar Wi-Fi y direcciones; cuando el servicio vuelve, el módem se estabiliza y el
     router se recupera solo. Si el módem también es router, se cae su propia red local: el síntoma de antes del
     Tenda. Solo pasa en algunas caídas, y desenroscar el cable da un corte limpio, no un ciclo. Es más débil si el
     servicio es de fibra.
  2. **Los reintentos de los equipos saturan al router:** sin respuesta de internet, el grabador, los celulares y las
     PC reintentan sin parar, y cada intento ocupa lugar en la memoria de un router barato. Encaja con que empiece y
     termine junto con el internet. Es menos probable: son unos 10 equipos, los intentos sin respuesta se borran en
     unos 30 s, una tabla llena no impide que el celular se una al Wi-Fi, y no explica que no pase en todas las caídas.
  3. **Por qué también se cae lo que va por cable:** si el problema solo traba al router, el switch sigue pasando el
     tráfico entre equipos con dirección vigente. Para que se caiga todo tiene que pasar algo más: equipos conectados a
     los puertos del Tenda o a su Wi-Fi; una tormenta de tráfico que el switch reparte a todos los puertos; equipos
     configurados por nombre o con WSD, que esperan al DNS del router trabado; o Windows que pasa la red a «Pública» y
     su firewall corta el ping y a eleventa. Antes, la impresora conectada directo a la PC sí funcionaba.

  **Descartadas:**
  - que el router se trabe por algo que se acumula con las horas (que se le venza su dirección o se le llene la
    memoria poco a poco): la red se cae y vuelve en el mismo momento que el internet;
  - que solo falle la caja 2 por su Wi-Fi: se cae toda la red;
  - un corte de luz como causa principal: quitar el cable no arreglaría un apagón (puede sumarse en alguna caída);
  - que el Tenda cambie su red local él solo (manual del AC10 V5, §9.1.2): también pasaría fuera de las caídas;
  - una página de «sin servicio» de Megacable o ajustes del internet (MTU, IPv6): no impedirían que se conecten los
    celulares.
- **El primer fin de semana no se provoca la falla a propósito:** desconectar no la reproduce y un resultado negativo
  no demuestra nada; ese fin de semana es solo mirar y anotar. Opcional, más adelante y solo con el visto bueno del
  dueño, una prueba de «hoyo negro»: con la tienda cerrada pero las PC, el grabador y un par de celulares prendidos,
  como en un día normal, en lugar del módem va la laptop del dueño con una USB de Linux en el puerto de internet del
  Tenda, dándole dirección pero sin contestar nada más, hasta una hora. Si la red se cae, apoya la causa 2; si no, no
  se concluye nada. No se compra nada.
- **Lo que se junta en la próxima caída real** (cada dos meses, más o menos, con la tienda abierta). Lo que más
  importa: si el módem se reinicia o resincroniza en ciclo (el video de sus luces y su registro de eventos), si el
  Tenda ve su internet subir y bajar (su registro), y si los equipos con IP fija en el switch se siguen viendo.
  - **El script de diagnóstico** (PowerShell 5.1, sin permisos de administrador) corre en las dos cajas desde que se
    inicia sesión, con un acceso directo (`powershell -ExecutionPolicy Bypass -WindowStyle Hidden -File …`) que no
    cambia la configuración de Windows. Cada 30 s guarda en un archivo:
    - la hora y el número de vuelta (los huecos muestran que la PC estuvo apagada o que se fue la luz);
    - cómo está conectada (cable o Wi-Fi, con Get-NetAdapter, sin activar la ubicación), la IP y de dónde viene (del
      Tenda, fija o una 169.254), la puerta de enlace y su MAC (avisa si cambia) y el perfil de red;
    - ping con límite de 1 s (con la clase Ping de .NET; el Test-Connection de PowerShell 5.1 no deja poner límite),
      con cuánto tarda, a la puerta de enlace, a la otra caja, a la impresora de tickets (solo ping: dejar abierta una
      conexión al 9100 bloquearía los tickets de eleventa), a cada Epson, al grabador, al módem y a 1.1.1.1 y 8.8.8.8.
      Windows no contesta el ping si no se le permite: al arrancar se anota si la otra caja contesta, y si no, no
      cuenta como caída;
    - con conexiones de 1 s como máximo: si contesta la página del Tenda (puerto 80) y, desde la caja 2, los puertos
      3050 (el Firebird de eleventa) y 3309 de la caja principal;
    - consultas rápidas de DNS (Resolve-DnsName -QuickTimeout) al Tenda para un nombre suyo (tendawifi.com) y uno de
      internet (si no contesta ni el suyo, está trabado), y cuánto tardan en resolverse el nombre de la caja principal
      y los de las impresoras que usen las colas o eleventa;
    - cuántos paquetes de difusión (los que van a todos los equipos) y cuántos descartados recibió la tarjeta de red,
      para ver si hay una tormenta de tráfico;
    - la hora del reinicio programado del Tenda, para saber si su registro se borró.

    Cada 5 minutos, y en cuanto algo cambie, guarda `ipconfig /all`, las fechas de la concesión, `arp -a`, la ruta
    hacia 1.1.1.1 (`tracert -d -h 5 -w 500`) y la hora en que arrancó la PC. El archivo se rota para no crecer sin fin
    y nunca va al repositorio. En la caja 2 tiene un ícono «Diagnóstico de red» con tres botones: «Se cayó la red», «Ya
    desconecté» y «Reconecté». Se prueba entre semana en una PC con Windows, con una cuenta común y una vez con el
    cable desconectado.
  - **El personal**, con la instrucción de la [guía](pruebas-en-tienda.md#durante-una-falla-de-red): antes de
    desconectar (máximo 3 minutos), un video de 60 s de las luces del módem, del Tenda y del switch, la captura del
    Wi-Fi de un celular y «Se cayó la red» en la caja 2. No apagar ni reiniciar nada: eso borra los registros.
    Desconectar solo el cable de red del módem al Tenda, nunca el redondo de Megacable. Como la red vuelve en cuanto
    regresa el internet, cada hora, sin venta abierta en la caja 2, se reconecta y se presiona «Reconecté»; no hay que
    guiarse por la luz de «en línea» del módem, que puede seguir prendida toda la caída. Al cerrar se deja conectado.
  - **Después, en este orden** (el dueño o quien él diga): con el cable todavía fuera (la red local funciona), y antes
    de la primera reconexión, guardar el registro del Tenda (exportarlo o foto de cada página), porque las reconexiones
    y su reinicio diario lo borran; la página de estado y el registro de eventos del módem, desde una laptop conectada
    a un puerto de red del módem; copiar a una USB la carpeta del script de las dos cajas; exportar del Visor de eventos
    los registros Sistema, Dhcp-Client, NetworkProfile y WLAN-AutoConfig (en Registros de aplicaciones y servicios >
    Microsoft > Windows); y el número de reporte y la hora de la caída según Megacable. Todo eso queda fuera del
    repositorio.
- **El arreglo propuesto, con lo que ya hay** (sin compras, 2026-10-09; por aprobar): que la venta y la impresión no
  dependan del router ([plan](plan.md#para-el-piloto-con-dos-cajas)):
  - IP fija puesta en cada equipo, dentro de la red local de hoy, fuera del rango que reparte el Tenda y reservada en
    él, para que un equipo que se prende o renueva su dirección con el router trabado no se quede sin ella (causa 3).
    Cambiar de red local (por ejemplo a 192.168.77.0/24) cambiaría la dirección de todo en un solo fin de semana: solo
    después, y solo si el primer fin de semana muestra que choca con la del módem;
  - todos los equipos de la tienda en el switch que ya hay, no en los puertos del Tenda (causa 3); si al switch le
    faltan puertos, lo que no quepa se queda en el Tenda;
  - impresoras y eleventa configuradas por IP, sin WSD ni nombres; en el firewall de Windows, el ping y los puertos 3050
    y 3309 permitidos en todos los perfiles de red, solo desde la red local (causa 3). Los dos los pide
    [eleventa](https://eleventa.com/aprender/conexion-multicaja) para que se comuniquen sus cajas; el POS solo usa el
    3050 (el Firebird);
  - la caja 2 por cable si ya hay uno que llegue. Si no, sigue por Wi-Fi: con eleventa, no puede vender mientras el
    router esté trabado; con el POS, pasa a contingencia y sigue vendiendo. Como la caja va por el Wi-Fi del Tenda,
    aplica la regla de que las cajas no compartan red con el público ([seguridad](#seguridad));
  - el módem solo al puerto de internet del Tenda y nada más conectado a él (causa 1);
  - en el Tenda, gratis y con poco efecto esperado: apagar UPnP e IPv6, concesiones largas (que la dirección que da a
    cada equipo dure lo más posible) y el reinicio diario de madrugada (anotar su hora: borra el registro). El firmware,
    al final y solo si las notas de esa versión de hardware traen un arreglo de seguridad o de estabilidad: si el Tenda
    queda inservible no hay otro, y se volvería a como estaba antes de él (el módem como router y Wi-Fi).

  Con eso, si lo que pasa solo traba al router (por el módem o por los reintentos), el switch sigue pasando el tráfico y
  los equipos con IP fija se siguen encontrando: la caja principal y lo que va por cable siguen vendiendo e imprimiendo,
  y se pierden el internet y el Wi-Fi de los celulares. **Si en la próxima caída los equipos que ya están en el switch
  no se contestan por IP, el arreglo no basta** (tormenta de tráfico o firewall de Windows). Se comprueba apagando el
  Tenda con la tienda cerrada ([guía](pruebas-en-tienda.md#segundo-fin-de-semana-arreglar-la-red)).
- **Referencia, por si un día se compra algo** (el dueño no quiere lista de compras, 2026-10-09; precios del
  2026-10-09, con IVA):
  - si el script muestra una tormenta de tráfico, un switch administrable con control de tormentas (TP-Link
    TL-SG108E, 579 + 138 de envío en Cyberpuerta; viene con ese control apagado);
  - si el que se traba es el Tenda, un router de negocio como único router y el Tenda solo como Wi-Fi (TP-Link ER605
    V2, 1,029 + 128 de envío en Cyberpuerta): aguanta muchas más conexiones y manda su registro a una PC; no sirve si
    lo que falla es el módem;
  - un MikroTik con Netwatch, solo si alguien que sepa RouterOS lo va a mantener (hEX lite, 899 + 148 de envío, con
    puertos de 100 Mbps): no apaga su puerto de internet, porque entonces no sabría cuándo volvió; mientras no hay
    internet, rechaza el tráfico de la tienda hacia internet, para que los equipos fallen rápido, como si se hubiera
    desconectado el cable;
  - el módem en modo puente, solo si el Tenda muestra una dirección de internet privada y los registros apuntan al
    módem: depende de Megacable y, según usuarios, pide IP pública con costo mensual (sin verificar);
  - para la caja 2, un cable de red hasta ella (Cat 6 de 20 m, 279 en Steren), o un punto de acceso propio conectado
    al switch (TP-Link EAP225, 1,210 en Amazon México); no sirven un puente Wi-Fi ni los adaptadores que llevan la red
    por los cables de la luz (powerline);
  - para el [internet de respaldo](ideas.md#internet-de-respaldo): Bait «Internet en Casa» (kit de 999 con 120 GB por
    30 días; recargas de 99 por 30 GB para 7 días o 349 por 120 GB para 30 días), Telcel «Internet en tu Casa Amigo»
    (recargas de 80 por 10 GB a 200 por 30 GB; kit sin verificar), Bait «Internet Portátil» (kit de 899 con 10 GB por 30
    días) o el ER605 de arriba para que el cambio sea automático. Cada chip nuevo se registra con la CURP del dueño
    (obligatorio en 2026).

  No conviene un enchufe inteligente que reinicie el módem: en 12 horas lo apagaría a cada rato y borraría su registro.
- **En el POS:** la venta de la caja principal no usa la red (habla con su propio servicio). La caja 2 pasa a
  contingencia y sigue vendiendo, pero hoy no vuelve a buscar a la principal si cambia su IP. La impresora de tickets,
  el SNMP y la dirección de la caja principal en la caja 2 aceptan nombres (el instalador sugiere el nombre de la PC):
  hay que configurarlos por IP, y que el instalador muestre la IP. Hace falta distinguir en pantalla «sin internet» (la
  venta sigue, no desconectes nada) de «sin red local», y una bitácora de red que junte sola los datos del diagnóstico.
- **Internet de respaldo:** las caídas de unas 12 horas también cortan lo que sí necesita internet (WhatsApp, recargas,
  Telegram, ver las cámaras). Queda como [idea](ideas.md#internet-de-respaldo), para después de arreglar la red local.

### Impresoras

- **En la matriz,** todo por red: una impresora de tickets (POS-8360) para las dos cajas, dos Epson WorkForce WF-6590 y
  una EcoTank L5590. Las WF-6590 aceptan IP fija y en su página (Web Config) se apagan WSD, Bonjour e IPP; la L5590
  también usa WSD (comprobar si se apaga) y tiene puerto de red por cable además de Wi-Fi.
- **El POS mide y cobra una sola impresora de inyección por sucursal** (`ImpresoraHost`). Mientras no se cambie, no hay
  que apuntar ninguna caja a la segunda WF-6590 ni a la L5590: saldrían faltantes falsos y costos en cero. Que la L5590
  se lea igual por SNMP falta probarlo.
- **Una impresora de tickets para dos cajas, y no hay cajón:** el cajón se conecta a la impresora de tickets, así que
  con una sola las dos cajas abrirían el mismo cajón, y eso choca con el turno y el corte ciego de cada cajero. Si las
  dos imprimen a la vez, hoy no hay reintento. No se recomienda un cajón compartido entre dos turnos. Opciones,
  **referencia, por si un día se compra** (el dueño no quiere lista de compras, 2026-10-09; precios del 2026-10-08):
  1. una segunda impresora de tickets (≈1,909 la 3nStar RPT008; confirmar que tenga red) y un cajón para cada una
     (≈799 cada uno);
  2. dos cajones en la misma impresora con un cable en Y (≈799 cada uno, más el cable), si la POS-8360 tiene el
     segundo pulso;
  3. un solo cajón, para la caja principal, y la regla «la caja 2 no cobra en efectivo».

### Luz y costo de los servicios

- **Tarifa de luz de negocio** (PDBT, baja tensión hasta 25 kW), octubre de 2026: 3.565 (Valle de México) a 4.255
  (Jalisco) $/kWh sin IVA en las seis divisiones del centro consultadas (las demás no se revisaron), más un cargo fijo
  de 25.25 a 70.34 al mes. No hay contador ni nada fiscal por ahora (el contador está en los planes del dueño,
  2026-10-09): el IVA de la luz es costo, y el $/kWh real es el cargo × 1.16. El dato que vale sale del recibo de la
  matriz. (CFE, consultado el 2026-10-08)
- **Consumo de las impresoras** (fichas de Epson): WF-6590, 36 W imprimiendo y 17 W en espera; L5590, 12 W. La luz por
  cara impresa es despreciable (≈0.0001 MXN); la espera (17 W, y 2.7 W en reposo) suma a lo más ≈25 MXN al mes por
  WF-6590 y va como luz no asignada. Lo que pesa es la tinta y la depreciación. El módulo trae 3.00 $/kWh y 25 W por
  omisión: hay que corregirlos.
- **La enmicadora** pesa por el tiempo que pasa encendida, no por cada uso: se estima con la potencia de su etiqueta y
  las horas que pasa encendida en un día normal (sin comprar medidor). Una engargoladora manual no gasta luz, solo se
  deprecia.
- **Depreciación:** (costo − valor final) ÷ meses de vida ÷ usos al mes. El equipo de la matriz que hoy está en el
  inventario de eleventa (impresoras, engargoladora, enmicadora, guillotina, PC) sale del inventario y entra como
  equipo. **No hay fecha de compra** (2026-10-09). Propuesta, por aprobar: se toma lo que vale hoy cada equipo y los
  años de uso que le quedan, y se deprecia desde el arranque del piloto; el costo que tenga en el inventario de
  eleventa sirve de referencia. El dueño confirma esos valores.
- **Hoy el POS cobra los servicios con costo 0:** la utilidad sale inflada hasta que lleguen las recetas. Mientras,
  conviene un costo estándar por unidad, como en eleventa.

### Reportes

- **Pospuestos** (2026-10-09): el dueño los deja para después de la
  [prueba con una persona real](plan.md#prueba-con-una-persona-real) y de que él la apruebe. Lo de abajo queda para
  entonces. La captura de cada gasto cuando se paga no es un reporte: no se pospone
  ([administración](plan.md#administración-y-punto-de-equilibrio)).
- **La utilidad y la ganancia solo las ve el dueño** (2026-10-09). Hoy ya es así en el margen mínimo, los costos de las
  impresiones y la cotización, y el resumen de Telegram (con la utilidad del día) solo va a los chats que vinculó el
  dueño. El encargado sí ve costos: al costear una compra (con el margen de cada producto), en la ficha del producto
  (F3) y en el conteo del día, porque los necesita para poner precios y contar. **Falla en el código:** el bot deja
  vincular un grupo, y como autoriza por chat y no por persona, si el dueño vinculara uno con el encargado, el
  encargado vería la utilidad y cualquiera del grupo podría apretar «Aprobar». Hay que corregirlo para cumplir lo
  decidido: rechazar el vínculo desde un grupo y revisar quién aprieta el botón.
- **Lo que usa el dueño en eleventa** (2026-10-08): ventas por día, mes y año a la fecha, y contra el año anterior. Su
  versión de eleventa no tiene apartados ni cotizaciones, y hoy no se factura nada.
- **El historial importado de eleventa** (tablas de ventas históricas) da el año anterior desde el primer día. Trae
  tickets, líneas y el costo capturado en eleventa (en los servicios, sumado a mano), así que la utilidad del año
  anterior se muestra como «utilidad según eleventa». El importador no lee caja ni cajero: el desglose por caja, cajero
  y forma de pago solo sale para lo vendido en el POS. Falta revisar con la copia real desde qué año hay historial, si
  la tabla de tickets de eleventa trae caja o cajero y si trae las recargas.
- **Hoy el POS** solo tiene las ventas del día de cada caja, el resumen de Telegram y el diagnóstico en Excel.

### Respaldo fuera de la tienda y aviso de apertura

- **Backblaze B2:** no pide tarjeta, 10 GB gratis y después 6.95 USD por TB al mes. Al llegar al tope deja de subir y
  no cobra de más. Una llave que no puede borrar todavía puede ocultar archivos: va con bloqueo de objetos de 14–30
  días y una regla que borre lo viejo.
- **Litestream** no tiene soporte oficial en Windows (v0.5.17): en la caja principal, una copia programada (rclone,
  cifrada) de la copia diaria y la del corte Z, con una restauración de ensayo de vez en cuando.
- **Healthchecks.io** (plan gratis): 20 vigilancias, admite uso en empresa, avisa por Telegram (con su propio bot),
  Pushover y correo. Lo opera una sola persona (en Letonia). Si nadie entra en más de un año, avisa y borra la cuenta a
  los 30 días. **Vigilancia de la caja principal:** se reporta cada pocos minutos en horario; si no abre o se apaga
  (luz, internet o la PC), Healthchecks.io avisa en minutos. Cada vigilancia necesita horario y días de descanso: para
  el piloto se configuran a mano en Healthchecks.io (en el POS la sucursal solo tiene hora de apertura y de cierre).
- **Alternativas de vigilancia:** Better Stack gratis (revisa cada 3 min); UptimeRobot gratis (dice «hobby and
  non-profit», aunque sus términos permiten uso comercial; sin latido ni Telegram); Uptime Kuma propio (no avisa si se
  cae su propio servidor). En ntfy, el nombre del tema funciona como contraseña. Correo gratis: Gmail ~500 al día, Brevo
  300 (con aprobación) y Resend 100.
- **Cloudflare R2:** pide tarjeta y cobra las escrituras arriba de un millón al mes.

### WhatsApp (API)

- **Coexistencia:** pide la app WhatsApp Business 2.24.17 o más nueva y un alta por un proveedor de Meta o como «Tech
  Provider» (verificar el negocio y pasar una revisión con videos, sin costo ni plazo publicados). Los proveedores
  piden al menos 7 días de uso del número (Meta ya no lo exige; Wati recomienda 30–60). Si un alta falla, el número debe
  usarse 1–2 meses en la app antes de reintentar.
- **La app de WhatsApp para Windows no se admite** en coexistencia: en las PC, WhatsApp Web en Chrome o Edge. Al
  conectar, Meta desvincula todos los dispositivos. El celular debe abrir la app al menos cada 13 días.
- **Tarifas desde octubre de 2026:** respuestas libres dentro de las 24 h, 1,000 gratis al mes por número; plantillas
  de utilidad, 0.1565 MXN si la cuenta factura en pesos (la moneda se elige al crearla); marketing, 0.0397 USD. Sin
  medio de pago, Meta deja de entregar después del mensaje 1,000. Las páginas de precios de Meta en español y la
  general están desactualizadas: estos precios son de la documentación para desarrolladores.
- **El webhook** pide HTTPS público con certificado válido y validar la firma de cada aviso. Meta reintenta hasta 7
  días (puede repetir: se descarta por ID) y el archivo hay que bajarlo antes de 7 días.
- **Sin central:** un receptor aparte, por ejemplo un Cloudflare Worker con su base D1 (gratis: 100,000 peticiones al
  día), o un proveedor que guarde los mensajes, como Kapso (25 USD al mes por hasta 3 números). Nunca un túnel en la
  caja principal ([seguridad](#seguridad)). Detalle en [WhatsApp](whatsapp.md).
- **Del Worker gratis:** la base D1 guarda hasta 5 GB y 100,000 escrituras al día; la cola (Queues) guarda solo 24 h;
  con dominio propio, apagar «Bot Fight Mode»; falta confirmar si Meta acepta `workers.dev` (si no, hace falta el
  dominio).
- **Kapso** cobra los mensajes con sus créditos en dólares y con margen (no aplica la tarifa en pesos), dice que la
  coexistencia es «menos estable» y, si avisa a la caja, reintenta solo 3 veces en ~50 s: la caja le pide los mensajes
  por su API al encender. Otras: 360dialog, 49 EUR por número (~987 MXN), y solo reenvía; YCloud gratis, sin confirmar
  que incluya coexistencia (soporte de 19:30 a 04:30, hora de la Ciudad de México).
- El número que manda WhatsApp para México puede no coincidir con el que se marca: se guarda tal cual y se normaliza a
  10 dígitos.

### Cámaras con una sola sucursal

- El grabador de la sucursal 2 (XVR5104HS-4KL-I3) tiene **función POS**: la caja le manda una línea por suceso al puerto
  38800 («CANCELACION F-1234 $250.00 CAJERO ANA», sin acentos) y queda escrita sobre el video, para buscarla después.
  También da la **foto de una cámara** (`snapshot.cgi`, que hay que probar). Las dos cosas funcionan de día, con la caja
  principal, sin central ni puente ([idea](ideas.md#cámaras-con-una-sola-sucursal)). Para probar la foto: usuario y
  clave con Digest y, en el grabador, «Security Authentication» en MD5 (si no, responde error 500); el canal empieza en
  1 (en algunos firmwares, en 0); tarda 1–3 s y las fotos se piden una por una; si sale chica (CIF), plan B: un cuadro
  del video RTSP con ffmpeg. Si `magicBox.cgi?action=getMachineName` da 404, esa función está apagada.
- **Lo que apaga su inteligencia:** el modo rostro, la mejora de grabación («Encode Enhancement», apaga las líneas de
  cruce) y agregarle cámaras IP. **No cuenta personas.**
- **DMSS:** desde el 15 de enero de 2026, agregar un equipo por P2P pide cuenta de Dahua. Para que llegue el aviso de
  persona detectada hay que activarlo en el grabador y suscribirlo en DMSS.
- **Privacidad:** letrero con el aviso simplificado y aviso integral, también para empleados (Ley de protección de
  datos de 2025). Las cámaras graban audio.

### Cuentas y pagos

- **Tailscale gratis** es solo para uso no comercial; el plan para negocio cuesta 8 USD por usuario al mes.
- **NetBird** (gratis, 5 usuarios y 100 equipos) admite uso en un negocio, pero puede retirar el plan con 14 días de
  aviso y parar hasta 2 horas por semana, sin zona horaria: en la Ciudad de México cae entre las 18:00 y las 19:00, con
  la tienda abierta. Sirve para el soporte a distancia, con tres cuidados desde el primer día: quitar la regla
  «Default» que conecta a todos con todos, contar que soporte ocupa uno de los 5 usuarios y, en la caja principal,
  usarla en modo normal (TUN), nunca en modo userspace ([seguridad](#seguridad)).
- **Azure for Students** es solo para estudiantes de tiempo completo y uso educativo no comercial: no sirve para probar
  el sistema del negocio.
- **Pagos al extranjero:** Vultr y el dominio cobran en dólares sin factura mexicana; Twilio da CFDI desde marzo de
  2026; AWS México cobra en pesos con CFDI desde febrero de 2026. Hoy no hay contador ni nada fiscal (2026-10-09).
  Para cuando haya contador (está en los planes del dueño, [idea](ideas.md#otras-ideas-que-han-salido)): llevarle la
  regla 2.7.1.14 de la RMF 2026 (permitiría deducir con la factura de un proveedor extranjero si trae ciertos datos;
  fuente secundaria) y preguntarle si se causa IVA por importar servicios. Toca a Kapso (WhatsApp) y, al retomar la
  multisucursal, a Vultr, Twilio, Pushover y el dominio.

## Para retomar la multisucursal

- **Lo que pidió el dueño:** administrar ante un suceso a cualquier hora (cámaras y contador), precios por sucursal,
  resistir cualquier flujo de mercancía y la mejor calidad-precio. Rechazó empezar sin central. La propuesta completa,
  con costos, está en [central](servidor-remoto.md).
- **Cuándo retomarla:** cuando el dueño decida pasar la sucursal 2 (o la 3.ª) al POS, unos 6 meses antes de esa fecha;
  mientras, la sucursal 2 abre con eleventa. Otras señales: ver las dos sucursales de noche más de una vez por semana,
  crédito compartido frecuente, más de ~5 traspasos a mano por semana o que el catálogo se desalinee entre sucursales.
  El volumen (60–120 tickets al día) no es señal.
- **Lo que hay que arreglar en el código antes:** la confianza en la misma PC; los supuestos de «primera sucursal»
  (`PreparacionCaja.cs:25`, `RegistroDepuracion.cs:300`, `ConteoCelular.razor:62`, `ConfigurarCaja.razor:202`); que
  con conexión un usuario de otra sucursal puede entrar y autorizar; registrar lo que se quita del catálogo; autor,
  sucursal y versión en el historial de precios; prefijo de sucursal en los códigos internos; las llaves únicas que
  chocarían entre sucursales (códigos, marcas, usuarios, ligas con proveedor); que guardar filas replicadas dispararía
  avisos, historial y versión nueva; los intentos de PIN fuera de lo versionado; los días de descanso.
- **Trampas del precio por sucursal:** el precio propio de una presentación le gana a la escala de la sucursal, y el
  ajuste de margen en compras revisa el precio de la sucursal pero cambia el base. Hoy ningún código crea un precio por
  sucursal.
- **Una sola versión de catálogo** para toda la base: cualquier cambio hace que todas las cajas vuelvan a bajar el
  paquete (~1 MB).
- **Sincronización:** se descartaron Dotmim.Sync (SQLite solo como cliente) y SignalR (se rinde tras 4 reintentos): se
  escribe a la medida.
- **Servidores** (precios al 2026-10-08):
  - Vultr Ciudad de México: 1 GB a 5 USD, 2 GB a 10, 4 GB a 20; el respaldo suma 20 %; se sube de plan, no se baja;
    ping desde aquí, 39 ms; si falla el pago puede suspender el servidor; usa Intel de generación anterior.
  - AWS Lightsail (sin región en México): 1 GB ≈146 MXN y 2 GB ≈315–325 con IVA y CFDI; su plan gratis suspende la
    cuenta a los 6 meses.
  - Descartados: Oracle gratis (recupera las máquinas ociosas), Hetzner (en EE. UU. casi triplicó su precio en 2026),
    Hostinger (paga 2 años por adelantado y renueva al doble), DigitalOcean, Linode y Google (en dólares, fuera de
    México).
  - Sin evaluar a fondo: Neubox VPS A (mexicano, en pesos; en promoción con 3 núcleos y 4 GB): revisar si da CFDI y
    dónde está.
- **Linux:** Ubuntu 26.04 LTS salió el 23 de abril de 2026, con parches hasta abril de 2031, y trae .NET 10 en sus
  repositorios (`apt install aspnetcore-runtime-10.0`; no trae .NET 8 ni 9). **Ubuntu Server pide desde 1.5 GB de
  memoria:** el plan de 1 GB queda por debajo, otra razón para medirlo antes de elegirlo. El servicio necesita libicu y
  tzdata (sin activar InvariantGlobalization), los puertos 5080/tcp y 5081/udp, y un servicio systemd Type=simple con
  Restart=always. El servicio
  arranca en Linux pero falta: la ruta de datos (sin configurar cae en `/usr/share` y no arranca), publicar para
  linux-x64, la unidad de systemd y el cortafuegos; la búsqueda en la red (UDP 5081) no cruza la VPN. El servicio usa
  ~190–230 MB; con un núcleo, .NET usa el recolector de estación.
- **Rendimiento por núcleo** (PassMark): Celeron N4020 1,122, N4500 1,252, N5100 1,361; A6-5400K 1,271, A6-7400K 1,448,
  A6-9500 1,620; un núcleo de Vultr ronda 1,690 (supuesto). Un Celeron N2840 (667) da resultados demasiado
  pesimistas.
- **Avisos que escalan:** Pushover (pago único de 4.99 USD o 99 MXN en App Store; la emergencia repite hasta 50 veces)
  y la llamada de Twilio (0.0473 USD el minuto a celular, voces en español, opciones por teclado; con el fijo de la
  tienda como identificador sale más barata). PagerDuty y Healthchecks de pago llaman en inglés.
- **Red privada y acceso:** NetBird gratis ([cuidados](#cuentas-y-pagos)); Tailscale para negocio como plan B; Headscale
  y wg-easy si la red vive en el central; Cloudflare Zero Trust pide tarjeta. Las llaves de acceso (passkeys) piden
  dominio propio. Let's Encrypt baja a certificados de 64 días en febrero de 2027 y de 45 en febrero de 2028; Caddy los
  renueva solo.
- **Dominio:** `.com` en Cloudflare, 188 MXN hasta el 31 de octubre de 2026 y 201 después; `.com.mx` en Akky, 249 con
  IVA el primer año. Sin central no corre prisa: comprarlo antes del 31 de octubre solo congela el primer año; haría
  falta antes solo si la API de WhatsApp con Worker lo pide.
- **Puente en la tienda:** Raspberry Pi 5 de 2 GB (kit de 2,788 MXN con IVA en 330ohms), arranca sola al volver la luz;
  el mini PC N150 (5,100–7,600) y los routers GL.iNet se descartaron.
- **Cámaras ligadas al central:** eventos del grabador por `eventManager.cgi` (el de persona solo lo confirma la
  comunidad), reproducción por hora por RTSP, NTP del grabador «solo TCP» según el manual, clips en calidad baja
  (≈8 MB). Contador: primero las líneas de cruce del grabador (gratis); si fallan, una cámara Dahua que cuenta de
  verdad (≈4,640, descontinuada) o Frigate (≈6,700); el sensor ESP32 se descartó (60–80 % de precisión).
- **Internet:** con CGNAT (la WAN del módem en 100.64.0.0/10) no hay dirección pública, pero los túneles de salida
  funcionan; el paquete residencial de Telmex no admite fines comerciales.
- **Luz (CFE, octubre de 2026):** una Pi 5 en tarifa de negocio cuesta 9–11 MXN al mes; una PC de 35 W en casa puede
  empujar el recibo a DAC.
- **Otros datos:** Cloudflare Tunnel sale solo por el puerto 7844 (funciona con CGNAT) pero pide el dominio en
  Cloudflare; su plan gratis no es para lo crítico, limita el video y cloudflared no se actualiza solo en Windows. La
  llave de Tailscale caduca a los 180 días. Telmex Gamer da dirección pública dinámica (499 MXN), Infinitum Negocio
  cuesta 349–899 y Starlink residencial usa CGNAT. En la Pi, la microSD se desgasta: el búfer de video va en RAM o SSD,
  y con más de ~6 canales quizá haga falta la de 4 GB. Pushover no está en español. Twilio cobra por minuto completo y
  hay que activar México en sus permisos geográficos.
- **Inteligencia artificial** para el piloto automático: un modelo pequeño (Claude Haiku 5.5) costaría menos de 1 USD
  al mes con ~30 sucesos al día; solo resume, nunca aprueba ni bloquea.
