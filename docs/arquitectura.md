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
  la red al darlas de alta (si después cambia la IP, hoy hay que reconfigurarlas).
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
- **En el POS:** que la caja 2 vuelva a buscar sola a la principal (con su llave) si cambia la IP; avisos si algo está
  configurado por nombre o con WSD; una franja que distinga «sin internet: la venta sigue, no desconectes nada» de «sin
  red local»; y una bitácora de red que junte sola el diagnóstico.
- **Cambiar la red de la tienda** afecta también a eleventa y a Control Print, que siguen en uso: se hace un sábado o
  domingo, con todo listado y la forma de regresar ([pruebas en la tienda](pruebas-en-tienda.md)).

## Respaldos

- **Local:** copia diaria de la base en cada caja principal, y otra con el corte Z.
- **Central** (pospuesto): por la sincronización, tendría la réplica casi en vivo de todas las sucursales.
- **Fuera:** desde el piloto, cada caja principal sube a Backblaze B2 su copia diaria y la del corte Z, cifradas, con
  una llave que no puede borrar y un bloqueo de 14–30 días. El central, copia continua a B2 y el respaldo diario del
  proveedor.

## Actualizaciones

El central guarda el instalador vigente; cada caja principal lo descarga y verifica (SHA-256) y las cajas lo toman de
su caja principal. Dentro de una sucursal todas las cajas deben tener la misma versión: primero se actualiza la caja
principal y después las demás cajas. Primero se actualiza una sucursal y después las demás.

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
