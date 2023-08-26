using Microsoft.EntityFrameworkCore;

namespace SenhasGpt.Domain.Entidade;

public class SenhaGpt : Entity<SenhaGpt, int>
{
    public string PalavraChave { get; set; }
    public string SenhaGerada { get; set; }
    public DateTime? DataCadastro { get; set; }
    public DateTime? DataVigencia { get; set; }
}
