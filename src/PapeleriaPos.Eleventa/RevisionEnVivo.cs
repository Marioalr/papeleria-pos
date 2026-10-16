using System.Globalization;

namespace PapeleriaPos.Eleventa;

/// <summary>
/// Mirando a eleventa trabajar de verdad (con <c>observar-eleventa</c>): entre dos fotos, cada cancelación, devolución o
/// cierre de turno que se ve se compara con lo que escribe la caja 2 en el piloto (docs/migracion-eleventa.md, «Cancelar,
/// devolver y cerrar el turno»). Dice qué coincide (✓), qué no (✗) y qué no se alcanza a ver en la foto (·). Las ventas y
/// las entradas y salidas de dinero no se revisan aquí.
/// </summary>
public static class RevisionEnVivo
{
    /// <summary>Lo que escribe la caja 2 al devolver o cerrar: si eleventa toca otra tabla, hay algo que todavía no hace.</summary>
    private static readonly HashSet<string> TablasConocidas = new(StringComparer.Ordinal)
    {
        "DEVOLUCIONES", "DEVOLUCIONES_ARTICULOS", "VENTATICKETS", "VENTATICKETS_ARTICULOS", "CORTE_MOVIMIENTOS", "INVENTARIO_HISTORIAL",
        "INVENTARIO_BALANCES", "INVENTARIO_APARTADO", "TURNOS", "CORTE_OPERACIONES", "CORTE_VENTAS_POR_DEPTO", "CORTE_VENTAS_DEPTO_OPERACIONES",
        "CAJAS",
    };

    public static IReadOnlyList<string> Revisar(FotoEleventa antes, FotoEleventa despues)
    {
        var f = new Fotos(antes, despues);
        var lineas = new List<string>();
        var devoluciones = f.Nuevos("DEVOLUCIONES").ToList();
        foreach (var d in devoluciones)
            lineas.AddRange(Devolucion(f, d));
        if (devoluciones.Count > 0)
            lineas.AddRange(AcumuladosDeDevoluciones(f, devoluciones));

        var cierres = f.Cambiados("TURNOS").Where(t => t.Antes["TERMINO_EN"] == "∅" && t.Despues["TERMINO_EN"] != "∅").ToList();
        foreach (var turno in cierres)
            lineas.AddRange(Cierre(f, turno));

        if (devoluciones.Count > 0 || cierres.Count > 0)
        {
            var otras = f.TablasCambiadas().Where(t => !TablasConocidas.Contains(t)).ToList();
            if (otras.Count > 0)
                lineas.Add($"✗ Eleventa también tocó {string.Join(", ", otras)}, que la caja 2 no escribe: hay que ver qué es.");
        }
        return lineas;
    }

