# Administrar desde fuera

> Propuesta vigente, en lluvia de ideas: puede cambiar.

## Lo que se sabe

- Las cajas principales **se apagan de noche** y las sucursales probablemente tendrán **horarios distintos**.
- La casa del dueño está en un lugar distinto a la matriz.
- El dueño revisa aperturas y cierres cada día; las autorizaciones a distancia serán cada vez menos.
- Las entregas de proveedores son 1 o 2 días por semana.
- Es posible que lleguen dispositivos propios que trabajan de noche (vigilancia): ver [ideas](ideas.md).

## Propuesta: servidor virtual como central

| | |
|---|---|
| Dónde | Vultr, Ciudad de México, 1 GB |
| Costo | ~5–6 USD/mes, más un dominio (~10–15 USD al año) para HTTPS |
| Qué corre | El mismo servicio POS en modo central (SQLite), panel del dueño, bot de Telegram, webhook de WhatsApp y, a futuro, la entrada de los dispositivos |
| Quién lo administra | Yo, con scripts: instalación, actualizaciones y respaldos automáticos. El equipo no necesita entrar ni saber Linux |
| Cuándo | Al abrir la sucursal 2. En el piloto (solo la matriz) no hace falta |

**Por qué cambió la recomendación:**

- Con las cajas apagadas de noche y horarios distintos, un central en la matriz no tendría el cierre de la sucursal 2
  si esta cierra después, y de noche no habría panel.
- La vigilancia nocturna y los mensajes de WhatsApp que llegan de noche necesitan algo prendido siempre.
- Un mini PC en casa sirve, pero dejaría la vigilancia y WhatsApp dependiendo de la luz y el internet de una casa, y
  necesita un túnel para recibir de fuera. El centro de datos es más confiable por unos 110 MXN al mes.
- Dejar la caja principal prendida toda la noche costaría más en luz que el servidor.
- Antes se descartó un "servidor Linux". Se retoma porque cambió el contexto, y el sistema operativo queda invisible
  para el equipo: es el mismo programa .NET, instalado y actualizado con scripts.

## Cómo funciona

- Cada sucursal sincroniza directo con el central mientras su caja principal está prendida.
- Lo que llega de noche espera en el central y se entrega al encender la caja principal.
- La matriz pasa a ser una sucursal más (donde llegan las compras).
- El panel solo se abre por Tailscale. Lo único público es el webhook de WhatsApp y, a futuro, la entrada de los
  dispositivos.
- Respaldo: el central tiene todo, y además sube una copia diaria cifrada a Backblaze B2 o Cloudflare R2 (gratis hasta
  10 GB).
- Si el central falla, las sucursales siguen vendiendo; se reconstruye desde el respaldo.

## Gestión por excepción

La rutina diaria no necesita abrir ningún panel; llega por Telegram:

- **Apertura:** aviso siempre o solo con excepciones (configurable): abrió tarde, fondo distinto, cajero no habitual.
- **Cierre:** resumen por sucursal (ventas, utilidad, corte y diferencias, cancelaciones, pedidos pendientes), en
  mensaje y en archivo.
- **Alertas** en el momento y **autorizaciones** con botones.

Cada sucursal manda sus propios mensajes directo a Telegram, así que esto funciona desde el piloto, antes de que exista
el central. El panel queda para el trabajo de fondo: precios, catálogo, compras y reportes.

## Opciones evaluadas

| Opción | Costo aprox. | Resultado |
|---|---|---|
| A. Caja principal de la matriz | $0 si se apaga de noche; 100–250 MXN/mes de luz si se deja prendida | Descartada para 2+ sucursales: de noche no hay central |
| B. Mini PC en casa del dueño | 4,000–6,000 MXN una vez + ~10–30 MXN/mes de luz | Alternativa si se prefiere no pagar mensualidad |
| **C. Servidor virtual (Vultr CDMX)** | **~5–6 USD/mes** | **Propuesta** |
| D. Plataforma administrada (Azure App Service u otras) | 13 USD/mes o más | Más cara sin ventaja real a este tamaño |

## Preguntas

- [ ] Confirmar el servidor virtual (o preferir el mini PC en casa).
- [ ] Horario de cada sucursal.
- [ ] Nombre del dominio (puede ser el del negocio).
