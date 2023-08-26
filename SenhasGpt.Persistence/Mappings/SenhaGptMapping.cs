using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Microsoft.EntityFrameworkCore;
using SenhasGpt.Domain.Entidade;

namespace Senha.Persistence.Mappings.Senha
{
    public class SenhaGptMapping : IEntityTypeConfiguration<SenhaGpt>
    {
        public void Configure(EntityTypeBuilder<SenhaGpt> builder)
        {
            builder.ToTable("SenhaGpt");
            builder.HasKey(x => x.Id);

            builder.Property(d => d.PalavraChave).HasColumnName("PalavraChave").HasColumnType("varchar(50)").IsRequired();
            builder.Property(d => d.SenhaGerada).HasColumnName("SenhaGerada").HasColumnType("varchar(50)").IsRequired();
            builder.Property(d => d.DataCadastro).HasColumnName("DataCadastro").HasColumnType("datetime");
            builder.Property(d => d.DataVigencia).HasColumnName("DataVigencia").HasColumnType("datetime");
        }
    }
}
