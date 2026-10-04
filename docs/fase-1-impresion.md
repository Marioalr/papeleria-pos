# Fase 1, entrega 5 — Módulo de impresión

> **Propuesta para revisar.** Lo que hoy hace [Control Print](https://github.com/Marioalr/control-print) (v1.12),
> dentro del POS: cada impresión de la WF-6590 se mide, se le pone precio y se cobra en el mismo ticket. Parte del
> [módulo de impresión](impresion.md). Aquí también se definen los **descuentos** que quedaron pendientes en la
> [entrega 2](fase-1-control.md).

## Qué se trae de Control Print

Control Print ya resolvió lo difícil; se trae su núcleo y se deja lo que el POS ya tiene.

| Se trae (se adapta al POS) | Lo reemplaza el POS |
|---|---|
| Lectura del spool EMF y del PrintTicket (papel, tamaño, color, dúplex, calidad) | Su base SQLite propia |
| Medición de tinta por página a 300 dpi y miniaturas | Sus usuarios, contraseñas y sesiones |
| Modelo de costo y precio: tinta, papel, mantenimiento, luz, depreciación, margen, mayoreo, cobro mínimo, redondeo | Su modo servidor y su protocolo de red |
| Calibración con los niveles reales de tinta | Sus reportes y su exportación a Excel |
| Lecturas SNMP: niveles, contadores, estado; verificación de las hojas que salieron (atascos y cancelaciones) | Su instalador y sus actualizaciones |
| Copias en el panel registradas a mano y su comparación con el contador | |
| Cotizar un PDF antes de imprimir | |

## Cómo encaja

```
 PC con la impresora (cada caja que imprime)        Caja principal (servicio)
 ┌──────────────────────────────────────────┐       ┌───────────────────────────────────────┐
 │ Caja (ventana de Windows)                │       │ Trabajos de impresión (base del POS)  │
 │  agente: vigila la cola de la WF-6590,   │──API─►│ Costos y precios de la sucursal       │
 │  mide cada trabajo y lo manda            │       │ Lecturas de la impresora (SNMP):      │
 │  (con la bandeja si no hay conexión)     │       │  niveles, contador, hojas reales      │
 └──────────────────────────────────────────┘       │ Calibración · avisos por Telegram     │
                                                    └───────────────────────────────────────┘
```

- **El agente vive en la caja:** la ventana de Windows ya corre en cada PC. Como Control Print, solo observa la cola
  (con "conservar documentos impresos"), lee cada trabajo cuando terminó y lo quita de la cola. Nunca pausa ni altera
  la impresión.
- **La caja principal lee la impresora** (una sola vez para todas las PC): niveles, contadores y la verificación de
  hojas.
- **Los costos solo los ve quien tiene permiso,** aplicado en el servicio: el cajero ve el precio, no el costo ni la
  ganancia.

## En la caja

- **Impresiones por cobrar (F2):** la cola de lo impreso que falta cobrar, de todas las PC, con miniatura, nombre
  del documento, páginas, color o B/N, quién lo imprimió y precio. Se elige y entra al ticket como una línea
  ("Impresión: Tarea de historia, 12 págs color") junto con lo demás que lleva el cliente.
- **Al cobrar el ticket**, esas impresiones quedan cobradas. Si se cancela o se devuelve, vuelven a estar por cobrar
  (o se marcan como no vendidas).
- **No vendida**, con motivo obligatorio (no era lo que quería, salió mal, atasco, otro): no se cobra y su costo
  queda como pérdida en el resumen.
- **Copias en el panel de la impresora:** se registran al cobrarlas (cuántas, color, tamaño, dúplex) y se comparan
  con el contador de la impresora: lo que contó la impresora y nadie cobró aparece en el corte y en el resumen del día.
- **Precio especial** de Control Print → se vuelve un **descuento** (ver abajo).

## Descuentos

Propuesta para cerrar lo pendiente de la entrega 2:

| Tipo | Cómo | Regla propuesta |
|---|---|---|
| Descuento en una línea o en todo el ticket | Porcentaje o monto, con motivo | Cajero hasta 10 % sin autorización; más, encargado |
| Precio especial en impresiones | El margen especial de Control Print (50 % por omisión), con el nombre del cliente | Permitido; queda en la bitácora con el nombre |
| Clientes especiales | Lista que administra el dueño (maestros, negocios vecinos) con su descuento | Se elige de la lista; no se escribe a mano |

Todo descuento va a la bitácora y, arriba del umbral, avisa por Telegram (como el cambio de precio). El descuento
nunca deja la línea debajo del costo sin autorización ("vender debajo del costo").

## Partes

| Parte | Contenido |
|---|---|
| **1** | Núcleo traído de Control Print (medición y costos); agente en la caja; trabajos en la base del POS; F2 Impresiones por cobrar; cobrarlas en el ticket; no vendidas; costos solo con permiso; configuración de costos (dueño) |
| **2** | La impresora desde la caja principal: niveles y contadores, verificación de hojas, calibración, avisos de consumibles por Telegram; copias en el panel con su comparación; costo real de las impresiones en la utilidad del día |
| **3** | Descuentos (línea, ticket, precio especial, clientes especiales) |
| Después | Cotizar PDF y cotizaciones guardadas (con los pedidos, fase 4); celular y copias detectadas por contador; documentos comunes (CURP, recibo de luz); varias impresoras (sucursal 2) |

## Preguntas

1. **Alcance y orden:** ¿te parecen las tres partes y dejar la cotización de PDF para la fase 4 (pedidos)?
2. **Historial de Control Print:** ¿se importa (trabajos, cotizaciones) o solo la configuración de costos y la
   calibración, y el historial se queda consultable en Control Print?
3. **Mientras se prueba:** Control Print sigue en uso y ambos no pueden vigilar la misma cola (los dos quitan los
   trabajos). Propuesta: probar el POS con la cola de laboratorio de Control Print (salida a archivo, sin papel) o en
   una PC donde no esté Control Print. ¿Cuál prefieres?
4. **Quién ve costos y ganancia:** ¿solo el dueño, o también el encargado?
5. **Descuentos:** ¿la regla de la tabla (cajero hasta 10 %, más con encargado) y la lista de clientes especiales
   te sirven? ¿El descuento se permite en todo o solo en algunas cosas (por ejemplo, no en recargas)?
6. **Precio de impresiones en el ticket:** ¿una línea por trabajo (recomendado, para saber qué se cobró) o una sola
   línea "Impresiones" con el total?
