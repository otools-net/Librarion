# Librarion as a container: its data in /app/data (a volume), books mounted read-only where you like (/books).
# Published as ghcr.io/otools-net/librarion by .github/workflows/docker-image.yml; to build it yourself:
#
#   docker build -t librarion .
#   docker run -d --name librarion -p 5090:8080 -v librarion-data:/app/data -v /path/to/books:/books:ro librarion
#   docker logs librarion        # the setup code, the first time
#
# Settings by environment variables (Librarion__Security__RequireHttps=true…), docs/configuration.md; with HTTPS
# in front for an address on the internet: docker-compose.yml, docs/docker.md.

# ---- The web client (Angular), built once whatever the target platform ----
FROM --platform=$BUILDPLATFORM node:24-bookworm-slim AS web
ENV NG_CLI_ANALYTICS=false
WORKDIR /src/src/Librarion.Web
COPY src/Librarion.Web/package.json src/Librarion.Web/package-lock.json ./
RUN npm ci --no-audit --no-fund
COPY src/Librarion.Web/ ./
# Writes ../Librarion.Api/wwwroot (angular.json).
RUN npm run build

# ---- The server: framework-dependent, so the same files run on amd64 and arm64 (native libraries for both) ----
FROM --platform=$BUILDPLATFORM mcr.microsoft.com/dotnet/sdk:10.0 AS server
# The commit, shown next to the version (.git isn't in the build context): the publishing workflow passes it.
ARG LIBRARION_COMMIT=
ENV DOTNET_CLI_TELEMETRY_OPTOUT=1 DOTNET_NOLOGO=1
WORKDIR /src
COPY Directory.Build.props Directory.Packages.props global.json ./
COPY src/Librarion.Core/Librarion.Core.csproj src/Librarion.Core/
COPY src/Librarion.Api/Librarion.Api.csproj src/Librarion.Api/
RUN dotnet restore src/Librarion.Api/Librarion.Api.csproj
COPY src/Librarion.Core/ src/Librarion.Core/
COPY src/Librarion.Api/ src/Librarion.Api/
# The server embeds the client's texts (the e-reader pages).
COPY src/Librarion.Web/src/i18n/en.json src/Librarion.Web/src/i18n/
COPY --from=web /src/src/Librarion.Api/wwwroot/ src/Librarion.Api/wwwroot/
RUN dotnet publish src/Librarion.Api/Librarion.Api.csproj -c Release --no-restore -p:LibrarionSkipWebClient=true \
        -p:UseAppHost=false ${LIBRARION_COMMIT:+-p:SourceRevisionId=$LIBRARION_COMMIT} -o /app \
    && mkdir -p /empty/data

# ---- The image: the program and nothing of the build. No RUN here: an arm64 image needs no emulation ----
FROM mcr.microsoft.com/dotnet/aspnet:10.0
COPY --from=server /app/ /app/
# The data folder belongs to the app's user ($APP_UID of the base image, 1654), so a named volume starts writable.
# Made outside /app: a COPY --chown into a folder that exists already leaves that folder root's.
COPY --from=server --chown=1654:1654 /empty/ /app/

# Urls rather than ASPNETCORE_HTTP_PORTS: appsettings.json sets Urls (localhost only), which would win.
ENV ASPNETCORE_HTTP_PORTS= \
    Urls=http://+:8080 \
    DOTNET_CLI_TELEMETRY_OPTOUT=1

USER $APP_UID
WORKDIR /app
VOLUME ["/app/data"]
EXPOSE 8080
ENTRYPOINT ["dotnet", "Librarion.Api.dll"]
