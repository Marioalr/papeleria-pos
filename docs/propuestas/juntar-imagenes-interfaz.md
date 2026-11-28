# «Juntar imágenes para Word»: propuesta de la interfaz

Propuesta del 28 de noviembre de 2026. Va con [la propuesta del módulo](juntar-imagenes.md) (versión 2: juntar
en WhatsApp y pegar en Word). Maqueta para probarla con el teclado: [juntar-imagenes-maqueta.html](juntar-imagenes-maqueta.html).

La idea guía: **lo más común se hace con tres teclas y un número.** `Ctrl+Alt+J`, copiar en WhatsApp, `Ctrl+Alt+J`,
escribir el alto, `Enter`, y en Word `Ctrl+V`.

---

## 1. Son dos piezas

| Pieza | Cuándo se ve | Cómo es |
|---|---|---|
| **El contador** | Mientras se junta | Una tira chica arriba a la derecha, justo debajo del marco de la ventana (donde están los botones para minimizar y cerrar), siempre encima, que **no toma el foco**: WhatsApp sigue al frente y el cajero sigue copiando |
| **La ventanita** | Al terminar de juntar | Una ventana mediana centrada (unos 820 × 560 px, cabe en 1366 × 768), con el tamaño, la hoja y las miniaturas |

Las dos son de la caja de Windows y funcionan **aunque la caja esté minimizada o detrás de WhatsApp**. Llevan el mismo
estilo de la caja (`caja.css`, colores y letras de la venta), para que se vea que son del POS.

---

## 2. El contador

```
 ┌ WhatsApp ─────────────────────────────────────────────────────────────────── ─  ▢  ✕ ┐
 │                                                     ┌──────────────────────────────┐   │
 │                                                     │ [▣] Juntando imágenes · 7    │   │
 │                                                     │     Ctrl+Alt+J para terminar │   │
 │                                                     └──────────────────────────────┘   │
```

**Dónde va (decidido por el dueño, 2026-11-28):** arriba a la derecha de la pantalla, justo debajo del marco de la
ventana, donde están minimizar y cerrar. Ahí tapa menos de WhatsApp que abajo: abajo está la caja para escribir y lo
último del chat. Es más chica que en la primera idea (unos 260 × 52 px).

- **[▣]** es la miniatura de la última imagen que entró: al copiar una, aparece ahí con un destello corto. Así el
  cajero ve que sí entró sin voltear a otro lado.
- **El número crece** con cada imagen.
- **Lo que no es imagen** (un texto, un número) no hace nada: ni cuenta ni avisa.

| Estado | Lo que dice | Color |
|---|---|---|
| Juntando | «Juntando imágenes · 7» | Azul de la caja |
| Una que ya estaba | «Esa ya estaba · siguen 7» por 2 segundos | Ámbar |
| Varios archivos de una carpeta | «+12 de la carpeta · 19» | Azul |
| Detenido por tiempo (15 min sin copiar) | «Se detuvo: 15 min sin copiar · Ctrl+Alt+J para ver las 7» | Ámbar, se queda |
| No se pudo leer (otro programa tenía el portapapeles) | «No alcancé a leer esa: cópiala otra vez» | Rojo, 3 segundos |

Con el mouse: un clic en el contador hace lo mismo que `Ctrl+Alt+J`. Si en alguna PC estorba, se puede arrastrar y se
recuerda dónde quedó.

**Sin sonido** (decidido por el dueño): el destello de la miniatura y el número bastan.

---

## 3. La ventanita

