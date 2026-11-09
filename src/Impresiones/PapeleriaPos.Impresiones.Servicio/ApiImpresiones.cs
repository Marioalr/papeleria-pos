using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using PapeleriaPos.Datos;
using PapeleriaPos.Impresiones.Contratos;
using PapeleriaPos.Impresiones.Datos;
using PapeleriaPos.Impresiones.Medicion;
using PapeleriaPos.Nucleo;
using PapeleriaPos.Nucleo.Contratos;
using PapeleriaPos.Nucleo.Control;

namespace PapeleriaPos.Impresiones.Servicio;

/// <summary>
/// Impresiones (entrega 5): lo que mandan los agentes, lo que falta cobrar, las no vendidas, cotizar un PDF, las copias
/// del panel y las impresoras de la sucursal (niveles, contador y calibración de cada una; varias desde el 2026-11-03).
/// </summary>
public static class ApiImpresiones
{
    /// <summary>
    /// Las rutas del módulo, sobre el grupo que le da el servicio:
    /// <c>app.MapGroup("/api").ConErrores().SoloSuCaja().MapearApiImpresiones()</c>. Lo que vale para toda la API (los
    /// errores para el cajero, que una caja solo opere la suya) lo pone el servicio, no el módulo. Lo que es del POS (los
    /// permisos de cada usuario, los nombres) sale de su base.
    /// </summary>
    public static RouteGroupBuilder MapearApiImpresiones(this RouteGroupBuilder api)
    {
        // El agente de la caja manda cada trabajo impreso; el servicio le pone costo y precio y lo suma al lote que se
        // verifica con el contador de la impresora.
        // En la caja 2 (con Control Print configurado, 2026-10-25) el precio sale de los costos de Control Print, leídos de su
        // servidor (de memoria mientras estén al día; si no contesta, los de antes).
        // De qué impresora es lo dice la caja (su número, 2026-11-03); sin decirlo, de la primera. Con Control Print, sus costos son los
        // de la sucursal, con lo propio de la impresora encima (la L5590 no está en Control Print).
        api.MapPost("/impresiones", async (TrabajoMedidoDto t, PosDbContext db, VigilanciaImpresora vigilancia, ControlPrintEnElServicio controlPrint) =>
        {
            var nuevo = await db.Set<TrabajoImpresion>().FindAsync(t.Id) is null;
            var costos = nuevo && controlPrint.Cola.Configurado ? (await controlPrint.Cola.CostosAsync())?.Costos : null;
            var trabajo = await ImpresionesDelPos.Registro(db).RegistrarAsync(t, costos);
            if (nuevo)
                await vigilancia.TrabajoRegistradoAsync(trabajo);
            return new { trabajo.Id, trabajo.PrecioCentavos };
        });

        // Lo que falta cobrar en la sucursal, de todas las PC y sus impresoras (con más de una, cada tarjeta dice de cuál). El costo solo
        // va a quien tiene «Ver costos, utilidad y margen».
        api.MapGet("/sucursales/{sucursalId:guid}/impresiones/por-cobrar", async (Guid sucursalId, Guid usuarioId, PosDbContext db) =>
        {
            var registro = ImpresionesDelPos.Registro(db);
            var trabajos = await registro.PorCobrarAsync(sucursalId);
            var costos = await registro.CostosAsync(sucursalId);
            var veCosto = await VeCostoAsync(db, usuarioId);
            var nombres = await NombresAsync(db, trabajos);
            var impresoras = await ImpresionesDelPos.Impresora(db).NombresAsync(sucursalId);
            return trabajos.Select(t => PorCobrar(t, nombres, veCosto, costos, impresoras)).ToList();
        });

        // La miniatura de una impresión por cobrar (PNG), cuando F2 la va a mostrar (revisión de #155): la lista ya no las trae todas.
        api.MapGet("/impresiones/{id:guid}/miniatura", async (Guid id, PosDbContext db) =>
            await ImpresionesDelPos.Registro(db).MiniaturaAsync(id) is { Length: > 0 } png ? Results.Bytes(png, "image/png") : Results.NoContent());

        // ── Control Print, solo leyendo (las tarjetas de su cola en F2, 2026-10-25) ──

        // Su cola por cobrar, sin lo que ya se cobró en esta caja. Con la versión que ya tiene la caja, nada (204): no se vuelve a
        // pintar. El costo, como el de las demás, solo a quien tiene «Ver costos, utilidad y margen».
        api.MapGet("/sucursales/{sucursalId:guid}/impresiones/control-print", async (Guid sucursalId, Guid usuarioId, long? version, PosDbContext db,
            ControlPrintEnElServicio controlPrint) =>
        {
            var cola = await controlPrint.ParaLaCajaAsync(db, sucursalId, await VeCostoAsync(db, usuarioId));
            return version == cola.Version ? Results.NoContent() : Results.Ok(cola);
        });

        // Cuántas faltan por cobrar (las de la sucursal y las de Control Print) y si Control Print contesta, sin las listas: el chip de
        // arriba y F2, que con esto sabe si tiene que volver a leer. También lo cobrado aquí y en Control Print (2026-10-29), para el chip.
        api.MapGet("/sucursales/{sucursalId:guid}/impresiones/por-cobrar/cuantas", async (Guid sucursalId, PosDbContext db, ControlPrintEnElServicio controlPrint) =>
        {
            var (cuantas, firma) = await ImpresionesDelPos.Registro(db).CuantosPorCobrarAsync(sucursalId);
            var cola = await controlPrint.ParaLaCajaAsync(db, sucursalId, conCosto: false);
            return new ResumenPorCobrarDto(cuantas, firma, cola.Trabajos.Count, cola.Version, cola.Configurado, cola.Contesta, cola.Aviso,
                cola.CobradasDosVeces);
        });

        // La miniatura de una tarjeta de Control Print (PNG), cuando la caja la va a mostrar: se guarda en memoria del servicio.
        api.MapGet("/impresiones/control-print/{id:long}/miniatura", async (long id, ControlPrintEnElServicio controlPrint) =>
            await controlPrint.Cola.MiniaturaAsync(id) is { } png ? Results.Bytes(png, "image/png") : Results.NoContent());

        // Cómo ve este servicio a Control Print (sin el código): lo lee poner-codigo-controlprint.ps1 después de poner el código.
        api.MapGet("/controlprint", (ControlPrintEnElServicio controlPrint, PosDbContext db) => controlPrint.EstadoParaVerAsync(db));

        api.MapPost("/impresiones/{id:guid}/no-vendida", async (Guid id, SolicitudNoVendida s, PosDbContext db) =>
        {
            await ImpresionesDelPos.Registro(db).NoVendidaAsync(id, s);
            return Results.NoContent();
        });

        // Con un atasco o una cancelación que contó la impresora: se cobra solo lo que salió.
        api.MapPost("/impresiones/{id:guid}/solo-lo-que-salio", async (Guid id, SolicitudSoloLoQueSalio s, PosDbContext db) =>
        {
            var registro = ImpresionesDelPos.Registro(db);
            var trabajo = await registro.SoloLoQueSalioAsync(id);
            return PorCobrar(trabajo, await NombresAsync(db, [trabajo]), await VeCostoAsync(db, s.UsuarioId), await registro.CostosAsync(trabajo.SucursalId),
                await ImpresionesDelPos.Impresora(db).NombresAsync(trabajo.SucursalId));
        });

        // El desglose de la línea "Impresiones": todo lo que se imprimió en un día y cómo se cobró, para quien tiene «Consultar
        // la bitácora» (con los usuarios de antes, el dueño y el encargado, como con los roles); el costo, con «Ver costos,
        // utilidad y margen».
        api.MapGet("/sucursales/{sucursalId:guid}/impresiones", async (Guid sucursalId, DateOnly fecha, Guid usuarioId, PosDbContext db) =>
        {
            var control = new ServicioControl(db);
            if (!await control.PuedeAsync(usuarioId, AccionControlada.ConsultarBitacora))
                throw new PermisoRequerido(AccionControlada.ConsultarBitacora, NivelPermiso.NoPermitido,
                    $"El desglose de las impresiones lo ve quien tiene el permiso «{Permisos.Nombre(AccionControlada.ConsultarBitacora)}»: "
                    + $"{Permisos.Nombres(await control.QuienesPuedenAsync(sucursalId, AccionControlada.ConsultarBitacora))}.");
            var veCosto = await VeCostoAsync(db, usuarioId);
            var trabajos = await ImpresionesDelPos.Registro(db).DelDiaAsync(sucursalId, fecha);
            var nombres = await NombresAsync(db, trabajos);
            var impresoras = await ImpresionesDelPos.Impresora(db).NombresAsync(sucursalId);
            return trabajos.Select(t => new ImpresionDelDiaDto(t.Id, t.Tipo, t.Documento, t.Pc, HoraMexico.ALocal(t.ImpresoUtc), t.Caras,
                t.CarasContadas, t.Color, t.PrecioCentavos, t.Cobro, t.MotivoNoVendida, t.AtendioId is { } a ? nombres.GetValueOrDefault(a) : null,
                veCosto ? RegistroImpresiones.Costo(t) : null, impresoras(t.ImpresoraId))).ToList();
        });

        // ── Cotizar y copias ─────────────────────────────────────────────────

        // Con qué se cotiza: los tipos de papel, y (2026-11-08) con qué se imprime de verdad en cada impresora y dónde imprime la caja que
        // pregunta: cotizar empieza con eso.
        api.MapGet("/sucursales/{sucursalId:guid}/impresiones/opciones", async (Guid sucursalId, Guid? cajaId, PosDbContext db) =>
        {
            var opciones = await ImpresionesDelPos.Registro(db).OpcionesAsync(sucursalId);
            var (habituales, deLaCaja) = await ImpresionesDelPos.Cotizaciones(db).HabitualesAsync(sucursalId, cajaId);
            return opciones with { Habituales = habituales, ImpresoraDeLaCaja = deLaCaja };
        });

        // La caja midió el PDF; aquí se le pone precio con los costos de la impresora donde se imprimiría, igual que al imprimirlo: en la caja
        // 2, con los de Control Print (2026-11-08: antes se cotizaba con los guardados y se imprimía con los leídos). Con su Id y el documento,
        // se guarda. El costo, para quien lo ve.
        api.MapPost("/impresiones/cotizar", async (SolicitudCotizacion s, PosDbContext db, ControlPrintEnElServicio controlPrint) =>
        {
            var costos = controlPrint.Cola.Configurado ? (await controlPrint.Cola.CostosAsync())?.Costos : null;
            return await ImpresionesDelPos.Registro(db).CotizarAsync(s, s.UsuarioId is { } u && await VeCostoAsync(db, u), deLaSucursal: costos);
        });

        // Las cotizaciones guardadas de la sucursal de los últimos 15 días (2026-11-08), sin costos.
        api.MapGet("/sucursales/{sucursalId:guid}/impresiones/cotizaciones", (Guid sucursalId, PosDbContext db) =>
            ImpresionesDelPos.Cotizaciones(db).RecientesAsync(sucursalId));

        // Una impresión cotizada: se cobra lo cotizado o lo que calculó la impresión (lo elige el cajero en F2).
        api.MapPost("/impresiones/{id:guid}/cotizado", async (Guid id, SolicitudCobroCotizado s, PosDbContext db) =>
        {
            var trabajo = await ImpresionesDelPos.Cotizaciones(db).CobrarLoCotizadoAsync(id, s.LoCotizado);
            var registro = ImpresionesDelPos.Registro(db);
            return PorCobrar(trabajo, await NombresAsync(db, [trabajo]), await VeCostoAsync(db, s.UsuarioId), await registro.CostosAsync(trabajo.SucursalId),
                await ImpresionesDelPos.Impresora(db).NombresAsync(trabajo.SucursalId));
        });

        api.MapPost("/impresiones/copias/precio", async (SolicitudPrecioCopias s, PosDbContext db) =>
        {
            var (costo, descripcion) = await ImpresionesDelPos.Registro(db).CalcularCopiasAsync(s.SucursalId, s.Cantidad, s.Color, s.Tamano, s.Duplex,
                s.Impresora);
            return new PrecioCopiasDto((long)Math.Round(costo.Price * 100, MidpointRounding.AwayFromZero), descripcion);
        });

        api.MapPost("/impresiones/copias", async (SolicitudCopias s, PosDbContext db) =>
        {
            var registro = ImpresionesDelPos.Registro(db);
            var trabajo = await registro.RegistrarCopiasAsync(s);
            return PorCobrar(trabajo, await NombresAsync(db, [trabajo]), false, await registro.CostosAsync(trabajo.SucursalId),
                await ImpresionesDelPos.Impresora(db).NombresAsync(trabajo.SucursalId));
        });

        // ── Las impresoras (varias por sucursal desde el 2026-11-03) ─────────

        // Todas las de la sucursal, con cómo está cada una (también las dadas de baja: la Configuración las enseña aparte). Como la de
        // antes, las ve quien abre «Impresoras e impresiones»; cambiarlas pide «Configurar la impresora».
        api.MapGet("/sucursales/{sucursalId:guid}/impresoras", async (Guid sucursalId, PosDbContext db, VigilanciaImpresora vigilancia) =>
        {
            var servicio = ImpresionesDelPos.Impresora(db);
            var lista = new List<ImpresoraDto>();
            foreach (var impresora in await servicio.ImpresorasAsync(sucursalId, tambienDeBaja: true))
                lista.Add(await servicio.DtoAsync(impresora, vigilancia.Error(impresora.Id), vigilancia.OtraSerie(impresora.Id)));
            return lista;
        });

        // Una impresora nueva (la segunda WF-6590, la L5590): quien entró con «Configurar la impresora». Con su IP, se lee al momento.
        api.MapPost("/sucursales/{sucursalId:guid}/impresoras", async (Guid sucursalId, SolicitudNuevaImpresora s, PosDbContext db,
            VigilanciaImpresora vigilancia) =>
        {
            var control = new ServicioControl(db);
            await control.ExigirDeLaSesionAsync(s.UsuarioId, sucursalId, null, AccionControlada.ConfigurarImpresora);
            if (!await db.Sucursales.AnyAsync(x => x.Id == sucursalId))
                throw new InvalidOperationException("La sucursal no existe.");
            var servicio = ImpresionesDelPos.Impresora(db);
            var impresora = await servicio.AgregarAsync(sucursalId, s.Nombre, s.Perfil, s.Host, guardar: false);
            control.Registrar(sucursalId, null, s.UsuarioId, AccionControlada.ConfigurarImpresora,
                $"Agregó la impresora {impresora.Numero}, «{impresora.Nombre}» ({PerfilesImpresora.De(impresora.Perfil).NombreCorto})");
            await db.SaveChangesAsync();
            var error = impresora.Host is null ? null : await vigilancia.LeerAhoraAsync(impresora.Id);
            return await servicio.DtoAsync(impresora, error, vigilancia.OtraSerie(impresora.Id));
        });

        // Cómo se lee, su nombre, o darla de baja (o de alta otra vez): quien entró con «Configurar la impresora», sin volver a escribir su
        // PIN (se guarda solo, como lo demás de la Configuración). Al cambiar cómo se lee, se lee al momento.
        api.MapPut("/sucursales/{sucursalId:guid}/impresoras/{numero:int}", async (Guid sucursalId, int numero, SolicitudCambioImpresora s,
            PosDbContext db, VigilanciaImpresora vigilancia) =>
        {
            var control = new ServicioControl(db);
            await control.ExigirDeLaSesionAsync(s.UsuarioId, sucursalId, null, AccionControlada.ConfigurarImpresora);
            var servicio = ImpresionesDelPos.Impresora(db);
            var impresora = await ImpresoraAsync(db, sucursalId, numero);
            var antes = (impresora.Nombre, impresora.Activa, impresora.FactorCotizacion);
            await servicio.CambiarAsync(impresora, s.Configuracion, s.Nombre, s.Activa, guardar: false, factorCotizacion: s.FactorCotizacion);
            if (Math.Abs(antes.FactorCotizacion - impresora.FactorCotizacion) > 0.001)
                control.Registrar(sucursalId, null, s.UsuarioId, AccionControlada.ConfigurarImpresora,
                    $"Cambió la corrección de cotizar de la impresora {impresora.Numero}: {antes.FactorCotizacion:0.00} → {impresora.FactorCotizacion:0.00}");
            if (antes.Nombre != impresora.Nombre)
                control.Registrar(sucursalId, null, s.UsuarioId, AccionControlada.ConfigurarImpresora,
                    $"Cambió el nombre de la impresora {impresora.Numero}: «{antes.Nombre}» → «{impresora.Nombre}»");
            if (antes.Activa != impresora.Activa)
                control.Registrar(sucursalId, null, s.UsuarioId, AccionControlada.ConfigurarImpresora,
                    $"{(impresora.Activa ? "Volvió a dar de alta" : "Dio de baja")} la impresora {impresora.Numero}, «{impresora.Nombre}»");
            await db.SaveChangesAsync();
            var error = s.Configuracion is not null && impresora.Host is not null && impresora.Activa ? await vigilancia.LeerAhoraAsync(impresora.Id)
                : vigilancia.Error(impresora.Id);
            return await servicio.DtoAsync(impresora, error, vigilancia.OtraSerie(impresora.Id));
        });

        api.MapPost("/sucursales/{sucursalId:guid}/impresoras/{numero:int}/leer", async (Guid sucursalId, int numero, PosDbContext db,
            VigilanciaImpresora vigilancia) =>
        {
            var impresora = await ImpresoraAsync(db, sucursalId, numero);
            var error = await vigilancia.LeerAhoraAsync(impresora.Id);
            return await ImpresionesDelPos.Impresora(db).DtoAsync(impresora, error, vigilancia.OtraSerie(impresora.Id));
        });

        api.MapGet("/sucursales/{sucursalId:guid}/impresoras/{numero:int}/calibracion", async (Guid sucursalId, int numero, PosDbContext db) =>
            await ImpresionesDelPos.Impresora(db).CalibracionAsync(await ImpresoraAsync(db, sucursalId, numero)));

        // Aplica la calibración de esa impresora quien entró a la caja con «Configurar la impresora», sin volver a escribir su PIN.
        api.MapPost("/sucursales/{sucursalId:guid}/impresoras/{numero:int}/calibracion", async (Guid sucursalId, int numero, SolicitudCalibracion s,
            PosDbContext db, ControlPrintEnElServicio controlPrint) =>
        {
            await new ServicioControl(db).ExigirDeLaSesionAsync(s.UsuarioId, sucursalId, null, AccionControlada.ConfigurarImpresora);
            var impresora = await ImpresoraAsync(db, sucursalId, numero);
            SinControlPrint(impresora, controlPrint);
            return await ImpresionesDelPos.Impresora(db).AplicarCalibracionAsync(impresora);
        });

        // Los consumibles propios de una impresora (la tinta de la L5590): como los costos de la sucursal, los ve quien tiene «Ver costos,
        // utilidad y margen» y los cambia quien además tiene «Configurar la impresora». Con Control Print también se cambian aquí: allá
        // no están. Qué cambió, sin los montos, a la bitácora; lo de antes, al historial.
        api.MapPut("/sucursales/{sucursalId:guid}/impresoras/{numero:int}/consumibles", async (Guid sucursalId, int numero,
            SolicitudConsumiblesImpresora s, PosDbContext db, ControlPrintEnElServicio controlPrint) =>
        {
            var control = new ServicioControl(db);
            await control.ExigirDeLaSesionAsync(s.UsuarioId, sucursalId, null, AccionControlada.ConfigurarImpresora);
            await control.ExigirParaVerAsync(s.UsuarioId, sucursalId, AccionControlada.VerUtilidad);
            var impresora = await ImpresoraAsync(db, sucursalId, numero);
            var cambios = await ImpresionesDelPos.Impresora(db).GuardarConsumiblesAsync(impresora, s, guardar: false);
            if (cambios.Count > 0)
            {
                // En el mismo guardado que los consumibles: qué cambió, sin los montos (la bitácora la lee también quien no ve los costos).
                control.Registrar(sucursalId, null, s.UsuarioId, AccionControlada.ConfigurarImpresora,
                    $"Cambió los costos de las impresiones: {CostosEnLaConfiguracion.Lista(cambios)}");
                await db.SaveChangesAsync();
            }
            return await EstadoCostosAsync(db, sucursalId, s.UsuarioId, controlPrint);
        });

        // Los márgenes propios de una impresora con sus propios consumibles (2026-11-08: cada impresora con su configuración de márgenes),
        // como sus consumibles: los cambia quien tiene «Configurar la impresora» y además ve los costos; con Control Print también (allá no
        // está). Sin márgenes, vuelve a los de la sucursal.
        api.MapPut("/sucursales/{sucursalId:guid}/impresoras/{numero:int}/margenes", async (Guid sucursalId, int numero,
            SolicitudMargenesImpresora s, PosDbContext db, ControlPrintEnElServicio controlPrint) =>
        {
            var control = new ServicioControl(db);
            await control.ExigirDeLaSesionAsync(s.UsuarioId, sucursalId, null, AccionControlada.ConfigurarImpresora);
            await control.ExigirParaVerAsync(s.UsuarioId, sucursalId, AccionControlada.VerUtilidad);
            var impresora = await ImpresoraAsync(db, sucursalId, numero);
            var deLaSucursal = await ImpresionesDelPos.Registro(db).CostosAsync(sucursalId);
            var cambios = await ImpresionesDelPos.Impresora(db).GuardarMargenesAsync(impresora, s, deLaSucursal, guardar: false);
            if (cambios.Count > 0)
            {
                control.Registrar(sucursalId, null, s.UsuarioId, AccionControlada.ConfigurarImpresora,
                    $"Cambió los costos de las impresiones: {CostosEnLaConfiguracion.Lista(cambios)}");
                await db.SaveChangesAsync();
            }
            return await EstadoCostosAsync(db, sucursalId, s.UsuarioId, controlPrint);
        });

        // Las curvas del margen, antes de usarlas (2026-11-08): el probador y la comparación con lo impreso. Son costos y márgenes: solo
        // para quien tiene «Ver costos, utilidad y margen», revisado aquí. No cambian nada.
        api.MapPost("/sucursales/{sucursalId:guid}/impresiones/curvas/probar", async (Guid sucursalId, SolicitudProbarCurvas s, PosDbContext db) =>
        {
            await new ServicioControl(db).ExigirParaVerAsync(s.UsuarioId, sucursalId, AccionControlada.VerUtilidad);
            return await ImpresionesDelPos.Registro(db).ProbarCurvasAsync(sucursalId, s);
        });

        api.MapPost("/sucursales/{sucursalId:guid}/impresiones/curvas/comparar", async (Guid sucursalId, SolicitudCompararCurvas s, PosDbContext db) =>
        {
            await new ServicioControl(db).ExigirParaVerAsync(s.UsuarioId, sucursalId, AccionControlada.VerUtilidad);
            return await ImpresionesDelPos.Registro(db).CompararCurvasAsync(sucursalId, s);
        });

        // ── La impresora de antes: la primera de la sucursal (una caja de antes sigue igual) ──

        api.MapGet("/sucursales/{sucursalId:guid}/impresora", async (Guid sucursalId, PosDbContext db, VigilanciaImpresora vigilancia) =>
        {
            var servicio = ImpresionesDelPos.Impresora(db);
            var primera = await servicio.PrimeraAsync(sucursalId);
            return await servicio.EstadoAsync(sucursalId, primera is null ? null : vigilancia.Error(primera.Id),
                primera is null ? null : vigilancia.OtraSerie(primera.Id));
        });

        // Cambia cómo se lee la primera impresora quien entró a la caja con «Configurar la impresora», sin volver a escribir su PIN.
        api.MapPut("/sucursales/{sucursalId:guid}/impresora", async (Guid sucursalId, SolicitudConfiguracionImpresora s, PosDbContext db,
            VigilanciaImpresora vigilancia) =>
        {
            await new ServicioControl(db).ExigirDeLaSesionAsync(s.UsuarioId, sucursalId, null, AccionControlada.ConfigurarImpresora);
            var servicio = ImpresionesDelPos.Impresora(db);
            var impresora = await servicio.GuardarConfiguracionAsync(sucursalId, s.Configuracion);
            var error = impresora.Host is null ? null : await vigilancia.LeerAhoraAsync(impresora.Id);
            return await servicio.EstadoAsync(impresora, error, vigilancia.OtraSerie(impresora.Id));
        });

        api.MapPost("/sucursales/{sucursalId:guid}/impresora/leer", async (Guid sucursalId, PosDbContext db, VigilanciaImpresora vigilancia) =>
        {
            var servicio = ImpresionesDelPos.Impresora(db);
            if (await servicio.PrimeraAsync(sucursalId) is not { } impresora)
                return await servicio.EstadoAsync(sucursalId, "Escribe la IP de la impresora.");
            return await servicio.EstadoAsync(impresora, await vigilancia.LeerAhoraAsync(impresora.Id), vigilancia.OtraSerie(impresora.Id));
        });

        // Sin impresoras todavía, la de una WF-6590 sin lecturas (sin darla de alta: es una consulta).
        api.MapGet("/sucursales/{sucursalId:guid}/impresora/calibracion", async (Guid sucursalId, PosDbContext db) =>
        {
            var servicio = ImpresionesDelPos.Impresora(db);
            return await servicio.CalibracionAsync(await servicio.PrimeraAsync(sucursalId)
                ?? new Impresora { SucursalId = sucursalId, Numero = 1, Nombre = PerfilesImpresora.WF6590.NombreCorto, Perfil = PerfilImpresora.WF6590 });
        });

        // Aplica la calibración de la primera quien entró a la caja con «Configurar la impresora», sin volver a escribir su PIN.
        api.MapPost("/sucursales/{sucursalId:guid}/impresora/calibracion", async (Guid sucursalId, SolicitudCalibracion s, PosDbContext db,
            ControlPrintEnElServicio controlPrint) =>
        {
            await new ServicioControl(db).ExigirDeLaSesionAsync(s.UsuarioId, sucursalId, null, AccionControlada.ConfigurarImpresora);
            var servicio = ImpresionesDelPos.Impresora(db);
            var impresora = (await servicio.PrimeraAsync(sucursalId, crear: true))!;
            SinControlPrint(impresora, controlPrint);
            return await servicio.AplicarCalibracionAsync(impresora);
        });

        // ── Los costos (Configuración › Impresoras e impresiones, 2026-10-31) ──

        // Son del negocio: los ve solo quien tiene «Ver costos, utilidad y margen», revisado aquí y no solo en la pantalla. Con Control
        // Print vienen de allá, con cuándo se leyeron.
        api.MapGet("/sucursales/{sucursalId:guid}/impresiones/costos", async (Guid sucursalId, Guid usuarioId, PosDbContext db,
            ControlPrintEnElServicio controlPrint) =>
        {
            await new ServicioControl(db).ExigirParaVerAsync(usuarioId, sucursalId, AccionControlada.VerUtilidad);
            return await EstadoCostosAsync(db, sucursalId, usuarioId, controlPrint);
        });

        // Los cambia quien entró a la caja con «Configurar la impresora» y que también los ve, sin volver a escribir su PIN (lo que se
        // escribe se guarda solo, como lo demás de la Configuración, un renglón a la vez). Los de antes se quedan en su historial y qué
        // cambió, sin los montos, en la bitácora. Con Control Print no: se cambian allá.
        api.MapPut("/sucursales/{sucursalId:guid}/impresiones/costos", async (Guid sucursalId, SolicitudCostosImpresion s, PosDbContext db,
            ControlPrintEnElServicio controlPrint) =>
        {
            var control = new ServicioControl(db);
            await control.ExigirDeLaSesionAsync(s.UsuarioId, sucursalId, null, AccionControlada.ConfigurarImpresora);
            await control.ExigirParaVerAsync(s.UsuarioId, sucursalId, AccionControlada.VerUtilidad);
            if (controlPrint.Cola.Configurado)
                throw new InvalidOperationException(CostosEnLaConfiguracion.VienenDeControlPrint);
            if (!await db.Sucursales.AnyAsync(x => x.Id == sucursalId))
                throw new InvalidOperationException("La sucursal no existe.");
            var registro = ImpresionesDelPos.Registro(db);
            var antes = await registro.CostosAsync(sucursalId);
            var despues = CostosEnLaConfiguracion.Aplicar(antes, s.Costos ?? throw new ArgumentException("Faltan los costos."));
            if (CostosEnLaConfiguracion.Cambios(antes, despues) is { Count: > 0 } cambios)
            {
                // En el mismo guardado que los costos (el mismo contexto). Qué cambió, sin los montos: la bitácora la lee también quien no
                // ve los costos (revisión del 2026-10-31); lo de antes, con sus montos, queda en el historial de los costos.
                control.Registrar(sucursalId, null, s.UsuarioId, AccionControlada.ConfigurarImpresora,
                    $"Cambió los costos de las impresiones: {CostosEnLaConfiguracion.Lista(cambios)}");
                await registro.GuardarCostosAsync(sucursalId, despues, origen: "Configuración");
            }
            return await EstadoCostosAsync(db, sucursalId, s.UsuarioId, controlPrint);
        });

        return api;
    }

