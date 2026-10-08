# Administrar desde fuera: el central

> Propuesta vigente, en lluvia de ideas: puede cambiar.

## Lo que se sabe

- Las cajas principales **se apagan de noche** y las sucursales probablemente tendrán **horarios distintos**.
- La casa del dueño está en un lugar distinto a la matriz.
- El dueño revisa aperturas y cierres cada día; las autorizaciones a distancia serán cada vez menos.
- Las entregas de proveedores son 1 o 2 días por semana.
- Es posible que lleguen dispositivos propios que trabajan de noche (vigilancia): ver [ideas](ideas.md).

## Qué hace el central

1. **Concentra los datos** de todas las sucursales (ventas, cortes, inventario, pedidos) para el panel y los reportes.
2. **Reparte catálogo y precios:** un cambio hecho en el panel le llega a cada sucursal cuando se conecta.
3. **Panel del dueño** disponible a cualquier hora.
4. **Bot de Telegram:** alertas, resúmenes y autorizaciones. Telegram solo permite un receptor de botones por bot, así
   que el bot vive en el central y reparte cada respuesta a su sucursal (en el piloto, sin central, vive en la caja
   principal).
5. **Vigila las aperturas:** conoce el horario de cada sucursal y avisa si una no se conectó a su hora. Solo es posible
   con algo siempre encendido.
6. **Cola de entrega:** lo que llega con la sucursal apagada (WhatsApp, cambios, autorizaciones) espera y se entrega al
   encender la caja principal.
7. **WhatsApp** (fase 6): recibe mensajes y archivos y los asigna a la sucursal del número.
8. **Actualizaciones:** guarda el instalador vigente para las sucursales.
9. **Respaldo** de todas las sucursales.
10. **Dispositivos** (a futuro): recibe sus eventos de día y de noche.

**Lo que no hace:** vender ni imprimir. Si el central se cae, las sucursales siguen trabajando; solo se pausan el
panel, la sincronización y lo que llega de fuera.

## Las dos opciones

| | Servidor virtual (Vultr CDMX, 1 GB) | Mini PC en casa del dueño |
|---|---|---|
| Costo inicial | $0 | 5,000–9,000 MXN (N150, 16 GB, 512 GB; en 2026 la memoria subió) + no-break si no hay |
| Mensual | 5 USD | 10–30 MXN de luz |
| Dominio | ~10–15 USD/año | Igual (el túnel también lo necesita) |
| A 3 años, aprox. | ~4,000 MXN | ~7,000–12,000 MXN |
| Disponibilidad | Centro de datos con energía y red de respaldo | Depende de la luz y el internet de la casa |
| Dirección pública | IP fija incluida | Túnel de Cloudflare (gratis) |
| Capacidad | 1 núcleo, 1 GB, 25 GB. Crecer es cambiar de plan en minutos | 4 núcleos, 16 GB, 512 GB: mucho más margen |
| Salida a internet | 1 TB/mes incluido, conexión de centro de datos | La velocidad de subida de la casa |
| Mantenimiento | Sin aparato; el sistema se actualiza solo | Aparato físico: disco, ventilador, alguien lo desconecta |
| Sistema | Linux (lo administro yo con scripts) | Windows, igual que las cajas |
| Dónde quedan los datos | En el proveedor, en Ciudad de México | En casa del dueño |
| Riesgo principal | La cuenta: si falla el cobro, la suspenden (se puede dejar saldo prepagado) | Una sola casa y un solo aparato |

**Ventajas del servidor virtual:** más barato a 3 años con los precios de 2026, más confiable, IP pública sin túnel y
cero hardware que cuidar. Si se descompone, se crea otro con un script y se restaura el respaldo.

**Ventajas del mini PC:** pago único sin depender de una tarjeta en dólares, mucha más capacidad, mismo Windows que
las cajas y los datos en casa.

## Hasta dónde llega el servidor virtual de 5 USD

| Uso | Alcance |
|---|---|
| Datos | 3 sucursales × 60 tickets/día ≈ 66 mil tickets al año ≈ unos cientos de MB al año. Los 25 GB alcanzan para más de una década |
| Memoria | El servicio usa ~190–230 MB (medido en la PC de desarrollo; la [prueba](prueba-sucursales-linux.md) lo mide con lo que tendría aquí); queda espacio para panel, bot, WhatsApp y dispositivos |
| Sucursales | Holgado para 10 o más al volumen actual: el límite no son las ventas |
| Dispositivos | Contadores y sensores mandan eventos pequeños: miles al día sin problema |
| WhatsApp | Archivos temporales que se borran a los N días; unos pocos GB de tráfico al mes, lejos del 1 TB |

**Lo que no cabe** (en ninguna de las dos opciones tiene sentido mandarlo al central):

- **Grabar video de cámaras.** Una cámara 1080p grabando todo el día son 0.6–1.3 TB al mes. La grabación se queda en la
  tienda; al central solo llegan alertas y fotos.