```
┌ Juntar imágenes ──────────────────────────────────────────────────────────── 24 imágenes · 1 repetida ┐
│                                                                                                       │
│  TAMAÑO   [ Alto  Alt+A ][ Ancho  Alt+N ][ En hojas  Alt+H ]      [   5.0 ] cm                        │
│           Salen 3 hojas · la última, a la mitad                                                       │
│                                                                                                       │
│  HOJA     [Carta ▾]  [Vertical ▾]   Márgenes de Word: 2.5 arriba y abajo, 3 a los lados  (Cambiar)    │
│           Separación [0.3] cm   [ ] Líneas de corte   [ ] Blanco y negro                              │
│                                                                                                       │
│  ┌ Hoja 1 ─────┐ ┌ Hoja 2 ─────┐ ┌ Hoja 3 ─────┐     ┌──────────────────────────────────────────┐     │
│  │ ▯ ▭ ▯ ▯ ▭   │ │ ▯ ▯ ▭ ▯ ▯   │ │ ▭ ▯ ▯       │     │ ⚠ 2 se verán borrosas a 5 cm             │     │
│  │ ▯ ▯ ▭ ▯     │ │ ▭ ▯ ▯ ▯     │ │             │     │   (la 9 y la 17: son chiquitas)          │     │
│  │ ▭ ▯ ▯ ▯ ▯   │ │ ▯ ▯ ▭ ▯ ▯   │ │             │     │ ↔ 1 no cabe a lo ancho: va de 15.6 cm    │     │
│  └─────────────┘ └─────────────┘ └─────────────┘     └──────────────────────────────────────────┘     │
│                                                                                                       │
│  EN ORDEN  ① ② ③ ④ ⑤ ⑥ ⑦ ⑧ ⑨⚠ ⑩ ⑪ ⑫ ⑬ ⑭ ⑮ ⑯ ⑰⚠ ⑱ ⑲ ⑳ …                                        │
│            ←→ moverse · Alt+←→ cambiar de lugar · Supr quitar · R girar · Espacio ver grande          │
│                                                                                                       │
│ ─────────────────────────────────────────────────────────────────────────────────────────────────────│
│  [ Copiar las 24 para Word  Enter ]   [ Abrir en Word  Ctrl+O ]   [ Juntar más  Ctrl+Alt+J ]  [ Esc ] │
└───────────────────────────────────────────────────────────────────────────────────────────────────────┘
```

### De arriba abajo

1. **Tamaño.** Tres botones juntos (Alto, Ancho, En hojas) y **un solo número** al lado, que cambia de unidad: «cm»
   para Alto y Ancho, «hojas» para En hojas. Abajo, en una línea, **el resultado**:
   - Alto o Ancho: «Salen 3 hojas · la última, a la mitad».
   - En hojas: «Salen de 6.3 cm de alto».
2. **Hoja.** Lo que casi nunca cambia, en una línea: tamaño, orientación y los márgenes de Word en texto. «Cambiar»
   abre los cuatro márgenes ahí mismo. La separación y las dos casillas, debajo.
3. **Cómo quedan las hojas.** Dibujos chicos de cada hoja con las imágenes en su lugar y en su proporción, para ver de
   un vistazo cuántas hojas salen y si la última queda casi vacía. Se recalcula al escribir, sin botón.
4. **Avisos**, a la derecha de las hojas, solo si hay algo:
   - **⚠ Borrosas:** cuántas y cuáles («la 9 y la 17»). En las miniaturas llevan el mismo ⚠.
   - **↔ No cabe a lo ancho:** cuántas se achicaron al ancho de la hoja.
   - Sin avisos, el espacio queda vacío: no se dice «todo bien».
5. **En orden.** Las miniaturas con su número, en el orden en que se copiaron. La que tiene el foco lleva un marco.
   **Espacio** la enseña grande encima de todo (Esc o Espacio la cierran), para ver si es la que se quería.
6. **Los botones**, siempre abajo y a la vista. El principal dice cuántas va a copiar: «Copiar las 24 para Word».

### El foco y el orden de Tab

- **Al abrir, el cursor está en el número del tamaño, con el número seleccionado.** Escribir `6` lo reemplaza y
  `Enter` copia. Es el camino de todos los días.
- **Tab** recorre: número → Carta → Vertical → Cambiar márgenes → Separación → Líneas de corte → Blanco y negro →
  miniaturas → botones. **Shift+Tab** regresa.
- **`Enter` copia desde cualquier lugar** salvo dentro de las miniaturas (ahí no hace nada, para no copiar por error al
  estar reordenando) y dentro de una lista abierta (ahí elige).
- **Alt+A, Alt+N, Alt+H** cambian el modo desde cualquier lugar y llevan el cursor al número.

### Después de copiar

**La ventanita se cierra sola** (decidido por el dueño) y el contador se queda un momento con: **«24 imágenes listas · pégalas en Word con Ctrl+V»**
(en verde, 10 segundos). Si se cierra Word sin pegar, se puede volver a copiar con `Ctrl+Alt+J` mientras no pasen los
15 minutos: se abre la ventanita con lo mismo.

