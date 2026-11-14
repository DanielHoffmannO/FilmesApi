namespace FilmesApi.Services;

public enum ResultadoFila { Adicionado, JaNaFila, Cheia }

// Fila manual "tocar a seguir": o que o Daniel mandou tocar depois do que está passando. Tem precedência sobre o próximo episódio da série;
// vazia, tudo funciona como antes. Fica em memória (reiniciar o servidor esvazia). Um item sai da fila quando começa a tocar (Selecionar).
public partial class PlayerStateService
{
    public const int FilaMaxima = 30;
    private readonly List<int> _fila = [];

    /// <summary>Ids na ordem em que vão tocar.</summary>
    public IReadOnlyList<int> Fila { get { lock (_lock) return [.. _fila]; } }

    public ResultadoFila Enfileirar(int filmeId)
    {
        lock (_lock)
        {
            if (_fila.Contains(filmeId)) return ResultadoFila.JaNaFila;
            if (_fila.Count >= FilaMaxima) return ResultadoFila.Cheia;
            _fila.Add(filmeId);
            return ResultadoFila.Adicionado;
        }
    }

    public bool RemoverDaFila(int filmeId)
    {
        lock (_lock) return _fila.Remove(filmeId);
    }

    public void LimparFila()
    {
        lock (_lock) _fila.Clear();
    }
}
