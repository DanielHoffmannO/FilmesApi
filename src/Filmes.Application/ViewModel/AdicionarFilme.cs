using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Filmes.Application.ViewModel
{
    public class AdicionarFilme
    {
        public short Id { get; set; }
        public string Titulo { get; set; }
        public byte Genero { get; set; }
        public decimal Duracao { get; set; }
        public bool Assistido { get; set; }
    }
}
