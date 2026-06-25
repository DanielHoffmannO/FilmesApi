🌐 [English](README.en.md) | [Español](README.es.md)

# 🎬 FilmesApi

[![.NET Build](https://github.com/DanielHoffmannO/FilmesApi/actions/workflows/dotnet.yml/badge.svg)](https://github.com/DanielHoffmannO/FilmesApi/actions/workflows/dotnet.yml)
![.NET 8](https://img.shields.io/badge/.NET-8.0-512BD4?logo=dotnet)
![SQLite](https://img.shields.io/badge/SQLite-003B57?logo=sqlite&logoColor=white)
![Docker](https://img.shields.io/badge/Docker-Alpine-2496ED?logo=docker&logoColor=white)
![License](https://img.shields.io/badge/License-MIT-green)

> API REST para gerenciar e streamar sua coleção pessoal de filmes na rede local.  
> Rode no Raspberry Pi, NAS ou qualquer máquina com Docker e assista direto na Smart TV.

---

## 🛠️ Tech Stack

| Camada | Tecnologia |
|--------|-----------|
| Runtime | .NET 8 / ASP.NET Core |
| Banco | SQLite (via EF Core) |
| Container | Docker multi-stage (Alpine) |
| CI | GitHub Actions |
| Streaming | Range requests nativo (pause/seek) |

---

## 🚀 Como Rodar

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

### Variáveis de Ambiente

| Variável | Padrão | Descrição |
|----------|--------|-----------|
| `ConnectionStrings__Default` | `Data Source=/data/filmes.db` | Connection string SQLite |
| `MediaPath` | `/media` | Pasta com os arquivos de vídeo |

---

## 📡 Endpoints

| Método | Rota | Descrição |
|--------|------|-----------|
| `GET` | `/api/filmes` | Lista filmes (filtro por `?genero=` e `?assistido=`) |
| `GET` | `/api/filmes/{id}` | Detalhes de um filme |
| `POST` | `/api/filmes` | Cadastra filme manualmente |
| `PUT` | `/api/filmes/{id}/assistido` | Toggle assistido/não assistido |
| `DELETE` | `/api/filmes/{id}` | Remove filme do catálogo |
| `POST` | `/api/filmes/scan` | Escaneia pasta de mídia e importa novos vídeos |
| `GET` | `/api/filmes/{id}/stream` | Stream de vídeo (suporta range/seek) |

### Gêneros disponíveis

`Acao`, `Aventura`, `Comedia`, `Drama`, `Terror`, `Romance`, `FiccaoCientifica`, `Fantasia`, `Suspense`, `Crime`, `Animacao`, `Documentario`, `Musical`, `SuperHeroi`, `Familia`

---

## 🏗️ Arquitetura

```
FilmesApi/
├── src/FilmesApi/
│   ├── Controllers/    # Endpoints REST
│   ├── Services/       # Lógica de negócio
│   ├── Models/         # Entidades e DTOs
│   ├── Data/           # DbContext (SQLite)
│   └── wwwroot/        # Frontend estático (fallback)
├── Dockerfile          # Multi-stage Alpine
├── docker-compose.yml  # Orquestração + volumes
└── .github/workflows/  # CI (build + restore)
```

- **Auto-migrate**: banco criado automaticamente na inicialização
- **Scan automático**: importa arquivos `.mp4`, `.mkv`, `.avi`, `.mov`, `.wmv`, `.flv`, `.webm`
- **Streaming otimizado**: buffer 64KB, sem timeout de data rate (TVs podem pausar)

---

## 📄 Licença

[MIT](LICENSE) — use, modifique e distribua livremente.

---

## 👤 Autor

**Daniel Hoffmann** — [GitHub](https://github.com/DanielHoffmannO)
