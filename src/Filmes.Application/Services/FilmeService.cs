using Filmes.Application.Interfece;
using Filmes.Domain.Dto;
using Filmes.Domain.Entidade;
using Filmes.Domain.Enum;
using Filmes.Domain.Exceptions;
using Filmes.Domain.Repository;
using Mapster;

namespace Filmes.Application.Services;

public class FilmeService : IFilmeService
{
    private readonly IFilmesRepository _filmesRepository;
    public FilmeService(IFilmesRepository filmesRepository)
    {
        _filmesRepository = filmesRepository;
    }

    public short AdicionarFilme(FilmeDto dto)
    {
        var filme = dto.Adapt<Filme>();

        _filmesRepository.Add(filme);
        if (_filmesRepository.Commit())
            return _filmesRepository.ObterUltimo()?.Id ?? 0;
        throw new SqlException("Erro ao salvar informação no banco.");
    }

    public bool AtualizarFilme(short id, FilmeDto dto)
    {
        var filme = _filmesRepository.GetById(id) ?? throw new SqlException("Filme nao encontrado.");

        filme.Atualizar(dto.Titulo, dto.Genero, dto.DuracaoMinutos, dto.AnoLancamento, dto.Diretor);
        _filmesRepository.Update(filme);
        return _filmesRepository.Commit();
    }

    public bool MarcarFilmeComoAssistido(short id)
    {
        var filme = _filmesRepository.GetById(id) ?? throw new SqlException("Filme nao encontrado.");
        filme.MarcarComoAssistido();
        _filmesRepository.Update(filme);
        return _filmesRepository.Commit();
    }

    public void DeletarFilme(short id) => _filmesRepository.Delete(id);

    public List<Filme> ObterLista() => _filmesRepository.ObterLista();
}