    private static IEnumerable<string> Devolucion(Fotos f, Renglon d)
    {
        var id = d.Clave;
        var cancelacion = d["TIPO_DEVOLUCION"] == "C";
        var total = Numero(d["TOTAL_DEVUELTO"]);
        var ticketId = d["TICKET_ID"];
        var ticket = f.Despues("VENTATICKETS", ticketId);
        var ticketAntes = f.Antes("VENTATICKETS", ticketId);
        var folio = ticket?["FOLIO"] ?? FolioDeLosMovimientos(f, ticketId);
        yield return $"{(cancelacion ? "Cancelación" : "Devolución")} del ticket #{folio} (DEVOLUCIONES {id}, {Dinero(total)}):";
        // Lo que falta ver en vivo (docs/pruebas-en-tienda.md): cómo anota eleventa la de un ticket de otro turno o de otra caja.
        // Mientras, la caja 2 la deja por revisar; esto dice dónde quedó.
        if ((ticket ?? f.Antes("VENTATICKETS", ticketId)) is { } delTicket && delTicket["TURNO_ID"] != d["TURNO_ID"])
            yield return $"  · el ticket se cobró en el turno {delTicket["TURNO_ID"]} (caja {delTicket["CAJA_ID"]}) y la devolución quedó en el turno "
                + $"{d["TURNO_ID"]} («{d["CAJA"]}»): así anota eleventa la de otro turno u otra caja; la caja 2 todavía la deja por revisar";

        var articulos = f.NuevosDonde("DEVOLUCIONES_ARTICULOS", r => r["DEVOLUCION_ID"] == id).ToList();
        if (cancelacion)
        {
            if (ticket is null)
                yield return "  · el ticket no sale en la foto (es de hace mucho): no se ve cómo quedó";
            else
            {
                yield return Si(ticket["ESTA_CANCELADO"] is "t" or "T", "el ticket quedó cancelado", $"ESTA_CANCELADO quedó en «{ticket["ESTA_CANCELADO"]}»");
                yield return Si(Math.Abs(Numero(ticket["GANANCIA"])) < 0.01m, "su ganancia quedó en cero", $"su ganancia quedó en {ticket["GANANCIA"]} (la caja 2 la deja en cero)");
                if (ticketAntes is not null)
                {
                    yield return Si(Numero(ticketAntes["TOTAL"]) == Numero(ticket["TOTAL"]), "conserva su total", $"su total pasó de {ticketAntes["TOTAL"]} a {ticket["TOTAL"]} (la caja 2 no lo cambia)");
                    yield return Si(Igual(Numero(ticketAntes["SUBTOTAL"]) - Numero(ticket["SUBTOTAL"]), total), "su subtotal bajó lo devuelto",
                        $"su subtotal pasó de {ticketAntes["SUBTOTAL"]} a {ticket["SUBTOTAL"]} (la caja 2 lo baja {Dinero(total)})");
                    // Se devuelve lo que queda en el ticket: sus renglones, que la cancelación no toca.
                    var lineas = f.TodosDespues("VENTATICKETS_ARTICULOS").Where(a => a["TICKET_ID"] == ticketId).Select(a => (a["PRODUCTO_CODIGO"], Numero(a["CANTIDAD"]))).ToList();
                    yield return lineas.Count == 0 ? "  · sus artículos no salen en la foto: no se ve cuánto debía bajar NUMERO_ARTICULOS"
                        : NumeroDeArticulos(f, ticketAntes, ticket, lineas);
                }
            }
            yield return Si(articulos.Count == 0, "sin renglones en DEVOLUCIONES_ARTICULOS", $"dejó {articulos.Count} renglones en DEVOLUCIONES_ARTICULOS (la caja 2 no deja ninguno)");
        }
        else
        {
            yield return Si(articulos.Count > 0 && Igual(articulos.Sum(a => Numero(a["DINERO_DEVUELTO"])), total),
                $"{articulos.Count} renglones en DEVOLUCIONES_ARTICULOS que suman lo devuelto",
                $"DEVOLUCIONES_ARTICULOS: {articulos.Count} renglones que suman {Dinero(articulos.Sum(a => Numero(a["DINERO_DEVUELTO"])))}");
            if (ticket is null || ticketAntes is null)
                yield return "  · el ticket no sale en las dos fotos: no se ve cuánto bajó su total";
            else
            {
                yield return Si(Igual(Numero(ticketAntes["TOTAL"]) - Numero(ticket["TOTAL"]), total), "el total del ticket bajó lo devuelto",
                    $"el total del ticket pasó de {ticketAntes["TOTAL"]} a {ticket["TOTAL"]} (la caja 2 lo baja {Dinero(total)})");
                yield return NumeroDeArticulos(f, ticketAntes, ticket, articulos.Select(a => (a["CODIGO_PRODUCTO"], Numero(a["CANTIDAD_DEVUELTA"]))));
            }
            foreach (var a in articulos)
                yield return ArticuloDevuelto(f, ticketId, a);
        }

        var movimientos = f.NuevosDonde("CORTE_MOVIMIENTOS", m => m["TIPO"] == "Devolucion" && m["DESCRIPCION"].EndsWith($"Ticket #{folio}", StringComparison.Ordinal)).ToList();
        var esperados = cancelacion ? f.Despues("VENTATICKETS_ARTICULOS")?.Renglones.Values.Count(a => Valor(f.Columnas("VENTATICKETS_ARTICULOS"), a, "TICKET_ID") == ticketId) : articulos.Count;
        yield return Si(movimientos.Count > 0 && Igual(movimientos.Sum(m => Numero(m["MONTO"])), total)
                && (esperados is null or 0 || movimientos.Count == esperados)
                && movimientos.All(m => m["FORMA_PAGO"] == "E" && m["RAZON_DEVOLUCION"] == "V"),
            $"{movimientos.Count} movimientos «Devolucion» del corte, uno por artículo, que suman lo devuelto",
            $"movimientos del corte: {movimientos.Count} que suman {Dinero(movimientos.Sum(m => Numero(m["MONTO"])))}" +
            (esperados is > 0 ? $" (se esperaban {esperados})" : "") + $"; forma y razón: {string.Join(", ", movimientos.Select(m => $"{m["FORMA_PAGO"]}/{m["RAZON_DEVOLUCION"]}").Distinct())}");

        var entradas = f.NuevosDonde("INVENTARIO_HISTORIAL", h => h["DESCRIPCION"] == $"Devolución de venta #{folio}").ToList();
        yield return Si(entradas.Count > 0 && entradas.All(h => Numero(h["CANTIDAD"]) < 0),
            $"{entradas.Count} entradas al inventario «Devolución de venta #{folio}», en negativo",
            entradas.Count == 0 ? "no hubo entrada al inventario (¿todo era común o volvió dañado?)" : "alguna entrada al inventario no va en negativo");
    }

