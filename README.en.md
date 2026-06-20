🌐 [Português](README.md) | [Español](README.es.md)

# FilmesApi

REST API in .NET 8 to manage and stream a personal movie collection. Runs in Docker, no authentication, accessible from any device on the local network.

## Stack

- .NET 8 / ASP.NET Core
- Entity Framework Core + SQLite
- Docker (Alpine multi-stage)

## How to Run

```bash
mkdir media data
docker compose up -d
# Access: http://IP:8080
```

## Endpoints

| Method | Route | Description |
|---|---|---|
| `GET` | `/api/filmes` | List (filters: `?genero=Acao&assistido=false`) |
| `GET` | `/api/filmes/{id}` | Details |
| `POST` | `/api/filmes` | Add manually |
| `PUT` | `/api/filmes/{id}/assistido` | Mark as watched |
| `DELETE` | `/api/filmes/{id}` | Remove |
| `POST` | `/api/filmes/scan` | Import videos from media folder |
| `GET` | `/api/filmes/{id}/stream` | Video stream |