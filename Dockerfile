# Spec §12: the `mimir` Compose service is built from this file.

FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

# Warm the NuGet cache against the project files alone, so editing source does not re-download
# packages.
COPY global.json Directory.Build.props Directory.Packages.props nuget.config ./
COPY src/Mimir.Contracts/Mimir.Contracts.csproj src/Mimir.Contracts/packages.lock.json src/Mimir.Contracts/
COPY src/Mimir.Server/Mimir.Server.csproj src/Mimir.Server/packages.lock.json src/Mimir.Server/
RUN dotnet restore src/Mimir.Server/Mimir.Server.csproj

COPY src/Mimir.Contracts/ src/Mimir.Contracts/
COPY src/Mimir.Server/ src/Mimir.Server/

# Deliberately NOT --no-restore. A restore that ran before wwwroot/ and Components/ existed leaves
# a static web asset manifest with no _framework/* routes, so blazor.web.js 404s and the app
# silently degrades to static rendering with no SignalR circuit. Letting publish restore again
# (a cache hit, thanks to the layer above) rebuilds the manifest against the real source tree.
#
# Locked here rather than on the cache-warming restore above: the implicit ASP.NET Core web-asset
# package set is only fully resolvable once wwwroot/Components exist, so the earlier restore's
# graph is a strict subset of the lock file and would fail RestoreLockedMode's exact-match check.
ENV CI=true
RUN dotnet publish src/Mimir.Server/Mimir.Server.csproj --configuration Release --output /app

# Fail the build rather than ship a UI that looks fine and never updates. Whitespace-tolerant
# because the compact "Route":"..." spelling is an undocumented SDK serialization detail: an SDK
# that pretty-prints this file must not fail the build over a manifest that is in fact correct.
# Still matched on Route rather than the bare filename — the unfingerprinted route is the one the
# script tag requests, and it can be absent while _framework/blazor.web.<hash>.js entries remain.
RUN grep -qE '"Route"[[:space:]]*:[[:space:]]*"_framework/blazor\.web\.js"' /app/Mimir.Server.staticwebassets.endpoints.json \
    || (echo "Static web asset manifest has no blazor.web.js route: the UI would fall back to static rendering with no SignalR circuit." >&2 && exit 1)

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
WORKDIR /app
COPY --from=build /app .

# The host publishes 6464 (§12); inside the container we are a plain HTTP service on 8080.
ENV ASPNETCORE_URLS=http://+:8080
EXPOSE 8080

USER $APP_UID
ENTRYPOINT ["dotnet", "Mimir.Server.dll"]
