FROM mcr.microsoft.com/dotnet/aspnet:8.0-alpine AS base
WORKDIR /app
EXPOSE 8080

FROM mcr.microsoft.com/dotnet/sdk:8.0-alpine AS build
WORKDIR /src
COPY src/FilmesApi/FilmesApi.csproj src/FilmesApi/
RUN dotnet restore src/FilmesApi/FilmesApi.csproj
COPY src/ src/
RUN dotnet publish src/FilmesApi/FilmesApi.csproj -c Release -o /app/publish --no-restore

FROM base AS final
WORKDIR /app
COPY --from=build /app/publish .
ENV ASPNETCORE_URLS=http://+:8080
ENTRYPOINT ["dotnet", "FilmesApi.dll"]
