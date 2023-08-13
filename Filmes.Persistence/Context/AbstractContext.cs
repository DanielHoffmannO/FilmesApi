using System.Reflection;

namespace Microsoft.EntityFrameworkCore
{
    public abstract class AbstractContext : DbContext
    {
        protected Assembly ConfigurationAssembly => GetConfigurationAssembly();
        protected Func<Type, bool> ConfigurationTypePredicate => GetConfigurationTypePredicate();

        protected AbstractContext() { }

        protected AbstractContext(DbContextOptions options) : base(options) { }

        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            if (ConfigurationAssembly == null)
                throw new ArgumentNullException(nameof(ConfigurationAssembly), "The configuration assembly for the context must be defined.");

            base.OnModelCreating(modelBuilder);
            modelBuilder.ApplyConfigurationsFromAssembly(ConfigurationAssembly, ConfigurationTypePredicate);
        }

        protected abstract Assembly GetConfigurationAssembly();
        protected abstract Func<Type, bool> GetConfigurationTypePredicate();

        public virtual bool Commit() =>
            SaveChanges() > 0;

        public virtual async Task<bool> CommitAsync() =>
            await SaveChangesAsync().ConfigureAwait(continueOnCapturedContext: true) > 0;
    }

}
