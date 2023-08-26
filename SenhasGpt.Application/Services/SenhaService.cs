using SenhasGpt.Application.Interfece;
using SenhasGpt.Domain.Repository;

namespace SenhasGpt.Application.Services;

public class SenhaService : ISenhaService
{
    private readonly ISenhaRepository _SenhaRepository;
    public SenhaService(ISenhaRepository SenhaRepository)
    {
        _SenhaRepository = SenhaRepository;
    }
}
