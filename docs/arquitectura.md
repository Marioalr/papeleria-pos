# Arquitectura

## Componentes

```
              Matriz                                        Sucursal 2
  ┌──────────────────────────────────┐           ┌───────────────────────────┐
  │ Caja principal                   │           │ Caja principal            │
  │ · servicio POS (API + SQLite)    │◀─────────▶│ · servicio POS            │
  │ · datos de todas las sucursales  │ Tailscale │   (API + SQLite)          │
  │ · panel del dueño y Telegram     │           │ · interfaz de caja        │
  │ · interfaz de caja               │           └─────────────┬─────────────┘
  └─────────────────┬────────────────┘                         │ LAN
                    │ LAN                               Cajas y celulares
       Caja 2 · Caja 3 · celulares Android
       Control Print (servidor en la red de la sucursal)
```

| Componente | Tecnología | Dónde corre |
|---|---|---|
| Servicio POS | ASP.NET Core (Kestrel) como servicio de Windows | Caja principal de cada sucursal |
| Caja | WPF | Todas las cajas, incluida la principal |
| Panel del dueño | Blazor, servido por el servicio de la matriz | Navegador del celular, por Tailscale |
| App Android | .NET MAUI | Celulares de la sucursal, por Wi-Fi |
| Núcleo | Biblioteca .NET sin dependencias de Windows | Compartido por todo; sus pruebas corren en cualquier máquina |

- **Cada sucursal funciona sola.** Internet solo se usa para sincronizar con la matriz, WhatsApp y Telegram. Si se
  cae, se vende igual.
- **Servicio de Windows, no app de bandeja.** A diferencia de Control Print (donde el servidor vive dentro de la app),
  el servicio corre aunque nadie haya iniciado sesión y Windows lo reinicia si falla.
- **Caja principal:** se recomienda no-break, porque guarda la base. Windows Update con horas activas configuradas para
  que no reinicie en horario de tienda.

## Datos

- SQLite en modo WAL. El servicio es el único que escribe; las cajas pasan por la API (mismo criterio que Control
  Print: SQLite en una carpeta compartida no es confiable con varios escritores).
- **Kardex:** cada venta, compra, ajuste, merma, traspaso o consumo de un servicio es un movimiento nuevo. La
  existencia es la suma. No hay que "pisar" cantidades, así que sincronizar no genera conflictos.
- **IDs UUIDv7** generados donde nace el registro: no chocan entre cajas ni sucursales.
- **Dinero en centavos enteros** (sin errores de redondeo). Fechas en UTC; se muestran en hora de México.
- **Folios por caja:** serie + consecutivo (A-000123), legibles para el ticket.
- Todo registro lleva su **sucursal** desde el primer día, aunque al principio solo exista una.

## Contingencia: caja sin servidor

- Cada caja guarda una copia local del catálogo y los precios, y una **bandeja de salida** (el patrón `AgentSync` de
  Control Print).
- Si la caja principal no responde, la caja entra en modo contingencia: vende con sus propios folios, imprime ticket y
  guarda todo para enviarlo después.
- Al volver el servidor, se sube la bandeja; el inventario no choca porque son movimientos.
- Limitaciones en contingencia: no se ven los pedidos de otras cajas ni saldos de crédito actualizados (se permite con
  aviso o se bloquea, según configuración).

## Varias sucursales

- **Dueño de cada dato:**
  - La matriz es dueña del catálogo, los precios base y por sucursal, los usuarios y los clientes registrados.
  - Cada sucursal es dueña de sus ventas, cortes, movimientos y pedidos.
- **Sincronización:** la sucursal inicia la conexión con la matriz. Envía su bandeja de salida y recibe los cambios de
  catálogo por número de versión. Cada evento lleva su ID: reenviarlo no duplica nada.
- **Si la matriz está apagada o sin internet,** las sucursales siguen trabajando y sincronizan cuando vuelve. Mientras
  tanto el panel del dueño no muestra datos nuevos de las demás sucursales.
- **El central es un rol, no una máquina:** puede pasar de la matriz a otro equipo sin cambiar código. Opciones y
  recomendación en [administrar desde fuera](servidor-remoto.md).
- **Traspasos:** documento con estados (enviado → recibido). Cada lado genera sus movimientos y las diferencias se
  marcan.

## Red

- **Tailscale** (plan gratuito) une las cajas principales de todas las sucursales, el celular del dueño y la PC de
  soporte. No se abren puertos y funciona con datos móviles.
- **Telegram:** el bot consulta a Telegram por su cuenta (conexión de salida), así que no necesita dirección pública.
- **WhatsApp:** es lo único que necesita una dirección pública (webhook). Ver [WhatsApp](whatsapp.md).

## Respaldos

- **Local:** copia diaria de la base a otra carpeta o disco, con N días de historia (como el `BackupService` de
  Control Print).
- **Matriz:** por la sincronización, tiene copia de los datos de todas las sucursales.
- **Fuera de la tienda:** copia diaria cifrada a almacenamiento gratuito (Backblaze B2 o Cloudflare R2, 10 GB). Sin
  esto, un robo o incendio en la matriz pierde todo.

## Actualizaciones

Mismo patrón que Control Print: la matriz guarda el instalador vigente, cada caja principal lo descarga y verifica
(SHA-256) y las cajas lo toman de su caja principal. Dentro de una sucursal todas las cajas deben tener la misma
versión. Se actualiza primero la matriz.

## Hardware

| Equipo | Notas |
|---|---|
| Cajas | i5-12400F, 16 GB, NVMe: de sobra. Para cajas nuevas basta un i3 o mini PC N100 con gráficos integrados |
| Monitor | Económico: diseño para 1366×768, alto contraste y letra grande |
| Impresora de tickets | Genérica ESC/POS (58 u 80 mm, por confirmar). Impresión directa con comandos: QR y códigos en el ticket |
| Lector | Genérico, lee QR, funciona como teclado |
| Cajón de dinero | Recomendado (~1,000 MXN). Se conecta a la impresora de tickets; cada apertura queda registrada |
| No-break | Recomendado para la caja principal |
| Etiquetas | Hojas adhesivas carta en la WF-6590 |
