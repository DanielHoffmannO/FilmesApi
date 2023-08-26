using Microsoft.EntityFrameworkCore;
using Senha.Persistence.Context;
using SenhasGpt.Domain.Entidade;
using SenhasGpt.Domain.Repository;

namespace Senha.Persistence.Repositories
{
    public class SenhaRepository : Repository<SenhaGpt, int>, ISenhaRepository
    {
        public SenhaRepository(SenhaContext context) : base(context)
        {
        }
    }
}
