# Varias sucursales y el central

> **Propuesta revisada el 2026-10-08** con precios y condiciones vigentes, en lluvia de ideas: puede cambiar. La
> versión anterior proponía un servidor virtual desde la sucursal 2 por ~4,000 MXN a 3 años. Esa cuenta no sumaba la
> red privada (Tailscale gratis no se puede usar en un negocio; pagando, el plan costaba 3,050–4,780 MXN al año) ni
> consideraba pagar en pesos con factura (AWS México, desde 2026). Con solo dos sucursales hay opciones más simples.
> Tipo de cambio: 17.98 MXN por dólar (FIX de Banxico del 7 de octubre de 2026). Cada precio dice si lleva IVA.

## Lo que se sabe

- **Solo serán 2 sucursales durante al menos un año;** una 3.ª, quizá en unos dos años.
- Unos 60 tickets al día por sucursal, con picos de ~120: el volumen no presiona nada.
- Cada sucursal vende sin internet: su servidor vive en la caja principal y las demás cajas tienen contingencia.
- Las cajas principales **se apagan de noche** y las sucursales probablemente tendrán **horarios distintos**.
- La casa del dueño está en un lugar distinto a la matriz. El dueño revisa aperturas y cierres cada día; las
  autorizaciones a distancia serán cada vez menos.
- Las entregas de proveedores son 1 o 2 días por semana.
- Es posible que lleguen dispositivos propios que trabajan de noche (vigilancia): ver [ideas](ideas.md).
- Pagar en dólares con tarjeta es una preocupación.

## Las opciones

