# Traspasos entre sucursales, con un archivo (provisional)

> **Provisional.** Pedido del dueño (2026-11-26): «una funcionalidad provisional hasta que exista el servidor». Hoy cada
> sucursal tiene su caja principal y su base, y no hay nada que las una ([pospuesto](plan.md#varias-sucursales-pospuesto)):
> lo que viaja de una sucursal a la otra es **un archivo**, en una memoria USB o como se pueda. Cuando exista el
> [central](servidor-remoto.md), esto se reemplaza por el traspaso con estados de allá; al final dice
> [qué se quita](#para-quitarlo-o-cambiarlo-cuando-exista-el-servidor).

Está en **Inventario (F4) › Traspasos**, la misma tarjeta en las dos sucursales.

## Cómo se usa

**En la sucursal que lo tramita (la que manda):**

1. «Nuevo traspaso: mandar mercancía». Se escanea lo que se manda (cada escaneo suma uno; el código de una caja de 12 suma
   doce) o se busca por nombre con F10. «Cuántos van» se corrige en su renglón; se ve lo que hay aquí. La lista se guarda
   sola, como borrador: se puede dejar y seguir después, o descartar.
2. Se escribe a qué sucursal va y «Exportar el archivo…». Pregunta antes, porque no se deshace: **ahí sale la mercancía**
   de la existencia de esta sucursal, y se guarda el archivo (`Traspaso a Sucursal 2 2026-11-26 4F2A-91C3.traspaso.json`)
   donde se elija, por ejemplo en la memoria.
3. La pantalla enseña la **clave** del traspaso (`4F2A-91C3`): se le dicta a la otra sucursal. El archivo se puede guardar
   otra vez las veces que haga falta (se perdió la memoria): es el mismo.

**En la sucursal que recibe:**

1. «Importar el archivo que llegó…» y se elige. Sale la lista: lo que dice el archivo junto al producto de aquí, y la
   misma clave. **Todavía no entra nada.**
2. Se escribe lo que **llegó** de cada producto (ya viene puesto lo que se mandó; si no llegó, 0), con una nota si hace
   falta. Lo que llegó y no venía se escanea o se busca, y entra como renglón «no venía». Lo que se escribe se guarda solo.
3. Para terminar:
   - Si llegó lo que dice el archivo, «Recibir»: pregunta, y **entra la mercancía**.
   - Si algo cambió, quien tiene el permiso «Traspasos: modificar lo que llegó» lo recibe así («Recibir con 2 cambios»).
     Quien no lo tiene lo deja «marcado para revisión»: el traspaso queda **en revisión**, con quién lo marcó, y lo abre
     quien sí tiene el permiso, que puede dejarlo como se marcó o cambiarlo, y lo recibe.

## Lo que se decidió (2026-11-26; lo marcado «por revisar» se decidió sin el dueño)

| Pregunta | Qué quedó | Estado |
|---|---|---|
| Dónde va | Inventario › Traspasos, una tarjeta junto a «Recibir mercancía», en las dos sucursales; adentro, «Nuevo traspaso» y «Importar el archivo», y la lista de los últimos 60 con su estado | Por revisar |
| Cuándo sale la mercancía | **Al exportar.** Un movimiento «Traspaso (salida)» por producto, a su costo promedio. El borrador no mueve nada | Por revisar |
| Cuándo entra | **Al recibir**, no al importar: mientras está «por recibir» o «en revisión» no entra nada (si se vende mientras, la existencia baja y se empareja al recibir, como siempre: no se bloquea la venta). Un «Traspaso (entrada)» por producto con lo que **llegó**, no con lo que decía el archivo | Por revisar |
| A qué costo entra | **Al costo de la sucursal que recibe** (su promedio; sin existencia, su último costo; sin ninguno, $0). El archivo **no lleva costos**: anda en una memoria, en manos de quien no los ve. El [modelo](modelo-datos.md) dice que un traspaso entra al costo con que salió; eso llega con el servidor, o antes si el dueño prefiere que el archivo los lleve | Por revisar |
| El permiso | Dos nuevos, en «Usuarios y permisos» › Inventario, los dos «Puede» o «No». **«Traspasos entre sucursales»**: armar, exportar, importar, marcar para revisión y recibir lo que dice el archivo. **«Traspasos: modificar lo que llegó»**: el del encargo (recibirlo distinto: otra cantidad, algo que no venía, otro producto). Al llegar, el primero lo tiene quien tiene «Agregar inventario» y el segundo quien tiene «Ajustar una existencia» (en «Puede»); el dueño los cambia cuando quiera | Por revisar: a quién le llegan |
| Sin el permiso de modificar | Escribe lo que llegó y sus notas (son marcas: nada entra), y «Dejar marcado para revisión». No lo puede recibir con cambios: el servicio lo rechaza y dice quién sí. Empatar un producto también cuenta como cambio | Hecho |
| El formato | Un JSON que se lee a simple vista (`.traspaso.json`): de dónde, a dónde, cuándo, quién, la nota y cada renglón con sus códigos de la pieza, su nombre, su unidad y cuántos. Con `formato`, `version` (una más nueva no se importa: primero se actualiza el POS) y `huella` | Hecho |
| Que no se importe dos veces | El traspaso tiene un número (su Id) que viaja en el archivo y es el mismo en las dos bases: el mismo archivo otra vez abre el que ya estaba y lo dice; en la sucursal de donde salió, no se importa | Hecho |
| Que no se importe alterado | La **huella** (SHA-256 de lo que dice) se vuelve a calcular al importar: si el archivo se dañó o alguien le cambió algo a mano, no entra. **No es un candado** contra quien sepa recalcularla: las dos sucursales no comparten ningún secreto (eso llega con el servidor). Para eso está la **clave** (8 letras de la huella): quien exporta la ve y la dicta; quien importa la ve y la compara. Queda en la bitácora de las dos | Por revisar: si hace falta más |
| Cómo se empatan los productos | Por los **códigos de la pieza** (el archivo los lleva todos). Se empata solo si el código está aquí, es de la pieza (no de un paquete), en la misma unidad, y todos sus códigos caen en un solo producto. La pantalla enseña los dos nombres, el del archivo y el de aquí: un código interno puede ser de productos distintos en cada sucursal, y así se ve | Por revisar |
| Lo que la sucursal no tiene | Queda **«sin empatar»**. Quien puede modificar lo empata con el producto de aquí (tiene otro código) o pone 0 en lo que llegó; ya dado de alta en Productos, entra con «Agregar inventario». No se dan de alta productos desde el traspaso (en el piloto, las altas son en eleventa). Con algo sin empatar y en más de cero no se recibe | Por revisar |
| El kardex | «Traspaso (salida)» y «Traspaso (entrada)», con «a «Sucursal 2» · 4F2A-91C3» o «de «Matriz» · …» y quién lo exportó o lo recibió; en Inventario › Movimientos ya se filtra por «Traspasos» y por quién | Hecho |
| La bitácora | Exportar (con lo que vale lo que salió), importar (con el nombre del archivo y quién lo exportó), marcar para revisión (con los cambios) y recibir (como venía, o con los cambios; esto último con el permiso de modificar) | Hecho |
| Deshacer | No hay: un borrador se descarta; lo exportado o lo recibido se corrige con un ajuste, como «Agregar inventario». Tampoco hay acuse de regreso: lo que llegó distinto no le avisa a la sucursal que mandó | Por revisar |
| **En el piloto con eleventa** | Hoy las dos sucursales llevan el inventario en eleventa, y su lectura deja aquí la existencia de allá: un movimiento hecho aquí se borraría solo. Por eso, con el piloto prendido, el traspaso **es solo el documento**: se arma, se exporta, se importa, se revisa y se recibe igual, queda en la bitácora, pero **no mueve existencias** en el POS, y la pantalla lo dice: lo que sale o entra se captura en eleventa. Al dejar eleventa, mueve las existencias sin cambiar nada | **Por revisar** (la más importante) |
| Sin la caja principal | No se hace: como «Agregar inventario», los traspasos solo van con conexión a la caja principal | Hecho |
| El celular | No: es de la caja de Windows (el dueño lo eligió así) | Hecho |

Límites: hasta 500 productos por traspaso, y de cada uno lo mismo que «Agregar inventario» (más de cero, hasta 99,999,
enteros en lo que se cuenta por pieza).

## Lo que no se pudo comprobar

- **La ventana de la caja de Windows.** Se probó en la caja del navegador (la que sirve el servicio en desarrollo), con
  dos servicios y dos bases ficticias, y con las pruebas automáticas. «Guardar como» y «Abrir» de Windows son los mismos
  de «Productos en Excel», pero con este archivo no se han visto en una PC con Windows: que el diálogo filtre
  `*.json` y que se pueda guardar en la memoria USB y abrir de ella.
- **Con el lector de verdad.** El escaneo se probó tecleando el código y Enter.
- **Dos sucursales de verdad:** si sus catálogos comparten los códigos (vienen del mismo eleventa) y cuánto queda «sin
  empatar».

## Para quitarlo o cambiarlo cuando exista el servidor

Todo está aparte, sin tocar lo demás del inventario:

- **Solo de esto** (se borran): `Traspaso.cs` y `ArchivoDeTraspaso.cs` (Nucleo/Inventario), `ContratosTraspasos.cs`,
  `RegistroTraspasos.cs`, `TraspasosConfiguracion.cs`, `ApiTraspasos.cs` (y su línea en `Program.cs`), `Traspasos.razor`,
  `TraspasosEnPantalla.cs`, el bloque «Traspasos» de `caja.css`, los métodos de `ClienteCaja` y sus pruebas
  (`TraspasosApiTests`, `TraspasosTests`, `ArchivoDeTraspasoTests`).
- **Se quedan**, porque el traspaso del servidor también los usa: los tipos de movimiento `TraspasoSalida` y
  `TraspasoEntrada` (ya existían), la tarjeta «Traspasos» de Inventario, los dos permisos (nunca se quitan del
  `enum`: la base los guarda por nombre y las cajas por número) y las tablas `Traspasos` y `TraspasosRenglones`, que son la
  historia de los movimientos del kardex que ya se hicieron (el traspaso del servidor puede seguir en ellas: ya llevan el
  estado, las dos cantidades y quién).
- **Lo que cambia:** el archivo deja de viajar (la lista llega sola a la otra sucursal), el Id sigue siendo el mismo en
  las dos, y lo que hoy no se puede (entrar al costo con que salió, avisarle a quien mandó lo que llegó distinto, un
  candado de verdad contra un archivo alterado) sale del servidor.
