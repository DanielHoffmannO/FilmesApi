using Microsoft.Extensions.DependencyInjection;
using SenhasGpt.Application.Interfece;
using SenhasGpt.Application.Services;

namespace SenhasGpt.Application.Configuration;

public static class DependencyInjection
{
    public static IServiceCollection RegisterApplication(this IServiceCollection services)
        => services.AddScoped<ISenhaService, SenhaService>();
}
