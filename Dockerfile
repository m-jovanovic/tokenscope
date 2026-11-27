FROM node:26-bookworm-slim AS client
WORKDIR /sample
COPY web/package*.json web/
RUN cd web && npm ci
COPY web/ web/
RUN cd web && npm run build && cd node_modules && \
    mkdir -p /client-notices && \
    cp --parents tailwindcss/LICENSE es-toolkit/NOTICE victory-vendor/lib-vendor/*/LICENSE /client-notices/

FROM mcr.microsoft.com/dotnet/sdk:11.0 AS build
WORKDIR /src
COPY src/TokenScope.Api/TokenScope.Api.csproj TokenScope.Api/
RUN dotnet restore TokenScope.Api/TokenScope.Api.csproj
COPY src/TokenScope.Api/ TokenScope.Api/
RUN dotnet publish TokenScope.Api/TokenScope.Api.csproj -c Release -o /out --no-restore -p:UseAppHost=false

FROM mcr.microsoft.com/dotnet/aspnet:10.0
WORKDIR /app
COPY --from=build /out/ ./
COPY --from=client /sample/web/dist/ wwwroot/
COPY LICENSE ./LICENSE
COPY licenses/ ./licenses/
COPY --from=client /client-notices/ ./licenses/
ENV ASPNETCORE_URLS=http://+:8080
ENV ASPNETCORE_ENVIRONMENT=Production
ENV DOTNET_EnableDiagnostics=0
EXPOSE 8080
USER $APP_UID
ENTRYPOINT ["dotnet", "TokenScope.Api.dll", "--urls", "http://+:8080"]
