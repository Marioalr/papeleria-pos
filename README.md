# Papelería POS

Punto de venta a la medida para una papelería con varias sucursales: venta, inventario, servicios (impresiones,
copias, engargolado, trámites…), pedidos y control de cada sucursal sin que el dueño tenga que estar presente.
Reemplaza a eleventa multicaja e integra [Control Print](https://github.com/Marioalr/control-print).

> **Estado: planeación.** Todavía no hay código. Las decisiones se cierran en el [plan](docs/plan.md) antes de
> escribir la primera línea.

## Documentos

| Documento | Contenido |
|---|---|
| [Plan](docs/plan.md) | Contexto del negocio, decisiones aprobadas, fases y pendientes |
| [Arquitectura](docs/arquitectura.md) | Componentes, datos, contingencia, varias sucursales, red, respaldos y hardware |
| [Caja y control](docs/caja-y-control.md) | Venta, cobro, cortes, autorizaciones configurables, bitácora y alertas |
| [Catálogo e inventario](docs/inventario.md) | Variantes, presentaciones, códigos, kardex, compras, conteos y precios |
| [Servicios y costeo](docs/servicios.md) | Recetas, servicios con parámetros, utilidad directa y neta |
| [Pedidos](docs/pedidos.md) | Trabajos que esperan: cola compartida, charolas, recogida, clientes registrados y duplicados |
| [WhatsApp](docs/whatsapp.md) | Cómo llegan los archivos de WhatsApp a los pedidos |
| [Control Print](docs/control-print.md) | Integración con el costo real de cada impresión |
| [Migración desde eleventa](docs/migracion-eleventa.md) | Importación, diagnóstico y arranque |
