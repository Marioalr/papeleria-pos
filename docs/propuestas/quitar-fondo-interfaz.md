# Panel de herramientas y «Quitar fondo»: propuesta de la interfaz

Propuesta del 28 de noviembre de 2026. Va con [la propuesta del módulo](quitar-fondo.md). Maqueta para probarla
con el teclado: [quitar-fondo-maqueta.html](quitar-fondo-maqueta.html).

**El cambio que pidió el dueño (2026-11-28):** un **solo atajo** abre un **panel de herramientas**, y ahí se escoge cuál
usar. Van a venir más herramientas, y aprenderse un atajo por cada una no es posible. Este panel reemplaza al
`Ctrl+Alt+J` de juntar (PR #233), que se ajusta en la misma entrega.

La idea guía: **un atajo para todo, y una letra que se ve en pantalla.** No hay que memorizar nada: el panel enseña
la letra de cada herramienta. Con la costumbre queda en dos teclas: `Ctrl+Alt+H`, `F`.

---

## 1. Son tres piezas

| Pieza | Cuándo se ve | Cómo es |
|---|---|---|
| **El panel de herramientas** | Al pulsar el atajo, desde cualquier programa | Una ventana chica centrada (unos 460 × 360 px) con la lista de herramientas, su letra y lo que hay en el portapapeles |
| **El contador** | Mientras se junta (sin cambios) | La tira de arriba a la derecha, sin tomar el foco |
| **La ventanita de cada herramienta** | Al escoger una | La de juntar (sin cambios) o la nueva de quitar fondo |

---

## 2. El atajo

**`Ctrl+Alt+H`** («herramientas»). Es por decidir con el dueño, igual que el de juntar.
- No choca con la arroba: en el teclado latinoamericano, `AltGr+Q` (que Windows lee como `Ctrl+Alt+Q`) escribe `@`. Por eso no se usa Q.
- Si otro programa ya tiene el atajo, se avisa al encender la caja y se escoge otro en Configuración, como ya se hace en juntar.
- Es el **único atajo global** del POS para estas herramientas. Las letras de adentro solo funcionan con el panel abierto, así que no le quitan teclas a WhatsApp ni a Word.

---

## 3. El panel

```
┌ Herramientas ─────────────────────────────────────────── Esc ┐
│                                                               │
│  EN EL PORTAPAPELES   [▣]  Imagen · 1080 × 1350               │
│                                                               │
│  ┌──────────────────────────────────────────────────────────┐ │
│  │ J  Juntar imágenes para Word                             │ │ ← elegida
│  │    Copia varias en WhatsApp y pégalas juntas             │ │
│  └──────────────────────────────────────────────────────────┘ │
│     F  Quitar fondo                                           │
│        A la imagen copiada: fondo blanco para documentos      │
│                                                               │
│     ·  (aquí irán las que vengan)                             │
│                                                               │
│  ↑↓ escoger · Enter abrir · la letra abre directo             │
└───────────────────────────────────────────────────────────────┘
```

- **Cada herramienta tiene su letra fija**, la inicial de lo que hace (J juntar, F fondo). Al pulsarla se abre sin `Enter`. Las letras no cambian de lugar aunque se agreguen más.
- **Flechas y `Enter`** sirven igual; con el mouse, un clic.
- **«En el portapapeles»** enseña lo que hay copiado: miniatura y medidas si es imagen, «Texto» o «Nada». Así se ve a qué foto se le va a quitar el fondo antes de escoger.
- **Herramienta que necesita una imagen y no la hay:** se ve apagada, con «Copia una foto en WhatsApp primero». Su letra no hace nada; el panel se queda abierto.
- **Orden:** el de la lista, fijo. La elegida al abrir es la última que se usó en esa caja. Así, con `Enter`, se repite lo de siempre.
- **`Esc`** cierra y regresa el foco a donde estaba (WhatsApp).
- **Sin sonido.** El panel se cierra solo al escoger.

### Mientras se junta

Si hay una junta en marcha, el panel enseña arriba, ya elegida, **«Terminar de juntar · 7 imágenes»**. Se termina con
`Ctrl+Alt+H`, `Enter`: una tecla más que en el #233, a cambio de no tener otro atajo.

```
│  ┌──────────────────────────────────────────────────────────┐ │
│  │ Enter  Terminar de juntar · 7 imágenes                   │ │ ← elegida
│  └──────────────────────────────────────────────────────────┘ │
│     Supr   Cancelar la junta (se borran las 7)                │
│     F      Quitar fondo                                       │
```

- El **clic en el contador** sigue terminando directo, sin pasar por el panel.
- El contador dice «Ctrl+Alt+H para terminar».
- **Quitar fondo durante una junta** sí se puede. Lo que copia la caja no lo cuenta el contador, porque la caja ya ignora lo que ella misma escribe en el portapapeles.

---

## 4. La ventanita de quitar fondo

Se abre con la foto **ya procesada**: no hay botón de «Quitar». Mientras el modelo trabaja (uno o dos segundos), el
«Después» dice «Quitando el fondo…».

```
┌ Quitar fondo ────────────────────────────────────────────────────────── 1080 × 1350 · listo en 1.4 s ┐
│                                                                                                    │
│   ANTES                              DESPUÉS                                                       │
│   ┌──────────────────┐               ┌──────────────────┐     FONDO   [Blanco Alt+B][Azul Alt+Z]   │
│   │   ▒▒▒▒▒▒▒▒▒▒▒▒   │               │                  │             [Transparente Alt+T]       │
│   │   ▒▒  ◠◡◠  ▒▒▒   │               │      ◠◡◠         │             [Otro Alt+O]               │
│   │   ▒▒ ╱███╲ ▒▒▒   │               │     ╱███╲        │                                        │
│   │   ▒ ███████ ▒▒   │               │    ███████       │     BORDE   [ − ]  0  [ + ]            │
│   └──────────────────┘               └──────────────────┘     ORILLA  [Suave ▾]  Alt+S           │
│                                                                                                    │
│   TAMAÑO   [Foto entera ▾]  Alt+M        COPIAS  [ 1 ]  Alt+C                                       │
│                                                                                                    │
│ ──────────────────────────────────────────────────────────────────────────────────────────────────│
│  [ Copiar para Word  Enter ]   [ Abrir en Word  Ctrl+O ]   [ Esc ]                                 │
└────────────────────────────────────────────────────────────────────────────────────────────────────┘
```

### Los controles

| Control | Teclas | Qué hace |
|---|---|---|
| Fondo | `Alt+B` blanco · `Alt+Z` azul · `Alt+T` transparente · `Alt+O` otro | El color de atrás. Se recuerda en cada caja. «Transparente» enseña cuadritos grises en la pantalla, y en Word queda sin fondo. «Otro» abre unos cuantos colores guardados y un campo para escribir el color. |
| Borde | `+` / `−` | De −5 a +5. «−» come orilla (quita el pedazo de pared que quedó). «+» deja más (cuando se comió el pelo o la oreja). |
| Orilla | `Alt+S` | Suave (de fábrica, para el pelo) o Dura (ropa lisa, uniformes). |
| Tamaño | `Alt+M` | «Foto entera» o una medida de la lista (ver abajo). |
| Copias | `Alt+C` y el número | Cuántas en la hoja. Con más de una, se acomodan como en juntar, con líneas de corte. |
| Copiar | `Enter` | Copia, se cierra sola, y el aviso verde sale en el contador: «Listo: pega en Word con Ctrl+V». |
| Abrir en Word | `Ctrl+O` | Como en juntar. |
| Cerrar | `Esc` | Sin preguntar: es una sola foto y sigue en WhatsApp. |

`Tab` recorre todo en orden. El foco empieza en **Copiar**, así que lo más común se hace con `Ctrl+Alt+H`, `F`, `Enter`.

### Con una medida

Al escoger una medida (`Alt+M`), el «Después» enseña el marco del recorte encima de la foto. Ya viene centrado en la
cara: aire arriba de la cabeza y los hombros abajo.

| Teclas | Qué hace |
|---|---|
| Flechas | Mueven el marco |
| `Alt+↑` / `Alt+↓` | Lo agrandan o achican, sin cambiar la proporción de la medida |
| `Inicio` | Lo regresa a donde lo puso la caja |

La lista trae «Foto entera», unas medidas comunes y **«Editar medidas…»**. Ahí se escriben nombre, ancho y alto en cm. Los valores exactos los pone el dueño.

### Avisos

| Caso | Aviso |
|---|---|
| No encontró a nadie | «No encontré a una persona en esta foto»; el «Después» enseña la foto sin cambios y Copiar queda apagado |
| Varias personas | «Hay más de una persona: quedan todas» (con medida, el marco va a la más grande) |
| Foto chica para la medida | Ámbar: «A 3.5 cm se verá borrosa (420 px de alto)», igual que en juntar |
| El modelo no cargó | Rojo: «No se pudo cargar la herramienta de fondo»; se anota en el registro diario |

---

## 5. Dentro de juntar

En la ventanita de juntar, la tecla **`F`** sobre una miniatura le quita el fondo a esa imagen, con el último fondo
que se usó. La miniatura sale con una marca «sin fondo», y **`F` otra vez** lo regresa. Para ajustar una sola
(borde, medida), **`Shift+F`** abre la ventanita de quitar fondo con esa imagen; al copiar, regresa a juntar.

---

## 6. Lo que cambia en el #233

- El atajo `Ctrl+Alt+J` se reemplaza por el panel. Para empezar a juntar: `Ctrl+Alt+H`, `J`. Para terminar: `Ctrl+Alt+H`, `Enter`, o un clic en el contador.
- El contador dice «Ctrl+Alt+H para terminar».
- El botón «Juntar más» de la ventanita ya no enseña un atajo.
- Aún no está en la tienda, así que nadie tiene la costumbre del atajo viejo.

---

## 7. Por decidir con el dueño

1. El atajo: `Ctrl+Alt+H`, u otro.
2. Si terminar de juntar debe ser `Ctrl+Alt+H` y `Enter` (propuesto), o el atajo directo mientras se junta.
3. La lista de medidas y sus valores.
4. El tono de azul.
