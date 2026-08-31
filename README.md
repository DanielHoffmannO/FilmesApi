[>] [English](README.en.md) | [Espanol](README.es.md)

# {o} FilmesApi

[![.NET CI](https://github.com/DanielHoffmannO/FilmesApi/actions/workflows/dotnet.yml/badge.svg)](https://github.com/DanielHoffmannO/FilmesApi/actions)
![.NET](https://img.shields.io/badge/.NET-8.0-512BD4?logo=dotnet)
![SQLite](https://img.shields.io/badge/SQLite-003B57?logo=sqlite&logoColor=white)
![Docker](https://img.shields.io/badge/Docker-Ready-2496ED?logo=docker&logoColor=white)
![License](https://img.shields.io/badge/license-MIT-green)

> API REST para gerenciar e streamar sua colecao pessoal de filmes na rede local.

## {=} Tech Stack

- .NET 8 / ASP.NET Core
- Entity Framework Core + SQLite
- Docker (Alpine multi-stage)

## [!] Como Rodar

```bash
mkdir media data
docker compose up -d
# Acessa: http://IP:8080
```

### Local

```bash
dotnet run --project src/FilmesApi
```

## [>] Endpoints

| Metodo | Rota | O que faz |
|--------|------|-----------|
| `GET` | `/api/filmes` | Lista (filtro: `?assistido=false`); traz o ponto de retomada de cada filme |
| `GET` | `/api/filmes/{id}` | Detalhes |
| `POST` | `/api/filmes` | Adiciona manualmente |
| `PUT` | `/api/filmes/{id}/assistido` | Alterna "assistido" |
| `DELETE` | `/api/filmes/{id}` | Remove |
| `POST` | `/api/filmes/scan` | Importa videos da pasta de midia |
| `GET` | `/api/filmes/{id}/stream` | Stream direto (codec compativel com o navegador) |
| `GET` | `/api/filmes/{id}/hls/playlist.m3u8` | Stream via HLS (transcodifica sob demanda) |
| `GET` | `/api/filmes/continuar` | Filmes com reproducao pendente ("continuar assistindo") |
| `GET` `PUT` `DELETE` | `/api/filmes/{id}/progresso` | Le / salva / esquece onde a reproducao parou |

## {/} Arquitetura

```
src/FilmesApi/
+-- Controllers/
+-- Services/
+-- Models/
+-- Data/
+-- wwwroot/
+-- Program.cs
```

## [$] Licenca

Este projeto esta sob a licenca [MIT](LICENSE).
