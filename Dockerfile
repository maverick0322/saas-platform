FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

COPY src/Saas.Platform.Api/Saas.Platform.Api.csproj src/Saas.Platform.Api/
RUN dotnet restore src/Saas.Platform.Api/Saas.Platform.Api.csproj

COPY docs/contracts/proto/platform_identity/v1/platform_identity.proto docs/contracts/proto/platform_identity/v1/platform_identity.proto
COPY src/Saas.Platform.Api/ src/Saas.Platform.Api/
RUN dotnet publish src/Saas.Platform.Api/Saas.Platform.Api.csproj \
    --configuration Release \
    --no-restore \
    --output /app/publish \
    /p:UseAppHost=false

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
WORKDIR /app

ENV ASPNETCORE_URLS=http://+:8080
EXPOSE 8080

COPY --from=build /app/publish .

USER $APP_UID
ENTRYPOINT ["dotnet", "Saas.Platform.Api.dll"]