    /// <summary>Los costos de la sucursal como los ve la Configuración: de dónde vienen, desde cuándo y lo que falta.</summary>
    private static async Task<EstadoCostosImpresionDto> EstadoCostosAsync(PosDbContext db, Guid sucursalId, Guid usuarioId, ControlPrintEnElServicio controlPrint)
    {
        var costos = await ImpresionesDelPos.Registro(db).CostosAsync(sucursalId);
        var guardados = await db.Set<ConfiguracionImpresion>().AsNoTracking().Where(c => c.SucursalId == sucursalId)
            .Select(c => (DateTime?)c.ModificadoUtc).FirstOrDefaultAsync();
        var deControlPrint = controlPrint.Cola.Configurado;
        var estado = controlPrint.Cola.Estado;
        var leidos = deControlPrint ? controlPrint.Cola.UltimosCostos?.LeidosUtc : null;
        var aviso = !deControlPrint ? null
            : estado.AvisoCostos ?? (estado.Contesta == false ? "Control Print no contesta: se cobra con los últimos costos que dio." : null);
        var puede = !deControlPrint && await new ServicioControl(db).PuedeAsync(usuarioId, AccionControlada.ConfigurarImpresora);
        // Los consumibles propios de cada impresora (la L5590) van aparte, y lo que les falta también se dice arriba (2026-11-03).
        var propios = await ImpresionesDelPos.Impresora(db).ConsumiblesPropiosAsync(sucursalId);
        return new EstadoCostosImpresionDto(CostosEnLaConfiguracion.De(costos), deControlPrint, leidos is { } l ? HoraMexico.ALocal(l.UtcDateTime) : null,
            guardados is { } g && g != default ? HoraMexico.ALocal(g) : null, aviso,
            [.. CostosEnLaConfiguracion.Faltan(costos), .. propios.SelectMany(p => p.Faltan)], puede, propios);
    }

