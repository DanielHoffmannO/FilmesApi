namespace FilmesApi.Services;

/// <summary>O que cada worker de fundo (scan, limpeza, pré-aquecimento, metadados) fez por último: quando,
/// quanto demorou, resultado. Só memória (zera ao reiniciar); aparece em <c>/api/status</c> e no <c>status.html</c>.</summary>
public sealed class RegistroDeTarefas
{
    public sealed record Tarefa(string Nome, string Estado, DateTime? UltimaExecucao, long? DuracaoMs,
        string? Resumo, string? Erro, int Execucoes, int Falhas);

    private sealed class Linha
    {
        public string Estado = "aguardando";
        public DateTime? Ultima; public long? Duracao; public string? Resumo, Erro;
        public int Execucoes, Falhas;
    }

    private readonly object _trava = new();
    private readonly Dictionary<string, Linha> _tarefas = [];   // ordem de declaração = ordem na tela
    private readonly List<string> _ordem = [];

    /// <summary>Faz a tarefa aparecer na lista mesmo sem nunca ter rodado ("desligada" quando a config a desliga).</summary>
    public void Declarar(string nome, bool ligada = true)
    {
        lock (_trava) Obter(nome).Estado = ligada ? "aguardando" : "desligada";
    }

    public Medicao Iniciar(string nome)
    {
        lock (_trava) Obter(nome).Estado = "rodando";
        return new Medicao(this, nome);
    }

    public IReadOnlyList<Tarefa> Snapshot()
    {
        lock (_trava)
            return _ordem.Select(n => { var l = _tarefas[n];
                return new Tarefa(n, l.Estado, l.Ultima, l.Duracao, l.Resumo, l.Erro, l.Execucoes, l.Falhas); }).ToList();
    }

    private Linha Obter(string nome)
    {
        if (!_tarefas.TryGetValue(nome, out var l)) { _tarefas[nome] = l = new Linha(); _ordem.Add(nome); }
        return l;
    }

    private void Concluir(string nome, long ms, string? resumo, string? erro)
    {
        lock (_trava)
        {
            var l = Obter(nome);
            l.Ultima = DateTime.UtcNow; l.Duracao = ms; l.Resumo = resumo; l.Erro = erro;
            l.Execucoes++;
            if (erro is null) l.Estado = "ok"; else { l.Estado = "falhou"; l.Falhas++; }
        }
    }

    public sealed class Medicao(RegistroDeTarefas dono, string nome)
    {
        private readonly System.Diagnostics.Stopwatch _relogio = System.Diagnostics.Stopwatch.StartNew();
        private int _fim;

        public void Ok(string? resumo = null) => Fim(resumo, null);
        public void Falhou(Exception ex) => Fim(null, ex.Message);

        private void Fim(string? resumo, string? erro)
        {
            if (Interlocked.Exchange(ref _fim, 1) == 1) return;
            dono.Concluir(nome, _relogio.ElapsedMilliseconds, resumo, erro);
        }
    }
}
