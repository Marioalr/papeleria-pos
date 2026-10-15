using Microsoft.JSInterop;
using PapeleriaPos.Interfaz;

namespace PapeleriaPos.Pruebas.Interfaz;

/// <summary>
/// Las teclas de la entrada (entrada.js): cada pantalla quita las suyas al irse, también si se va antes de que entrada.js
/// conteste. Si no, se quedarían oyendo en la venta con una pantalla que ya no existe.
/// </summary>
public class TeclasEntradaTests
{
    /// <summary>Hace de entrada.js: guarda lo que se le pide, y puede tardar en cargar el módulo o en registrar.</summary>
    private sealed class EntradaJsFalso : IJSRuntime, IJSObjectReference
    {
        public List<string> Llamadas { get; } = [];
        public List<object?[]> Argumentos { get; } = [];
        public TaskCompletionSource? Importar { get; set; }
        public TaskCompletionSource? Registrar { get; set; }
        public bool Desechado { get; private set; }

        public ValueTask<TValue> InvokeAsync<TValue>(string identifier, object?[]? args)
        {
            Llamadas.Add(identifier);
            Argumentos.Add(args ?? []);
            var espera = identifier switch
            {
                "import" => Importar?.Task,
                "registrar" => Registrar?.Task,
                _ => null,
            };
            var valor = identifier == "import" ? (TValue)(object)this : default!;
            return espera is null ? ValueTask.FromResult(valor) : new ValueTask<TValue>(espera.ContinueWith(_ => valor, TaskScheduler.Default));
        }

        public ValueTask<TValue> InvokeAsync<TValue>(string identifier, CancellationToken cancellationToken, object?[]? args) =>
            InvokeAsync<TValue>(identifier, args);

        public ValueTask DisposeAsync()
        {
            Desechado = true;
            return ValueTask.CompletedTask;
        }
    }

    private sealed class Pantalla;

    [Fact]
    public async Task Al_irse_la_pantalla_quita_las_teclas_que_puso()
    {
        var js = new EntradaJsFalso();
        using var pantalla = DotNetObjectReference.Create(new Pantalla());
        var teclas = new TeclasEntrada(js);

        await teclas.PonerAsync(pantalla, TeclasEntrada.Fichas);
        await teclas.DisposeAsync();

        Assert.Equal(["import", "registrar", "quitar"], js.Llamadas);
        Assert.Equal(TeclasEntrada.Fichas, js.Argumentos[1][1]);
        // Quita con la misma ficha con que puso: las de la pantalla que llegó después se quedan.
        Assert.Equal(js.Argumentos[1][2], js.Argumentos[2][0]);
        Assert.True(js.Desechado);
    }

    [Fact]
    public async Task Si_la_pantalla_se_va_mientras_carga_el_modulo_no_pone_nada()
    {
        var js = new EntradaJsFalso { Importar = new TaskCompletionSource() };
        using var pantalla = DotNetObjectReference.Create(new Pantalla());
        var teclas = new TeclasEntrada(js);

        var poner = teclas.PonerAsync(pantalla, TeclasEntrada.SoloCambiar);
        await teclas.DisposeAsync();
        js.Importar.SetResult();
        await poner;

        Assert.Equal(["import"], js.Llamadas);
        Assert.True(js.Desechado);
    }

    [Fact]
    public async Task Si_la_pantalla_se_va_mientras_registra_las_quita_igual()
    {
        var js = new EntradaJsFalso { Registrar = new TaskCompletionSource() };
        using var pantalla = DotNetObjectReference.Create(new Pantalla());
        var teclas = new TeclasEntrada(js);

        var poner = teclas.PonerAsync(pantalla, TeclasEntrada.SoloCambiar);
        await teclas.DisposeAsync();
        js.Registrar.SetResult();
        await poner;

        // «quitar» sale después de «registrar» y con su misma ficha: en entrada.js van en ese orden.
        Assert.Equal(["import", "registrar", "quitar"], js.Llamadas);
        Assert.Equal(js.Argumentos[1][2], js.Argumentos[2][0]);
        Assert.True(js.Desechado);
    }
}
