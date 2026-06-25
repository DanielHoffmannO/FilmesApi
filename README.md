ðŸŒ [English](README.en.md) | [EspaÃ±ol](README.es.md)

# ðŸŽ¬ FilmesApi

[![.NET Build](https://github.com/DanielHoffmannO/FilmesApi/actions/workflows/dotnet.yml/badge.svg)](https://github.com/DanielHoffmannO/FilmesApi/actions/workflows/dotnet.yml)
![.NET 8](https://img.shields.io/badge/.NET-8.0-512BD4?logo=dotnet)
![SQLite](https://img.shields.io/badge/SQLite-003B57?logo=sqlite&logoColor=white)
![Docker](https://img.shields.io/badge/Docker-Alpine-2496ED?logo=docker&logoColor=white)
![License](https://img.shields.io/badge/License-MIT-green)

> API REST para gerenciar e streamar sua coleÃ§Ã£o pessoal de filmes na rede local.  
> Rode no Raspberry Pi, NAS ou qualquer mÃ¡quina com Docker e assista direto na Smart TV.

---

## ðŸ› ï¸ Tech Stack

| Camada | Tecnologia |
|--------|-----------|
| Runtime | .NET 8 / ASP.NET Core |
| Banco | SQLite (via EF Core) |
| Container | Docker multi-stage (Alpine) |
| CI | GitHub Actions |
| Streaming | Range requests nativo (pause/seek) |

---

## ðŸš€ Como Rodar

### Docker (recomendado)

```bash
git clone https://github.com/DanielHoffmannO/FilmesApi.git
cd FilmesApi

# Coloque seus filmes na pasta ./media
mkdir -p media data

docker compose up -d
```

Acesse: `http://localhost:8080/swagger`

### Local (.NET SDK)

```bash
dotnet restore
dotnet run --project src/FilmesApi
```

### VariÃ¡veis de Ambiente

| VariÃ¡vel | PadrÃ£o | DescriÃ§Ã£o |
|----------|--------|-----------|
| `ConnectionStrings__Default` | `Data Source=/data/filmes.db` | Connection string SQLite |
| `MediaPath` | `/media` | Pasta com os arquivos de vÃ­deo |

---

## ðŸ“¡ Endpoints

| MÃ©todo | Rota | DescriÃ§Ã£o |
|--------|------|-----------|
| `GET` | `/api/filmes` | Lista filmes (filtro por `?genero=` e `?assistido=`) |
| `GET` | `/api/filmes/{id}` | Detalhes de um filme |
| `POST` | `/api/filmes` | Cadastra filme manualmente |
| `PUT` | `/api/filmes/{id}/assistido` | Toggle assistido/nÃ£o assistido |
| `DELETE` | `/api/filmes/{id}` | Remove filme do catÃ¡logo |
| `POST` | `/api/filmes/scan` | Escaneia pasta de mÃ­dia e importa novos vÃ­deos |
| `GET` | `/api/filmes/{id}/stream` | Stream de vÃ­deo (suporta range/seek) |

### GÃªneros disponÃ­veis

`Acao`, `Aventura`, `Comedia`, `Drama`, `Terror`, `Romance`, `FiccaoCientifica`, `Fantasia`, `Suspense`, `Crime`, `Animacao`, `Documentario`, `Musical`, `SuperHeroi`, `Familia`

---

## ðŸ—ï¸ Arquitetura

```
FilmesApi/
â”œâ”€â”€ src/FilmesApi/
â”‚   â”œâ”€â”€ Controllers/    # Endpoints REST
â”‚   â”œâ”€â”€ Services/       # LÃ³gica de negÃ³cio
â”‚   â”œâ”€â”€ Models/         # Entidades e DTOs
â”‚   â”œâ”€â”€ Data/           # DbContext (SQLite)
â”‚   â””â”€â”€ wwwroot/        # Frontend estÃ¡tico (fallback)
â”œâ”€â”€ Dockerfile          # Multi-stage Alpine
â”œâ”€â”€ docker-compose.yml  # OrquestraÃ§Ã£o + volumes
â””â”€â”€ .github/workflows/  # CI (build + restore)
```

- **Auto-migrate**: banco criado automaticamente na inicializaÃ§Ã£o
- **Scan automÃ¡tico**: importa arquivos `.mp4`, `.mkv`, `.avi`, `.mov`, `.wmv`, `.flv`, `.webm`
- **Streaming otimizado**: buffer 64KB, sem timeout de data rate (TVs podem pausar)

---

## ðŸ“„ LicenÃ§a

[MIT](LICENSE) â€” use, modifique e distribua livremente.

---

## ðŸ‘¤ Autor

**Daniel Hoffmann** â€” [GitHub](https://github.com/DanielHoffmannO)
