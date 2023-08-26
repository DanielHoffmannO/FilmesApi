using Microsoft.EntityFrameworkCore;
using SenhasGpt.Domain.Entidade;


namespace SenhasGpt.Domain.Repository;

public interface ISenhaRepository : IRepository<SenhaGpt, int>
{
}
