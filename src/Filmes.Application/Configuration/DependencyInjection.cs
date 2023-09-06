using Microsoft.Extensions.DependencyInjection;
using Filmes.Application.Services;
using Filmes.Application.Interfece;

namespace Filmes.Application.Configuration;

public static class DependencyInjection
{
    public static IServiceCollection RegisterApplication(this IServiceCollection services)
        => services.AddScoped<IFilmeService, FilmeService>();
}
