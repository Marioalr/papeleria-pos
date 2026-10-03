# Fase 1 — Caja

> **Propuesta para revisar.** Cierra lo que falta decidir antes de programar la caja. Lo que la caja debe hacer ya
> está en [caja y control](caja-y-control.md); aquí va cómo se construye, en qué orden y con qué datos.

## Entregas

La fase es grande, así que se parte en entregas. Cada una se prueba con la base importada de eleventa en una PC
aparte, sin tocar la operación de la tienda, hasta el piloto (fase 5).

| # | Entrega | Qué incluye |
|---|---|---|
| 1 | **Vender y cobrar en una caja** | Pantalla de venta, búsqueda y escaneo, ventas en espera, cobro mixto, ticket con logo y QR, cajón, turno con fondo, retiros y gastos, corte ciego (X y Z), entrada con usuario y PIN |
| 2 | **Control** | Roles configurables, autorizaciones con PIN, bitácora, cancelaciones y devoluciones escaneando el ticket, verificar transferencias |
| 3 | **Varias cajas** | La caja principal como servidor de la sucursal, las demás cajas por red, contingencia con bandeja de salida |
| 4 | **Telegram** | Alertas, autorización a distancia y resumen diario |
| 5 | **Módulo de impresión** | Lo que hoy hace Control Print ([detalle](impresion.md)) |

Este documento detalla la entrega 1; las demás se cierran cuando les toque.

## Entrega 1: cómo se arma

```
 PC de la caja ─────────────────────────────────────────────────────────────
 │                                                                         │
 │  PapeleriaPos.Caja (ventana de Windows)        PapeleriaPos.Servicio    │
 │  ┌──────────────────────────────┐   HTTP      ┌──────────────────────┐  │
 │  │ PapeleriaPos.Interfaz        │──localhost─▶│ API + reglas         │  │
 │  │ (pantallas Blazor)           │             │ único que escribe    │  │
 │  │ impresión ESC/POS ──▶ POS-8360│             │ SQLite (WAL)         │  │
 │  └──────────────────────────────┘             └──────────────────────┘  │
 ───────────────────────────────────────────────────────────────────────────
```

- **La caja habla con el servicio por la API desde el primer día,** aunque estén en la misma PC. Así la entrega 3
  (varias cajas por red) no obliga a rehacer nada.
- **Proyectos nuevos:**
  - `PapeleriaPos.Interfaz`: componentes Blazor, compartidos después con el panel y la app Android.
  - `PapeleriaPos.Servicio`: ASP.NET Core; en la entrega 1 corre como programa, y como servicio de Windows desde la
    entrega 3.
  - `PapeleriaPos.Caja`: ventana WPF con WebView2, que muestra la interfaz e imprime.
- **La impresora la maneja la caja,** no el servicio: está conectada a esa PC.

## Ticket

**Impresora:** POS-8360, térmica de 80 mm, comandos ESC/POS. Son 576 puntos de ancho: 48 caracteres por línea con la
letra normal. El ticket se manda directo con comandos, no como documento de Windows: sale más rápido y permite el QR.

Mismo orden que el ticket de eleventa, para que nadie extrañe nada:

```
            [ logo de la tienda ]
         NOMBRE DE LA PAPELERÍA
        Este ticket se expide en
         Calle, número y colonia
             Teléfono
 ──────────────────────────────────────────────
 Folio A-000123            03/10/2026  12:47
 Caja 1                   Atendió: Ana
 ──────────────────────────────────────────────
 Cant.  Descripción                     Importe
 ══════════════════════════════════════════════
 2      Nylon C10 x metro                $120.00
 1      Goma M40 Pelikan                   $5.00
 8      Impresión blanco y negro           $8.00
 0.5 m  Listón 2.5 cm rojo                 $4.00
 ──────────────────────────────────────────────
                     No. de artículos:     4
                                Total: $137.00
                 Efectivo: $200.00  Cambio: $63.00
                      (precios con IVA incluido)
 ──────────────────────────────────────────────
              [ QR del folio ]
          Gracias por su compra
     Síguenos en nuestras redes sociales
```

- Encabezado, pie y logo se configuran (los mismos textos que hoy tiene eleventa).
- **Logo:** se convierte una vez a blanco y negro al ancho del papel y se manda con cada ticket.
- **QR con el folio:** sirve para devoluciones y, más adelante, para encontrar pedidos.
- **Cajón:** se abre con el comando de la impresora al cobrar en efectivo, y cada apertura queda registrada.
- Las descripciones largas pasan a la siguiente línea; el importe siempre va alineado a la derecha.

## Pantalla de venta

Diseñada para 1366×768, todo con teclado y letra grande.

