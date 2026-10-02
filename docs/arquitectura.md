# Arquitectura

> Punto de partida en lluvia de ideas: puede cambiar mientras se cierra el plan.

## Componentes

```
              ┌───────────────────────────────────────────┐
              │ Central: servidor virtual (Vultr CDMX)    │
              │ servicio POS en modo central · SQLite     │
              │ panel del dueño · webhook de WhatsApp     │
              │ dispositivos propios (MQTT, a futuro)     │
              └─────────▲────────────────────▲────────────┘
                        │ HTTPS, siempre lo inicia la sucursal
          ┌─────────────┴─────────┐   ┌──────┴────────────────┐
          │ Matriz                │   │ Sucursal 2            │
          │ caja principal:       │   │ caja principal:       │
          │ servicio POS + SQLite │   │ servicio POS + SQLite │
          └──────────┬────────────┘   └──────────┬────────────┘
                     │ LAN (router Tenda)        │ LAN
       Cajas · celulares · impresoras      Cajas · celulares · impresoras

  Dueño: Telegram (alertas, cierres, autorizaciones) y panel por Tailscale
```

| Componente | Tecnología | Dónde corre |
|---|---|---|
| Servicio POS | ASP.NET Core como servicio de Windows | Caja principal de cada sucursal |
| Central | El mismo servicio en modo central | Servidor virtual ([por qué](servidor-remoto.md)) |
| Caja | Ventana de Windows (WPF) que muestra la interfaz compartida | Todas las cajas |
| App Android | .NET MAUI con la interfaz compartida | Celulares de la sucursal, por Wi-Fi |
| Panel del dueño | La interfaz compartida, servida por el central | Navegador, por Tailscale |
| Módulo de impresión | Agente en cada PC que imprime ([detalle](impresion.md)) | PCs con impresora |
| Núcleo | Biblioteca .NET sin dependencias de Windows | Compartido por todo; sus pruebas corren en cualquier máquina |

- **Una sola interfaz.** Componentes Blazor (HTML/CSS + C#) en una biblioteca compartida: la caja los muestra en una
  ventana de Windows (con WebView2, que ya trae Windows 11), la app Android en MAUI y el panel en el navegador. Se
  escriben una vez para los tres. .NET ya trae una plantilla para este esquema (MAUI Blazor Hybrid + Web). En la caja
  todo corre en la misma PC, sin red de por medio, así que la velocidad no se sacrifica.
- **Cada sucursal funciona sola.** Internet solo se usa para sincronizar, WhatsApp y Telegram. Si se cae, se vende
  igual.
- **Servicio de Windows, no app de bandeja:** corre aunque nadie haya iniciado sesión y Windows lo reinicia si falla.
- **Caja principal:** se recomienda no-break, porque guarda la base. Windows Update con horas activas configuradas para
  que no reinicie en horario de tienda.

## Datos

- SQLite en modo WAL. En cada lugar, el servicio es el único que escribe; las cajas pasan por la API (SQLite en una
  carpeta compartida no es confiable con varios escritores).
- **Kardex:** cada venta, compra, ajuste, merma, traspaso o consumo de un servicio es un movimiento nuevo. La
  existencia es la suma. No hay que "pisar" cantidades, así que sincronizar no genera conflictos.
- **IDs UUIDv7** generados donde nace el registro: no chocan entre cajas ni sucursales.
- **Dinero en centavos enteros** (sin errores de redondeo). Fechas en UTC; se muestran en hora de México.
- **Folios por caja:** serie + consecutivo (A-000123), legibles para el ticket.
- Todo registro lleva su **sucursal** desde el primer día, aunque al principio solo exista una.

## Contingencia: caja sin servidor

- Cada caja guarda una copia local del catálogo y los precios, y una **bandeja de salida**.
- Si la caja principal no responde, la caja entra en modo contingencia: vende con sus propios folios, imprime ticket y
  guarda todo para enviarlo después.
- Al volver el servidor, se sube la bandeja; el inventario no choca porque son movimientos.
- Limitaciones en contingencia: no se ven los pedidos de otras cajas ni saldos de crédito actualizados (se permite con
  aviso o se bloquea, según configuración).

## Varias sucursales

- **Dueño de cada dato:**
  - El central es dueño del catálogo, los precios base y por sucursal, los usuarios y los clientes registrados. Se
    editan en el panel (o desde cualquier caja con permiso) y bajan a las sucursales.
  - Cada sucursal es dueña de sus ventas, cortes, movimientos y pedidos.
- **Sincronización:** mientras está prendida, la caja principal de cada sucursal se conecta al central: envía su
  bandeja de salida y recibe los cambios de catálogo por número de versión. Cada evento lleva su ID: reenviarlo no
  duplica nada.
- **De noche** las cajas se apagan. Lo que llega mientras tanto (WhatsApp, eventos de dispositivos) espera en el
  central y se entrega al encender la caja principal.
- **Si el central falla,** las sucursales siguen vendiendo; se pierden por un rato el panel y la sincronización. Se
  reconstruye desde el respaldo.
- **La matriz es una sucursal más;** ahí llegan las compras.
- **Traspasos:** documento con estados (enviado → recibido). Cada lado genera sus movimientos y las diferencias se
  marcan.

## Red

- **Router Tenda** en cada sucursal: IP fija reservada para la caja principal. Las cajas también la encuentran solas
  por búsqueda en la red.
- **Tailscale** (plan gratuito): panel del dueño y soporte. El panel no queda expuesto a internet.
- **Públicos en el central** (HTTPS con dominio propio): solo el webhook de WhatsApp y, a futuro, la entrada de los
  dispositivos.
- **Telegram:** cada sucursal y el central se conectan a Telegram por su cuenta (conexión de salida).

## Respaldos

- **Local:** copia diaria de la base en cada caja principal, con N días de historia.
- **Central:** por la sincronización, tiene copia de los datos de todas las sucursales.
- **Fuera:** copia diaria cifrada del central a almacenamiento gratuito (Backblaze B2 o Cloudflare R2, 10 GB). En el
  piloto, sin central todavía, la caja principal sube su copia directo.

## Actualizaciones

El central guarda el instalador vigente; cada caja principal lo descarga y verifica (SHA-256) y las cajas lo toman de
su caja principal. Dentro de una sucursal todas las cajas deben tener la misma versión. Primero se actualiza una
sucursal y después las demás.

## Dispositivos propios (a futuro)

Contador de clientes, presencia en la zona del efectivo, vigilancia nocturna: ver [ideas](ideas.md). No condicionan la
arquitectura hoy; solo se deja lugar para que sus eventos entren por el mismo camino que los de las cajas.

## Hardware

| Equipo | Notas |
|---|---|
| Cajas | i5-12400F, 16 GB, NVMe: de sobra. Para cajas nuevas basta un i3 o mini PC N100 con gráficos integrados |
| Monitor | Económico: diseño para 1366×768, alto contraste y letra grande |
| Impresora de tickets | Genérica ESC/POS (58 u 80 mm, por confirmar). Impresión directa con comandos: QR y códigos en el ticket |
| Lector | Genérico, lee QR, funciona como teclado |
| Cajón de dinero | Recomendado (~1,000 MXN). Se conecta a la impresora de tickets; cada apertura queda registrada |
| Router | Tenda en cada sucursal |
| No-break | Recomendado para la caja principal (y para el router si llega la vigilancia nocturna) |
| Etiquetas | Hojas adhesivas carta en la WF-6590 |
