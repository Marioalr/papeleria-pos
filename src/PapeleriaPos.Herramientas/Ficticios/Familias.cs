using PapeleriaPos.Nucleo.Catalogo;

namespace PapeleriaPos.Herramientas.Ficticios;

public enum Temporada
{
    Ninguna,
    /// <summary>Regreso a clases: de mediados de julio a principios de septiembre, con el pico a fines de agosto.</summary>
    Escolar,
    Navidad,
}

/// <summary>
/// Plantilla de productos inventados. Cada marca da un producto y cada combinación de <see cref="Dimensiones"/>
/// una variante: "Pluma punto fino" × 5 marcas × 4 colores = 5 productos y 20 variantes.
/// </summary>
/// <param name="Precio">Rango del precio por unidad base, en pesos.</param>
/// <param name="Popularidad">De 1 (casi no se vende) a 10 (lo que más sale).</param>
/// <param name="DescuentoMayoreo">Si tiene, el paquete siempre lleva escala de mayoreo con este descuento.</param>
public sealed record Familia(
    string Departamento,
    string Nombre,
    string[] Marcas,
    string[][] Dimensiones,
    (decimal Min, decimal Max) Precio,
    int Popularidad,
    Temporada Temporada = Temporada.Ninguna,
    UnidadBase Unidad = UnidadBase.Pieza,
    (string Nombre, int Piezas)? Paquete = null,
    decimal? DescuentoMayoreo = null,
    TipoProducto Tipo = TipoProducto.Mercancia);

public static class Familias
{
    private static readonly string[] SinMarca = [];
    private static readonly string[] Unica = [""];
    private static readonly string[] Colores4 = ["Azul", "Negro", "Rojo", "Verde"];
    private static readonly string[] Colores8 = ["Blanco", "Amarillo", "Rosa", "Azul", "Verde", "Rojo", "Naranja", "Negro"];
    private static readonly string[] Colores10 = [.. Colores8, "Morado", "Café"];
    private static readonly string[] Colores14 = [.. Colores10, "Gris", "Dorado", "Plateado", "Turquesa"];
    private static readonly string[] Colores20 = [.. Colores14, "Lila", "Verde limón", "Azul cielo", "Rosa mexicano", "Beige", "Vino"];

    private static readonly string[] ColoresLapiz =
    [
        "Blanco", "Negro", "Gris frío", "Amarillo canario", "Amarillo limón", "Naranja", "Rojo carmín", "Rojo escarlata",
        "Rosa", "Magenta", "Violeta", "Lila", "Azul ultramar", "Azul cielo", "Azul verdoso", "Turquesa", "Verde pasto",
        "Verde olivo", "Verde limón", "Café oscuro", "Siena tostada", "Ocre", "Durazno", "Carne",
    ];

    private static readonly string[] Portadas = ["Lisa", "Diseño infantil", "Diseño juvenil", "Diseño casual"];

    private static readonly string[] TemasMonografia =
    [
        "Benito Juárez", "Miguel Hidalgo", "José María Morelos", "Josefa Ortiz", "Emiliano Zapata", "Francisco Villa",
        "Lázaro Cárdenas", "Sor Juana", "Independencia", "Revolución", "Reforma", "Expropiación petrolera",
        "Batalla de Puebla", "Niños Héroes", "Conquista", "Culturas prehispánicas", "Aztecas", "Mayas", "Olmecas",
        "Aparato digestivo", "Aparato respiratorio", "Aparato circulatorio", "Sistema nervioso", "Esqueleto",
        "Cuerpo humano", "Los sentidos", "Sistema solar", "Ciclo del agua", "Estados de la materia", "La célula",
        "Animales vertebrados", "Animales invertebrados", "Plantas", "La flor", "Ecosistemas", "Cadena alimenticia",
        "Símbolos patrios", "Estados de México", "Continentes", "Medios de transporte",
    ];

