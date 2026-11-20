-- Las ventas de impresiones y copias de eleventa por día, para `analizar-control-print --eleventa` (docs/impresion.md, «Analizarlos»).
-- SOLO LEE, y siempre sobre una COPIA de la base (nunca la de la caja principal): con el isql de Firebird,
--   isql -u SYSDBA -ch WIN1252 -i ventas-impresiones-eleventa.sql <copia>.fdb -o ventas-eleventa.csv
-- Deja un renglón «día,código,renglones,cantidad,importe» por día y producto; los encabezados y espacios de isql se saltan al leerlo.
-- Los códigos: IMP «Impresiones» y COP «Copias» ($10 a granel) e IMPEPWFC6590 (los centavos para montos exactos). Desde el 2026-09-26,
-- cuando empezó Control Print.
SET HEADING OFF;
SELECT CAST(CAST(T.VENDIDO_EN AS DATE) AS VARCHAR(10)) || ',' || A.PRODUCTO_CODIGO || ',' || CAST(COUNT(*) AS VARCHAR(10)) || ','
       || CAST(CAST(SUM(A.CANTIDAD) AS NUMERIC(15, 3)) AS VARCHAR(20)) || ','
       || CAST(CAST(SUM(COALESCE(A.TOTAL_ARTICULO, A.CANTIDAD * COALESCE(A.PRECIO_FINAL, A.PRECIO_USADO))) AS NUMERIC(15, 2)) AS VARCHAR(20))
FROM VENTATICKETS_ARTICULOS A JOIN VENTATICKETS T ON T.ID = A.TICKET_ID
WHERE T.ESTA_CANCELADO = 'f' AND T.ESTA_ABIERTO = 'f' AND A.PRODUCTO_CODIGO IN ('IMP', 'COP', 'IMPEPWFC6590')
  AND T.VENDIDO_EN >= '2026-09-26'
GROUP BY CAST(T.VENDIDO_EN AS DATE), A.PRODUCTO_CODIGO
ORDER BY 1;
