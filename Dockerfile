FROM mcr.microsoft.com/dotnet/sdk:9.0-alpine AS build
WORKDIR /src
COPY src/FilmesApi/FilmesApi.csproj src/FilmesApi/
RUN dotnet restore src/FilmesApi/FilmesApi.csproj
COPY src/ src/
RUN dotnet publish src/FilmesApi/FilmesApi.csproj -c Release -o /app/publish --no-restore

# Base Debian (não Alpine): o ffmpeg do apk não tem suporte a rkmpp (VPU do RK3399/RK3588).
# O jellyfin-ffmpeg traz esse suporte pronto (--enable-rkmpp), incluindo libs bundladas.
FROM mcr.microsoft.com/dotnet/aspnet:9.0-bookworm-slim AS final
ENV DEBIAN_FRONTEND=noninteractive
# Roda como usuário não-root (UID/GID 1000, convenção do primeiro usuário em
# Debian/Armbian/Raspberry Pi OS) em vez de root. No grupo "video" pra ter chance de acessar
# o device node da VPU (RK3399/RK3588) quando ele for passado via `devices:` no compose — ver
# RkmppCapabilityService. Se a pasta ./data do host no seu Radxa pertencer a outro UID, rode
# `chown -R 1000:1000 ./data` no host ou sobrescreva com `user:` no docker-compose.yml.
#
# O nome do pacote jellyfin-ffmpeg muda com a major do ffmpeg (jellyfin-ffmpeg7, 8, ...) —
# JELLYFIN_FFMPEG_PKG abaixo pega sempre a mais nova disponível no repositório.
RUN apt-get update \
 && apt-get install -y --no-install-recommends ca-certificates gnupg curl \
 && mkdir -p /etc/apt/keyrings \
 && curl -fsSL https://repo.jellyfin.org/jellyfin_team.gpg.key | gpg --dearmor -o /etc/apt/keyrings/jellyfin.gpg \
 && { \
      echo "Types: deb"; \
      echo "URIs: https://repo.jellyfin.org/debian"; \
      echo "Suites: bookworm"; \
      echo "Components: main"; \
      echo "Architectures: $(dpkg --print-architecture)"; \
      echo "Signed-By: /etc/apt/keyrings/jellyfin.gpg"; \
    } > /etc/apt/sources.list.d/jellyfin.sources \
 && apt-get update \
 && JELLYFIN_FFMPEG_PKG=$(apt-cache search '^jellyfin-ffmpeg[0-9]+$' | sort -V | tail -1 | cut -d' ' -f1) \
 && apt-get install -y --no-install-recommends "$JELLYFIN_FFMPEG_PKG" \
 && apt-get purge -y --autoremove gnupg \
 && rm -rf /var/lib/apt/lists/* \
 && groupadd -g 1000 filmesapi \
 && useradd -u 1000 -g filmesapi -G video -M -s /usr/sbin/nologin filmesapi \
 && mkdir -p /data && chown filmesapi:filmesapi /data

WORKDIR /app
EXPOSE 8080
COPY --from=build --chown=filmesapi:filmesapi /app/publish .
ENV ASPNETCORE_URLS=http://+:8080
# jellyfin-ffmpeg instala em /usr/lib/jellyfin-ffmpeg/, sem symlink garantido em /usr/bin.
ENV FfmpegPath=/usr/lib/jellyfin-ffmpeg/ffmpeg
ENV FfprobePath=/usr/lib/jellyfin-ffmpeg/ffprobe
USER filmesapi
ENTRYPOINT ["dotnet", "FilmesApi.dll"]