```
┌──────────────────────────────────────────────────────────────────────────────────┐
│ Matriz · Caja 1 · Ana · Turno desde 08:02                     En espera: 1  [F6] │
├──────────────────────────────────────────────────────────────────────────────────┤
│ Código o búsqueda: [ 7501015213247__________________ ]                           │
├──────┬──────────────────────────────────────────────┬────────────┬───────────────┤
│ Cant │ Descripción                                  │     Precio │       Importe │
├──────┼──────────────────────────────────────────────┼────────────┼───────────────┤
│    2 │ Nylon C10 x metro                            │     $60.00 │       $120.00 │
│    1 │ Goma M40 Pelikan                             │      $5.00 │         $5.00 │
│▶   8 │ Impresión blanco y negro                     │      $1.00 │         $8.00 │
├──────┴──────────────────────────────────────────────┴────────────┴───────────────┤
│                                                     4 artículos     $133.00      │
├──────────────────────────────────────────────────────────────────────────────────┤
│ F2 Buscar  F3 Cantidad  Supr Quitar  F5 Espera  F6 Recuperar  F12 Cobrar         │
└──────────────────────────────────────────────────────────────────────────────────┘
```

- El lector escribe en el cuadro de búsqueda; si el código existe, se agrega solo.
- Escribir texto busca por nombre, código o marca sin importar acentos, y muestra resultados mientras se escribe.
- `*` antes del código cambia la cantidad (`3*7501015213247`), como en muchas cajas.
- **Cobro:** total grande; efectivo recibido y cambio; se puede repartir entre efectivo, tarjeta y transferencia.
  La transferencia queda **por verificar**, y la tarjeta guarda la referencia de la terminal.

## Turno y corte ciego

1. **Abrir turno:** el cajero entra con su usuario y PIN y cuenta el fondo.
2. **Durante el turno:** retiros y gastos de caja, siempre con motivo.
3. **Corte X (parcial):** informativo; no cierra el turno.
4. **Corte Z (cierre):** el cajero cuenta el efectivo por denominación **sin ver cuánto debería haber.** El sistema
   compara con lo esperado (fondo + efectivo cobrado − cambio − retiros − gastos), guarda la diferencia y la marca si
   pasa del umbral. Tarjeta y transferencias se listan aparte.

## Datos nuevos

Se suman a los del [modelo de la fase 0](modelo-datos.md), con las mismas reglas (UUIDv7, centavos, milésimas, UTC).

| Tabla | Campos principales |
|---|---|
| **Usuario** | Nombre, usuario, hash del PIN, rol (Dueño, Encargado, Cajero), sucursal, activo, intentos fallidos, bloqueado hasta |
| **Caja** | Sucursal, nombre, serie de folios (A, B…), es principal |
| **Turno** | Caja, cajero, abierto y cerrado (UTC), fondo |
| **MovimientoCaja** | Turno, tipo (retiro, gasto), monto, motivo, quién |
| **Venta** | Sucursal, caja, turno, cajero, serie y folio (A-000123), estado (en captura, en espera, pagada, cancelada), total, IVA desglosado, fechas |
| **VentaLinea** | Venta, variante, presentación, descripción tal como se vendió, cantidad, precio, importe, costo al momento de vender |
| **Cobro** | Venta, forma (efectivo, tarjeta, transferencia), monto, recibido y cambio, referencia, verificación (por verificar, verificada, rechazada) |
| **Corte** | Turno, tipo (X o Z), esperado por forma de pago, contado, diferencia, quién |
| **CorteConteo** | Corte, denominación, cantidad |
| **AperturaCajon** | Caja, turno, quién, motivo (venta o sin venta), fecha |

- Cada línea vendida genera su salida en el [kardex](modelo-datos.md#kardex), en la misma transacción que la venta.
- El folio sale de un contador por caja (la tabla `Secuencias` ya existe).
- El IVA se desglosa de precios que ya lo incluyen, según el impuesto de cada producto: con IVA 16 %, base =
  importe ÷ 1.16; con 0 % o exento, todo es base.

## Preguntas

1. **Turnos:** ¿cuántos hay al día y quién hace el corte, el cajero o el encargado?
2. **Varios cajeros en una caja:** ¿comparten el turno o cada uno tiene el suyo? La propuesta es un turno por cajero
   (cada quien responde por su efectivo), con cambio de cajero = corte del que sale.
3. **Teclas:** ¿copiamos las de eleventa para que los cajeros no tengan que aprender otras? Una foto de la barra de
   teclas de eleventa ayudaría. La pantalla de arriba usa F12 para cobrar, como eleventa.
4. **Fondo de caja:** ¿es siempre el mismo monto?
5. **Logo:** el archivo del logo que sale en el ticket.