    private static readonly string[] TemasLamina =
    [
        "Abecedario", "Números", "Tablas de multiplicar", "Figuras geométricas", "Colores", "Frutas", "Verduras",
        "Animales de la granja", "Animales salvajes", "Partes de la planta", "El reloj", "Días de la semana",
        "Meses del año", "Estaciones del año", "Profesiones", "Medidas", "Fracciones", "Signos de puntuación",
        "Verbos", "Sustantivos", "Planetas", "Dinosaurios", "Higiene", "Alimentación saludable", "Reciclaje",
        "Regiones de México", "Ríos de México", "Volcanes", "Banderas del mundo", "Mapa de México",
    ];

    private static readonly string[] TemasEstampa =
    [
        "Corazones", "Estrellas", "Caritas", "Animales", "Dinosaurios", "Princesas", "Superhéroes", "Futbol", "Flores",
        "Mariposas", "Unicornios", "Autos", "Espacio", "Frutas", "Emojis", "Letras", "Números", "Calaveras",
        "Navidad", "Halloween", "Día de muertos", "Amor y amistad", "Felicitaciones", "Escolares", "Premios",
        "Brillantes", "Fosforescentes", "Holográficas", "De fieltro", "De foamy", "Mascotas", "Comida", "Música",
        "Deportes", "Selva", "Océano", "Granja", "Profesiones", "Banderas", "Paisajes",
    ];

    private static readonly string[] TitulosColorear =
    [
        "Animales", "Dinosaurios", "Princesas", "Autos", "Mandalas", "Superhéroes", "Granja", "Océano", "Espacio",
        "Unicornios", "Navidad", "Abecedario", "Números", "Vehículos", "Mascotas",
    ];

    private const string Escritura = "Escritura";
    private const string Papel = "Papel";
    private const string Cuadernos = "Cuadernos";
    private const string Arte = "Arte y manualidades";
    private const string Escolar = "Escolar";
    private const string Oficina = "Oficina";
    private const string Mochilas = "Mochilas y estuches";
    private const string Regalos = "Regalos y envolturas";
    private const string Tecnologia = "Tecnología";
    private const string Servicios = "Servicios";
    private const string Recargas = "Recargas";

    private static string[][] D(params string[][] dimensiones) => dimensiones;

