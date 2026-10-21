using System.Net;

namespace PapeleriaPos.Herramientas.Simulador;

/// <summary>
/// La red entre una caja simulada y la caja principal, con un interruptor (como el de las pruebas): mientras está caída no se
/// puede conectar (nada llega a la caja principal) y lo que iba a medias se corta, también el aviso (SSE) que se queda
/// conectado y la copia que se estaba bajando. La caja ve lo mismo que con el Wi-Fi caído: lo que pidió en ese momento puede
/// haber llegado o no, y lo siguiente ni sale. Solo esa caja: las demás siguen con la caja principal.
/// </summary>
public sealed class RedSimulada
{
    private readonly Lock _candado = new();
    private CancellationTokenSource _corte = new();

    /// <summary>Está caída: no se puede conectar a la caja principal.</summary>
    public bool Caida
    {
        get
        {
            lock (_candado)
                return _caida;
        }
    }

    private bool _caida;

    /// <summary>Se cae: lo que iba a medias se corta.</summary>
    public void Caer()
    {
        CancellationTokenSource corte;
        lock (_candado)
        {
            if (_caida)
                return;
            _caida = true;
            corte = _corte;
        }
        corte.Cancel();
    }

    /// <summary>Vuelve: lo siguiente ya llega.</summary>
    public void Volver()
    {
        lock (_candado)
        {
            if (!_caida)
                return;
            // La de antes se queda cortada: lo que se cortó, cortado está.
            _corte = new CancellationTokenSource();
            _caida = false;
        }
    }

    private (bool Caida, CancellationToken Corte) Estado()
    {
        lock (_candado)
            return (_caida, _corte.Token);
    }

    /// <summary>Lo que va por esta red pasa por el interruptor; <paramref name="interno"/> es la red de verdad (o la de la prueba).</summary>
    public HttpMessageHandler Envolver(HttpMessageHandler interno) => new Interruptor(this, interno);

    private sealed class Interruptor(RedSimulada red, HttpMessageHandler interno) : DelegatingHandler(interno)
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var (caida, corte) = red.Estado();
            // Como con la caja principal apagada o el Wi-Fi caído: ni se conecta (la caja sabe que no llegó).
            if (caida)
                throw new HttpRequestException(HttpRequestError.ConnectionError, "La red de esta caja está caída (simulada).");
            using var unidos = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, corte);
            HttpResponseMessage respuesta;
            try
            {
                respuesta = await base.SendAsync(request, unidos.Token);
            }
            catch (OperationCanceledException) when (corte.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
            {
                // Se cayó a media petición: pudo haber llegado (la caja no lo sabe).
                throw new HttpRequestException("Se cayó la red de esta caja a media petición (simulada).");
            }
            respuesta.Content = new ContenidoCortable(respuesta.Content, corte);
            return respuesta;
        }
    }

    /// <summary>El cuerpo de la respuesta: si la red se cae mientras se lee (la copia, el aviso), la lectura falla.</summary>
    private sealed class ContenidoCortable : HttpContent
    {
        private readonly HttpContent _interno;
        private readonly CancellationToken _corte;

        public ContenidoCortable(HttpContent interno, CancellationToken corte)
        {
            (_interno, _corte) = (interno, corte);
            foreach (var (nombre, valores) in interno.Headers)
                Headers.TryAddWithoutValidation(nombre, valores);
        }

        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context) =>
            SerializeToStreamAsync(stream, context, CancellationToken.None);

        protected override async Task SerializeToStreamAsync(Stream stream, TransportContext? context, CancellationToken cancellationToken)
        {
            await using var origen = await CreateContentReadStreamAsync(cancellationToken);
            await origen.CopyToAsync(stream, cancellationToken);
        }

        protected override Task<Stream> CreateContentReadStreamAsync() => CreateContentReadStreamAsync(CancellationToken.None);

        protected override async Task<Stream> CreateContentReadStreamAsync(CancellationToken cancellationToken) =>
            new FlujoCortable(await _interno.ReadAsStreamAsync(cancellationToken), _corte);

        protected override bool TryComputeLength(out long length)
        {
            length = _interno.Headers.ContentLength ?? 0;
            return _interno.Headers.ContentLength is not null;
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
                _interno.Dispose();
            base.Dispose(disposing);
        }
    }

    private sealed class FlujoCortable(Stream interno, CancellationToken corte) : Stream
    {
        private static IOException Cortada() => new("Se cayó la red de esta caja (simulada).");

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (corte.IsCancellationRequested)
                throw Cortada();
            using var unidos = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, corte);
            try
            {
                return await interno.ReadAsync(buffer, unidos.Token);
            }
            catch (OperationCanceledException) when (corte.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
            {
                throw Cortada();
            }
        }

        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
            ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

        public override int Read(byte[] buffer, int offset, int count) =>
            corte.IsCancellationRequested ? throw Cortada() : interno.Read(buffer, offset, count);

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override void Flush()
        {
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            if (disposing)
                interno.Dispose();
            base.Dispose(disposing);
        }

        public override async ValueTask DisposeAsync()
        {
            await interno.DisposeAsync();
            await base.DisposeAsync();
        }
    }
}
