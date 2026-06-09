using FilmesApi.Models;
using Microsoft.EntityFrameworkCore;

namespace FilmesApi.Data;

public class AppDbContext : DbContext
{
    public AppDbContext(DbContextOptions<AppDbContext> options) : base(options) { }

    public DbSet<Filme> Filmes => Set<Filme>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<Filme>(e =>
        {
            e.HasKey(f => f.Id);
            e.Property(f => f.Titulo).IsRequired().HasMaxLength(200);
            e.Property(f => f.Genero).HasConversion<byte>();
        });
    }
}
