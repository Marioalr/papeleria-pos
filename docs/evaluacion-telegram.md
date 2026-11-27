# Evaluación y comparativa: bot de Telegram y canales de aviso

> **En evaluación (2026-11-26).** Todavía **no se toma una decisión** sobre el bot de Telegram ni sobre qué canal
> alternativo usar, si hace falta alguno.
>
> **Regla de alcance (2026-11-26):** sin importar qué se decida al cerrar esta comparativa, **ni Telegram ni otro bot o
> canal remoto entran en la primera fase del modo servidor** ([central](servidor-remoto.md)). Durante esa primera fase,
> la ausencia del dueño en la tienda se gestiona mediante **permisos locales** (por ejemplo, un encargado con suficiente
> poder de decisión en caja e inventario) y la auditoría en bitácora desde el panel.

## Contexto de la evaluación

La propuesta original del 2026-10-08 diseñó el bot de Telegram como pieza central para tres tareas en tiempo real:
destrabar autorizaciones de caja a distancia, recibir fotos de cámaras y escalar alertas críticas de noche con Pushover
y llamadas por Twilio.

Los ajustes del **2026-11-26** cambian ese punto de partida:

- **Videovigilancia fuera del servidor** y **sin IoT en la 1.ª fase del servidor:** desaparece el envío de fotos/clips de
  cámaras y la alarma nocturna por sensores desde el servidor.
- **Permisos locales para gestionar la ausencia:** al dar facultades suficientes a un encargado en tienda (con umbrales
  en `Descuento`, `Devolucion` y `Merma`, permiso de traspasos, y manteniendo `VerUtilidad` y `VerReportes` separados si
  se desea; ver [permisos](../src/PapeleriaPos.Nucleo/Control/Permisos.cs)), la operación diaria de la caja ya no se
  detiene esperando respuesta remota del dueño.

Con esto, lo que realmente se evalúa para una etapa posterior queda en tres usos posibles:

1. **Avisos pasivos de control y anomalías** (cortes con diferencia, cancelaciones, salidas de efectivo, ventas bajo
   costo, cambios de precio o privilegios, crédito vencido, base dañada).
2. **Resumen diario de cierre y consultas bajo demanda** (`/resumen`).
3. *(Opcional)* **Autorizaciones excepcionales** que ni el encargado tenga permitidas en caja.

## Lo que ya existe en el código de Telegram

El módulo de Telegram ya está programado y probado con un doble simulado (sin internet), descrito en
[fase-1-telegram.md](fase-1-telegram.md):

- **Cola persistente y deduplicación** (`MensajesTelegram` en `AlertasTelegram.cs`): cada alerta nace en la misma
  transacción de SQLite que el movimiento o la bitácora (`RevisarBitacora`), evita duplicados por `Clave` y descarta
  avisos con más de 24 h sin poder enviarse.
- **24 tipos de alerta** (`TipoAlerta` en `Telegram.cs`): cubre corte, cancelación, devolución, cambio de precio, venta
  bajo costo, salida de efectivo, autorizaciones negadas, transferencias rechazadas, caja sin conexión, fuera de horario,
  resumen diario, impresoras, hojas faltantes, descuentos, ajustes/conteos, crédito, vencidos y base dañada.
- **Solo conexiones de salida** (`TelegramBot.cs` y `MotorTelegram.cs`): usa `getUpdates` (*long polling* de 25 s), por
  lo que no exige IP pública ni abrir puertos.
- **Autorización con botones inline** (`ServicioTelegram.cs`, `SolicitudRemota`): botones **Aprobar** / **Rechazar**
  con caducidad de 10 minutos y actualización del texto del mensaje tras responder.

### Limitaciones y deudas detectadas

1. **Vinculación por grupo (`notas.md`):** `VincularAsync` y `TelegramBot.LeerAsync` registran `chat.id`, pero no validan
   `from.id` ni bloquean grupos. Si se vinculara un grupo con personal, cualquiera vería la utilidad (`VerUtilidad`) y
   podría aprobar.
2. **Aprobación sin PIN:** basta apretar el botón desde el teléfono desbloqueado (`notas.md`); falta decidir si pide un
   PIN o confirmación adicional.
3. **Un solo receptor por bot:** la Bot API de Telegram solo admite un lector de `getUpdates` por token. Sin central, dos
   sucursales exigen dos bots distintos.
4. **Caducidad de 24 h en Telegram:** si el servicio pasa más de un día desconectado, las respuestas y comandos acumulados
   en los servidores de Telegram expiran sin entregarse.
5. **No rompe el modo «No molestar»:** Telegram no puede sonar como alerta crítica cuando el teléfono está en silencio.
6. **Fricción de adopción y textos en pausa:** si el dueño no usa Telegram en su rutina diaria, el bot corre riesgo de
   quedar silenciado o ignorado. Mientras sigue en pausa, la interfaz aún muestra botones como «Pedir por Telegram»
   (`plan.md`).

## Comparativa de alternativas (noviembre 2026)

