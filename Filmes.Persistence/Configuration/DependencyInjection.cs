using Filmes.Domain.Repository;
using Filmes.Persistence.Context;
using Filmes.Persistence.Repositories;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Serilog;

namespace Filmes.Persistence
{
    public static class DependencyInjection
    {
        public static IServiceCollection RegisterPersistence(this IServiceCollection services) =>
            services.AddDbContext<FilmeContext>(options =>
            {
                options.UseSqlServer("Data Source=HOFFNOTE;User ID=sa;Password=daniel20;Connect Timeout=30;Encrypt=False;Trust Server Certificate=False;Application Intent=ReadWrite;Multi Subnet Failover=False");
#if DEBUG
                var loggerFactory = new LoggerFactory().AddSerilog(Log.Logger);
                options.UseLoggerFactory(loggerFactory);
                options.EnableDetailedErrors();
                options.EnableSensitiveDataLogging();
#endif
            }).RegisterRepositories();

        public static IServiceCollection RegisterRepositories(this IServiceCollection services) =>
            services.AddScoped<IFilmesRepository, FilmesRepository>();
    }
}
