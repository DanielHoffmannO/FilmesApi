using Filmes.Domain.Entidade;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Filmes.Persistence.Mappings.Filmes
{
    public class FilmesMapping : IEntityTypeConfiguration<Filme>
    {
        public void Configure(EntityTypeBuilder<Filme> builder)
        {
            builder.ToTable("Filme");
            builder.HasKey(x => x.Id);

            builder.Property(d => d.Titulo).HasColumnName("Titulo").HasColumnType("varchar(100)").IsRequired();
            builder.Property(d => d.Genero).HasColumnName("Genero").HasColumnType("tinyint");
            builder.Property(d => d.DuracaoMinutos).HasColumnName("DuracaoMinutos").HasColumnType("int");
            builder.Property(d => d.AnoLancamento).HasColumnName("AnoLancamento").HasColumnType("int");
            builder.Property(d => d.Diretor).HasColumnName("Diretor").HasColumnType("varchar(100)");
            builder.Property(d => d.DataCadastro).HasColumnName("DataCadastro").HasColumnType("datetime");
            builder.Property(d => d.DataAssistido).HasColumnName("DataAssistido").HasColumnType("datetime");
        }
    }
}
