# Fase 1, entrega 5 — Módulo de impresión

> **Revisado.** Lo que hoy hace [Control Print](https://github.com/Marioalr/control-print) (v1.12),
> dentro del POS: cada impresión de la WF-6590 se mide, se le pone precio y se cobra en el mismo ticket. Parte del
> [módulo de impresión](impresion.md). Aquí también se definen los **descuentos** que quedaron pendientes en la
> [entrega 2](fase-1-control.md).

## Avance

**Parte 1 (hecha):**

- **Núcleo traído de Control Print:**
  - `PapeleriaPos.Medicion` calcula costos y precio; lo usa el servicio.
  - `PapeleriaPos.Medicion.Windows` lee el spool y mide la tinta.
  - Las pruebas de Control Print vienen junto con el código.
- **Agente en la caja de cada PC** (`ColaImpresion` en la configuración de la caja). Manda cada trabajo por la bandeja, sin que la caja pase a modo sin conexión.
- **F2 Impresiones por cobrar:** de todas las PC, con no vendidas que llevan motivo. El costo solo lo ve el dueño.
- **Ticket:** una sola línea "Impresiones". Si se cancela o se devuelve, los trabajos vuelven a estar por cobrar.
- **Importar la configuración de Control Print:** `importar-control-print`.

**Parte 2 (hecha):**

- **Cotizar a un botón (CTRL+Q):** se elige el PDF y sale el precio. La caja de Windows mide la tinta de cada página con el motor de PDF de Windows, igual que una impresión, y la caja principal pone el precio. Cada opción recalcula al instante: color, copias, páginas, tamaño, papel, calidad, páginas por hoja y doble cara. "Copiar resumen" deja el texto para WhatsApp, sin costos. El dueño ve además el costo y la ganancia. Solo funciona en la ventana de Windows: el navegador no tiene con qué medir el PDF.
- **La impresora desde la caja principal:** lee por la red (SNMP y la página de estado) los niveles de tinta, la caja de mantenimiento y el contador.
  - Lee cada 5 minutos y poco después de cada impresión.
  - La IP se escribe en Configuración → Impresora; `importar-control-print` la trae si hay una sola sucursal.
- **Verificación de hojas:** con el contador se sabe cuántas caras salieron de verdad. Si faltan (un atasco o una cancelación en el panel), F2 lo dice y ofrece "Cobrar lo que salió", y el costo baja a lo que se gastó.
  - Si la verificación tiene dudas, no marca nada: puede no ver un faltante, pero nunca inventarlo.
  - Necesita la hora en que cada trabajo entró a la cola, que manda el agente.
- **Copias del panel (en F2):** cuántas, color, tamaño y doble cara. Se sugiere el precio con la tinta típica y se puede cobrar otro. Entran a la línea "Impresiones".
- **Contador contra lo cobrado:** la pestaña Impresora y el resumen del día dicen cuántas caras contó la impresora sin que nadie las registrara: copias sin cobrar, el celular o páginas de prueba.
- **Calibración:** compara lo que bajaron los niveles con lo que estimó el cálculo. El dueño la aplica con su PIN; no es automática, porque con la cola de laboratorio lo registrado no sale de la impresora.
- **Avisos por Telegram:**
  - una tinta o la caja de mantenimiento en el umbral (10 % por omisión), y cuando se cambia un consumible;
  - la impresora sin responder 30 minutos en horario;
  - una impresión incompleta.
- **Desglose de la línea "Impresiones":** en la pestaña Impresora, todo lo impreso en el día y cómo se cobró. Lo ven el dueño y el encargado; el costo, solo el dueño.
- **El resumen del día** suma las impresiones no vendidas (su costo) y el contador.

**Parte 3 (hecha): descuentos en la mercancía**

- **CTRL+D Descuento:** al renglón seleccionado o a todo el ticket, en porcentaje o en monto, siempre con motivo. Un monto en todo el ticket se reparte entre los artículos en proporción a su importe. Las impresiones no llevan descuento (su precio especial va aparte).
- **La regla "Hacer un descuento"** (Configuración → Reglas): el cajero da hasta 10 % solo; más, con autorización del encargado o del dueño (con PIN o por Telegram). El encargado no tiene tope.
- **Clientes especiales** (Configuración → Clientes especiales): los da de alta el dueño con su descuento. En la caja se eligen de la lista ("A todo el ticket"), no se escriben a mano, y no piden autorización. Lo que se agregue después al ticket también lleva su descuento. Quitar a un cliente lo deja inactivo.
- **Nunca debajo del costo sin autorización:** si un descuento deja un artículo debajo de lo que cuesta, se pide la autorización de "Vender debajo del costo". La caja no ve el costo: solo cuáles artículos y cuánto falta.
- **Bitácora y Telegram:** todo descuento queda en la bitácora con su motivo. Cuando pasó de lo que el cajero da solo, te avisa por Telegram. El resumen del día dice cuántos tickets llevaron descuento y cuánto sumaron.
- **El ticket impreso** pone el descuento debajo de cada artículo y "Usted ahorró" antes del total. Una devolución regresa lo que se cobró, ya con el descuento.
- **Sin conexión:** la regla y los clientes especiales funcionan con la copia de la caja y el descuento sube con la venta. Lo que no se revisa sin conexión es el costo.

**Mientras se prueba con la cola de laboratorio:** el POS registra impresiones que no salen de la impresora y la impresora cuenta lo que manda Control Print. La comparación con el contador y la calibración no sirven hasta pasar a la cola real. Los niveles y los avisos de tinta sí.

**Probar con la cola de laboratorio** (sin papel, sin tocar la cola real de Control Print):

1. Como administrador, correr `research\lab-setup.ps1` de Control Print. Crea "ControlPrint Lab (WF-6590)" con el mismo driver y salida a archivo.
2. En `C:\ProgramData\PapeleriaPos\caja.json` de esa PC, poner `"ColaImpresion": "ControlPrint Lab (WF-6590)"`.
3. Importar los costos y la impresora:

   ```
   dotnet run --project src/PapeleriaPos.Herramientas -- importar-control-print --base <base del POS>
   ```

4. Imprimir algo en la cola de laboratorio desde esa PC: aparece en F2 con su precio.

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
