# Notas de investigación

> Lo que se descubrió el **2026-10-08** al planear la multisucursal, que el dueño **pospuso** ese mismo día para
> completar primero el POS de una sola sucursal con caja principal y secundaria. Nada se retira: la propuesta completa
> queda en [central](servidor-remoto.md), [cámaras](camaras.md) y [prueba en Linux](prueba-sucursales-linux.md), y las
> ideas en [ideas](ideas.md#varias-sucursales-pospuesto). Cada dato lleva su fuente; los precios y planes cambian: hay
> que volver a revisarlos al retomar. Tipo de cambio usado: 17.98 MXN por dólar y 20.12 por euro (FIX de Banxico del
> 7 de octubre de 2026).

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

- **Las 8 pruebas que fallan en Linux** son por SkiaSharp sin su biblioteca para Linux (logo del ticket). Se arreglan
  con el paquete `SkiaSharp.NativeAssets.Linux.NoDependencies` 4.153.1. Vender e imprimir no lo usan.
- **Las pruebas en Windows de GitHub Actions** solo corren cuando cambia `instalacion/`: la mayoría de los PR no tienen
  ningún check. Conviene correrlas en cada PR.
- **El PR #45** (mensajes sin «(Parameter …)») quedó fuera de la pila de PR encadenados: al fusionar en orden no entra
  solo.
- **Al fusionar los PR:** en la rama de código, `fase-1-varias-cajas.md` dice que el respaldo fuera de la tienda llega
  con el central; debe quedar «desde el piloto, en Backblaze B2», como en el plan.
- **No hay simulador de cajas** para medir el día pico ni la contingencia, y la única medición de búsquedas (18 ms, 19
  de cada 20) es de la PC de desarrollo (i9): falta medir en una PC como las de caja.
- **Una sola impresora de inyección por sucursal:** el servicio vigila una sola dirección (`ImpresoraHost`). Si la
  matriz tiene otra (¿la L5590?), sus hojas no se miden.
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

- **No-break** para la caja principal, que guarda la base. Uno de 480 VA (~639 MXN) puede quedarse corto si también
  alimenta módem y grabador; los de 800–1000 VA cuestan 1,009–1,799 (dato previo, sin reverificar). En el BIOS: encender
  sola al volver la luz; Windows Update fuera del horario de la tienda.
- **Falta comprar el cajón** (~1,000 MXN) para probarlo con las dos cajas.
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
  2026; AWS México cobra en pesos con CFDI desde febrero de 2026. La deducción con factura extranjera la confirma el
  contador: llevarle la regla 2.7.1.14 de la RMF 2026 (permitiría deducir con la factura de un proveedor extranjero si
  trae ciertos datos; fuente secundaria) y preguntarle si se causa IVA por importar servicios. Aplica ya a Kapso
  (WhatsApp) y, al retomar, a Vultr, Twilio, Pushover y el dominio.

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
