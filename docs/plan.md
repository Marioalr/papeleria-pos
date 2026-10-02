# Papelería POS — Plan

## Contexto

- Papelería con servicios: impresiones, copias, engargolado, enmicado, recargas, pago de servicios y trámites
  (CURP, actas…).
- Hoy: **1 sucursal con 2 cajas** (se planea una 3.ª). **2.ª sucursal en camino** y una 3.ª en unos dos años.
- Unos **60 tickets al día**, con picos de ~120. La temporada de regreso a clases pesa mucho.
- Inventario de ~500 mil MXN a costo, que creció 10 veces en 4 años; margen bruto anual de ~65 %.
- Se usa **eleventa multicaja** (sin el módulo de clientes).
- Un número de WhatsApp Business por sucursal, en un celular (también para llamadas); el grueso del trabajo se hace
  desde la PC.
- **Objetivo:** que el dueño no tenga que estar presente en cada sucursal y tenga control preciso de todas.
- **Fuera de alcance por ahora:** facturación y temas fiscales.

## Decisiones aprobadas

| Tema | Decisión |
|---|---|
| Lenguaje | C# / .NET 10 en todo, como Control Print |
| Caja | WPF en Windows 11, todo operable con teclado, diseñada para 1366×768 (monitores económicos) |
| Servidor | Vive en la **caja principal** de cada sucursal, como en eleventa, como servicio de Windows (corre sin sesión iniciada y se reinicia solo) |
| Varias sucursales | La caja principal de la **matriz** concentra los datos de todas. "Central" es un rol que puede moverse después a otro equipo ([propuesta](servidor-remoto.md)) |
| Base de datos | SQLite en todas partes; el servicio es el único que escribe |
| Núcleo | Sin dependencias de Windows: lo comparten la caja, el servicio y la app Android |
| Inventario | Kardex: solo se agregan movimientos y la existencia es la suma |
| IDs, dinero y fechas | UUIDv7, centavos enteros, UTC; folios legibles por caja (A-000123) |
| Contingencia | Cada caja guarda copia del catálogo y una bandeja de salida: si la caja principal se apaga, las demás siguen vendiendo |
| Roles | Dueño, encargado y cajero; lo que puede hacer cada uno es **configurable** |
| Clientes | El celular solo identifica un **pedido** al recogerlo. **Clientes registrados**: pocos, los de confianza |
| WhatsApp | API oficial en modo coexistencia; nunca librerías no oficiales. Antes de eso, captura manual desde la PC |
| Alertas y autorización remota | Bot de Telegram |
| Panel del dueño | Web (Blazor) servida por la caja principal de la matriz; desde el celular por Tailscale |
| App Android | .NET MAUI, sin publicar en la tienda |
| Fiscal | Fuera de alcance, pero se guardan clave SAT, unidad y desglose de impuestos para poder facturar después |
| Nube | Azure for Students solo para pruebas |
| Código | Hasta cerrar el plan, solo documentos |

### Cambios respecto a las primeras propuestas

| Antes | Ahora | Motivo |
|---|---|---|
| Go + Tauri + Svelte | .NET 10 + WPF | Reusar Control Print y los patrones que ya funcionan en el negocio |
| PostgreSQL en cada sucursal | SQLite | Cero administración en un sitio sin técnico; el volumen es bajo |
| Mini PC dedicado como servidor | Caja principal | Decisión del negocio (como eleventa); la contingencia de las cajas cubre que se apague |
| Servidor central en la nube (Linux + PostgreSQL) | Caja principal de la matriz | Decisión del negocio: sin servidores en la nube |
| Niveles de confianza automáticos por celular | Celular solo para el pedido; clientes registrados a mano | No guardar números de más |

## Fases

Sin tiempos: se avanza cuando la fase anterior funciona en la tienda.

| Fase | Contenido |
|---|---|
| 0. Base e importación | Modelo de datos, importador de eleventa y diagnóstico con los datos reales |
| 1. Caja | Venta, cobro mixto, ticket, turnos y corte ciego, roles y autorizaciones, bitácora, contingencia, Control Print |
| 2. Inventario | Compras, recepción ciega, presentaciones, conteos cíclicos, etiquetas, sugerido de compra |
| 3. Precios y clientes | Mayoreo, promociones, kits, clientes registrados, crédito |
| 4. Servicios y pedidos | Recetas y costeo, pedidos, cola compartida, charolas, recogida por celular, duplicados, apartados, listas escolares, captura manual desde WhatsApp |
| 5. Piloto en la matriz | Conteo físico inicial, arranque, eleventa como respaldo |
| 6. Varias sucursales y WhatsApp | Sincronización con la matriz, panel del dueño, Telegram, API de WhatsApp → pedidos |
| 7. Sucursal 2 | Precios por sucursal, traspasos, compras que se reparten |
| 8. App Android | Preventa y pedidos desde el celular |
| 9. A la medida | Listas escolares avanzadas y lo que se vaya describiendo |

## Pendientes

Para empezar la fase 0:

- [ ] Copia de la carpeta de datos de eleventa (base Firebird `PDVDATA.FDB`, por confirmar)
- [ ] Modelo y ancho (58 u 80 mm) de la impresora de tickets; foto de un ticket actual

Por definir:

- [ ] Demás funciones a la medida (las describe el dueño)
- [ ] Privilegios de los clientes registrados: pedido sin anticipo, crédito, apartar sin pagar… ¿qué más?
- [ ] Regla para pedidos de no registrados: monto máximo que se prepara sin anticipo, y días para borrar el celular
- [ ] Dónde vive el central a largo plazo y dónde se guardan los respaldos fuera de la tienda: ver
      [administrar desde fuera](servidor-remoto.md#preguntas)
- [ ] WhatsApp: ver [pendientes](whatsapp.md#pendientes)
