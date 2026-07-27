# =====================================================================================
# One-shot `migrate` container (C003, D7' §2.2 / §3).
#
# Runs to completion before app-services start and exits 0 when the database is up to
# date. Build context is the repository root — the .sql scripts live in db/migrations and
# are embedded into the assembly at publish time.
#
#   docker build -f backend/src/MageRide.Migrations/Dockerfile -t mageride/migrate .
#   docker run --rm -e ConnectionStrings__Postgres="..." mageride/migrate
# =====================================================================================

# ---- build ----
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

# Restore against just the project graph so a source-only change reuses the package layer.
COPY ["global.json", "./"]
COPY ["backend/Directory.Build.props", "backend/Directory.Packages.props", "backend/"]
COPY ["backend/src/MageRide.Migrations/MageRide.Migrations.csproj", "backend/src/MageRide.Migrations/"]
# The csproj globs db/migrations/*.sql as embedded resources, so restore needs them present.
COPY ["db/migrations/", "db/migrations/"]
RUN dotnet restore "backend/src/MageRide.Migrations/MageRide.Migrations.csproj"

COPY ["backend/src/MageRide.Migrations/", "backend/src/MageRide.Migrations/"]
RUN dotnet publish "backend/src/MageRide.Migrations/MageRide.Migrations.csproj" \
      -c Release -o /app /p:UseAppHost=false

# ---- runtime ----
# runtime, not aspnet: this is a batch job with no HTTP surface and no health endpoint.
FROM mcr.microsoft.com/dotnet/runtime:10.0-alpine AS runtime
# The .NET 10 alpine images already ship a non-root `app` user, so D7' §2.2's unconditional
# `addgroup -S app` fails with "group 'app' in use". Create it only if the base has not.
RUN getent group app >/dev/null 2>&1 || addgroup -S app; \
    getent passwd app >/dev/null 2>&1 || adduser -S app -G app
# Npgsql probes for GSSAPI when it opens a connection. Without krb5-libs the alpine image
# prints "Error: Error loading shared library libgssapi_krb5.so.2" on every run — harmless
# (the connection proceeds) but indistinguishable from a real migration failure in a
# compose log. Added by C009, which runs this image as the one-shot `migrate` service.
RUN apk add --no-cache krb5-libs
WORKDIR /app
COPY --from=build /app .
USER app

# Postgres may still be in initdb when compose starts this alongside it.
ENV MIGRATE_WAIT_SECONDS=60 \
    DOTNET_gcServer=0

ENTRYPOINT ["dotnet", "MageRide.Migrations.dll"]