### Abrir en Word

`Ctrl+O` crea el documento con la hoja y los márgenes puestos y lo abre. El contador dice «Abriendo Word…» y luego
«Listo en Word». **Si la PC no tiene Word**, el botón sale apagado con «No encontré Word en esta PC» y «Copiar» sigue
funcionando (sirve para pegar en LibreOffice u otro).

### Cancelar

`Esc` cierra y borra lo juntado. Si hay más de 3 imágenes, primero pregunta, en la misma ventanita y sin otra
ventana: **«¿Borrar las 24 imágenes? Enter sí · Esc no»**.

### Casos raros

| Caso | Qué pasa |
|---|---|
| Se terminó sin juntar ninguna | La ventanita no se abre; el contador dice «No juntaste ninguna imagen» 3 segundos |
| Una sola imagen | Se abre igual (sirve para ponerle el tamaño exacto) |
| Más de 100 | Se abre igual; las hojas se dibujan más chicas y con barra |
| El atajo ya lo tiene otro programa | Al encender la caja, un aviso en la barra de arriba: «Ctrl+Alt+J lo usa otro programa: escoge otro en Configuración» |
| Se sale del usuario o se cierra la caja mientras junta | Se cancela y se borra lo juntado |

---

## 4. Lo que se recuerda y dónde se configura

- **Se recuerda en cada caja:** el último modo y su número, la hoja, la orientación, los márgenes, la separación, las
  casillas y la esquina del contador. La siguiente vez abre igual.
- **En Configuración › Caja › «Juntar imágenes»:** el atajo, los márgenes de Word por omisión y el tiempo para borrar
  (15 minutos). Lo cambia quien tenga el permiso de configurar la caja.
- **Sin permisos nuevos:** juntar y copiar lo puede hacer cualquier cajero; no toca dinero.

---

## 5. Cómo se ve y se lee

- **Mismos colores y letras de la caja**, en claro (la caja no tiene modo oscuro).
- **Letra de 15 px; el número del tamaño, grande (24 px)**, porque es lo que se escribe siempre.
- **Cada botón dice su tecla** (`Alt+A`, `Enter`, `Ctrl+O`), como en la venta.
- **Los avisos con ícono y palabra**, no solo color: ⚠ «borrosas», ↔ «no cabe a lo ancho».
- **Lectores de pantalla:** cada miniatura dice «Imagen 9 de 24, 320 por 320, se verá borrosa»; el contador anuncia
  el número cuando cambia.

---

## 6. Para quien lo programe

- **El contador:** una ventana WPF propia, `Topmost`, sin barra de título, que no se activa al mostrarse ni al
  actualizarse (estilo de ventana «no activar»). No usa WebView2: es una tira con una imagen y dos textos, y así no
  pesa en la caja 2.
- **La ventanita:** una ventana WPF con un `BlazorWebView`, para usar los mismos componentes y `caja.css` de la caja.
  Con la misma carpeta de datos de WebView2 que la caja, para no arrancar otro navegador (como recomienda Microsoft y
  como se hizo con el panel de trámites).
- **El dibujo de las hojas** sale de la misma cuenta del acomodo en `PapeleriaPos.Nucleo` que arma el bloque para Word:
  lo que se ve es lo que se pega.
- **Las miniaturas** se hacen chicas solo para la pantalla. **Lo que va a Word son las imágenes tal como llegaron**,
  con todos sus píxeles y su formato.
- **Prueba sin mouse:** abrir, cambiar a «En hojas», escribir 1, reordenar dos, quitar una, copiar. Y otra que
  verifique que el contador no le quita el foco a otra ventana.

---

## Lo que decidió el dueño (2026-11-28)

1. **El contador va arriba a la derecha**, debajo del marco de la ventana: así cubre menos.
2. **Sin sonido.**
3. **La ventanita se cierra sola** al copiar.
4. **En las cajas se usa la app oficial de WhatsApp** (la de Windows), no WhatsApp Web. La maqueta y la propuesta ya
   hablan de ella. Falta probar en la caja qué deja en el portapapeles su «Copiar» de una imagen («Lo que hay que probar» en la propuesta del módulo).

Siguen abiertas las de la propuesta del módulo: el alto más común, la PC y el Word que tienen, los márgenes, las líneas
de corte y el atajo.
