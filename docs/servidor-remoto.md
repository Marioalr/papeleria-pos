# Administrar desde fuera

> **Propuesta en discusión.** Falta confirmar las preguntas del final.

## Qué necesita el dueño

- **Cada día:** ver las aperturas y los cierres de todas las sucursales (ventas, cortes, diferencias).
- **Administrar desde fuera:** precios, catálogo, compras y traspasos (las entregas son 1 o 2 días por semana) y
  reportes.
- **Autorizar excepciones:** cada vez menos, una vez que el sistema funcione bien.
- **Más adelante:** recibir WhatsApp por la API (necesita una dirección siempre disponible) y respaldo fuera de la
  tienda.

## El central es un rol, no una máquina

El servicio POS es el mismo programa en todas partes; "central" es un modo de configuración. Moverlo de la matriz a
otro equipo es restaurar un respaldo y cambiar una dirección, sin cambiar código.

Por eso se puede empezar con lo más barato y moverlo solo cuando haga falta.

## Gestión por excepción

La rutina diaria no necesita abrir ningún panel; llega por Telegram:

- **Apertura:** aviso siempre o solo con excepciones (configurable): abrió tarde, fondo distinto, cajero no habitual.
- **Cierre:** resumen por sucursal (ventas, utilidad, corte y diferencias, cancelaciones, pedidos pendientes), en
  mensaje y en archivo.
- **Alertas** en el momento y **autorizaciones** con botones.

Cada sucursal manda sus propios mensajes directo a Telegram (conexión de salida): funciona aunque la matriz esté
apagada. El panel queda para el trabajo de fondo: precios, catálogo, compras y reportes.

## Opciones para el central

| Opción | Costo aprox. | Disponible | Notas |
|---|---|---|---|
| **A. Caja principal de la matriz** | $0 si se apaga de noche. Si se deja prendida 24/7, una PC de escritorio gasta del orden de 100–250 MXN/mes de luz | En horario de tienda | Sin equipo nuevo. Fuera de horario no hay panel |
| **B. Mini PC en casa del dueño** | Unos 4,000–6,000 MXN una vez, más ~10–30 MXN/mes de luz | 24/7, si hay luz e internet en casa | Windows y el mismo instalador. Además es respaldo fuera de la tienda |
| **C. Servidor virtual (Vultr, Ciudad de México, 1 GB)** | ~6 USD/mes | 24/7, en centro de datos | El sistema operativo (Linux) no se ve: un solo programa que se despliega con un script |
| **D. Plataforma administrada (Azure App Service u otras)** | 13 USD/mes o más para algo que no se "duerma" | 24/7 | Más caro sin ventaja real a este tamaño |

## Recomendación

**Etapa 1, al abrir la sucursal 2:** opción A más Telegram, Tailscale y respaldos cifrados a almacenamiento gratuito
(Backblaze B2 o Cloudflare R2: 10 GB gratis, de sobra para la base). **Costo mensual: $0.**

Lo que no se tiene en esta etapa:

- Panel con todas las sucursales fuera del horario de tienda (lo cubre el resumen de cierre por Telegram).
- Si la matriz se queda sin internet, la sucursal 2 sigue vendiendo, pero los cambios de precio hechos en la matriz le
  llegan cuando vuelve.
- WhatsApp por la API (se usa la captura manual desde la PC).

**Etapa 2, cuando llegue WhatsApp por la API, la 3.ª sucursal o haga falta el panel fuera de horario:** mover el
central a la opción **B**. Si no se quiere tener un equipo en casa, la **C**.

## Red local

- Router Tenda en cada sucursal: reservar una IP fija para la caja principal. Las cajas también la encuentran solas
  por búsqueda en la red, como Control Print.
- Tailscale no requiere configurar el router.

## Preguntas

- [ ] ¿Las cajas principales se apagan de noche? ¿Las sucursales abren y cierran a la misma hora?
- [ ] ¿La casa del dueño está en un lugar distinto a la matriz? (Para la opción B.)
- [ ] ¿Qué prefiere el dueño: equipo en casa (pago único) o pago mensual?