    /// <summary>
    /// <c>NUMERO_ARTICULOS</c> baja las piezas devueltas; lo de granel (<c>TVENTA = 'D'</c> en <c>PRODUCTOS</c>) contó uno al
    /// vender y no baja.
    /// </summary>
    private static string NumeroDeArticulos(Fotos f, Renglon antes, Renglon despues, IEnumerable<(string Codigo, decimal Cantidad)> devuelto)
    {
        var granel = f.TodosDespues("PRODUCTOS").Where(p => p["TVENTA"].Trim() == "D").Select(p => p["CODIGO"].Trim()).ToHashSet(StringComparer.Ordinal);
        var piezas = devuelto.Where(a => !granel.Contains(a.Codigo.Trim())).Sum(a => Math.Round(a.Cantidad, MidpointRounding.AwayFromZero));
        var queda = Numero(antes["NUMERO_ARTICULOS"]) - piezas;
        return Si(Numero(despues["NUMERO_ARTICULOS"]) == queda, $"NUMERO_ARTICULOS bajó las piezas (lo de granel no): quedó en {queda:0}",
            $"NUMERO_ARTICULOS pasó de {antes["NUMERO_ARTICULOS"]} a {despues["NUMERO_ARTICULOS"]} (la caja 2 lo deja en {queda:0}: baja las piezas, lo de granel no)");
    }

