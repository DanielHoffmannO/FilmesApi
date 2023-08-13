using Filmes.Domain.Entidade;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Filmes.Persistence.Mappings.Filmes;

public class FilmesMapping : IEntityTypeConfiguration<Filme>
{
    public void Configure(EntityTypeBuilder<Filme> builder)
    {
        builder.ToTable("Filme");
        builder.HasKey(x => x.Id);

        builder.Property(d => d.Titulo).HasColumnName("Titulo").HasColumnType("varchar(50)");
        builder.Property(d => d.Genero).HasColumnName("Genero").HasColumnType("tinyint");
        builder.Property(d => d.Duracao).HasColumnName("Duracao").HasColumnType("decimal(10, 2)");
        builder.Property(d => d.Assistido).HasColumnName("Assistido").HasColumnType("bit");
    }
}
