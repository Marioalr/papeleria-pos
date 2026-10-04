# Fase 1, entrega 4 — Telegram

> **Propuesta para revisar.** El dueño se entera de lo importante y autoriza sin estar en la tienda. Parte de las
> [alertas](caja-y-control.md#alertas-al-dueño-telegram) y de la [gestión por excepción](servidor-remoto.md#gestión-por-excepción).

## Qué incluye

- **Un bot de Telegram propio** del negocio. Lo crea el dueño con @BotFather (es gratis); el token se guarda en la
  configuración de la caja principal, nunca en el repositorio.
- **Vincular** a quien recibe mensajes y autoriza: desde la caja, con su usuario y PIN.
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
2. A quienes pueden autorizar esa acción (el dueño para cambiar precios; encargado o dueño para lo demás) les llega:
   *"Caja 2 · Ana pide cancelar el ticket A-000123 por $245.00. Motivo: el cliente se arrepintió."* con botones
   **Aprobar** y **Rechazar**.
3. La caja muestra "Esperando respuesta…" (se puede cancelar). El primero que contesta decide; a los demás se les
   actualiza el mensaje ("Lo aprobó Luis").
4. Al aprobar, la caja sigue sola, con la misma autorización de un solo uso que da el PIN. Si nadie contesta en 5
   minutos, la solicitud vence.
5. Aprobada o rechazada, queda en la bitácora: quién la pidió, quién contestó y que fue por Telegram.

Sin conexión con la caja principal (contingencia) no se puede pedir por Telegram: queda el PIN.

## Alertas propuestas

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
| Caja abierta fuera de horario | Un turno abierto antes o después del horario de la sucursal | Con el horario |

Cada alerta se puede apagar o cambiar su umbral desde Configuración → Telegram (solo el dueño). Las de inventario
(existencia negativa, conteos) llegan con la fase 2.

## Resumen diario

Al hacer el último corte Z del día (o a una hora fija, si alguna caja se quedó abierta):

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

## Preguntas

1. **Quién:** ¿solo el dueño recibe alertas y autoriza por Telegram, o también los encargados (cada uno las de su
   sucursal)?
2. **Umbrales:** ¿te parecen los de la tabla (corte $50, devolución $200, salida $500, caja sin conexión 10 min)?
   ¿Alguna alerta sobra o falta?
3. **Resumen y horario:** ¿el resumen sale con el último corte Z del día o a una hora fija? ¿Cuál es el horario de la
   tienda (para "abierta fuera de horario")?
4. **Esperar respuesta:** ¿5 minutos para que alguien conteste una autorización remota está bien?
5. **El bot:** hay que crearlo con @BotFather desde el Telegram del dueño (yo no puedo crear cuentas). Para probar,
   guarda el token en un archivo fuera del repositorio (por ejemplo `C:\Users\salva\Documents\TPV\telegram-token.txt`)
   y lo leo de ahí; no hace falta pegarlo en el chat.
