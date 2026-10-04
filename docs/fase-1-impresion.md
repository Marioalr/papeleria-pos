# Fase 1, entrega 5 — Módulo de impresión

> **Revisado.** Lo que hoy hace [Control Print](https://github.com/Marioalr/control-print) (v1.12),
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
- **Precio especial** de impresiones: aparte, cuando el cálculo esté afinado.

## Descuentos

Propuesta para cerrar lo pendiente de la entrega 2:

| Tipo | Cómo | Regla propuesta |
|---|---|---|
| Descuento en una línea o en todo el ticket | Porcentaje o monto, con motivo | Cajero hasta 10 % sin autorización; más, encargado |
| Precio especial en impresiones | Aparte: espera a que el cálculo de impresiones esté afinado | — |
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

## Decisiones de la revisión

| Pregunta | Respuesta | Efecto |
|---|---|---|
| Alcance | Las tres partes, pero cotizar tiene que estar a un botón | "Cotizar" entra en esta entrega (parte 2), no en la fase 4 |
| Historial de Control Print | Solo la configuración | Se importan costos, márgenes y calibración; los trabajos se quedan en Control Print |
| Mientras se prueba | Con la cola de laboratorio | El agente del POS vigila la cola de laboratorio (salida a archivo, sin papel); Control Print sigue con la real |
| Costos y ganancia | Solo el dueño | El servicio no se los manda a nadie más |
| Descuentos | La regla y los clientes especiales sirven; las impresiones van aparte | Los descuentos (parte 3) son para la mercancía; el precio especial de impresiones espera a que el cálculo esté afinado |
| En el ticket | Una sola línea "Impresiones" | El ticket lleva el total de las impresiones; el desglose por trabajo lo ven el dueño y el encargado |

Con esto, las partes quedan así:

| Parte | Contenido |
|---|---|
| **1** | Núcleo traído de Control Print; agente en la caja (cola de laboratorio); trabajos en la base del POS; F2 Impresiones por cobrar; una línea "Impresiones" en el ticket; no vendidas; costos solo para el dueño; importar la configuración de Control Print |
| **2** | Cotizar con un botón; la impresora desde la caja principal (niveles, contadores, verificación de hojas, calibración, avisos por Telegram); copias en el panel con su comparación |
| **3** | Descuentos en la mercancía (línea, ticket, clientes especiales) |
