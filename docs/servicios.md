# Servicios y costeo

Hoy, en eleventa, el costo de un servicio se suma a mano y se queda congelado. Si sube la mica o cambia el tiempo que
toma un trámite, nadie se entera. Aquí cada servicio tiene una **receta** y su costo se calcula solo.

## Receta de un servicio

| Componente | Ejemplo: engargolado carta | De dónde sale |
|---|---|---|
| Materiales | 1 espiral de 10 mm, 2 micas, 1 contraportada | De la receta; se descuentan del inventario al cobrar, al costo promedio |
| Impresión | 40 páginas en blanco y negro | Costo real del [módulo de impresión](impresion.md) |
| Uso del equipo | La engargoladora | Su depreciación: (costo − valor final) ÷ meses de vida ÷ usos al mes. El equipo de la matriz no tiene fecha de compra; propuesta, por aprobar: tomar como costo lo que vale hoy y como meses de vida los que le quedan |
| Luz del equipo | La enmicadora encendida | kWh del equipo × $/kWh del recibo (con IVA: sin contador, hoy no se acredita) ÷ usos del mes. En las impresoras ya va por cara |
| Tiempo | 6 minutos | Minutos estándar × costo por minuto del personal (informativo: los sueldos se restan una vez al mes) |
| Gastos indirectos | Internet, renta, la luz y la depreciación que no se cargaron a un equipo | Gastos del mes, al cierre |

## Servicios con parámetros

El cajero captura el parámetro y el sistema elige materiales y precio de una tabla:

- **Engargolado:** número de hojas → tamaño de espiral y precio.
- **Enmicado:** tamaño (credencial, carta, oficio) → mica y precio.
- **Impresión y copia:** páginas, color o blanco y negro, tipo de papel (lo calcula el módulo de impresión).

## Tres niveles de costo

Revisado el 2026-10-08: el dueño pidió que en engargolado y enmicado se deprecie el equipo y se cuenten la luz y los
consumibles del servicio (micas, espirales, portadas: van en la receta como materiales)
([notas](notas.md#luz-y-costo-de-los-servicios)).

- **Costo directo,** al cobrar, entra al costo de lo vendido: materiales de la receta (salen del inventario), impresión
  (con la configuración de la impresora que se usó), uso del equipo y luz del equipo. Ya no hay servicios con costo 0.
- **Utilidad después de atención,** informativa: resta los minutos × costo por minuto del personal.
- **Ganancia neta,** al cierre del mes: a la utilidad directa (precio − costo directo) se le restan sueldos, renta,
  internet, consumibles, la luz no asignada (el recibo menos la luz ya cargada a los equipos) y la depreciación no
  asignada (PC, mobiliario, equipo que no se usó). Al cierre, la luz y la depreciación de cada equipo se reparten con
  los usos reales.

**Para no contar dos veces:** tintas, caja de mantenimiento y papel de impresión van en el costo de impresión; micas y
espirales, en la receta; ninguno en los consumibles del mes (cinta, limpieza…). La luz del recibo se resta una sola vez.
La compra del equipo no es gasto del mes: entra como depreciación.

Así una CURP de $10 deja de parecer rentable si en realidad se lleva 8 minutos de atención.

## Alertas de margen

Cuando una compra cambia el costo de un material, se recalculan los servicios que lo usan. El sistema avisa cuáles
quedaron debajo del margen objetivo y sugiere un precio (`costo × (1 + margen)`, redondeado como en Control Print).

## Cobros a cuenta de terceros

Las recargas y los pagos de servicios no son venta propia: el dinero es del proveedor y solo la **comisión** es
ingreso. Se registran aparte para que no inflen las ventas ni la utilidad, y el corte de caja los separa.

**Pospuesto** (2026-10-08): integrarlos al sistema queda como [idea](ideas.md#recargas-y-pago-de-servicios).
Propuesta: mientras, en la depuración se marcan como cobro a terceros para que no cuenten como venta propia en los
reportes (falta esa decisión en la depuración).
