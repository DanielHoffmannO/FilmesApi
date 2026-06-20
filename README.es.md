🌐 [Português](README.md) | [English](README.en.md)

# FilmesApi

API REST en .NET 8 para gestionar y hacer streaming de una colección personal de películas. Corre en Docker, sin autenticación, accesible desde cualquier dispositivo en la red local.

## Stack

- .NET 8 / ASP.NET Core
- Entity Framework Core + SQLite
- Docker (Alpine multi-stage)

## Cómo Ejecutar

```bash
mkdir media data
docker compose up -d
# Acceso: http://IP:8080
```

## Endpoints

| Método | Ruta | Descripción |
|---|---|---|
| `GET` | `/api/filmes` | Lista (filtros: `?genero=Acao&assistido=false`) |
| `GET` | `/api/filmes/{id}` | Detalles |
| `POST` | `/api/filmes` | Agregar manualmente |
| `PUT` | `/api/filmes/{id}/assistido` | Marcar como visto |
| `DELETE` | `/api/filmes/{id}` | Eliminar |
| `POST` | `/api/filmes/scan` | Importar videos de la carpeta de medios |
| `GET` | `/api/filmes/{id}/stream` | Stream del video |