- **Procesar imágenes con IA** (leer fotos de listas escolares): mejor un servicio externo que se paga por uso, cuando
  llegue.

**Para crecer:** 2 GB por 10 USD o 4 GB por 20 USD, en minutos y sin reinstalar.

## Estructura del servidor virtual

```
Servidor virtual · Ubuntu LTS · 1 núcleo · 1 GB · 25 GB
│
├─ Cortafuegos ........ a internet solo el puerto 443 (HTTPS)
├─ Tailscale .......... entrada privada: panel del dueño y soporte
├─ Caddy .............. HTTPS automático con certificados gratuitos
│
├─ Servicio POS en modo central (.NET, se reinicia solo)
│  ├─ Sincronización con las sucursales (una llave por sucursal)
│  ├─ Panel del dueño (solo por Tailscale)
│  ├─ Bot de Telegram
│  ├─ Vigilancia de aperturas y resumen de cierre
│  ├─ Cola de entrega
│  ├─ Instaladores para actualizar
│  ├─ WhatsApp (fase 6) · dispositivos (a futuro)
│  └─ central.db (SQLite)
│
├─ Litestream ......... copia continua de la base a Backblaze B2 o Cloudflare R2
└─ Parches de seguridad automáticos del sistema

Fuera: Healthchecks.io avisa si el central deja de reportarse
```

## Qué hay que instalar

| Pieza | Para qué | Costo |
|---|---|---|
| Ubuntu Server LTS | Sistema base con años de parches de seguridad | Gratis |
| Actualizaciones automáticas | Parches de seguridad sin intervención | Gratis |
| Cortafuegos | Solo HTTPS abierto; el acceso de soporte no queda expuesto a internet | Gratis |
| Memoria de intercambio (1–2 GB) | Colchón en un plan de 1 GB | Gratis |
| Tailscale | Panel y soporte por red privada | Gratis hasta 6 usuarios, pero solo para uso no comercial; para negocios, 8 USD por usuario al mes ([pregunta](#preguntas)) |
| Caddy | HTTPS automático (Let's Encrypt) | Gratis |
| Servicio POS en modo central | Todo lo de "qué hace el central" | Nuestro |
| Litestream | Copia continua de SQLite; se puede restaurar a cualquier momento | Gratis; 10 GB de almacenamiento gratis |
| Healthchecks.io | Vigilancia desde fuera | Gratis |
| Dominio | HTTPS para WhatsApp y dispositivos | ~10–15 USD/año |
| Bot de Telegram | Alertas y autorizaciones | Gratis |
| Cuenta de Meta para WhatsApp | Fase 6 | Por mensaje |

- **Instalación con un script** que queda en el repositorio. El servidor es desechable: si muere, se crea otro, se corre
  el script y se restaura la base en 15–30 minutos.
- **Actualizaciones del POS:** se publican desde la PC de desarrollo por Tailscale y se reinicia el servicio. La versión
  anterior se conserva para regresar si algo falla.

### Si fuera el mini PC

Cambia esto:

- Windows 11 con el mismo instalador de la caja principal, en modo central.
- Túnel de Cloudflare (servicio de Windows) en lugar de Caddy, porque la casa no tiene IP pública.
- No-break, BIOS configurado para encender solo al regresar la luz, suspensión desactivada y Windows Update fuera de
  horario.

El resto queda igual: Tailscale, respaldo continuo a B2/R2 y Healthchecks.io.

## Gestión por excepción

La rutina diaria no necesita abrir ningún panel; llega por Telegram:

- **Apertura:** aviso siempre o solo con excepciones (configurable): abrió tarde, fondo distinto, cajero no habitual,
  no abrió a su hora.
- **Cierre:** resumen por sucursal (ventas, utilidad, corte y diferencias, cancelaciones, pedidos pendientes), en
  mensaje y en archivo.
- **Alertas** en el momento y **autorizaciones** con botones.

El panel queda para el trabajo de fondo: precios, catálogo, compras y reportes.

## Antes de contratar

El central se prueba primero en una máquina propia con Linux, con el mismo script de instalación, y al final unas
horas en un servidor virtual de verdad, que se cobra por hora. Ver [la prueba](prueba-sucursales-linux.md).

## Recomendación

**Servidor virtual**, desde que abra la sucursal 2. En el piloto no hace falta. El mini PC queda como alternativa si se
prefiere pago único.

## Preguntas

- [ ] Confirmar el servidor virtual (o preferir el mini PC en casa).
- [ ] Horario de cada sucursal.
- [ ] Nombre del dominio (puede ser el del negocio).
- [ ] Forma de pago al proveedor: tarjeta o saldo prepagado.
- [ ] Tailscale: su plan gratuito es solo para uso no comercial. ¿Se paga el plan para negocios (8 USD por usuario al
  mes; con el dueño y soporte, 16 USD, más que el propio servidor) o se busca otra forma de entrar al panel? La
  [prueba en Linux](prueba-sucursales-linux.md) lo usa desde la etapa 2.