| Criterio | Bot de Telegram | Panel Web propio (+ PWA / Web Push) | WhatsApp Cloud API | Correo (Brevo / Resend / SMTP) | Push dedicado (`ntfy` / Pushover) | Discord / Slack / Matrix / Signal |
|---|---|---|---|---|---|---|
| **Costo mensual / por mensaje** | **$0** (ilimitado, uso comercial permitido) | **$0** (vive en el mismo central de Oracle Cloud) | ~$0.1565 MXN por plantilla utilidad (1,000 respuestas gratis/mes si hubo mensaje en 24 h); requiere tarjeta y proveedor o revisión Meta | **$0** (Brevo: 300/día con pie de marca; Resend: 100/día y 3,000/mes; Gmail: ~500/día) | **`ntfy.sh`:** $0 con cuota diaria (o autoalojado gratis). **Pushover:** $99–105 MXN pago único | **Discord:** $0.<br>**Slack:** $0 (historial de 90 días).<br>**Matrix:** $0 autoalojado.<br>**Signal:** **sin API oficial** (riesgo de baneo) |
| **¿Exige dominio o HTTPS público?** | **No** (solo salida HTTPS hacia `api.telegram.org`) | **Sí dominio/HTTPS** para Web Push en iOS/Android; sin push corre 100 % dentro de **NetBird** | **Sí** (HTTPS público con dominio propio o Worker) | **No** (solo salida; Resend/Brevo piden dominio para remitente propio) | **No** (en servicio externo); autoalojado depende del central | **Discord/Slack:** no (WebSocket saliente).<br>**Matrix:** según servidor |
| **Botones interactivos y consultas** | **Completos** (botones inline, edición, `/resumen`) | **Completos** (toda la interfaz Blazor: reportes, bitácora, filtros) | Botones limitados en plantillas / 24 h; consultas por texto | **No** (solo lectura; enlace al panel) | Limitado en Android (solo notificaciones simples) | **Discord/Slack:** nativos completos.<br>**Matrix:** parcial según cliente |
| **Fricción para el dueño** | Media: obliga a instalar y atender Telegram | Baja-media: abre el panel cuando quiere; en iOS **Web Push exige «Agregar a pantalla de inicio»** | **Muy baja:** ya usa WhatsApp a diario | Baja para resumen nocturno; lenta para alertas en el momento | Media: app dedicada solo para alertas, sin ruido social | **Alta** en comercio en México (apps ajenas al día a día del dueño) |
| **Seguridad para utilidad y control** | Requiere corregir validación por usuario (`from.id`) y eventual PIN | **Alta** (sesión propia, passkey/PIN, sin intermediarios) | Media (queda mezclado en el chat personal del celular) | Baja-media (buzón suele reenviarse o compartirse) | Media (cuidado con vista previa en pantalla bloqueada) | Baja-media (riesgo de canales compartidos) |
| **Comportamiento en silencio (DND) y sin red** | Cola local 24 h en POS; no salta silencio | Web Push **no salta «No molestar»**; iOS duerme PWAs poco usadas | Retraso si el celular está sin datos; cola de Meta 7 días | Solo cuando consulta correo | Pushover sí permite alertas críticas en silencio (iOS/Android); `ntfy` en Android con excepción de DND | No saltan silencio por defecto |
| **Trabajo pendiente en código** | **~5 %** (corregir validación de grupo y decidir PIN) | **Ya planeado** como parte del panel central | **100 % nuevo** para alertas al dueño | **100 % nuevo** (muy simple por SMTP/API) | **100 % nuevo** (muy simple por HTTP POST) | **100 % nuevo** |

### Lectura preliminar de los candidatos

1. **Si basta con revisar resultados y auditar al encargado sin chats ni bots:** el **Panel Web del central** (eventualmente
   complementado con **un resumen diario por correo** o **alertas puntuales por `ntfy`/Pushover**) evita mantener bots,
   tokens y vinculaciones, y deja los permisos locales como única fuente de autorización operativa.
2. **Si se siguen queriendo avisos inmediatos en chat, `/resumen` o botones excepcionales:** **Telegram** sigue por encima
   de Discord, Slack, Matrix y Signal por costo $0, ligereza, retención, botones nativos y porque **la implementación ya
   está hecha en el POS** (solo faltaría cerrar la regla de grupos).
3. **WhatsApp para avisos internos del dueño:** solo conviene considerarlo más adelante si el dueño rechaza usar
   cualquier otra app *y* en una fase futura ya se contrató/aprobó la API oficial de WhatsApp para clientes.
4. **Signal** queda descartado por carecer de API oficial de bots y prohibir automatización comercial en sus términos.

## Preguntas abiertas para decidir más adelante

- [ ] Con un encargado con permisos suficientes en tienda, ¿el dueño necesita autorizar acciones desde el celular o le
      basta con **ver avisos/resumen** y auditar la bitácora en el panel?
- [ ] ¿El dueño está dispuesto a usar Telegram en su celular, o prefiere **cero bots** (solo panel web + correo o push
      simple)?
- [ ] Mientras Telegram sigue fuera de la 1.ª fase del servidor, ¿se ocultan en la interfaz de caja los textos de
      «Pedir por Telegram» (pendiente en [plan.md](plan.md#recomendaciones))?
- [ ] Si en una etapa posterior se activa Telegram, ¿se exige confirmar con un PIN al aprobar desde el bot además de
      restringir por usuario (`from.id`)?