    /// <summary>La impresora con ese número en la sucursal; si no hay, un error para el cajero.</summary>
    private static async Task<Impresora> ImpresoraAsync(PosDbContext db, Guid sucursalId, int numero) =>
        await ImpresionesDelPos.Impresora(db).ImpresoraAsync(sucursalId, numero)
        ?? throw new InvalidOperationException($"La sucursal no tiene la impresora {numero}.");

    /// <summary>El costo, la ganancia y el margen de las impresiones: solo con «Ver costos, utilidad y margen» (al migrar, el dueño).</summary>
    private static Task<bool> VeCostoAsync(PosDbContext db, Guid usuarioId) =>
        new ServicioControl(db).PuedeAsync(usuarioId, AccionControlada.VerUtilidad);

    private static async Task<Dictionary<Guid, string>> NombresAsync(PosDbContext db, IEnumerable<TrabajoImpresion> trabajos)
    {
        var ids = trabajos.Where(t => t.AtendioId != null).Select(t => t.AtendioId!.Value).Distinct().ToList();
        return await db.Usuarios.Where(u => ids.Contains(u.Id)).ToDictionaryAsync(u => u.Id, u => u.Nombre);
    }

    /// <summary>
    /// Con Control Print, los costos de la sucursal vienen de allá (y se reemplazan cuando cambian): la calibración que se guarda en ellos
    /// (la de la 1) no llegaría a las impresiones y se perdería sola. Se hace en Control Print, como los costos (auditoría, 2026-11-04). Las
    /// que calibran en lo suyo (la L5590, otra WF-6590) sí se aplican aquí.
    /// </summary>
    private static void SinControlPrint(Impresora impresora, ControlPrintEnElServicio controlPrint)
    {
        if (controlPrint.Cola.Configurado && impresora.CalibraEnLaSucursal(ServicioImpresora.Propios(impresora)))
            throw new InvalidOperationException("Los costos vienen de Control Print: la calibración de esta impresora se aplica allá, como los costos.");
    }

    private static ImpresionPorCobrarDto PorCobrar(TrabajoImpresion t, Dictionary<Guid, string> nombres, bool veCosto, CostSettings costos,
        Func<Guid?, string?> impresoras) =>
        new(t.Id, t.Documento, t.Pc, HoraMexico.ALocal(t.ImpresoUtc), t.Paginas, t.Caras, t.Color, t.Tamano, t.PrecioCentavos,
            t.AtendioId is { } a ? nombres.GetValueOrDefault(a) : null, null, t.Nota,
            veCosto ? RegistroImpresiones.Costo(t) : null, t.Tipo, t.CarasContadas, RegistroImpresiones.PrecioSoloLoQueSalio(t, costos),
            t.PrecioCompletoCentavos, impresoras(t.ImpresoraId), t.Estado, t.Medido, t.Copias, t.CarasColor, t.TipoPapel, t.Duplex,
            t.Miniatura is not null, t.PrecioCotizadoCentavos, t.PrecioImpresoCentavos, t.CobraLoCotizado);
}
