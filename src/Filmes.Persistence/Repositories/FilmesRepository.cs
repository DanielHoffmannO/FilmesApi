using Filmes.Domain.Entidade;
using Filmes.Domain.Repository;
using Filmes.Persistence.Context;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using System.Data;

namespace Filmes.Persistence.Repositories
{
    public class FilmesRepository : Repository<Filme, short>, IFilmesRepository
    {
        public FilmesRepository(FilmeContext context) : base(context)
        {
        }

        public List<Filme> ObterLista() => DbSet.ToList();
        public Filme ObterUltimo() => DbSet.OrderByDescending(x=>x.Id).First();
    }
}
