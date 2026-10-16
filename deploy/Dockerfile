# Build template. Requires network and a real dependency/build verification before release.
FROM node:24.15.0-bookworm-slim AS web
WORKDIR /build/web
COPY web/package*.json ./
# All dependency versions are committed and must match the npm manifest.
RUN npm ci
COPY web/ ./
RUN npm run build
FROM mcr.microsoft.com/dotnet/sdk:10.0.402 AS backend
WORKDIR /build
COPY Directory.*.props global.json Permora.slnx ./
COPY src/ src/
ARG PROJECT=Permora.Api
RUN dotnet restore src/${PROJECT}/${PROJECT}.csproj --locked-mode
RUN dotnet publish src/${PROJECT}/${PROJECT}.csproj --no-restore -c Release -o /out
FROM mcr.microsoft.com/dotnet/aspnet:10.0.13
WORKDIR /app
ARG PROJECT=Permora.Api
ENV PERMORA_ENTRYPOINT=${PROJECT}.dll
ENV ASPNETCORE_HTTP_PORTS=8080
COPY --from=backend /out/ ./
COPY --from=web /build/web/dist/permora/browser/ ./wwwroot/
USER $APP_UID
ENTRYPOINT ["/bin/sh", "-c", "exec dotnet \"$PERMORA_ENTRYPOINT\""]
