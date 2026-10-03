# Migración desde eleventa

## Fuente

- La carpeta de datos de eleventa multicaja. Guarda todo en una base Firebird, `PDVDATA.FDB` (≈ 90 MB, páginas de
  16 KB). En la tienda corre con Firebird 2.0.1 de 32 bits (carpeta `FirebirdSQL20`).
- La base es **ODS 10.1, creada con Firebird 1.5 de 32 bits.** El Firebird de 64 bits no la abre ("not a valid
  database"): las bases de antes de Firebird 3 dependen de la plataforma.
- Se lee **siempre sobre una copia,** nunca sobre la base en uso.

## Leer la base

El importador copia la base a una carpeta temporal, la respalda con el `gbak` de Firebird 2.5 **de 32 bits** (el
respaldo no depende de la plataforma), lo restaura como base de **64 bits** y lee esa. Al terminar borra la carpeta
temporal. Hace falta, fuera del repositorio, junto a la carpeta de la base:

| Carpeta | Qué lleva | De dónde |
|---|---|---|
| `firebird25` | Firebird 2.5 embebido de 64 bits (`fbembed.dll` y compañía) | `Firebird-2.5.9.27139-0_x64_embed.zip` |
| `firebird25-32\embed` | Firebird 2.5 embebido de 32 bits, más `gbak.exe` y `fbembed.dll` copiado como `fbclient.dll` | `Firebird-2.5.9.27139-0_Win32_embed.zip` y `gbak.exe` de `Firebird-2.5.9.27139-0_Win32.zip` |

Los tres ZIP están en los [releases oficiales de Firebird](https://github.com/FirebirdSQL/firebird/releases/tag/R2_5_9).
Nada se instala: son DLL que el importador carga.

```
dotnet run --project src/PapeleriaPos.Herramientas -- importar-eleventa --fdb C:\ruta\PDVDATA.FDB --salida eleventa.db
```

## Cómo guarda eleventa los datos (esquema 216)

| Dato | Dónde y cómo |
|---|---|
| Productos | `PRODUCTOS`: el **código es la llave** (las ventas lo guardan por código, no por ID). `TVENTA` 'U' por pieza, 'D' a granel. `PFINAL` es el precio con impuestos (aquí igual a `PVENTA`: no se desglosa IVA). `ELIMINADO_EN` marca los borrados |
| Unidades | `MEDIDA_ID` → `MEDIDAS`: 1 pieza, 3 metro, 4 kilo, 5 litro. Muchos productos a granel tienen KG aunque se venden por metro o por pieza suelta (y la medida KG está desactivada) |
| Departamentos | `DEPARTAMENTOS` (`DEPTS` es una tabla vieja) |
| Existencias | `INVENTARIO_BALANCES.CANTIDAD_ACTUAL`; `PRODUCTOS.DINVENTARIO` está desactualizado |
| Tickets | `VENTATICKETS`: `VENDIDO_EN` (hora local; vacío si el ticket quedó abierto), `ESTA_CANCELADO` 't'/'f'. Solo efectivo |
| Líneas | `VENTATICKETS_ARTICULOS`: `GANANCIA` es la utilidad de toda la línea (costo = importe − ganancia). `TOTAL_ARTICULO` **está vacío en las líneas viejas**: ahí el importe es `PRECIO_FINAL × CANTIDAD`, y así todos los tickets cuadran |
| Mayoreo | `PMAYOREOFINAL`: un segundo precio que el cajero **activa a mano** en la venta (`USA_MAYOREO`), no por cantidad. Las escalas por cantidad están en `PROMOCIONES_POR_CANTIDAD` |
| Texto | Charset NONE con texto en **Windows-1252** (la ñ es el byte F1): se lee con la conexión en WIN1252 |
| Proveedores | `PROVEEDORES` está vacía; `PROVEEDORES_BASE` y `PRODUCTOS_BASE` son catálogos que trae eleventa, no de la tienda |
| Servicios | Impresiones, copias, engargolado y escaneo son productos a granel con fracciones ("0.8 a $10" = 8 impresiones de $1) |

## Qué se importa

- **Catálogo:** cada producto de eleventa queda como un producto con una sola variante, con su código, precio y
  departamento. Los borrados entran inactivos. Lo vendido con un código que ya no existe se crea como producto
  inactivo, para no perder esas ventas.
- **Último costo** (`PCOSTO`), bajo un proveedor genérico, porque eleventa no tiene proveedores capturados.
- **Existencias** como inventario inicial al costo de eleventa (solo como referencia: el inventario de arranque sale
  del conteo físico). Las negativas no entran.
- **Historial de ventas** completo, con cancelados marcados: alimenta el análisis ABC, el sugerido de compra y la
  temporada.
- **No se importa** el precio de mayoreo manual (el modelo usa escalas por cantidad) y se avisa cuántos hay.

El importador termina con una lista de lo que hubo que interpretar ("para revisar"): unidades dudosas, códigos
inexistentes, mayoreo no importado, existencias negativas y tickets abiertos.

## Diagnóstico: primer entregable

Antes de que exista la caja, con los datos reales. Ya funciona sobre los datos ficticios
(`dotnet run --project src/PapeleriaPos.Herramientas -- diagnostico --base ficticios.db`) y deja un Excel con una
hoja por análisis:

| Hoja | Qué muestra |
|---|---|
| Resumen | Venta propia, costo, margen, ticket promedio, inventario, rotación y los hallazgos de las demás hojas |
| Dinero parado | Mercancía con existencia sin venta en los últimos 90 días, de mayor a menor valor |
| 80-20 | Variantes de la que más vende a la que menos, con clase A (80 % de la venta), B (15 %) y C (5 %) |
| Margen por departamento | Venta, costo, utilidad y margen; las recargas aparte porque solo la comisión es ingreso |
| Revisar catálogo | Sin código, con algún precio debajo del costo (menudeo, mayoreo o paquete) o posibles duplicados |
| Temporada | Venta por mes y lo que mueve el regreso a clases (15 de julio a 15 de septiembre) |

- **Duplicados:** mismo nombre con las palabras en otro orden, otras mayúsculas o acentos, o abreviadas ("p." por
  "punto"). Es estricto a propósito: "Pluma Bic azul" y "Pluma Bic negra" no se marcan.
- **Costo para comparar precios:** el promedio de la existencia; si no hay existencia, el último costo de compra.

## Limpieza

- **Variantes:** en eleventa probablemente son productos sueltos ("Pluma azul", "Pluma roja"). Se agrupan de forma
  semiautomática por parecido del nombre y una persona revisa.
- **Servicios:** se convierten en servicios con receta (ver [servicios](servicios.md)).

## Convivencia con eleventa (idea a evaluar)

> Propuesta del dueño, todavía sin decidir. Se evalúa antes del piloto (fase 5).

Un módulo pequeño para la adaptación: el POS nuevo **se hace pasar por una caja más de eleventa multicaja** y
escribe cada venta también en la base de eleventa. Así eleventa sigue siendo el sistema oficial (existencias, cortes,
reportes de siempre) mientras el POS nuevo se prueba en la tienda, y se pasa de uno a otro poco a poco: primero una
caja, luego todas, hasta que eleventa ya no haga falta.

Lo que hay que resolver antes:

- **Entender cómo escribe eleventa** una venta: qué tablas toca (venta, líneas, existencias, folios, turno o corte),
  en qué orden y con qué reglas. Se averigua sobre la copia, comparando la base antes y después de ventas de prueba
  hechas en un eleventa de pruebas, nunca en la tienda.
- **Riesgo de dañar la base de eleventa.** Escribir en una base ajena sin conocer todas sus reglas puede descuadrar
  existencias o cortes. Hace falta respaldo diario y probarlo semanas en una copia.
- **Firebird 2.0.1 de 32 bits con una base ODS 10.1:** para leer ya sabemos cómo (ver «Leer la base»), pero
  escribir en la base viva requiere hablarle al servidor de la tienda por red, como hace una caja de eleventa.
- **Qué pasa con lo que eleventa no tiene:** servicios con receta, pedidos, fracciones o códigos nuevos. Se mandaría
  solo lo que eleventa entiende (venta, cobro, existencia) y lo demás vive solo en el POS nuevo.

Una variante más segura para empezar: **solo leer** de eleventa (catálogo, precios y existencias cada pocos minutos)
para que el POS nuevo trabaje con datos al día sin escribir nada en eleventa. Si eso funciona, se agrega la escritura.

## Arranque (fase 5)

1. Depuración del catálogo sobre la base importada ([plan](plan.md#depuración-del-inventario)): bajas, duplicados,
   equipo de la tienda, unidades y servicios.
2. Conteo físico completo, por departamentos, en las semanas previas. Reemplaza todas las existencias de eleventa.
3. Arranque en un día tranquilo.
4. Eleventa queda instalado solo para consulta unas semanas, como respaldo, con un plan para regresar si algo falla.
5. Capacitación a cajeros y encargado antes del arranque, con datos de prueba.
