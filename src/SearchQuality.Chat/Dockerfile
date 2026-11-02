# syntax=docker/dockerfile:1.7

FROM mcr.microsoft.com/dotnet/sdk:10.0.201 AS build
WORKDIR /source

COPY ["Directory.Build.props", "."]
COPY ["global.json", "."]
COPY ["src/SearchQuality/SearchQuality.csproj", "src/SearchQuality/"]
COPY ["src/SearchQuality.Chat/SearchQuality.Chat.csproj", "src/SearchQuality.Chat/"]
RUN dotnet restore "src/SearchQuality.Chat/SearchQuality.Chat.csproj"

COPY src/SearchQuality/ src/SearchQuality/
COPY src/SearchQuality.Chat/ src/SearchQuality.Chat/
COPY ["appsettings.json", "."]
COPY ["ChatStudio.slnx", "."]
RUN dotnet publish "src/SearchQuality.Chat/SearchQuality.Chat.csproj" \
    --configuration Release \
    --no-restore \
    --output /out \
    /p:UseAppHost=false

FROM mcr.microsoft.com/dotnet/aspnet:10.0.5 AS runtime
WORKDIR /app

ENV ASPNETCORE_HTTP_PORTS=5198 \
    DOTNET_EnableDiagnostics=0 \
    DOTNET_SYSTEM_GLOBALIZATION_INVARIANT=false

COPY --from=build /out/ ./
COPY --from=build /source/appsettings.json ./appsettings.json
COPY --from=build /source/ChatStudio.slnx ./ChatStudio.slnx
RUN mkdir -p /app/data/chat-history /app/data/artifacts \
    && chown -R $APP_UID:$APP_UID /app/data

USER $APP_UID
EXPOSE 5198
ENTRYPOINT ["dotnet", "SearchQuality.Chat.dll"]
