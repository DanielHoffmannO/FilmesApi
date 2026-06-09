# 🎬 FilmesApi — Mini Jellyflix Caseiro

API REST para gerenciar e assistir sua coleção de filmes na TV. Sem login, sem complicação — sobe no Docker e acessa de qualquer dispositivo na LAN.

## 🎯 Features

- **Scan automático** de pasta de mídia (detecta `.mp4`, `.mkv`, `.avi`, etc.)
- **Streaming de vídeo** com Range Processing (funciona em Smart TV, Chromecast, browser)
- **CRUD** de filmes com filtro por gênero e status (assistido/não assistido)
- **SQLite** — zero config de banco
- **CORS aberto** — acessa de qualquer dispositivo na rede
- **Docker** — um `docker compose up` e pronto

## 📁 Estrutura

```
FilmesApi/
├── src/
│   └── FilmesApi/
│       ├── Controllers/FilmesController.cs
│       ├── Services/FilmeService.cs
│       ├── Models/Filme.cs + Dtos.cs
│       ├── Data/AppDbContext.cs
│       ├── Program.cs
│       └── appsettings.json
├── docker-compose.yml
├── Dockerfile
└── media/              ← seus filmes aqui
```

## 🚀 Como Rodar

### Docker (recomendado)

```bash
# Coloque seus filmes na pasta media/
mkdir media data

# Sobe
docker compose up -d

# Acessa na TV: http://IP_DO_PC:8080
```

### Local (dev)

```bash
dotnet run --project src/FilmesApi
# Swagger: http://localhost:5000
```

## 📡 Endpoints

| Método | Rota | Descrição |
|---|---|---|
| `GET` | `/api/filmes` | Lista filmes (filtros: `?genero=Acao&assistido=false`) |
| `GET` | `/api/filmes/{id}` | Detalhes de um filme |
| `POST` | `/api/filmes` | Adiciona filme manualmente |
| `PUT` | `/api/filmes/{id}/assistido` | Marca como assistido |
| `DELETE` | `/api/filmes/{id}` | Remove |
| `POST` | `/api/filmes/scan` | Escaneia pasta de mídia e importa novos vídeos |
| `GET` | `/api/filmes/{id}/stream` | Stream do vídeo (pra assistir direto) |

### Assistir na TV

1. Suba o container
2. Abra o browser da TV: `http://192.168.x.x:8080/api/filmes`
3. Pra assistir: `http://192.168.x.x:8080/api/filmes/1/stream`

## 🛠️ Stack

- .NET 8 / ASP.NET Core
- Entity Framework Core + SQLite
- Docker (Alpine multi-stage)

## 📄 Licença

Projeto pessoal de portfólio.