    /// <summary>En la parcial, el artículo baja su cantidad, o se borra si se devolvió completo.</summary>
    private static string ArticuloDevuelto(Fotos f, string ticketId, Renglon devuelto)
    {
        var codigo = devuelto["CODIGO_PRODUCTO"];
        var cantidad = Numero(devuelto["CANTIDAD_DEVUELTA"]);
        bool DelTicket(Renglon r) => r["TICKET_ID"] == ticketId && r["PRODUCTO_CODIGO"] == codigo;
        var antes = f.TodosAntes("VENTATICKETS_ARTICULOS").FirstOrDefault(DelTicket);
        if (antes is null)
            return $"  · «{codigo}» no sale en la foto de antes: no se ve cómo quedó";
        var despues = f.Despues("VENTATICKETS_ARTICULOS", antes.Clave);
        if (despues is null)
            return Si(Igual(Numero(antes["CANTIDAD"]), cantidad), $"«{codigo}» se devolvió completo y su renglón se borró",
                $"«{codigo}» tenía {antes["CANTIDAD"]}, se devolvieron {devuelto["CANTIDAD_DEVUELTA"]} y su renglón se borró (la caja 2 solo lo borra si se devuelve completo)");
        return Si(Igual(Numero(antes["CANTIDAD"]) - Numero(despues["CANTIDAD"]), cantidad), $"«{codigo}» bajó {devuelto["CANTIDAD_DEVUELTA"]}",
            $"«{codigo}» pasó de {antes["CANTIDAD"]} a {despues["CANTIDAD"]} con {devuelto["CANTIDAD_DEVUELTA"]} devueltos");
    }

    /// <summary>
    /// Los acumulados del turno, el día y los departamentos, de todas las devoluciones que se vieron juntas. Una venta sube
    /// el efectivo vendido (una devolución no lo toca): si en el mismo rato hubo ventas, lo vendido no se puede separar y
    /// solo se revisa lo devuelto.
    /// </summary>
    private static IEnumerable<string> AcumuladosDeDevoluciones(Fotos f, List<Renglon> devoluciones)
    {
        var juntas = devoluciones.Count > 1 ? $" (de las {devoluciones.Count} juntas)" : "";
        var turnosSinVentas = new HashSet<string>();
        foreach (var turnoId in devoluciones.Select(d => d["TURNO_ID"]).Distinct())
        {
            var (antes, despues) = (f.Antes("TURNOS", turnoId), f.Despues("TURNOS", turnoId));
            if (antes is null || despues is null)
            {
                yield return $"  · el turno {turnoId} no sale en las fotos";
                continue;
            }
            decimal Delta(string columna) => Numero(despues[columna]) - Numero(antes[columna]);
            var deEste = devoluciones.Where(d => d["TURNO_ID"] == turnoId).ToList();
            var (suma, cuantas) = (deEste.Sum(d => Numero(d["TOTAL_DEVUELTO"])), deEste.Count(d => d["TIPO_DEVOLUCION"] == "C"));
            yield return Si(Igual(Delta("DEVOLUCIONES_VENTAS_EFECTIVO"), suma), $"el turno {turnoId}{juntas}: lo devuelto subió {Dinero(suma)}",
                $"el turno {turnoId}{juntas}: lo devuelto cambió {Dinero(Delta("DEVOLUCIONES_VENTAS_EFECTIVO"))} (la caja 2: +{Dinero(suma)})");
            if (!Igual(Delta("VENTAS_EFECTIVO"), 0))
            {
                yield return $"  · el turno {turnoId}: en el mismo rato hubo ventas, no se puede separar lo vendido";
                continue;
            }
            turnosSinVentas.Add(turnoId);
            yield return Si(Igual(Delta("ACUMULADO_VENTAS"), -suma) && Igual(Delta("NUMERO_VENTAS"), -cuantas),
                $"el turno {turnoId}{juntas}: lo vendido bajó {Dinero(suma)} y el número de ventas {cuantas}",
                $"el turno {turnoId}{juntas}: lo vendido cambió {Dinero(Delta("ACUMULADO_VENTAS"))} y el número de ventas {Delta("NUMERO_VENTAS"):0} (la caja 2: −{Dinero(suma)} y −{cuantas})");
        }

        var total = devoluciones.Sum(d => Numero(d["TOTAL_DEVUELTO"]));
        var delDia = f.Cambiados("CORTE_OPERACIONES").Sum(o => Numero(o.Despues["DEVOLUCIONES_VENTAS_EFECTIVO"]) - Numero(o.Antes["DEVOLUCIONES_VENTAS_EFECTIVO"]));
        yield return Si(Igual(delDia, total), $"el día{juntas}: lo devuelto subió {Dinero(total)}", $"el día{juntas}: lo devuelto cambió {Dinero(delDia)} (la caja 2: +{Dinero(total)})");

        if (turnosSinVentas.Count > 0)
        {
            var suma = devoluciones.Where(d => turnosSinVentas.Contains(d["TURNO_ID"])).Sum(d => Numero(d["TOTAL_DEVUELTO"]));
            var deptos = f.Cambiados("CORTE_VENTAS_POR_DEPTO").Where(x => turnosSinVentas.Contains(x.Despues["ID_TURNO"]))
                .Sum(x => Numero(x.Despues["ACUMULADO_VENTAS"]) - Numero(x.Antes["ACUMULADO_VENTAS"]))
                + f.Nuevos("CORTE_VENTAS_POR_DEPTO").Where(x => turnosSinVentas.Contains(x["ID_TURNO"])).Sum(x => Numero(x["ACUMULADO_VENTAS"]));
            yield return Si(Igual(deptos, -suma), $"los departamentos del turno{juntas}: bajaron {Dinero(suma)}",
                $"los departamentos del turno{juntas}: cambiaron {Dinero(deptos)} (la caja 2: −{Dinero(suma)})");
        }
    }

