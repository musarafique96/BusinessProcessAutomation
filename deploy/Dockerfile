# BPA API image. Build from the repository root:  docker build -f deploy/Dockerfile .

FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY . .
RUN dotnet publish src/Bpa.Api/Bpa.Api.csproj -c Release -o /app/publish /p:UseAppHost=false

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
WORKDIR /app
COPY --from=build /app/publish .

# Session keys are written here. Mount a volume on this folder so keys survive restarts.
RUN mkdir /keys && chown app:app /keys

ENV ASPNETCORE_URLS=http://+:8080 \
    ASPNETCORE_ENVIRONMENT=Production \
    DataProtection__KeysPath=/keys

USER app
EXPOSE 8080
ENTRYPOINT ["dotnet", "Bpa.Api.dll"]
