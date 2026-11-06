using FilmesApi.Models;
using Microsoft.EntityFrameworkCore;

namespace FilmesApi.Data;

public class AppDbContext : DbContext
{
    public AppDbContext(DbContextOptions<AppDbContext> options) : base(options) { }

    public DbSet<Filme> Filmes => Set<Filme>();
    public DbSet<ProgressoReproducao> Progressos => Set<ProgressoReproducao>();
    public DbSet<PreferenciaSerie> PreferenciasSerie => Set<PreferenciaSerie>();
    public DbSet<PreferenciaEpisodio> PreferenciasEpisodio => Set<PreferenciaEpisodio>();
    public DbSet<PreferenciaTemporada> PreferenciasTemporada => Set<PreferenciaTemporada>();
    public DbSet<AberturaEpisodio> Aberturas => Set<AberturaEpisodio>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<Filme>(e =>
        {
            e.HasKey(f => f.Id);
            e.Property(f => f.Titulo).IsRequired().HasMaxLength(200);
            e.Property(f => f.TituloOriginal).HasMaxLength(300);
            e.Property(f => f.PosterUrl).HasMaxLength(500);
            e.Property(f => f.Sinopse).HasMaxLength(2000);
            e.HasIndex(f => f.ArquivoPath).IsUnique();
        });

        modelBuilder.Entity<PreferenciaSerie>(e =>
        {
            e.ToTable("PreferenciasSerie");
            e.HasKey(p => p.ChaveSerie);
            e.Property(p => p.IdiomaAudio).IsRequired();
        });

        modelBuilder.Entity<PreferenciaEpisodio>(e =>
        {
            e.ToTable("PreferenciasEpisodio");
            e.HasKey(p => p.FilmeId);
            e.HasOne(p => p.Filme).WithMany().HasForeignKey(p => p.FilmeId).OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<PreferenciaTemporada>(e =>
        {
            e.ToTable("PreferenciasTemporada");
            e.HasKey(p => new { p.ChaveSerie, p.Temporada });
        });

        modelBuilder.Entity<AberturaEpisodio>(e =>
        {
            e.ToTable("Aberturas");
            e.HasKey(a => a.FilmeId);
            e.HasOne(a => a.Filme).WithMany().HasForeignKey(a => a.FilmeId).OnDelete(DeleteBehavior.Cascade);
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