    private static IEnumerable<string> Cierre(Fotos f, (Renglon Antes, Renglon Despues) turno)
    {
        var t = turno.Despues;
        var id = t.Clave;
        yield return $"Cierre del turno {id} (caja {t["ID_CAJA"]}):";

        var esperado = Numero(t["DINERO_INICIAL"]) + Numero(t["VENTAS_EFECTIVO"]) - Numero(t["DEVOLUCIONES_VENTAS_EFECTIVO"])
            + Numero(t["ACUMULADO_ENTRADAS"]) - Numero(t["ACUMULADO_SALIDAS"]);
        yield return Si(Igual(Numero(t["EFECTIVO_AL_CIERRE"]), esperado),
            $"el efectivo al cierre ({Dinero(Numero(t["EFECTIVO_AL_CIERRE"]))}) es el fondo más el efectivo, menos lo devuelto, más entradas y menos salidas",
            $"el efectivo al cierre es {Dinero(Numero(t["EFECTIVO_AL_CIERRE"]))} y la cuenta de la caja 2 da {Dinero(esperado)}");
        yield return Si(t["ID_OPERACION_FIN"] == "∅", "ID_OPERACION_FIN sigue vacío", $"ID_OPERACION_FIN quedó en {t["ID_OPERACION_FIN"]} (la caja 2 no lo llena)");

        // Al cobrar, eleventa usa el ticket abierto para la venta y abre otro: si se cobró en el mismo rato, el de antes ya
        // es una venta y el que se borró no alcanza a salir en ninguna foto.
        bool Abierto(Renglon v) => v["TURNO_ID"] == id && v["ESTA_ABIERTO"] is "t" or "T";
        var abiertos = f.TodosAntes("VENTATICKETS").Where(Abierto).ToList();
        var siguen = f.TodosDespues("VENTATICKETS").Where(Abierto).ToList();
        if (abiertos.Count == 0 && siguen.Count == 0)
            yield return "  · su ticket abierto no sale en la foto de antes";
        else
            yield return Si(siguen.Count == 0,
                abiertos.All(v => f.Despues("VENTATICKETS", v.Clave) is null) ? "su ticket abierto se borró"
                    : "no le quedó ticket abierto: el que tenía se cobró en el mismo rato y el siguiente se borró",
                "su ticket abierto sigue ahí (la caja 2 lo borra)");

        var diferencias = f.NuevosDonde("CORTE_MOVIMIENTOS", m => m["ID_TURNO"] == id
            && (m["DESCRIPCION"].StartsWith("Efectivo faltante", StringComparison.Ordinal) || m["DESCRIPCION"].StartsWith("Efectivo sobrante", StringComparison.Ordinal))).ToList();
        decimal Delta(string columna) => Numero(t[columna]) - Numero(turno.Antes[columna]);
        if (diferencias.Count == 0)
            yield return Si(Igual(Delta("ACUMULADO_SALIDAS"), 0) && Igual(Delta("ACUMULADO_ENTRADAS"), 0), "cuadró: sin faltante ni sobrante",
                "las entradas o salidas del turno cambiaron sin un movimiento de faltante o sobrante");
        foreach (var m in diferencias)
        {
            var falto = m["DESCRIPCION"].StartsWith("Efectivo faltante", StringComparison.Ordinal);
            var columna = falto ? "ACUMULADO_SALIDAS" : "ACUMULADO_ENTRADAS";
            yield return Si(m["TIPO"] == (falto ? "Salida" : "Entrada") && Igual(Delta(columna), Numero(m["MONTO"])),
                $"el {(falto ? "faltante" : "sobrante")} de {Dinero(Numero(m["MONTO"]))} quedó como {m["TIPO"]} y en {columna} del turno",
                $"el {(falto ? "faltante" : "sobrante")} de {Dinero(Numero(m["MONTO"]))} quedó como «{m["TIPO"]}» y {columna} del turno cambió {Dinero(Delta(columna))}");
        }
        // El día lo lleva solo si ya tenía renglón al cerrar (eleventa no lo crea para esto), como una entrada o salida.
        if (diferencias.Count > 0)
        {
            var fecha = t["TERMINO_EN"].Length >= 10 ? t["TERMINO_EN"][..10] : "";
            var delDia = f.TodosAntes("CORTE_OPERACIONES").FirstOrDefault(o => o["ID_CAJA"] == t["ID_CAJA"] && o["DIA"].StartsWith(fecha, StringComparison.Ordinal));
            var cambiados = f.Cambiados("CORTE_OPERACIONES")
                .Where(o => o.Antes["ACUMULADO_SALIDAS"] != o.Despues["ACUMULADO_SALIDAS"] || o.Antes["ACUMULADO_ENTRADAS"] != o.Despues["ACUMULADO_ENTRADAS"]).ToList();
            if (delDia is null)
                yield return Si(cambiados.Count == 0, "ese día no tenía renglón: no lo lleva", "ese día no tenía renglón, pero algún día cambió sus entradas o salidas");
            else
            {
                var despues = f.Despues("CORTE_OPERACIONES", delDia.Clave);
                var neto = diferencias.Sum(m => m["DESCRIPCION"].StartsWith("Efectivo faltante", StringComparison.Ordinal) ? -Numero(m["MONTO"]) : Numero(m["MONTO"]));
                var cambio = despues is null ? 0
                    : Numero(despues["ACUMULADO_ENTRADAS"]) - Numero(delDia["ACUMULADO_ENTRADAS"]) - (Numero(despues["ACUMULADO_SALIDAS"]) - Numero(delDia["ACUMULADO_SALIDAS"]));
                yield return Si(Igual(cambio, neto), "el día también lo lleva (ya tenía renglón)",
                    $"el día ya tenía renglón y sus entradas menos salidas cambiaron {Dinero(cambio)} (la caja 2: {Dinero(neto)})");
            }
        }
    }

