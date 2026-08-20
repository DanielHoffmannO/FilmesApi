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
RUN apt-get update \
 && apt-get install -y --no-install-recommends ca-certificates gnupg curl \
 && mkdir -p /etc/apt/keyrings \
 && curl -fsSL https://repo.jellyfin.org/jellyfin_team.gpg.key | gpg --dearmor -o /etc/apt/keyrings/jellyfin.gpg \
 && printf 'Types: deb\nURIs: https://repo.jellyfin.org/debian\nSuites: bookworm\nComponents: main\nArchitectures: %s\nSigned-By: /etc/apt/keyrings/jellyfin.gpg\n' "$(dpkg --print-architecture)" \
    > /etc/apt/sources.list.d/jellyfin.sources \
 && apt-get update \
 # nome do pacote muda com a major do ffmpeg (jellyfin-ffmpeg7, 8, ...) — pega sempre a mais nova disponível.
 && JELLYFIN_FFMPEG_PKG=$(apt-cache search '^jellyfin-ffmpeg[0-9]+$' | sort -V | tail -1 | cut -d' ' -f1) \
 && apt-get install -y --no-install-recommends "$JELLYFIN_FFMPEG_PKG" \
 && apt-get purge -y gnupg curl \
 && apt-get autoremove -y \
 && rm -rf /var/lib/apt/lists/*
WORKDIR /app
EXPOSE 8080
COPY --from=build /app/publish .
ENV ASPNETCORE_URLS=http://+:8080
# jellyfin-ffmpeg instala em /usr/lib/jellyfin-ffmpeg/, sem symlink garantido em /usr/bin.
ENV FfmpegPath=/usr/lib/jellyfin-ffmpeg/ffmpeg
ENV FfprobePath=/usr/lib/jellyfin-ffmpeg/ffprobe
ENTRYPOINT ["dotnet", "FilmesApi.dll"]
