using Microsoft.EntityFrameworkCore;
using System.Reflection;

namespace Filmes.Persistence.Context
{
    public class FilmeContext : AbstractContext
    {
        public FilmeContext(DbContextOptions<FilmeContext> options) : base(options) { }

        protected override Assembly GetConfigurationAssembly()
            => GetType().Assembly;

        protected override Func<Type, bool> GetConfigurationTypePredicate()
            => type => type.Namespace != null && type.Namespace.EndsWith("Mappings.Filmes");
    }
}
