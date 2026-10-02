# Pedidos

Un **pedido** es todo lo que entra, espera y se cobra después:

- una impresión mandada por WhatsApp
- un engargolado o enmicado
- un trámite
- una lista escolar
- un apartado

Todos funcionan igual: tienen líneas de productos y servicios, archivos, estado, quién lo tomó, charola donde quedó
físicamente y anticipos. Al entregarse se convierte en venta (ticket) y se descuentan los anticipos.

## Estados

```
Recibido ─→ Esperando cliente ────────────────────────────────┐
        └─→ Por preparar → En preparación (Ana) → Listo (charola C-3) → Entregado y cobrado
                                                          └─→ No recogido (N días) → merma
```

## El celular y los clientes registrados

- **El celular identifica el pedido, no al cliente.** Sirve para encontrarlo cuando van a recogerlo. No se crea un
  cliente por cada número.
- Después de entregado el pedido, y pasados N días (configurable), el número se borra; solo quedan los últimos 4
  dígitos en el historial.
- **Clientes registrados:** pocos, los de confianza, dados de alta a mano por el encargado o el dueño. Sus privilegios
  se configuran por cliente:
  - pedidos sin anticipo (por ejemplo, imprimir un trabajo grande)
  - crédito, con límite y estado de cuenta
  - apartar sin pagar
  - *por definir qué más*
- **No registrados (regla configurable):** se prepara sin anticipo hasta cierto monto. Arriba de eso, el pedido queda
  en *Esperando cliente* (el archivo ya está en el sistema y cotizado, pero no se imprime) hasta que llega o deja
  anticipo.

## Quién lo prepara

- **Una sola cola por sucursal,** visible en todas las cajas.
- El sistema ofrece el pedido a la caja que no tiene una venta en curso.
- El cajero presiona **Tomar** y el pedido queda a su nombre ("Lo prepara Ana"). Nadie más puede imprimirlo.
- Si nadie lo toma en cierto tiempo, se resalta y se avisa.
- Se imprime **desde el pedido.** El trabajo llega a la impresora con el folio en el nombre (`P-0042 · archivo.pdf`) y
  Control Print lo liga solo, con su costo real.

## Listo y recogida

Al marcarlo **Listo**:

1. Se asigna una **charola numerada** (C-3) donde se deja el trabajo.
2. La impresora de tickets saca una **etiqueta** para engrapar: folio, nombre, últimos 4 dígitos del celular, total y
   QR.
3. Si se quiere, se avisa por WhatsApp: "Tu pedido está listo, total $X".

**Cuando llega el cliente,** cualquier cajero libre teclea los últimos 4 dígitos del celular, el nombre, o escanea el
QR. Ve el estado, la charola, el total, el anticipo, el saldo y quién lo preparó. **Cobrar** lo agrega al ticket.

No importa si quien lo preparó está ocupado: el pedido vive en el servidor, no en el cajero.

Requisito físico: charolas o casilleros numerados detrás del mostrador.

## No recogidos

- Pasados N días (configurable) el pedido pasa a **No recogido**.
- Su costo se registra como merma y sus archivos se borran.

## Archivos

- Se guardan en la caja principal mientras el pedido está activo.
- Se borran N días después de entregado o no recogido.
- Queda solo la huella para detectar repetidos, nunca el contenido. La CURP, la INE y las actas son datos personales:
  se necesita un aviso de privacidad.

## Documentos repetidos

Evitan imprimir de más cuando dos personas mandan lo mismo, o la misma persona lo manda dos veces.

- **Mismo archivo:** huella SHA-256. Aviso: "ya se imprimió en P-0042 a las 10:32".
- **Mismo contenido en otro archivo** (la CURP descargada de nuevo, un PDF exportado otra vez): huella visual de cada
  página. Control Print ya dibuja cada página a 300 dpi para medir la tinta, así que sacarla cuesta casi nada. Funciona
  también con lo que se imprime directo desde Word, sin pasar por un pedido.
- Si se reimprime de todas formas, se pide motivo (y, según configuración, autorización).
- Después, el detector de documentos comunes de Control Print (CURP, recibo de luz) sugiere el servicio que
  corresponde cobrar.