    private static string FolioDeLosMovimientos(Fotos f, string ticketId) =>
        f.Nuevos("CORTE_MOVIMIENTOS").Select(m => m["DESCRIPCION"]).FirstOrDefault(d => d.Contains("Ticket #", StringComparison.Ordinal)) is { } descripcion
            ? descripcion[(descripcion.LastIndexOf('#') + 1)..]
            : $"(ID {ticketId})";

    private static string Si(bool coincide, string bien, string distinto) => coincide ? $"  ✓ {bien}" : $"  ✗ {distinto}";

    private static bool Igual(decimal a, decimal b) => Math.Abs(a - b) < 0.01m;

    private static decimal Numero(string texto) =>
        decimal.TryParse(texto, NumberStyles.Float, CultureInfo.InvariantCulture, out var n) ? n
        : double.TryParse(texto, NumberStyles.Float, CultureInfo.InvariantCulture, out var d) ? (decimal)d : 0;

    private static string Dinero(decimal pesos) => pesos.ToString("$#,##0.00;−$#,##0.00", CultureInfo.InvariantCulture);

    private static string Valor(IReadOnlyList<string> columnas, string[] valores, string columna) =>
        columnas.ToList().IndexOf(columna) is var i and >= 0 && i < valores.Length ? valores[i] : "∅";