| Opción | Cómo funciona | Al mes | Veredicto |
|---|---|---|---|
| **Sucursales autónomas y enlazadas** | Cada sucursal con su servidor en la caja principal. La matriz publica el catálogo; lo que debe estar siempre prendido lo hacen servicios gratis | 0 | **Recomendada el primer año** |
| Central ligero | Un servidor virtual recibe ventas, cortes y movimientos de las dos, reparte el mismo catálogo y da un panel de las dos juntas | 90 (Vultr) a 146 (Lightsail, con IVA) MXN | Si aparece una [señal](#cuándo-montar-el-central) |
| Central completo | Lo anterior y además clientes, crédito, usuarios y un solo bot compartidos (era el plan) | 90 a 146 MXN | Con la 3.ª sucursal o una [señal](#cuándo-montar-el-central) |
| Un solo servidor en la nube para las dos | Todas las cajas trabajan por internet contra el servidor | 90 a 180 MXN | Descartada: sin internet, toda la sucursal queda en contingencia |
| La matriz atiende también a la sucursal 2 | Las cajas de la sucursal 2 se conectan por internet a la caja principal de la matriz | 0 | Descartada: la sucursal 2 depende de la luz, el internet y el horario de la matriz |
| Sincronizar directo entre las dos | Las cajas principales se pasan los cambios entre sí | 0 | Descartada: el mismo código grande que el central, y solo funciona con las dos prendidas a la vez |
| Cada una con su catálogo | Igual que la recomendada, sin repartir el catálogo | 0 | Solo como puente de unas semanas: doble captura de productos y precios |

**Por qué no un servidor para las dos:** en contingencia una caja vende, pero no cancela, no devuelve, no cobra
impresiones, no recibe compras ni cuenta inventario: eso espera a la conexión. Con el servidor lejos, cada corte de
internet deja así a toda la sucursal.

## El primer año, sin central

Lo que el plan le pedía al central, y quién lo cubre el primer año:

| Lo que hacía el central | El primer año |
|---|---|
| Repartir catálogo y precios | La matriz publica el **catálogo completo** en Backblaze B2; la sucursal 2 lo aplica al encender y cada pocos minutos. No tienen que estar prendidas a la vez. Precios iguales; la sucursal 2 no edita el catálogo |
| Concentrar los datos y el panel | Cada sucursal guarda los suyos. De noche llega el resumen de cierre de cada una por Telegram. De día, el dueño abre en su PC la caja con su usuario y PIN, conectada por la red privada a la sucursal que elija (una a la vez) |
| Bot de Telegram | Un bot por sucursal: dos chats |
| Vigilar aperturas | Healthchecks.io: una vigilancia por sucursal con su horario. Si la sucursal no se reporta a su hora, avisa, aunque la tienda no tenga luz ni internet |
| Respaldo fuera de la tienda | Cada caja principal sube a B2 la copia diaria y la del corte Z, comprimidas y cifradas, y borra lo viejo con una regla |
| Cola de entrega | No hace falta: el catálogo espera en B2, y los mensajes de WhatsApp en su receptor, hasta que la caja principal enciende |
| WhatsApp | Captura desde la PC ([etapa 1](whatsapp.md)). Si se quiere la API, no obliga a tener central: basta un receptor aparte |
| Actualizaciones | El mismo día en las dos sucursales, por acceso remoto |
| Dispositivos propios | Para avisar no necesitan central ([ideas](ideas.md)); para cruzar sus eventos con la caja, sí |

**Lo que se pierde frente al central:** ver las dos sucursales juntas (de día se ve cada una por separado; de noche,
solo el resumen), el aviso de una caja principal que se apaga a media jornada, el crédito y los clientes compartidos,
los precios por sucursal, las compras repartidas y los traspasos automáticos (se capturan a mano: salida en la matriz
y entrada en la sucursal 2).

**Qué hay que programar** (mediano, mucho menos que el central):

- Dar de alta la sucursal 2 en una base nueva a partir del catálogo de la matriz, con los mismos Id.
- El paquete de catálogo completo, con versión y huella. Tiene que ser completo: hoy los códigos y presentaciones que
  se quitan se borran sin rastro (los precios quitados solo quedan en su historial), y ningún borrado sube la versión
  del catálogo; una bajada de «solo lo cambiado» no los vería.
- El catálogo de solo lectura en la sucursal 2 (también para el dueño), con su regla por sucursal.
- Que el POS se reporte con Healthchecks al abrir el turno y al terminar el respaldo.
- El nombre de la sucursal en los mensajes de Telegram.
- Que el dueño abra desde su PC la sucursal que elija: hoy una caja se da de alta en una sola sucursal y el servicio
  solo acepta la red local.
- El traspaso a mano: la salida en la matriz y la entrada en la sucursal 2 con el costo de la salida (hoy no hay
  pantalla).
- **Seguridad:** hoy el servicio deja pasar sin llave lo que llega de la misma PC. Ningún túnel ni intermediario
  (Caddy, cloudflared, Tailscale Serve) va en una caja principal mientras sea así.

### Servicios

| Servicio | Para qué | Costo | Ojo |
|---|---|---|---|
| **Backblaze B2** | Respaldo y paquete de catálogo | 0 hasta 10 GB; después ~0.12 MXN por GB al mes | No pide tarjeta. Sin una regla que borre lo viejo (por ejemplo, 14 días) se llena en semanas; al tope deja de subir |
| **Healthchecks.io** (Hobbyist) | Aviso si una sucursal no abrió o no respaldó | 0 (hasta 20 vigilancias; se usan 4: apertura y respaldo de cada sucursal) | Permite uso en empresa y no pide tarjeta. Lo opera una sola persona. Borra la cuenta tras un año sin entrar. Si al abrir se cae el internet, avisa aunque sí se abrió. Avisa por su propio bot de Telegram y por correo |
| **NetBird** (Free) | Red privada: el dueño y soporte llegan a cada caja principal sin abrirla a internet | 0 (5 usuarios, 100 equipos) | Sus términos admiten uso en un negocio. Puede quitar el plan con 14 días de aviso: el plan B es Tailscale para negocio con 1 usuario, 8 USD al mes (≈144 MXN). Falta confirmar si pide tarjeta (no cobra) |
| **RustDesk** | Control remoto para soporte, por la red privada | 0 | Programa libre; por la red privada no necesita servidor propio |
| **Bot de Telegram** | Alertas, resumen y autorizaciones | 0 | Uno por sucursal |

Las cuentas las abre el dueño a nombre del negocio, con su correo y verificación en dos pasos; soporte entra como
invitado.

**Descartados:** Tailscale y ZeroTier gratis (solo uso no comercial); Cloudflare Tunnel y Access (hacen de intermediario
en la misma PC; Access además pide tarjeta aunque no cobre); Cloudflare R2 (pide tarjeta y cobra las escrituras arriba
de un millón); Litestream en las cajas principales (no tiene soporte oficial en Windows: la copia que ya hace el POS
basta).

## Cuándo montar el central

**Central ligero**, con cualquiera de estas señales:

- El dueño necesita ver las dos sucursales juntas, de noche o en fin de semana, más de una vez por semana.
- Llegan los dispositivos de vigilancia y sus eventos se tienen que cruzar con la caja.
- Llega la API de WhatsApp y no se quiere un receptor aparte.

**Central completo**, con la primera de estas:

- Se decide la 3.ª sucursal. Hay que empezar unos 6 meses antes: la sincronización es grande y el ensayo largo dura
  de 2 a 4 semanas.
- Clientes con crédito compran seguido en las dos sucursales.
- La sucursal 2 necesita dar de alta productos o tener precios propios con frecuencia.
- Los traspasos capturados a mano dejan de cuadrar (más de unos 5 por semana).

## Dónde, cuando haga falta

| Opción | Al mes | A favor | En contra |
|---|---|---|---|
| **Vultr Ciudad de México** (1 vCPU, 1 GB, 25 GB) | 89.89 MXN (5 USD; no cobra IVA) | Lo más barato con región en México. Se cobra por hora, acepta saldo prepagado y crece en minutos (2 GB: 179.78 MXN) | Cobra en dólares y no da factura |
| **AWS Lightsail** de 1 GB, facturado por AWS México | ≈146 MXN con IVA | Desde 2026 cobra en pesos con tarjeta mexicana y da CFDI | Está en EE. UU. (no importa para esto). Hay que elegir el plan de pago desde el inicio: el plan gratis suspende la cuenta a los 6 meses |
| **Una máquina propia en casa del dueño** | Luz con IVA: 10–34 MXN la laptop, 34–120 la PC | Ya existe | Depende de la luz y el internet de la casa y de que nadie la apague. Solo si pasa la [prueba](prueba-sucursales-linux.md) |

**Descartados:** Oracle Cloud gratis (recupera las máquinas que casi no usan procesador, como esta); servidores pagados
en pesos por adelantado como Hostinger o Neubox (piden 1 o 2 años por adelantado; Hostinger sale más barato los primeros
2 años pero renueva al doble, y no está confirmado que dé CFDI); DigitalOcean, Akamai, Hetzner, Azure y Google (Google y
Akamai cuestan igual o menos, pero en dólares, fuera de México y sin CFDI; Hetzner casi triplicó su precio en EE. UU. en
2026); un mini PC nuevo (5,600–10,000 MXN de capacidad que no se usa); un equipo prendido toda la noche en la matriz (si
la matriz se queda sin luz o internet, no puede avisar que no abrió).

### Costo

| | Primer año | A 3 años |
|---|---|---|
| Sin central (recomendada) | 0 | 0; si la 3.ª llega en unos dos años (central desde el mes 19): 1,620–2,630 MXN |
| El plan anterior: Vultr, Tailscale para negocio y dominio | 3,050–4,780 MXN | 9,160–14,340 MXN |
| Central ligero en Vultr o Lightsail, con red privada en el mismo servidor | 1,080–1,750 MXN | 3,240–5,260 MXN |

### Cómo se arma

```
Servidor virtual · Ubuntu 26.04 LTS · 1 núcleo · 1 GB
│
├─ Cortafuegos ........ a internet solo lo indispensable
├─ WireGuard .......... red privada de las sucursales, el dueño y soporte (reemplaza a NetBird)
├─ Caddy .............. HTTPS automático, solo si hay algo público (WhatsApp, dispositivos)
├─ Servicio POS en modo central (.NET 10, se reinicia solo)
│  └─ central.db (SQLite)
├─ Litestream ......... copia continua de la base a Backblaze B2
└─ Parches de seguridad automáticos
Fuera: Healthchecks.io avisa si el central deja de reportarse
```

- **Instalación con un script** que queda en el repositorio. El servidor es desechable: si muere, se crea otro, se corre
  el script y se restaura la base en 15–30 minutos. Antes se prueba en una máquina propia
  ([prueba](prueba-sucursales-linux.md)) y unas horas en el servidor virtual, que se cobra por hora.
- **Memoria:** el servicio de sucursal usa ~190–230 MB (medido en la PC de desarrollo). Se espera que 1 GB alcance
  para 2 o 3 sucursales; la [prueba](prueba-sucursales-linux.md) lo confirma (si no, el plan de 2 GB).
- **Dominio** (solo si hay algo público): `.com.mx` en Akky, 249 MXN con IVA el primer año, en pesos (renovación por
  confirmar).

## Gestión por excepción

La rutina diaria no necesita abrir ningún panel; llega por Telegram:

- **Apertura:** Healthchecks avisa si una sucursal no abrió a su hora.
- **Cierre:** el resumen de cada sucursal (ventas, utilidad, corte y diferencias, cancelaciones) al hacer el último
  corte Z.
- **Alertas** en el momento y **autorizaciones** con botones.

## Preguntas

- [ ] ¿Cuándo abre la sucursal 2? ¿Abre con eleventa o directo con el POS nuevo?
- [ ] ¿Qué horario tiene cada sucursal y qué días descansa? (para el aviso de «no abrió»)
- [ ] ¿Le basta el resumen de cierre por Telegram y entrar de día a cada sucursal, o necesita ver las dos juntas de
  noche? De eso depende si el primer año hace falta un servidor.
- [ ] ¿Los precios pueden ser iguales en las dos sucursales el primer año? ¿Acepta que solo la matriz dé de alta
  productos y que la sucursal 2 venda lo nuevo como artículo común?
- [ ] ¿Cómo le llega la mercancía a la sucursal 2: por traspaso desde la matriz o directo del proveedor? ¿Cuántos
  traspasos por semana espera?
- [ ] ¿Hay clientes con crédito que compren en las dos sucursales?
- [ ] ¿Le parece bien tener dos chats de Telegram, uno por sucursal, más el de Healthchecks para el aviso de «no
  abrió»?
- [ ] ¿NetBird gratis (sus términos admiten un negocio, pero puede quitar el plan con 14 días de aviso; entonces se
  pasa a Tailscale) o Tailscale para negocio desde el principio, 8 USD al mes (≈144 MXN) con tarjeta que acepte
  dólares?
- [ ] ¿Abre usted las cuentas de Backblaze, Healthchecks.io y NetBird a nombre del negocio? ¿Acepta que las copias
  cifradas se guarden fuera de México?
- [ ] ¿La caja principal de la sucursal 2 tendrá SSD y no-break?
- [ ] Cuando haga falta un servidor, ¿en pesos con factura (AWS México, ≈146 MXN al mes con IVA) o en dólares sin
  factura (Vultr, 89.89 MXN al mes)?
- [ ] ¿Quiere la API de WhatsApp el primer año, o sigue la captura desde la PC? ¿La sucursal 2 tendrá un número
  nuevo? Conviene usarlo con clientes unas semanas antes del alta.
- [ ] ¿Qué tan segura es la 3.ª sucursal y para cuándo? Marca cuándo empezar el central, unos 6 meses antes.
