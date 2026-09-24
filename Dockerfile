FROM mcr.microsoft.com/dotnet/sdk:10.0-alpine AS build
WORKDIR /src
COPY src/FilmesApi/FilmesApi.csproj src/FilmesApi/
RUN dotnet restore src/FilmesApi/FilmesApi.csproj
COPY src/ src/
RUN dotnet publish src/FilmesApi/FilmesApi.csproj -c Release -o /app/publish --no-restore

# .NET 10 não publica mais imagem Debian nenhuma (só Ubuntu Noble, Alpine e Azure Linux) —
# Noble porque o ffmpeg do apk (Alpine) não tem suporte a rkmpp (VPU do RK3399/RK3588); o
# jellyfin-ffmpeg traz esse suporte pronto (--enable-rkmpp), incluindo libs bundladas.
FROM mcr.microsoft.com/dotnet/aspnet:10.0-noble AS final
ENV DEBIAN_FRONTEND=noninteractive
# Roda como usuário não-root (UID/GID 1000, convenção do primeiro usuário em
# Debian/Armbian/Raspberry Pi OS) em vez de root. No grupo "video" pra ter chance de acessar
# o device node da VPU (RK3399/RK3588) quando ele for passado via `devices:` no compose — ver
# RkmppCapabilityService. Se a pasta ./data do host no seu Radxa pertencer a outro UID, rode
# `chown -R 1000:1000 ./data` no host ou sobrescreva com `user:` no docker-compose.yml.
#
# repo.jellyfin.org (usado até a migração pra .NET 10) só publica suíte Debian (bookworm/
# trixie) -- sem "noble", instalar o .deb feito pra bookworm aqui seria Frankendebian (libs de
# runtime mais novas no Noble, risco real de ABI incompatível num pacote que já embute
# libav*). O GitHub Release do jellyfin-ffmpeg publica um .deb OFICIAL por suíte Ubuntu
# (bookworm/jammy/noble/trixie/resolute), então busca o mais novo direto de lá em vez do apt
# repo -- "$ARCH" e "noble" filtram o asset certo pra essa imagem.
#
# userdel -r ubuntu: Ubuntu Noble já vem com um usuário "ubuntu" padrão nesse mesmo UID/GID
# 1000 (Debian não tinha isso) -- sem remover antes, groupadd/useradd abaixo falham com
# "already exists".
RUN apt-get update \
 && apt-get install -y --no-install-recommends ca-certificates curl \
 && ARCH=$(dpkg --print-architecture) \
 && ASSET_URL=$(curl -fsSL https://api.github.com/repos/jellyfin/jellyfin-ffmpeg/releases/latest \
      | grep -oE "\"browser_download_url\": *\"[^\"]*-noble_${ARCH}\.deb\"" \
      | head -1 | cut -d'"' -f4) \
 && test -n "$ASSET_URL" \
 && curl -fsSL -o /tmp/jellyfin-ffmpeg.deb "$ASSET_URL" \
 && apt-get install -y --no-install-recommends /tmp/jellyfin-ffmpeg.deb \
 && rm -f /tmp/jellyfin-ffmpeg.deb \
 && rm -rf /var/lib/apt/lists/* \
 && userdel -r ubuntu \
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
