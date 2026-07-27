# =====================================================================================
# api-gateway container (C008, D7' §2.2).
#
# The single public HTTP entry point. HAProxy terminates TLS in front of it and forwards
# 443 here; MQTT (8883) never passes through this process — it is L4 SNI passthrough to
# EMQX at HAProxy (ADD §12.2).
#
#   docker build -f backend/src/ApiGateway/Dockerfile -t mageride/api-gateway .
#   docker run --rm -p 5000:5000 \
#     -e ReverseProxy__Clusters__iam-svc__Destinations__primary__Address=http://iam-svc:5000/ \
#     mageride/api-gateway
#
# Build context is the repository root.
# =====================================================================================

# ---- build ----
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

# Restore against just the project graph so a source-only change reuses the package layer.
COPY ["global.json", "./"]
COPY ["backend/Directory.Build.props", "backend/Directory.Packages.props", "backend/"]
COPY ["backend/src/ApiGateway/ApiGateway.csproj", "backend/src/ApiGateway/"]
COPY ["backend/src/MageRide.Shared/MageRide.Shared.csproj", "backend/src/MageRide.Shared/"]
RUN dotnet restore "backend/src/ApiGateway/ApiGateway.csproj"

COPY ["backend/src/ApiGateway/", "backend/src/ApiGateway/"]
COPY ["backend/src/MageRide.Shared/", "backend/src/MageRide.Shared/"]
RUN dotnet publish "backend/src/ApiGateway/ApiGateway.csproj" \
      -c Release -o /app /p:UseAppHost=false

# ---- runtime ----
FROM mcr.microsoft.com/dotnet/aspnet:10.0-alpine AS runtime
# The .NET 10 alpine images already ship a non-root `app` user, so D7' §2.2's unconditional
# `addgroup -S app` fails with "group 'app' in use". Create it only if the base has not.
RUN getent group app >/dev/null 2>&1 || addgroup -S app; \
    getent passwd app >/dev/null 2>&1 || adduser -S app -G app
WORKDIR /app
COPY --from=build /app .
USER app

ENV ASPNETCORE_URLS=http://+:5000 \
    DOTNET_gcServer=1
EXPOSE 5000

HEALTHCHECK --interval=10s --timeout=3s --start-period=20s --retries=3 \
  CMD wget -qO- http://localhost:5000/health/ready || exit 1

ENTRYPOINT ["dotnet", "MageRide.ApiGateway.dll"]
