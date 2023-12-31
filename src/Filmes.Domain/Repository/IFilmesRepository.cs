﻿using Filmes.Domain.Entidade;
using Microsoft.EntityFrameworkCore;

namespace Filmes.Domain.Repository;

public interface IFilmesRepository : IRepository<Filme, short>
{
    Filme ObterUltimo();
    List<Filme> ObterLista();
}
