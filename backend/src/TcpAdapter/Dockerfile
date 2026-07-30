# =====================================================================================
# tcp-adapter container (C043, D7' §2.1's Container 9, D7' §2.2's template).
#
# Hardware GPS tracker ingest: GT06 on 5023, JT/T 808 on 5024, H02 on 5025 and generic
# NMEA over UDP on 5026. 5023-5025 are L4 passthrough behind HAProxy; 5026 is published
# by this container directly, because HAProxy has no UDP forwarder (see the header of
# infra/deploy/haproxy.cfg).
#
#   docker build -f backend/src/TcpAdapter/Dockerfile -t mageride/tcp-adapter .
#
# Build context is the repository root.
#
# --- Two deliberate departures from D7' §2.2 -----------------------------------------
#
# (a) `aspnet:10.0-alpine`, not `runtime:10.0-alpine`. §2.2 says this service "uses
#     mcr.microsoft.com/dotnet/runtime:10.0-alpine (no ASP.NET)", and it cannot:
#     backend/CLAUDE.md requires every service to reference MageRide.Shared, and that
#     assembly carries `FrameworkReference Microsoft.AspNetCore.App` for the middleware,
#     health checks and minimal-API results the other twenty services use. A process
#     built against it will not start on the runtime-only image, whatever it does at
#     run time. The cost is about 10 MB of layer; the alternative is a second copy of the
#     kernel. Recorded as a micro-change-set in the C043 handoff.
#
# (b) No ASPNETCORE_URLS and no HTTP health check. `mqtt-topics.md` §7: "tcp-adapter …
#     has no HTTP surface". There is no Kestrel in this process, so §2.2's
#     `wget http://localhost:5000/health/ready` would fail against a perfectly healthy
#     adapter. D7' §5.1 gives this container a TCP-socket probe instead, and the compose
#     file runs `nc -z 127.0.0.1 5023` — busybox `nc` is in the alpine base.
# =====================================================================================

# ---- build ----
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

# Restore against just the project graph so a source-only change reuses the package layer.
COPY ["global.json", "./"]
COPY ["backend/Directory.Build.props", "backend/Directory.Packages.props", "backend/"]
COPY ["backend/src/TcpAdapter/TcpAdapter.csproj", "backend/src/TcpAdapter/"]
COPY ["backend/src/MageRide.Shared/MageRide.Shared.csproj", "backend/src/MageRide.Shared/"]
RUN dotnet restore "backend/src/TcpAdapter/TcpAdapter.csproj"

COPY ["backend/src/TcpAdapter/", "backend/src/TcpAdapter/"]
COPY ["backend/src/MageRide.Shared/", "backend/src/MageRide.Shared/"]
RUN dotnet publish "backend/src/TcpAdapter/TcpAdapter.csproj" \
      -c Release -o /app /p:UseAppHost=false

# ---- runtime ----
FROM mcr.microsoft.com/dotnet/aspnet:10.0-alpine AS runtime
# The .NET 10 alpine images already ship a non-root `app` user, so §2.2's unconditional
# `addgroup -S app` fails with "group 'app' in use". Create it only if the base has not.
RUN getent group app >/dev/null 2>&1 || addgroup -S app; \
    getent passwd app >/dev/null 2>&1 || adduser -S app -G app
WORKDIR /app
COPY --from=build /app .
USER app

# gcServer for the same reason the other .NET containers set it: this process holds up to
# ADD §7.7.6's 10 000 sockets and allocates a buffer per session.
ENV DOTNET_gcServer=1

EXPOSE 5023 5024 5025
EXPOSE 5026/udp

# D7' §5.1: "TCP socket on 5023", 10 s interval, threshold 5 — the compose file sets the
# same values. A listener that has bound is a live adapter; readiness is not a thing this
# container can answer, because a device that connects while EMQX is away is still served
# (see TcpAdapterApplication's remarks).
HEALTHCHECK --interval=10s --timeout=5s --start-period=20s --retries=5 \
  CMD nc -z 127.0.0.1 5023 || exit 1

ENTRYPOINT ["dotnet", "MageRide.TcpAdapter.dll"]
