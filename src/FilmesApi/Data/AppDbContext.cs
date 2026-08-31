using FilmesApi.Models;
using Microsoft.EntityFrameworkCore;

namespace FilmesApi.Data;

public class AppDbContext : DbContext
{
    public AppDbContext(DbContextOptions<AppDbContext> options) : base(options) { }

    public DbSet<Filme> Filmes => Set<Filme>();
    public DbSet<ProgressoReproducao> Progressos => Set<ProgressoReproducao>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<Filme>(e =>
        {
            e.HasKey(f => f.Id);
            e.Property(f => f.Titulo).IsRequired().HasMaxLength(200);
        });

        modelBuilder.Entity<ProgressoReproducao>(e =>
        {
            e.HasKey(p => p.FilmeId);
            e.HasOne(p => p.Filme)
                .WithOne(f => f.Progresso)
                .HasForeignKey<ProgressoReproducao>(p => p.FilmeId)
                .OnDelete(DeleteBehavior.Cascade);
        });
    }
}