    /// <summary>Un renglón de una foto, por nombre de columna; lo que no está es «∅».</summary>
    private sealed record Renglon(string Clave, IReadOnlyList<string> Columnas, string[] Valores)
    {
        public string this[string columna] => Valor(Columnas, Valores, columna);
    }

    private sealed class Fotos(FotoEleventa antes, FotoEleventa despues)
    {
        public TablaEnFoto? Despues(string tabla) => despues.Tablas.GetValueOrDefault(tabla);

        public IReadOnlyList<string> Columnas(string tabla) => (despues.Tablas.GetValueOrDefault(tabla) ?? antes.Tablas.GetValueOrDefault(tabla))?.Columnas ?? [];

        public Renglon? Antes(string tabla, string clave) => Buscar(antes, tabla, clave);

        public Renglon? Despues(string tabla, string clave) => Buscar(despues, tabla, clave);

        public IEnumerable<Renglon> TodosAntes(string tabla) => Todos(antes, tabla);

        public IEnumerable<Renglon> TodosDespues(string tabla) => Todos(despues, tabla);

        /// <summary>Lo que aparece en la foto de después y no estaba antes (si la de antes lo alcanzaba a ver).</summary>
        public IEnumerable<Renglon> Nuevos(string tabla)
        {
            var vieja = antes.Tablas.GetValueOrDefault(tabla);
            var desde = vieja is { Completa: false } ? vieja.Renglones.Keys.Select(Llave).Where(n => n is not null).Min() : null;
            return Todos(despues, tabla).Where(r => vieja is null || !vieja.Renglones.ContainsKey(r.Clave) && (desde is null || Llave(r.Clave) >= desde));
        }

        public IEnumerable<Renglon> NuevosDonde(string tabla, Func<Renglon, bool> condicion) => Nuevos(tabla).Where(condicion);

        public IEnumerable<(Renglon Antes, Renglon Despues)> Cambiados(string tabla) =>
            Todos(despues, tabla).Select(r => (Antes: Antes(tabla, r.Clave), Despues: r))
                .Where(x => x.Antes is not null && !x.Antes.Valores.SequenceEqual(x.Despues.Valores))
                .Select(x => (x.Antes!, x.Despues));

        public IEnumerable<string> TablasCambiadas() =>
            despues.Tablas.Where(t => antes.Tablas.GetValueOrDefault(t.Key) is not { } vieja || vieja.Filas != t.Value.Filas
                    || t.Value.Renglones.Any(r => !vieja.Renglones.TryGetValue(r.Key, out var v) || !v.SequenceEqual(r.Value)))
                .Select(t => t.Key);

        private static Renglon? Buscar(FotoEleventa foto, string tabla, string clave) =>
            foto.Tablas.GetValueOrDefault(tabla) is { } t && t.Renglones.TryGetValue(clave, out var valores) ? new Renglon(clave, t.Columnas, valores) : null;

        private static IEnumerable<Renglon> Todos(FotoEleventa foto, string tabla) =>
            foto.Tablas.GetValueOrDefault(tabla) is { } t ? t.Renglones.Select(r => new Renglon(r.Key, t.Columnas, r.Value)) : [];

        private static long? Llave(string clave) => long.TryParse(clave, NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) ? n : null;
    }
}
