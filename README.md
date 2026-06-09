# FilmesApi

API REST em .NET 8 para gerenciar e fazer streaming de uma coleção pessoal de filmes. Roda em Docker, sem autenticação, acessível por qualquer dispositivo na rede local.

## Stack

- .NET 8 / ASP.NET Core
- Entity Framework Core + SQLite
- Docker (Alpine multi-stage)

## Estrutura

```
FilmesApi/
├── src/FilmesApi/
│   ├── Controllers/
│   ├── Services/
│   ├── Models/
│   ├── Data/
│   └── Program.cs
├── docker-compose.yml
├── Dockerfile
├── media/           ← vídeos aqui
└── data/            ← SQLite persiste aqui
```

## Como Rodar

```bash
mkdir media data
docker compose up -d
# Acessa: http://IP:8080
```

## Endpoints

| Método | Rota | Descrição |
|---|---|---|
| `GET` | `/api/filmes` | Lista (filtros: `?genero=Acao&assistido=false`) |
| `GET` | `/api/filmes/{id}` | Detalhes |
| `POST` | `/api/filmes` | Adiciona manualmente |
| `PUT` | `/api/filmes/{id}/assistido` | Marca como assistido |
| `DELETE` | `/api/filmes/{id}` | Remove |
| `POST` | `/api/filmes/scan` | Importa vídeos da pasta de mídia |
| `GET` | `/api/filmes/{id}/stream` | Stream do vídeo |
