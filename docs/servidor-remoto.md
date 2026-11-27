# Central para varias sucursales (1.ª fase)

> **Decisión tomada (2026-11-26) — Infraestructura elegida:** se selecciona **Oracle Cloud Always Free en modo
> Pay-As-You-Go (PAYG)** (región Querétaro `mx-queretaro-1`, instancia ARM `VM.Standard.A1.Flex`) como servidor
> central a **$0 MXN al mes**, con respaldo continuo en Backblaze B2 para mantener el servidor desechable (Vultr queda
> como plan B).
>
> **Ajustes de alcance (2026-11-26):**
> - **Videovigilancia:** queda **fuera del alcance del servidor** (sin puentes Raspberry Pi 5, sin clips/fotos de cámaras
>   en el central ni vigilancia nocturna desde el POS).
> - **Bot de Telegram / alertas remotas:** pasa a etapa de evaluación y comparativa ([evaluación](evaluacion-telegram.md));
>   sin importar lo que se decida, **no entra en la primera fase del modo servidor**. La ausencia del dueño se gestiona con
>   **permisos locales** (un encargado con el suficiente poder de decisión en caja e inventario).
> - **WhatsApp:** la integración con la API de WhatsApp ([whatsapp.md](whatsapp.md)) **tampoco entra** en la primera fase
>   del modo servidor.
> - **Integración IoT:** contador de personas, detector de presencia y demás ideas IoT **tampoco entran** en la primera
>   fase con servidor.
>
> **Estado del desarrollo:** la multisucursal sigue **pospuesta** frente al piloto de una sucursal
> ([plan](plan.md#varias-sucursales-pospuesto); [notas](notas.md#aplica-ya-con-una-sola-sucursal)). Esta propuesta deja
> cerrada la arquitectura y la nube de la 1.ª fase del servidor para cuando se retome.

## Lo que pide el dueño para la 1.ª fase del servidor

1. **Sincronización y control a distancia desde el panel:** ver ventas, cortes, existencias y bitácora de las sucursales
   cuando no está en tienda. La operación diaria en su ausencia se resuelve con **permisos locales** (un encargado con
   capacidad de autorizar en tienda y límites configurados).
2. **Precios por sucursal:** iguales el primer día; con seguridad se irán ajustando.
3. **Resistir cualquier flujo de mercancía:** traspasos en línea entre sucursales (reemplazando el archivo provisional
   cuando exista), compras donde una sucursal paga y otra recibe, y compras repartidas; sin clientes con crédito
   compartidos.
4. **Costo mínimo:** operar el servidor a **$0 MXN mensuales** sin comprometer la operación de las cajas.

Y lo de siempre: cada sucursal vende sin internet, las cajas principales se apagan de noche, los horarios son
distintos y solo serán 2 sucursales durante al menos un año (una 3.ª, quizá en unos dos).

## La propuesta

```
Cada sucursal                                    Central: Oracle Cloud Always Free (PAYG)
├─ Caja principal: vende sin internet ──HTTPS──► sincronización, catálogo maestro y precios
│  sube cada cambio; baja catálogo,              por sucursal, traspasos y compras, panel del
│  precios, permisos y actualizaciones           dueño y reportes consolidados
└─ Cajas y celulares: contingencia, como hoy

Fuera: NetBird gratis (red privada), Backblaze B2 (respaldos ≤10 GB gratis), Healthchecks.io (vigilancia gratis)
```

- **Cada sucursal vende sola:** su servidor sigue en la caja principal y ninguna venta pasa por el central. Si el central
  o el internet fallan, las cajas cobran igual y se sincronizan al volver.
- **El central** corre en **Oracle Cloud Always Free en modo Pay-As-You-Go (PAYG)** en Querétaro (`mx-queretaro-1`), con
  instancia ARM64 (`VM.Standard.A1.Flex`, p. ej. 2 OCPUs y 4–8 GB de RAM dentro del pool gratuito): **$0 MXN al mes**,
  sin apagado por inactividad (exento por PAYG) y con hasta 200 GB de disco y 10 TB/mes de salida.
- **Sincronización hecha a la medida:** la caja principal sube cada cambio en segundos cuando hay internet, y baja
  catálogo, precios, usuarios/permisos, traspasos y actualizaciones. La conexión siempre la abre la tienda: nada entra a
  la tienda desde fuera.
- **Panel del dueño:** la interfaz compartida de Blazor servida por el central, accesible de forma privada por **NetBird
  gratis** (sin abrir puertos ni pagar dominio) o por dominio propio / `duckdns.org` si más adelante se quiere entrar sin
  VPN con llave de acceso (*passkey*).
- **Control en ausencia mediante permisos locales:** en [Permisos.cs](../src/PapeleriaPos.Nucleo/Control/Permisos.cs)
  cada usuario tiene su nivel (`Permitido`, `RequiereAutorizacion`, `NoPermitido`) y umbrales; el encargado opera y
  autoriza en piso mientras lo sensible (`VerUtilidad`, `VerReportes`, `CorteDelDia`, `EditarReglas`) queda bajo el
  control que defina el dueño, con todo registrado en bitácora.

### Cómo se cumple cada respuesta

| Respuesta | Cómo |
|---|---|
| 1. Control sin estar en tienda | **En piso:** permisos locales del encargado con umbrales y bitácora de cada acción. **En el central:** el panel del dueño muestra ventas, cortes, existencias, diferencias y bitácora de todas las sucursales casi en vivo, con cuadre diario automático. Sin videovigilancia, sin bot ni llamadas en la 1.ª fase |
| 2. Precios por sucursal | El cobro ya usa el precio de la sucursal y, si no hay, el base, con conexión y sin ella. El día 1 todo queda en el base. En F3, un interruptor por producto: «igual en todas» o «distinto», y un permiso aparte para «precio de mi sucursal». Un cambio hecho sin internet sube como propuesta: si nadie tocó ese precio entra; si alguien lo tocó, gana el de mayor rango, nunca por la hora del reloj, y al otro se le avisa |
| 3. Cualquier flujo de mercancía | El kardex dice dónde está cada pieza. Cualquier sucursal compra y recibe; la compra guarda quién paga y quién recibe, y el retiro con que se pagó se liga a ella aunque la reciba la otra sucursal. La orden lleva el destino de cada línea, así que el proveedor puede entregar directo en cada sucursal. En una compra repartida, quien recibe registra todo al costo de la nota y la parte de la otra sucursal queda «de paso» (no se vende) hasta que sale como traspaso, al mismo costo. Traspaso en línea con estados (borrador → enviado → recibido → cerrado). Un almacén futuro es una sucursal sin cajas. El crédito es de cada sucursal y sube al central solo para verlo |
| 4. Costo mínimo ($0/mes) | **Oracle Cloud Always Free (PAYG)** para el central, **NetBird gratis** para acceso privado y soporte, **Backblaze B2 gratis** (10 GB) para respaldos y **Healthchecks.io gratis** para vigilar caídas ([opciones](#opciones-comparadas)) |

## Opciones comparadas

| Pieza | Elegida | Costo | Lo que se evaluó / descartó y por qué |
|---|---|---|---|
| **Central** | **Oracle Cloud Always Free en modo PAYG** (`mx-queretaro-1`, ARM `A1.Flex`) | **$0 MXN/mes** (con tarjeta física para PAYG y alerta en $1 USD) | **Por qué PAYG:** la cuenta Always Free sin tarjeta apaga VMs tras 7 días con <20 % de uso y sufre falta de capacidad en Querétaro; con PAYG sigue en $0 dentro de límites gratuitos y evita el apagado por inactividad. **Plan B:** Vultr CDMX 2 GB (~215.74 MXN/mes con respaldo) o Lightsail (~315–325 MXN con IVA/CFDI). **Otras evaluadas:** Google Cloud `e2-micro` (solo 1 GB RAM en HDD y 1 GB/mes salida gratis); laptop Celeron propia con Linux + NetBird ($0 nube, ~15–40 MXN/mes de luz, depende de luz/internet en casa); Cloudflare Workers + D1 (obliga a reescribir todo fuera de .NET 10). **Descartadas:** Render/Koyeb (sin disco persistente y duermen), Railway ($1/mes), AWS Free (6 meses), Azure for Students (no comercial) |
| **Red privada** | **NetBird gratis** (5 usuarios, 100 equipos; admite negocio) | **$0** | **Plan B:** Tailscale negocio (143.82 MXN/mes por 1 usuario). **Descartados:** Tailscale/ZeroTier gratis (solo uso no comercial) |
| **Dominio / HTTPS** | **Solo NetBird en 1.ª fase** (sin dominio obligatorio) | **$0** | Al quedar WhatsApp fuera de la 1.ª fase, no se exige HTTPS público para webhooks. **Opciones si se abre al navegador sin VPN:** `*.duckdns.org` (en *Public Suffix List*, admite Let's Encrypt y passkeys a $0) o dominio `.com` en Cloudflare (~188–201 MXN/año ≈ 16/mes) |
| **Respaldo** | **Backblaze B2** con llaves que no pueden borrar y bloqueo de 14–30 días | **$0** hasta 10 GB (sin tarjeta) | Sin clips de video de cámaras (que pesaban ≈24 GB), `central.db` + respaldos diarios de tiendas caben holgados en los 10 GB gratuitos. **Descartado:** Cloudflare R2 (pide tarjeta) |
| **Vigilancia externa** | **Healthchecks.io gratis** (20 vigilancias; correo/Telegram propio) | **$0** | Vigila que el central y las cajas principales sigan reportando; mientras no haya bot de POS en 1.ª fase, avisa por correo |
| **Videovigilancia y puentes** | *Fuera del alcance del servidor (2026-11-26)* | — | Se eliminan las 2 Raspberry Pi 5 (~5,892 MXN), microSD, no-breaks de puentes y el almacenamiento de clips. Las cámaras siguen independientes en sus grabadores/DMSS ([cámaras](camaras.md)) |
| **Bot de Telegram / alertas** | *En evaluación, fuera de la 1.ª fase (2026-11-26)* | — | Evaluado aparte en [evaluación de Telegram](evaluacion-telegram.md). Se omiten Pushover y llamadas de Twilio (~37–55 MXN/mes) de la 1.ª fase |
| **WhatsApp e IoT** | *Fuera de la 1.ª fase (2026-11-26)* | — | Sin receptor de WhatsApp ni contador de personas/sensores en esta etapa |

## Costo de la 1.ª fase del servidor

| Concepto | Una vez | Al mes |
|---|---|---|
| Central (Oracle Cloud Always Free en modo PAYG, ARM64 Querétaro) | $0 *(retención temporal de verificación en tarjeta)* | **$0** |
| Red privada (NetBird), respaldos (Backblaze B2 ≤10 GB), vigilancia (Healthchecks.io) y software | $0 | **$0** |
| Dominio `.com` *(opcional; $0 si se entra solo por NetBird o con DuckDNS)* | — | **$0** *(o ≈16 MXN si se compra `.com`)* |

- **Total de la 1.ª fase del servidor:** **$0 MXN al mes** (con tarjeta dada de alta en OCI PAYG y alerta de presupuesto en $1 USD).
- **Si en algún momento Oracle falla o cambia su capa gratuita:** el servidor es desechable y se levanta en minutos en Vultr (~215.74 MXN/mes) restaurando desde Backblaze B2.

## Entregas

| Cuándo | Qué |
|---|---|
| **Al retomar** | Crear cuenta OCI en Querétaro, pasar a PAYG con alerta de $1 USD, levantar VM ARM con script reproducible, configurar Backblaze B2, Healthchecks.io y NetBird. Lo previo aplicable a una sola sucursal sigue en el [plan](plan.md#para-el-piloto-con-dos-cajas) |
| **Arreglos previos** (chico) | Cerrar el hueco de tráfico local sin llave antes de poner un proxy; identidad fija de cada sucursal; quitar supuestos de «primera sucursal» ([notas](notas.md#para-retomar-la-multisucursal)); registrar bajas del catálogo (códigos, presentaciones, ligas); autor, sucursal y versión en historial de precios; prefijo de sucursal en códigos internos; días de descanso; binarios Linux/ARM64 (incluido `SkiaSharp` si aplica) |
| **6.1** Central de subida y cuadre (grande) | Llave por sucursal, bandeja de subida, réplica sin duplicados, cuarentena de lo que no cuadra, versión del protocolo y respaldo continuo con Litestream en Backblaze B2. Cuadre diario automático |
| **6.2** Bajada, precios y permisos (grande) | Catálogo maestro, precios por sucursal en F3, propuestas sin internet, arreglo del ajuste de margen en compras, alta de la sucursal 2 desde el central con los mismos Id y sincronización de usuarios/permisos |
| **6.3** Panel del dueño (mediano) | Panel web con la interfaz compartida (Blazor): ventas, cortes, existencias, bitácora, precios y estado de cada sucursal, protegido por NetBird (o HTTPS + passkey si se usa dominio) |
| **6.4** Mercancía entre sucursales (grande) | Sucursal o almacén, traspasos en línea con estados (reemplazando el archivo provisional cuando exista), orden con destino por línea, compra con quién paga separado de quién recibe, compra repartida «de paso» al costo de la nota y devolución cruzada entre sucursales |
| **6.5** Ensayo largo (2–4 semanas) | Fallas provocadas, apagado de red/central, restauraciones desde Backblaze B2 en una VM nueva (medir los 15–30 min de recuperación) y medición de memoria/disco en Oracle ARM |
| **Fase 7** (sucursal 2) | Pasa la sucursal 2 al POS con precios iguales el día 1 después de aprobar 6.1–6.5 y la etapa 2 de la [prueba](prueba-sucursales-linux.md) |
| **Fuera de la 1.ª fase** | Evaluación/activación de bot o canal de avisos ([evaluación](evaluacion-telegram.md)), API oficial de WhatsApp ([whatsapp.md](whatsapp.md)) e integración IoT (contador/sensores) |
| **Fuera del alcance del servidor** | Videovigilancia (cámaras, clips, puentes Raspberry Pi 5) |

## Si algo falla

| Falla | Qué sigue funcionando |
|---|---|
| **El central (Oracle Cloud)** | Ambas sucursales siguen vendiendo, cobrando e imprimiendo en su propia caja principal; guardan todo en su bandeja y se sincronizan al volver. Healthchecks.io avisa de la caída. Mientras vuelve se pierden el panel, la sincronización entre tiendas y los traspasos en línea |
| **Oracle cambia términos o cierra capacidad en la cuenta** | Al tener respaldo continuo de `central.db` con Litestream en Backblaze B2 y script de instalación en el repositorio, se levanta una VM en Vultr (o en la laptop Celeron con NetBird) y se restaura en 15–30 minutos |
| **NetBird** | La venta y la sincronización entre tiendas siguen funcionando si la ruta de sync es pública HTTPS, o reintentan solas al volver NetBird si todo vive dentro de la VPN |
| **Internet de una tienda** | La venta local sigue intacta; al regresar el internet sube la bandeja pendiente y baja cambios de catálogo/precios |

## Respaldos

1. **Local:** copia diaria y copia del corte Z en cada caja principal, como hoy.
2. **El central:** réplica casi en vivo de ambas sucursales en `central.db`.
3. **Fuera (≤10 GB gratis):** copia cifrada de cada sucursal en Backblaze B2 (llave sin borrado + bloqueo de 14–30 días).
4. **El central mismo:** copia continua con **Litestream** hacia Backblaze B2 (todo entra en los 10 GB gratis al no guardar videos).

**Ensayos:** restauración automática semanal, restauración completa mensual en máquina Linux de prueba ([prueba](prueba-sucursales-linux.md#las-máquinas)) y reconstrucción del central desde cero en una VM limpia cada pocos meses.

## Cómo se arma el central

```
Oracle Cloud Always Free (PAYG) · Querétaro (mx-queretaro-1)
VM.Standard.A1.Flex (ARM64) · Ubuntu LTS · 2 OCPUs · 4–8 GB RAM · 50–100 GB disco
│
├─ Cortafuegos (ufw) y parches automáticos; SSH solo por NetBird
├─ NetBird (acceso privado de administración, panel y soporte)
├─ [Opcional si se usa dominio/DuckDNS] Caddy: HTTPS automático para sync y panel con passkey
├─ Servicio POS en modo central (.NET 10, linux-arm64): réplica, catálogo, precios, traspasos, panel
│  └─ central.db (SQLite en modo WAL; <1–2 GB estimados)
└─ Litestream: copia continua de central.db a Backblaze B2 (≤10 GB gratis)
Fuera: Healthchecks.io vigila latido del central y de cada caja principal
```

- **Instalación reproducible con script** en el repositorio para mantener el servidor desechable (reconstruible en 15–30 min).
- **Memoria:** con 4–8 GB de RAM en ARM `A1.Flex` hay margen de 8× a 15× sobre los ~230–453 MB medidos en pruebas de `.NET 10`, sin riesgo de OOM ni necesidad de medir apreturas de 1 GB salvo para la laptop de respaldo.
- **Disco:** sin clips de videovigilancia (~24 GB), `central.db` + bitácoras caben holgados en los 50 GB mínimos de disco de arranque de OCI (200 GB gratis disponibles).

## Riesgos

- **Mucho código en sincronización y mercancía:** entregas 6.1 a 6.4 ordenadas, simulador con fallas y cuadre diario. La sucursal 2 puede seguir con eleventa hasta que la prueba salga limpia.
- **Dependencia de cuenta Oracle Cloud Always Free (PAYG):**
  - requiere tarjeta física internacional (no prepago/virtual) y hace una retención temporal de verificación (~100 USD);
  - aunque PAYG exime de la recuperación por inactividad y no cobra dentro de los límites Always Free, un error al crear un disco o IP pagados genera cargos: obligatorio configurar **Budget Alert en $1 USD** al crear la cuenta;
  - puede haber demoras temporales de capacidad (`Out of host capacity`) en `mx-queretaro-1` al crear la VM por primera vez (una vez creada y en PAYG, no se toca). Plan B inmediato: Vultr CDMX.
- **Precios mal cobrados al separar un producto:** hoy el precio propio de presentación gana a la escala de sucursal y el ajuste de margen en compras mueve el precio base; deben quedar cubiertos con pruebas antes de 6.2.
- **Gestión en ausencia con permisos locales:** sin bot de alertas ni videovigilancia en la 1.ª fase, la prevención de errores o abusos depende de calibrar bien los umbrales del encargado en [Permisos.cs](../src/PapeleriaPos.Nucleo/Control/Permisos.cs) y revisar la bitácora y cortes en el panel.
- **NetBird gratis:** puede cambiar condiciones con 14 días de aviso y tener ventanas semanales de mantenimiento (~2 h, ~18:00–19:00 MX); Tailscale para negocio es el plan B.

## Preguntas

- [ ] Confirmar quién será el encargado con poder de decisión en cada sucursal y qué permisos/umbrales exactos tendrá
      (descuentos, devoluciones, mermas, corte del día, modificar traspasos, ver costos/utilidad).
- [ ] ¿Se accede al panel del dueño solo por NetBird ($0, sin dominio) o con dominio propio / `duckdns.org` para abrirlo
      desde cualquier navegador con passkey?
- [ ] Confirmar tarjeta física disponible para activar el modo PAYG en Oracle Cloud (con alerta en $1 USD).
- [ ] ¿Cuándo pasa la sucursal 2 de eleventa al POS y con cuántas cajas? ¿Qué horario y días de descanso tiene cada una?
- [ ] Precios: ¿el encargado de cada sucursal puede cambiar precios de su sucursal o solo el dueño? ¿Se permite cambiar
      sin internet?
- [ ] Mercancía: ¿dónde llegan hoy las compras? ¿Habrá entrega directa a la sucursal 2? En una compra repartida, ¿el
      reparto se decide al pedir o al recibir? En un traspaso con faltante, ¿quién responde?
- [ ] ¿Se puede devolver en una sucursal lo comprado en la otra? ¿El descuento de un cliente registrado vale en las dos?
