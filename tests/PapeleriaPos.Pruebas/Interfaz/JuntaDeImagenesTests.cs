using System.Text.RegularExpressions;
using PapeleriaPos.Interfaz;
using PapeleriaPos.Nucleo.Imagenes;
using PapeleriaPos.Pruebas.Impresion;
using SkiaSharp;

namespace PapeleriaPos.Pruebas.Interfaz;

/// <summary>Lo que se junta en «Juntar imágenes para Word», sin Windows: sin repetir, lo que no es imagen no cuenta, y los 15 minutos.</summary>
public class JuntaDeImagenesTests
{
    private static readonly DateTimeOffset Ahora = new(2026, 11, 28, 12, 0, 0, TimeSpan.FromHours(-6));

    [Fact]
    public void Solo_mientras_junta_entran_las_imagenes_y_la_misma_cuenta_una_vez()
    {
        var junta = new JuntaDeImagenes(new AjustesDeJuntarImagenes());
        var foto = ImagenesDePrueba.Foto(300, 600);
        Assert.Equal(LoQueSeCopio.NoEsImagen, junta.Agregar(foto, Ahora));

        junta.Empezar(Ahora);
        Assert.Equal(LoQueSeCopio.Nueva, junta.Agregar(foto, Ahora));
        Assert.Equal(LoQueSeCopio.Repetida, junta.Agregar(foto.ToArray(), Ahora));
        Assert.Equal(LoQueSeCopio.NoEsImagen, junta.Agregar("un nombre"u8.ToArray(), Ahora));
        Assert.Equal(LoQueSeCopio.Nueva, junta.Agregar(ImagenesDePrueba.Foto(301, 600, SKEncodedImageFormat.Png), Ahora));
        Assert.Equal(2, junta.Imagenes.Count);
        Assert.Equal(1, junta.Repetidas);
        Assert.Same(foto, junta.Imagenes[0].Datos);
    }

    [Fact]
    public void Terminar_sin_nada_no_abre_la_ventanita()
    {
        var junta = new JuntaDeImagenes(new AjustesDeJuntarImagenes());
        junta.Empezar(Ahora);
        Assert.False(junta.Terminar(Ahora));
        Assert.Equal(EstadoDeLaJunta.Nada, junta.Estado);
    }

    [Fact]
    public void Para_word_respeta_el_orden_los_giros_y_guarda_los_ajustes()
    {
        var junta = new JuntaDeImagenes(new AjustesDeJuntarImagenes());
        junta.Empezar(Ahora);
        junta.Agregar(ImagenesDePrueba.Foto(400, 800), Ahora);
        junta.Agregar(ImagenesDePrueba.Foto(800, 400, SKEncodedImageFormat.Png), Ahora);
        Assert.True(junta.Terminar(Ahora));
        var vista = junta.Vista(0);
        Assert.StartsWith("data:image/jpeg;base64,", vista.Miniatura);
        Assert.Equal((400, 800), (vista.AnchoPx, vista.AltoPx));

        var ajustes = new AjustesDeJuntarImagenes { Alto = 4 };
        var rtf = junta.ParaWord([new ImagenOrdenada(1, 0), new ImagenOrdenada(0, 90)], ajustes, conHoja: false, Ahora)!;
        // La segunda (800×400) va primero, como estaba; la primera, girada: 800×400 en PNG. Las dos de 4 cm de alto: 8 cm de ancho.
        var picts = Regex.Matches(rtf, @"\\pict\\(png|jpeg)blip\\picw(\d+)\\pich(\d+)\\picwgoal(\d+)\\pichgoal(\d+)");
        Assert.Equal(["png", "png"], picts.Select(m => m.Groups[1].Value));
        Assert.All(picts, m => Assert.Equal(("800", "400", "4535", "2268"), (m.Groups[2].Value, m.Groups[3].Value, m.Groups[4].Value, m.Groups[5].Value)));
        Assert.Equal(EstadoDeLaJunta.Copiadas, junta.Estado);
        Assert.Equal(ajustes, junta.Ajustes);
        Assert.Equal(2, junta.Orden!.Count);
        Assert.Null(junta.ParaWord([new ImagenOrdenada(0, 0)], ajustes with { Alto = 0 }, false, Ahora));
    }

    [Fact]
    public void Se_vence_a_los_15_minutos_sin_copiar_o_despues_de_copiar()
    {
        var junta = new JuntaDeImagenes(new AjustesDeJuntarImagenes());
        junta.Empezar(Ahora);
        junta.Agregar(ImagenesDePrueba.Foto(100, 100), Ahora.AddMinutes(5));
        Assert.False(junta.Vencida(Ahora.AddMinutes(19)));
        Assert.True(junta.Vencida(Ahora.AddMinutes(20)));

        junta.Terminar(Ahora.AddMinutes(6));
        junta.ParaWord([new ImagenOrdenada(0, 0)], junta.Ajustes, false, Ahora.AddMinutes(7));
        Assert.False(junta.Vencida(Ahora.AddMinutes(21)));
        Assert.True(junta.Vencida(Ahora.AddMinutes(22)));
        junta.Borrar();
        Assert.Empty(junta.Imagenes);
        Assert.False(junta.Vencida(Ahora.AddDays(1)));
    }

    [Fact]
    public void Juntar_mas_sigue_sobre_las_mismas_con_su_orden()
    {
        var junta = new JuntaDeImagenes(new AjustesDeJuntarImagenes());
        junta.Empezar(Ahora);
        junta.Agregar(ImagenesDePrueba.Foto(100, 100), Ahora);
        junta.Terminar(Ahora);
        junta.Seguir([new ImagenOrdenada(0, 180)], new AjustesDeJuntarImagenes { Ancho = 3, Modo = ModoDelTamano.Ancho }, Ahora);
        Assert.Equal(EstadoDeLaJunta.Juntando, junta.Estado);
        Assert.Equal(LoQueSeCopio.Nueva, junta.Agregar(ImagenesDePrueba.Foto(120, 100), Ahora));
        Assert.Equal(2, junta.Imagenes.Count);
        Assert.Equal(180, junta.Orden![0].Giro);
        Assert.Equal("2 imágenes · ancho 3 cm", JuntaDeImagenes.Resumen(2, junta.Ajustes));
    }
}
