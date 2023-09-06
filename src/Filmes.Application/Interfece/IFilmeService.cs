
using Filmes.Application.ViewModel;
using Filmes.Domain.Dto;
using Filmes.Domain.Entidade;

namespace Filmes.Application.Interfece
{
    public interface IFilmeService
    {
        bool MarcarFilmeComoAssistido(short id);
        short AdicionarFilme(FilmeDto filme);
        bool AtualizarFilme(short id, FilmeDto dto);
        void DeletarFilme(short id);
        List<Filme> ObterLista();
    }
}
