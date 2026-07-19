# Spec §12: the `mimir` Compose service is built from this file.

FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

# Warm the NuGet cache against the project files alone, so editing source does not re-download
# packages.
COPY global.json Directory.Build.props Directory.Packages.props ./
COPY src/Mimir.Contracts/Mimir.Contracts.csproj src/Mimir.Contracts/
COPY src/Mimir.Server/Mimir.Server.csproj src/Mimir.Server/
RUN dotnet restore src/Mimir.Server/Mimir.Server.csproj

COPY src/Mimir.Contracts/ src/Mimir.Contracts/
COPY src/Mimir.Server/ src/Mimir.Server/

# Deliberately NOT --no-restore. A restore that ran before wwwroot/ and Components/ existed leaves
# a static web asset manifest with no _framework/* routes, so blazor.web.js 404s and the app
# silently degrades to static rendering with no SignalR circuit. Letting publish restore again
# (a cache hit, thanks to the layer above) rebuilds the manifest against the real source tree.
RUN dotnet publish src/Mimir.Server/Mimir.Server.csproj --configuration Release --output /app

# Fail the build rather than ship a UI that looks fine and never updates.
RUN grep -q '"Route":"_framework/blazor.web.js"' /app/Mimir.Server.staticwebassets.endpoints.json \
    || (echo "Static web asset manifest has no blazor.web.js: the UI would fall back to static rendering with no SignalR circuit." >&2 && exit 1)

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
WORKDIR /app
COPY --from=build /app .

# The host publishes 6464 (§12); inside the container we are a plain HTTP service on 8080.
ENV ASPNETCORE_URLS=http://+:8080
EXPOSE 8080

USER $APP_UID
ENTRYPOINT ["dotnet", "Mimir.Server.dll"]
