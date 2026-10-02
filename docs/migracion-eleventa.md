# Migración desde eleventa

## Fuente

- La carpeta de datos de eleventa multicaja. Guarda todo en una base Firebird (`PDVDATA.FDB`, por confirmar con la
  copia).
- Se lee directo con el cliente de Firebird para .NET, **siempre sobre una copia,** nunca sobre la base en uso.

## Qué se importa

- Departamentos, productos, códigos, precios (incluido mayoreo) y costos
- Existencias (solo como referencia: el inventario inicial sale del conteo físico)
- Proveedores, si están capturados
- **Historial de ventas:** alimenta el análisis ABC, el sugerido de compra y la temporada

## Diagnóstico: primer entregable

Antes de que exista la caja, con los datos reales:

- Inventario sin movimiento (dinero parado)
- El 20 % de productos que hace el 80 % de la venta
- Margen real por departamento
- Productos sin código, con precio bajo costo o duplicados en el catálogo
- Estacionalidad: cuánto pesa el regreso a clases y qué productos lo mueven

## Limpieza

- **Variantes:** en eleventa probablemente son productos sueltos ("Pluma azul", "Pluma roja"). Se agrupan de forma
  semiautomática por parecido del nombre y una persona revisa.
- **Servicios:** se convierten en servicios con receta (ver [servicios](servicios.md)).

## Arranque (fase 5)

1. Conteo físico completo, por departamentos, en las semanas previas.
2. Arranque en un día tranquilo.
3. Eleventa queda instalado solo para consulta unas semanas, como respaldo, con un plan para regresar si algo falla.
4. Capacitación a cajeros y encargado antes del arranque, con datos de prueba.
