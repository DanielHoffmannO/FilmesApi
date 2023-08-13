# API do Controller de Filmes

Este projeto implementa uma API web para gerenciar filmes. Ela fornece pontos de extremidade para executar várias ações relacionadas a filmes, como recuperar uma lista de filmes, adicionar novos filmes, atualizar detalhes de filmes, marcar filmes como assistidos e remover filmes.

## Sumário

- [Começando](#começando)
  - [Pré-requisitos](#pré-requisitos)
  - [Instalação](#instalação)
- [Pontos de Extremidade](#pontos-de-extremidade)
  - [GET /Filme](#get-filme)
  - [POST /Filme](#post-filme)
  - [PUT /Filme/{id}](#put-filmeid)
  - [PUT /Filme/{id}/MarcarAssistido](#put-filmeidmarcarassistido)
  - [DELETE /Filme/{id}](#delete-filmeid)
- [Enumerações](#enumerações)
- [Mapeamento de Entidades](#mapeamento-de-entidades)
- [Bibliotecas](#bibliotecas)

## Começando

### Pré-requisitos

- [.NET Core SDK](https://dotnet.microsoft.com/download) (versão 3.1 ou superior)

### Instalação

1. Clone este repositório para a sua máquina local.
2. Navegue até o diretório raiz do projeto usando a linha de comando.
3. Execute o seguinte comando para iniciar a aplicação:

   ```bash
   dotnet run
   ```

A API estará acessível em `https://localhost:5001` por padrão.

## Pontos de Extremidade

### GET /Filme

Recupera uma lista de filmes.

### POST /Filme

Adiciona um novo filme à coleção.

### PUT /Filme/{id}

Atualiza os detalhes de um filme existente identificado pelo seu ID.

### PUT /Filme/{id}/MarcarAssistido

Marca um filme como assistido, com base no seu ID.

### DELETE /Filme/{id}

Remove um filme da coleção usando o seu ID.

## Enumerações

O projeto inclui uma enumeração `EGenero` que representa vários gêneros de filmes.

## Mapeamento de Entidades

A classe `FilmesMapping` define o mapeamento de entidade para tabela de banco de dados para a entidade `Filme`.

## Bibliotecas

O projeto utiliza as seguintes bibliotecas:

- [Daniel.Repository](https://www.nuget.org/packages/Daniel.Repository): Uma biblioteca para gerenciar operações de repositório de dados.
- [Mapster](https://www.nuget.org/packages/Mapster): Um mapeador de objetos rápido e personalizável.
- [Microsoft.EntityFrameworkCore](https://www.nuget.org/packages/Microsoft.EntityFrameworkCore): Entity Framework Core para operações de banco de dados.
- [Microsoft.EntityFrameworkCore.SqlServer](https://www.nuget.org/packages/Microsoft.EntityFrameworkCore.SqlServer): Suporte ao SQL Server para o Entity Framework Core.
- [Serilog.AspNetCore](https://www.nuget.org/packages/Serilog.AspNetCore): Biblioteca de registro para aplicações ASP.NET Core.
- [Serilog.Extensions.Hosting](https://www.nuget.org/packages/Serilog.Extensions.Hosting): Integração do Serilog para .NET Core Generic Host.
- [Swashbuckle.AspNetCore](https://www.nuget.org/packages/Swashbuckle.AspNetCore): Fornece documentação gerada automaticamente para a API usando o Swagger.

Sinta-se à vontade para explorar o código para aprender mais sobre como a API funciona e como ela pode ser personalizada para atender às necessidades do seu projeto. Se tiver alguma dúvida ou precisar de mais assistência, não hesite em perguntar!