    public static readonly Familia[] Todas =
    [
        // Escritura
        new(Escritura, "Pluma punto fino", ["Bic", "Pelikan", "Paper Mate", "Zebra", "Pilot"], D(Colores4), (8, 15), 10, Temporada.Escolar, Paquete: ("Caja 12", 12)),
        new(Escritura, "Pluma punto mediano", ["Bic", "Kores", "Paper Mate"], D(["Azul", "Negro", "Rojo"]), (6, 10), 9, Temporada.Escolar, Paquete: ("Caja 12", 12)),
        new(Escritura, "Pluma de gel", ["Pilot", "Zebra", "Pelikan", "Paper Mate"], D(Colores10), (18, 35), 6),
        new(Escritura, "Lápiz del 2", ["Mirado", "Dixon", "Pelikan", "Maped", "Staedtler"], D(["Hexagonal", "Triangular"]), (5, 9), 10, Temporada.Escolar, Paquete: ("Caja 12", 12)),
        new(Escritura, "Portaminas", ["Pentel", "Pilot", "Bic", "Pelikan"], D(["0.5 mm", "0.7 mm"]), (25, 60), 4),
        new(Escritura, "Minas", ["Pentel", "Pilot"], D(["0.5 mm HB", "0.7 mm HB", "0.5 mm 2B"]), (12, 20), 4),
        new(Escritura, "Marcatextos", ["Sharpie", "Pelikan", "Bic", "Zebra"], D(["Amarillo", "Verde", "Rosa", "Naranja", "Azul", "Lila", "Pastel verde", "Pastel rosa"]), (12, 20), 7, Paquete: ("Caja 12", 12)),
        new(Escritura, "Marcador permanente", ["Sharpie", "Pelikan", "Bic", "Azor"], D(Colores4), (14, 25), 6),
        new(Escritura, "Marcador para pizarrón", ["Expo", "Pelikan", "Azor", "Baco"], D(Colores4), (18, 30), 5),
        new(Escritura, "Corrector líquido", ["Pelikan", "Bic", "Kores"], D(["Pluma", "Frasco"]), (15, 30), 6, Temporada.Escolar),
        new(Escritura, "Corrector en cinta", ["Bic", "Pelikan", "Maped"], D(Unica), (25, 45), 5),
        new(Escritura, "Goma de borrar", ["Pelikan", "Factis", "Maped", "Staedtler", "Pentel"], D(["Blanca", "Migajón"]), (4, 12), 9, Temporada.Escolar, Paquete: ("Caja 20", 20)),
        new(Escritura, "Sacapuntas", ["Maped", "Pelikan", "Barrilito", "Staedtler"], D(["Metálico", "Con depósito", "Doble"]), (5, 30), 8, Temporada.Escolar),
        new(Escritura, "Plumones de colores", ["Crayola", "Pelikan", "Maped", "Baco"], D(["Caja 10", "Caja 12", "Caja 24"]), (35, 120), 6, Temporada.Escolar),
        new(Escritura, "Colores", ["Crayola", "Prismacolor", "Mapita", "Maped", "Pelikan", "Norma"], D(["Caja 12", "Caja 24", "Caja 36"]), (30, 250), 8, Temporada.Escolar),
        new(Escritura, "Crayones", ["Crayola", "Baco", "Pelikan"], D(["Caja 12", "Caja 24"]), (20, 60), 5, Temporada.Escolar),
        new(Escritura, "Bolígrafo multicolor", ["Bic"], D(["4 colores"]), (25, 35), 3),
        new(Escritura, "Lápiz de color suelto", ["Prismacolor", "Mapita"], D(ColoresLapiz), (6, 18), 5, Temporada.Escolar),
        new(Escritura, "Plumón punto fino", ["Stabilo", "Pelikan"], D(Colores20), (15, 28), 4),

        // Papel
        new(Papel, "Hojas blancas", ["Scribe", "HP", "Chamex", "Copamex"], D(["Carta", "Oficio"]), (0.25m, 0.40m), 9, Paquete: ("Paquete 500", 500), DescuentoMayoreo: 0.30m),
        new(Papel, "Hojas de color", ["Scribe", "Copamex"], D(["Amarillo", "Rosa", "Verde", "Azul", "Naranja", "Rojo", "Morado", "Canario", "Lila", "Verde limón"]), (0.5m, 1), 5, Paquete: ("Paquete 100", 100), DescuentoMayoreo: 0.20m),
        new(Papel, "Papel opalina", ["Copamex", "Scribe"], D(["Carta 120 g", "Carta 225 g"]), (1.5m, 3), 4, Paquete: ("Paquete 100", 100)),
        new(Papel, "Cartulina", SinMarca, D([.. Colores10, "Gris", "Turquesa"]), (6, 10), 7, Temporada.Escolar),
        new(Papel, "Cartulina fluorescente", SinMarca, D(["Verde", "Rosa", "Naranja", "Amarilla"]), (10, 14), 4, Temporada.Escolar),
        new(Papel, "Papel crepé", SinMarca, D(Colores14), (5, 8), 4, Temporada.Escolar),
        new(Papel, "Papel china", SinMarca, D(Colores14), (2, 3), 4),
        new(Papel, "Papel lustre", SinMarca, D(Colores14), (3, 5), 4, Temporada.Escolar),
        new(Papel, "Papel América", SinMarca, D(Colores8), (5, 8), 3),
        new(Papel, "Papel kraft", SinMarca, D(["Natural"]), (8, 12), 3, Unidad: UnidadBase.Metro),
        new(Papel, "Foamy", SinMarca, D(["Carta", "Pliego"], Colores14), (4, 25), 5, Temporada.Escolar),
        new(Papel, "Foamy diamantado", SinMarca, D(Colores10), (8, 12), 4),
        new(Papel, "Forro adherible", ["Con-Tact", "Plastifilm"], D(["Transparente", "Cuadros", "Estampado"]), (15, 30), 5, Temporada.Escolar, UnidadBase.Metro),
        new(Papel, "Forro de plástico para libros", SinMarca, D(["Transparente", "Azul"]), (8, 12), 5, Temporada.Escolar, UnidadBase.Metro),
        new(Papel, "Papel fotográfico", ["HP", "Epson"], D(["Carta brillante", "Carta mate", "4 × 6"]), (6, 15), 2, Paquete: ("Paquete 20", 20)),
        new(Papel, "Papel para envolver", SinMarca, D(["Cumpleaños", "Navidad", "Bebé", "Liso rojo", "Liso azul", "Kraft"]), (8, 15), 4, Temporada.Navidad),

        // Cuadernos
        new(Cuadernos, "Cuaderno profesional raya", ["Scribe", "Norma", "Estrella", "Jean Book"], D(["100 hojas", "200 hojas"], Portadas), (25, 70), 9, Temporada.Escolar),
        new(Cuadernos, "Cuaderno profesional cuadro chico", ["Scribe", "Norma", "Estrella", "Jean Book"], D(["100 hojas", "200 hojas"], Portadas), (25, 70), 8, Temporada.Escolar),
        new(Cuadernos, "Cuaderno profesional cuadro grande", ["Scribe", "Norma", "Estrella"], D(["100 hojas", "200 hojas"], Portadas), (25, 70), 6, Temporada.Escolar),
        new(Cuadernos, "Cuaderno forma italiana", ["Scribe", "Norma", "Estrella"], D(["Raya", "Cuadro", "Doble raya"], ["Lisa", "Diseño infantil", "Diseño juvenil"]), (18, 35), 7, Temporada.Escolar),
        new(Cuadernos, "Cuaderno forma francesa", ["Scribe", "Norma", "Estrella"], D(["Raya", "Cuadro chico", "Cuadro grande", "Blanco"], ["Lisa", "Diseño infantil", "Diseño juvenil"]), (15, 30), 7, Temporada.Escolar),
        new(Cuadernos, "Cuaderno de dibujo", ["Scribe", "Norma"], D(["Profesional", "Francés"]), (25, 45), 4, Temporada.Escolar),
        new(Cuadernos, "Block de hojas", ["Scribe", "Estrella"], D(["Raya", "Cuadro", "Blanco"]), (25, 40), 4),
        new(Cuadernos, "Cuaderno pautado de música", ["Scribe"], D(Unica), (30, 40), 1),
        new(Cuadernos, "Agenda", ["Norma"], D(["2026", "2027"]), (90, 180), 2, Temporada.Navidad),

        // Arte y manualidades
        new(Arte, "Pintura acrílica 20 ml", ["Politec", "Vinci", "Pelikan"], D(Colores20), (12, 25), 5, Temporada.Escolar),
        new(Arte, "Pintura textil", ["Politec", "Vinci"], D(Colores14), (18, 30), 2),
        new(Arte, "Témpera", ["Vinci", "Pelikan", "Crayola"], D(["Caja 6", "Caja 12"]), (30, 70), 5, Temporada.Escolar),
        new(Arte, "Acuarelas", ["Pelikan", "Vinci", "Crayola"], D(["12 colores", "24 colores"]), (30, 90), 4),
        new(Arte, "Pincel", ["Vinci", "Pelikan"], D(["No. 2", "No. 4", "No. 6", "No. 8", "No. 10", "No. 12"]), (6, 25), 4, Temporada.Escolar),
        new(Arte, "Plastilina", ["Pelikan", "Vinci", "Crayola"], D(Colores10), (6, 10), 5, Temporada.Escolar),
        new(Arte, "Diamantina sobre", SinMarca, D(["Dorada", "Plateada", "Roja", "Azul", "Verde", "Rosa", "Morada", "Tornasol"]), (3, 6), 4),
        new(Arte, "Diamantina a granel", SinMarca, D(["Dorada", "Plateada", "Roja", "Tornasol"]), (180, 250), 2, Unidad: UnidadBase.Kilogramo),
        new(Arte, "Silicón en barra", ["Pegaso"], D(["Delgada", "Gruesa"]), (2, 6), 6, Paquete: ("Paquete 12", 12)),
        new(Arte, "Pistola de silicón", ["Pegaso", "Truper"], D(["Chica", "Grande"]), (45, 120), 3),
        new(Arte, "Limpiapipas", SinMarca, D(["Surtido"]), (15, 25), 3),
        new(Arte, "Palitos de madera", SinMarca, D(["Abatelenguas", "Paleta", "Brocheta"]), (0.5m, 1), 4, Paquete: ("Paquete 50", 50), DescuentoMayoreo: 0.15m),
        new(Arte, "Bola de unicel", SinMarca, D(["3 cm", "5 cm", "7 cm", "10 cm"]), (2, 12), 3, Temporada.Escolar),
        new(Arte, "Placa de unicel", SinMarca, D(["1 cm", "2 cm"]), (15, 30), 3, Temporada.Escolar),
        new(Arte, "Listón", SinMarca, D(["0.5 cm", "1 cm", "2.5 cm"], Colores14), (3, 10), 5, Unidad: UnidadBase.Metro),
        new(Arte, "Cordón de seda", SinMarca, D(["Rojo", "Azul", "Blanco", "Dorado", "Negro", "Verde"]), (3, 6), 2, Unidad: UnidadBase.Metro),
        new(Arte, "Estambre", ["Omega"], D([.. Colores14, "Lila", "Azul cielo"]), (15, 30), 3),
        new(Arte, "Ojos móviles", SinMarca, D(["Chicos", "Medianos", "Grandes"]), (10, 20), 3),
        new(Arte, "Pegamento blanco a granel", ["Resistol"], D(Unica), (80, 120), 3, Unidad: UnidadBase.Litro),
        new(Arte, "Pegamento blanco", ["Resistol", "Pritt"], D(["60 g", "125 g", "250 g"]), (12, 45), 6, Temporada.Escolar),
        new(Arte, "Lápiz adhesivo", ["Pritt", "Pelikan", "Dixon"], D(["10 g", "20 g", "40 g"]), (12, 40), 7, Temporada.Escolar),

        // Escolar
        new(Escolar, "Regla", ["Maped", "Barrilito", "Baco"], D(["15 cm", "30 cm", "Flexible 30 cm"]), (5, 25), 6, Temporada.Escolar),
        new(Escolar, "Juego de geometría", ["Maped", "Barrilito", "Baco"], D(["Escolar", "Profesional"]), (25, 90), 5, Temporada.Escolar),
        new(Escolar, "Compás", ["Maped", "Barrilito", "Staedtler"], D(["Escolar", "De precisión"]), (25, 120), 4, Temporada.Escolar),
        new(Escolar, "Transportador", ["Barrilito", "Baco"], D(["180°", "360°"]), (5, 12), 3),
        new(Escolar, "Tijeras escolares", ["Maped", "Barrilito", "Pelikan"], D(["Punta roma", "Punta fina"]), (15, 45), 6, Temporada.Escolar),
        new(Escolar, "Calculadora", ["Casio", "Texas Instruments"], D(["Básica", "Científica"]), (60, 450), 3, Temporada.Escolar),
        new(Escolar, "Mapa", SinMarca, D(["Carta", "Doble carta"], ["México división política", "México físico", "México con nombres", "América", "Mundo", "Europa", "Asia", "África", "Oceanía", "Mi estado"]), (1.5m, 4), 7, Temporada.Escolar),
        new(Escolar, "Monografía", SinMarca, D(TemasMonografia), (5, 6), 5, Temporada.Escolar),
        new(Escolar, "Lámina didáctica", SinMarca, D(TemasLamina), (12, 18), 3, Temporada.Escolar),
        new(Escolar, "Estampas", SinMarca, D(TemasEstampa), (5, 15), 4),
        new(Escolar, "Libro para colorear", SinMarca, D(TitulosColorear), (25, 45), 2),
        new(Escolar, "Etiquetas escolares", SinMarca, D(["Niño", "Niña", "Neutras"]), (15, 25), 5, Temporada.Escolar),
        new(Escolar, "Pizarrón blanco", SinMarca, D(["30 × 40 cm", "60 × 90 cm"]), (120, 450), 1),
        new(Escolar, "Gises", ["Vinci"], D(["Blanco", "Colores"]), (15, 30), 2),

        // Oficina
        new(Oficina, "Clips", ["Acco"], D(["No. 1", "No. 2", "Mariposa", "Jumbo"]), (0.15m, 0.4m), 5, Paquete: ("Caja 100", 100), DescuentoMayoreo: 0.20m),
        new(Oficina, "Grapas", ["Swingline", "Pegaso"], D(["Estándar", "Uso rudo"]), (15, 40), 4),
        new(Oficina, "Engrapadora", ["Swingline", "Pegaso", "Acco"], D(["Mini", "Estándar", "Industrial"]), (35, 350), 2),
        new(Oficina, "Perforadora", ["Acco", "Pegaso", "Maped"], D(["1 orificio", "2 orificios", "3 orificios"]), (30, 250), 2),
        new(Oficina, "Notas adhesivas", ["Post-it", "Pelikan"], D(["Amarillo", "Neón surtido"], ["3 × 3", "1.5 × 2"]), (12, 60), 5),
        new(Oficina, "Banderitas adhesivas", ["Post-it", "Pelikan"], D(["Neón", "Pastel"]), (25, 45), 3),
        new(Oficina, "Cinta adhesiva transparente", ["Scotch", "Janel", "Tuk"], D(["12 mm", "18 mm", "24 mm"]), (8, 35), 7),
        new(Oficina, "Cinta de empaque", ["Scotch", "Janel", "Tuk"], D(["Canela 48 mm", "Masking 24 mm", "Masking 48 mm", "Doble cara"]), (15, 60), 5),
        new(Oficina, "Sobre", SinMarca, D(["Manila carta", "Manila oficio", "Bolsa", "Blanco carta", "Coin"]), (1.5m, 6), 6, Paquete: ("Paquete 50", 50)),
        new(Oficina, "Folder", ["Oxford", "Mapasa"], D(["Carta", "Oficio"], ["Crema", "Azul", "Rojo", "Verde", "Amarillo", "Rosa", "Naranja", "Morado"]), (2, 4), 7, Temporada.Escolar, Paquete: ("Paquete 100", 100), DescuentoMayoreo: 0.20m),
        new(Oficina, "Carpeta de argollas", ["Avery"], D(["1\"", "1.5\"", "2\"", "3\""], ["Blanca", "Negra", "Azul", "Roja", "Verde"]), (40, 120), 3, Temporada.Escolar),
        new(Oficina, "Protector de hojas", ["Avery"], D(["Carta", "Oficio"]), (1, 2), 4, Paquete: ("Paquete 100", 100)),
        new(Oficina, "Separadores", ["Avery"], D(["5 divisiones", "10 divisiones", "A-Z"]), (15, 50), 2),
        new(Oficina, "Broche para archivo", ["Baco", "Acco"], D(["8 cm"]), (0.6m, 1), 3, Paquete: ("Caja 50", 50)),
        new(Oficina, "Ligas a granel", SinMarca, D(["No. 18", "No. 33", "Surtidas"]), (150, 200), 2, Unidad: UnidadBase.Kilogramo),
        new(Oficina, "Cojín para sello", ["Pelikan"], D(["Negro", "Azul", "Rojo"]), (25, 40), 1),

        // Mochilas y estuches
        new(Mochilas, "Lapicera", ["Chenson"], D(["Sencilla", "Doble cierre", "Cartuchera"], ["Rojo", "Azul", "Negro", "Rosa", "Morado", "Verde", "Gris", "Estampada"]), (45, 180), 3, Temporada.Escolar),
        new(Mochilas, "Mochila", ["Chenson"], D(["Primaria", "Secundaria"], ["Azul", "Negro", "Rosa", "Morado", "Gris", "Estampada"]), (250, 650), 2, Temporada.Escolar),
        new(Mochilas, "Lonchera", SinMarca, D(["Azul", "Rosa", "Verde"]), (120, 250), 1, Temporada.Escolar),

        // Regalos y envolturas
        new(Regalos, "Bolsa de regalo", SinMarca, D(["Chica", "Mediana", "Grande"], ["Cumpleaños", "Bebé", "Navidad", "Amor", "Lisa", "Graduación", "Infantil", "Kraft"]), (12, 45), 4, Temporada.Navidad),
        new(Regalos, "Tarjeta de felicitación", SinMarca, D(["Cumpleaños", "Amor", "Día de las madres", "Día del padre", "Navidad", "Graduación", "Bebé", "Boda", "Pésame", "Gracias", "Aniversario", "Maestro"]), (15, 35), 3),
        new(Regalos, "Moño adherible", SinMarca, D(["Rojo", "Azul", "Dorado", "Plateado", "Rosa", "Verde"]), (3, 8), 4, Temporada.Navidad),
        new(Regalos, "Globo", SinMarca, D(["Látex 9\"", "Látex 12\"", "Metálico"], Colores10), (2, 35), 3),
        new(Regalos, "Vela de número", SinMarca, D(["0", "1", "2", "3", "4", "5", "6", "7", "8", "9"], ["Dorada", "Plateada", "Rosa", "Azul"]), (15, 25), 3),
        new(Regalos, "Papel celofán", SinMarca, D(["Transparente", "Rojo", "Azul", "Amarillo"]), (6, 10), 2, Unidad: UnidadBase.Metro),

        // Tecnología
        new(Tecnologia, "Pila", ["Duracell", "Energizer"], D(["AA", "AAA", "9 V", "CR2032"]), (15, 80), 4),
        new(Tecnologia, "Memoria USB", ["Kingston", "Adata"], D(["16 GB", "32 GB", "64 GB"]), (90, 250), 2),
        new(Tecnologia, "Cartucho de tinta", ["HP", "Epson", "Canon"], D(["Modelo A", "Modelo B", "Modelo C"], ["Negro", "Color"]), (250, 600), 2),
        new(Tecnologia, "Cable USB", ["Steren", "Belkin"], D(["Tipo C", "Micro USB", "Lightning"], ["1 m", "2 m"]), (60, 250), 2),
        new(Tecnologia, "Audífonos", ["Steren", "Sony"], D(["Alámbricos", "Inalámbricos"], ["Negro", "Blanco"]), (80, 600), 1),
        new(Tecnologia, "Disco", ["Verbatim"], D(["CD", "DVD"]), (8, 15), 1),

        // Servicios: no llevan inventario
        new(Servicios, "Impresión blanco y negro", SinMarca, D(["Carta", "Oficio"]), (2, 3), 10, Tipo: TipoProducto.Servicio),
        new(Servicios, "Impresión a color", SinMarca, D(["Carta", "Oficio"]), (5, 10), 8, Tipo: TipoProducto.Servicio),
        new(Servicios, "Copia blanco y negro", SinMarca, D(["Carta", "Oficio", "Doble cara"]), (1, 2), 10, Tipo: TipoProducto.Servicio),
        new(Servicios, "Copia a color", SinMarca, D(["Carta"]), (5, 8), 5, Tipo: TipoProducto.Servicio),
        new(Servicios, "Engargolado", SinMarca, D(["Hasta 50 hojas", "51 a 100 hojas", "101 a 200 hojas"]), (25, 60), 4, Temporada.Escolar, Tipo: TipoProducto.Servicio),
        new(Servicios, "Enmicado", SinMarca, D(["Credencial", "Carta", "Oficio"]), (8, 25), 5, Tipo: TipoProducto.Servicio),
        new(Servicios, "Escaneo", SinMarca, D(Unica), (5, 5), 3, Tipo: TipoProducto.Servicio),
        new(Servicios, "Trámite", SinMarca, D(["CURP", "Acta de nacimiento", "Constancia de situación fiscal", "Recibo de luz"]), (10, 60), 5, Tipo: TipoProducto.Servicio),

        // Cobros a cuenta de terceros: el precio es el monto de la recarga
        new(Recargas, "Recarga", ["Telcel", "AT&T", "Movistar"], D(["$20", "$50", "$100", "$200"]), (0, 0), 5, Tipo: TipoProducto.CobroTerceros),
    ];
}
