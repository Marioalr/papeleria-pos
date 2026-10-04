# Fase 1, entrega 4 — Telegram

> **Revisado.** El dueño se entera de lo importante y autoriza sin estar en la tienda. Parte de las
> [alertas](caja-y-control.md#alertas-al-dueño-telegram) y de la [gestión por excepción](servidor-remoto.md#gestión-por-excepción).

**Avance:** ya funciona todo, probado con un Telegram simulado (sin internet):

- vincular el chat del dueño con `/vincular` y el código de la caja;
- alertas sacadas de la bitácora, también lo que llega con la bandeja de una caja sin conexión;
- autorización a distancia con botones Aprobar / Rechazar;
- resumen diario con el último corte Z, y `/resumen` a cualquier hora;
- avisos de caja sin conexión y fuera de horario;
- Configuración → Telegram.

Falta probarlo con el bot de verdad.

**Instalar el bot:**

1. Crear el bot con @BotFather.
2. Guardar su token en `C:\ProgramData\PapeleriaPos\telegram-token.txt`, en la caja principal. También se puede
   poner en `Telegram:Token` o `Telegram:ArchivoToken`.
3. Reiniciar el servicio.

## Qué incluye

- **Un bot de Telegram propio** del negocio. Lo crea el dueño con @BotFather (es gratis); el token se guarda en la
  configuración de la caja principal, nunca en el repositorio.
- **Vincular** el celular del dueño (el único que recibe mensajes y autoriza por Telegram): desde la caja, con su
  usuario y PIN.
- **Alertas en el momento:** lo que el dueño quiere saber al instante (ver abajo).
- **Autorización a distancia:** en la ventana "requiere autorización", un botón para pedirla por Telegram. Llega con el
  detalle y botones Aprobar / Rechazar.
- **Resumen diario** al cierre: ventas, utilidad, cortes y lo que pasó en el día.

## Cómo funciona

```
 Caja ──► Servicio de la caja principal ──(sale a internet)──► Telegram ──► celular del dueño
                    ▲                                                          │
                    └─────────── respuesta (Aprobar / Rechazar) ◄──────────────┘
```

- **Solo conexiones de salida.** El servicio le pregunta a Telegram cada pocos segundos si hay respuestas (no
  necesita IP pública ni abrir puertos). Telegram solo deja un receptor por bot, así que el bot vive en la caja
  principal; cuando llegue el central (fase 6), se muda ahí y reparte a cada sucursal.
- **Cola de salida.** Cada mensaje se guarda antes de mandarse; si no hay internet, sale cuando vuelva, sin repetirse.
- **Solo chats vinculados.** El bot ignora a cualquiera que no se haya vinculado, aunque encuentre el bot.

### Vincular un celular

1. En la caja, Configuración → Telegram: el usuario escribe su usuario y PIN y la caja muestra un código de 6 dígitos
   (vale 10 minutos).
2. En Telegram, le escribe al bot `/vincular 123456`.
3. El bot contesta "Listo, Ana: aquí te llegarán las alertas". Desde ahí, ese chat es de ese usuario: si autoriza,
   queda en la bitácora con su nombre.

El dueño puede desvincular un chat desde la misma pantalla (por ejemplo, si cambia de celular).

### Autorizar a distancia

1. El cajero intenta algo que requiere autorización (cancelar un ticket, cambiar un precio…). En la ventana de
   autorización aparece **"Pedir por Telegram"**, además del PIN.
2. Al dueño le llega:
   *"Caja 2 · Ana pide cancelar el ticket A-000123 por $245.00. Motivo: el cliente se arrepintió."* con botones
   **Aprobar** y **Rechazar**.
3. La caja muestra "Esperando respuesta…" (se puede cancelar). Si el dueño tiene más de un chat vinculado, el primero
   que contesta decide y en los demás se actualiza el mensaje.
4. Al aprobar, la caja sigue sola, con la misma autorización de un solo uso que da el PIN. Si nadie contesta en 10
   minutos, la solicitud vence.
5. Aprobada o rechazada, queda en la bitácora: quién la pidió, quién contestó y que fue por Telegram.

Sin conexión con la caja principal (contingencia) no se puede pedir por Telegram: queda el PIN.

## Alertas

| Alerta | Cuándo | Propuesta |
|---|---|---|
| Diferencia en el corte | Al cerrar un turno, si el faltante o sobrante pasa del umbral | $50 |
| Cancelación de ticket | Cada una | Siempre |
| Devolución | Arriba del umbral | $200 |
| Cambio de precio | Cada uno, con quién lo autorizó | Siempre |
| Venta debajo del costo | Cada una | Siempre |
| Salida de efectivo | Arriba del umbral | $500 |
| Autorizaciones negadas | PIN incorrecto o usuario bloqueado al autorizar | Siempre |
| Transferencia rechazada | Al rechazarla el encargado | Siempre |
| Transferencias sin verificar | En el resumen del cierre | Siempre |
| Caja sin conexión | Una caja lleva más de N minutos en contingencia, o algo no pudo subir | 10 minutos |
| Caja abierta fuera de horario | Un turno abierto antes o después del horario de la sucursal | 8:00 a 21:00 (configurable) |

Cada alerta se puede apagar o cambiar su umbral desde Configuración → Telegram (solo el dueño). Las de inventario
(existencia negativa, conteos) llegan con la fase 2.

## Resumen diario

Al hacer el último corte Z del día (cuando ya no queda ningún turno abierto en la sucursal):

- Ventas y número de tickets, por caja; utilidad bruta (venta − costo de lo vendido).
- Cada corte: esperado, contado y diferencia.
- Cancelaciones, devoluciones, cambios de precio y salidas de efectivo del día.
- Transferencias por verificar.
- Ventas hechas sin conexión y lo que no pudo subir.

## Cómo se arma

| Pieza | Qué cambia |
|---|---|
| `PapeleriaPos.Servicio` | Tarea de fondo que habla con Telegram (manda la cola y lee respuestas); endpoints para vincular y pedir autorización |
| `PapeleriaPos.Datos` | Alertas que se disparan al guardar (corte, cancelación, salida…); armado del resumen |
| `PapeleriaPos.Interfaz` | Configuración → Telegram (vincular, alertas y umbrales); "Pedir por Telegram" en la ventana de autorización |
| Datos nuevos | Chat vinculado (usuario, chat, fecha), solicitud de autorización remota (estado, quién contestó), mensaje en cola (texto, enviado), ajustes de alertas por sucursal |

**Pruebas:** con un Telegram falso en las pruebas automáticas (sin internet), y al final con el bot de verdad.

## Decisiones de la revisión

| Pregunta | Respuesta | Efecto |
|---|---|---|
| Quién recibe y autoriza | Solo el dueño | Solo se vinculan chats del dueño; las autorizaciones remotas las contesta él |
| Umbrales | Los propuestos | Corte $50, devolución $200, salida $500, caja sin conexión 10 min; todos configurables |
| Resumen diario | Con el último corte Z del día | Sale cuando ya no queda turno abierto en la sucursal |
| Horario | Simulado y configurable | 8:00 a 21:00 por omisión; se cambia en Configuración → Telegram |
| Espera de una autorización remota | 10 minutos | Después vence y la caja vuelve a pedir PIN o a pedirla de nuevo |
| El bot | Lo crea el dueño con @BotFather | El token va en un archivo fuera del repositorio; las pruebas usan un Telegram simulado |
