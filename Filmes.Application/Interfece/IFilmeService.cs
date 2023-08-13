
using Filmes.Application.ViewModel;
using Filmes.Domain.Entidade;

namespace Filmes.Application.Interfece
{
    public interface IFilmeService
    {
        Filme GetById();
    }
}
