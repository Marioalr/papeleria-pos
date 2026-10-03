# Migración desde eleventa

## Fuente

- La carpeta de datos de eleventa multicaja. Guarda todo en una base Firebird, `PDVDATA.FDB` (confirmado con la
  copia: ≈ 90 MB, páginas de 16 KB).
- El encabezado de la copia dice **ODS 10, es decir Firebird 1.x.** Firebird 3 en adelante no abre ese formato
  directo: para leerla hará falta un Firebird 2.5 (que sí lo abre) o pasarla a un formato nuevo con respaldo y
  restauración (`gbak`). Se resuelve al llegar a la importación de prueba.
- Se lee con el cliente de Firebird para .NET, **siempre sobre una copia,** nunca sobre la base en uso.

## Qué se importa

- Departamentos, productos, códigos, precios (incluido mayoreo) y costos
- Existencias (solo como referencia: el inventario inicial sale del conteo físico)
- Proveedores, si están capturados
- **Historial de ventas:** alimenta el análisis ABC, el sugerido de compra y la temporada

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
- **Firebird 1.x** (ODS 10): el cliente de .NET para Firebird tiene que poder hablar con esa versión, o hay que
  pasar la base a una versión más nueva sin que eleventa deje de funcionar.
- **Qué pasa con lo que eleventa no tiene:** servicios con receta, pedidos, fracciones o códigos nuevos. Se mandaría
  solo lo que eleventa entiende (venta, cobro, existencia) y lo demás vive solo en el POS nuevo.

Una variante más segura para empezar: **solo leer** de eleventa (catálogo, precios y existencias cada pocos minutos)
para que el POS nuevo trabaje con datos al día sin escribir nada en eleventa. Si eso funciona, se agrega la escritura.

## Arranque (fase 5)

1. Conteo físico completo, por departamentos, en las semanas previas.
2. Arranque en un día tranquilo.
3. Eleventa queda instalado solo para consulta unas semanas, como respaldo, con un plan para regresar si algo falla.
4. Capacitación a cajeros y encargado antes del arranque, con datos de prueba.
