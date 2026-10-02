# Servicios y costeo

Hoy, en eleventa, el costo de un servicio se suma a mano y se queda congelado. Si sube la mica o cambia el tiempo que
toma un trámite, nadie se entera. Aquí cada servicio tiene una **receta** y su costo se calcula solo.

## Receta de un servicio

| Componente | Ejemplo: engargolado carta | De dónde sale |
|---|---|---|
| Materiales | 1 espiral de 10 mm, 2 micas, 1 contraportada | De la receta; se descuentan del inventario al cobrar, al costo promedio |
| Impresión | 40 páginas en blanco y negro | Costo real del [módulo de impresión](impresion.md) |
| Tiempo | 6 minutos | Minutos estándar × costo por minuto del personal |
| Gastos indirectos | Internet, luz, renta | Gastos del mes, repartidos al cierre |

## Servicios con parámetros

El cajero captura el parámetro y el sistema elige materiales y precio de una tabla:

- **Engargolado:** número de hojas → tamaño de espiral y precio.
- **Enmicado:** tamaño (credencial, carta, oficio) → mica y precio.
- **Impresión y copia:** páginas, color o blanco y negro, tipo de papel (lo calcula el módulo de impresión).

## Dos utilidades

- **Directa:** materiales + impresión + tiempo. Se ve al momento, en cada venta.
- **Neta:** se calcula al cierre de mes, con los gastos indirectos repartidos:
  - internet → a los servicios que lo usan (trámites, descargas)
  - luz → por páginas impresas
  - el resto → en proporción a las ventas

Así una CURP de $10 deja de parecer rentable si en realidad se lleva 8 minutos de atención.

## Alertas de margen

Cuando una compra cambia el costo de un material, se recalculan los servicios que lo usan. El sistema avisa cuáles
quedaron debajo del margen objetivo y sugiere un precio (`costo × (1 + margen)`, redondeado como en Control Print).

## Cobros a cuenta de terceros

Las recargas y los pagos de servicios no son venta propia: el dinero es del proveedor y solo la **comisión** es
ingreso. Se registran aparte para que no inflen las ventas ni la utilidad, y el corte de caja los separa.
