using Filmes.Domain.Repository;
using Filmes.Persistence.Context;
using Filmes.Persistence.Repositories;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Configuration;
using Serilog;

namespace Filmes.Persistence
{
    public static class DependencyInjection
    {
        public static IServiceCollection RegisterPersistence(this IServiceCollection services, IConfiguration configuration) =>
            services.AddDbContext<FilmeContext>(options =>
            {
                options.UseSqlServer(configuration.GetConnectionString("FilmeConnection"));
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
