using Filmes.Application.Interfece;
using Filmes.Domain.Entidade;
using Filmes.Domain.Repository;

namespace Filmes.Application.Services;

public class FilmeService : IFilmeService
{
    private readonly IFilmesRepository _filmesRepository;
    public FilmeService(IFilmesRepository filmesRepository)
    {
        _filmesRepository = filmesRepository;
    }

    public Filme GetById()
    {
        return _filmesRepository.GetFilmeById(1);
    }
}
