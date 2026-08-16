# D7′ — MageRide DevOps & Platform Configuration

> **🔄 Aligned to ADD v3.0 / URD v2.6 (AL-01…AL-46; realigned 2026-07-05 — Pass 1).** Earlier pass (v2.6): `wallet-portal` → **`admin-portal`** (`admin.mageride.lk`, AL-02) + new **`fleet-portal`** (`fleet.mageride.lk`, AL-03); `fleet-svc` **Phase 1**; apps Phone OTP only (AL-07); SQL-script migrations (DbUp/Grate, not EF Core). **This pass (v2.7→v3.0 deltas):** `app-services` now hosts **21 domain services** incl. **`transit-svc`** (GTFS routing + paste-link, AL-18/20) and **`public-bff`** (Passenger Web subview `/public/track/*`, AL-44); **admin MFA removed (AL-37** — supersedes the earlier "Password/Google+MFA" wording**)**; a **`gtfs-import`** job joins the scheduled/infra jobs; production substrate pinned to **DigitalOcean DOKS, Singapore** (hosting decision 2026-07-05); external SaaS drops the bank-transfer IPG (AL-05).

> **Phase B deliverable (Prompt B7).** Transformed from the Namma Yatri Phase-A DevOps extraction
> (`nammayatri-extraction/D7_devops_config.md`) onto MageRide's **.NET 10 LTS Minimal API + Dapper + KMP + Docker/K3s**
> deployment, per ADD v2.4 §10 (physical), §16 (capacity), §17 (MVP vs scale), §18 (stack), §19
> (roadmap), §1.3–§1.7 deficit log; **canonical container layout = `lightweight-production-replica.md`**
> (10 core + optional containers); service list cross-checked vs D3′.
>
> **Stack delta:** NY = **Nix flakes + process-compose** (no docker-compose, no in-repo k8s), single fat
> `dockerTools` image (all exes, `Cmd`-selected), Dhall config (not env), Passetto at-rest encryption,
> Juspay/HyperSDK Android variants, Kafka+ZK, external LTS. **MageRide = Docker Compose / K3s
> (same manifests), per-service .NET 10 alpine images, env-var config, pgcrypto+Vault secrets,
> OnePay, Redpanda, EMQX.** Every item tagged; `[DELTA:NIX]`/`[DELTA:JUSPAY]` resolved; Phase-A
> `[UNVERIFIED]` (image size, k8s manifests, rollout, alert rules, rotation) all produced here.

---

## 1. Build System   [REPLACE] (NY Nix flakes/cabal/PureScript/Fastlane → .NET/Gradle/Xcode/Next.js)

| Component | Tool | Command | Tag |
|---|---|---|---|
| Backend services (.NET 10 LTS) | `dotnet` SDK 10 | `dotnet restore && dotnet publish -c Release -r linux-musl-x64 /p:PublishAot=false -o /app` | [REPLACE] |
| KMP shared module | Gradle (KMP plugin) | `./gradlew :shared:build :shared:assembleXCFramework` | [NEW] |
| Driver/Passenger Android | Gradle + AGP | `./gradlew :driver-android:assembleRelease :passenger-android:assembleRelease` | [REPLACE] |
| Driver/Passenger iOS | Xcode + SPM (KMP framework) | `xcodebuild -scheme DriverApp archive → -exportArchive → .ipa` | [NEW] |
| Admin Portal (`admin.mageride.lk`) + Fleet Portal (`fleet.mageride.lk`) | Next.js (Node 24 LTS — Δ 2026-07-23) + **Tailwind CSS** (PostCSS, compiled at build — AL-52) | `npm ci && npm run build` | [REPLACE] (AL-02/03; `wallet-portal` removed) |
| OSM tiles | osm2pgsql + tippecanoe + go-pmtiles | `osm-pipeline` batch (see §10) | [NEW] |

**Resolved `[DELTA:NIX]`:** flake/cabal/devour-flake/process-compose/`dockerTools.buildImage`/
`-Werror` ARM `-O1` → standard `dotnet`/`gradle`/Dockerfile multistage + GitHub Actions matrix.
**Resolved `[DELTA:JUSPAY]`:** per-merchant HyperSDK Android keystore bundles → single signed keystore;
Passetto encryption service → `pgcrypto` + Vault transit (§13).

---

## 2. Container Specifications   [REPLACE] (NY single fat scratch image → per-service alpine)

Base image `mcr.microsoft.com/dotnet/aspnet:10.0-alpine` (runtime), `mcr.microsoft.com/dotnet/sdk:10.0`
(build). All services **2 GB / 1 vCPU** in production pods (ADD §10.1); replica co-locates per the
canonical layout below.

### 2.1 Canonical container layout (cite `lightweight-production-replica.md` Resource Summary)

| Container | Image | RAM/vCPU | Ports | Services inside |
|---|---|---|---|---|
| `haproxy` | `haproxy:2.9-alpine` | 256 MB/0.25 | 443, 8883, 8084, 5023–5026 | edge LB / TLS / L4 passthrough |
| `emqx` | `emqx/emqx:5.8` | 2 GB/1.0 | 1883, 8883, 8084, 18083 | MQTT broker + rule-engine bridge |
| `redpanda` | `redpandadata/redpanda:v24.2` | 1 GB/0.5 | 9092, 9644, 8081/8082 | event backbone (RF=1 dev) |
| `redis` | `redis:7-alpine` | 1.5 GB/0.5 | 6379 | live geo + dispatch/ride locks + caches |
| `postgres` | **`timescale/timescaledb-ha:pg16`** (PostGIS + TimescaleDB) | 4 GB/1.0 | 5432 | system of record + hypertable |
| `pgbouncer` | `edoburu/pgbouncer` | 128 MB/0.25 | 6432 | transaction-mode pooler |
| `hot-path` | custom .NET 10 | 2 GB/1.0 | — | mqtt-bridge, position-processor, persistence-writer, fleet-health |
| `app-services` | custom .NET 10 | 3 GB/1.5 | 5000 | 21 domain svcs behind YARP (incl. **transit-svc** AL-18 + **public-bff** AL-44; + embedded **step-ca** in provisioning-svc, vol `provisioning-ca-data`) |
| `fanout` | custom ASP.NET SignalR | 2 GB/1.0 | 5001 (WSS) | fanout-svc |
| `tcp-adapter` | custom .NET 10 worker | 512 MB/0.5 | 5023–5026 | adapter-gt06/jt808/h02/nmea-udp (T-01) |
| **core 10 total** | | **~16.4 GB / ~6.5 vCPU** | | fits 24 GB VPS |
| `voip` *(opt)* | `livekit/livekit-server` + `coturn` | 1 GB/0.5 | 7880, 7881, 3478/UDP, 50000-50100/UDP | VoIP SFU + TURN (host UDP, NOT via HAProxy) |
| `admin-portal` *(opt)* | Next.js Node24-alpine | 512 MB/0.25 | 3001 | Admin Portal `admin.mageride.lk` — back-office for all six internal roles (AL-02; or Vercel/CF Pages) |
| `fleet-portal` *(opt)* | Next.js Node24-alpine | 512 MB/0.25 | 3002 | Fleet Portal `fleet.mageride.lk` (AL-03; or Vercel/CF Pages) |
| `nominatim` *(opt)* | `mediagis/nominatim:4.4` | **8 GB/1.0** | 8080 | geocoder — **host on separate VPS** (D-14) |
| `monitoring` *(opt)* | `prom/prometheus` + `grafana` | 1 GB/0.5 | 9090, 3000 | metrics |
| `osm-pipeline` *(batch)* | custom | 2 GB/1.0 (transient) | — | weekly tile/geocoder refresh (D-15) |

> A **24 GB / 6 vCPU Contabo VPS-30** runs core 10 + voip + admin-portal + fleet-portal + monitoring (~18.9 GB) with
> ~5 GB OS headroom; Nominatim on a separate cheap 8 GB VPS; osm-pipeline runs on schedule and exits.

### 2.2 Dockerfile template (.NET 10 LTS service)   [NEW]
```dockerfile
# ---- build ----
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY ["src/${SERVICE}/${SERVICE}.csproj", "src/${SERVICE}/"]
COPY ["src/Shared/Shared.csproj", "src/Shared/"]
RUN dotnet restore "src/${SERVICE}/${SERVICE}.csproj"
COPY . .
RUN dotnet publish "src/${SERVICE}/${SERVICE}.csproj" -c Release -o /app /p:UseAppHost=false
# ---- runtime ----
FROM mcr.microsoft.com/dotnet/aspnet:10.0-alpine AS runtime
RUN addgroup -S app && adduser -S app -G app
WORKDIR /app
COPY --from=build /app .
USER app
ENV ASPNETCORE_URLS=http://+:5000 DOTNET_gcServer=1
EXPOSE 5000
HEALTHCHECK --interval=10s --timeout=3s --start-period=20s --retries=3 \
  CMD wget -qO- http://localhost:5000/health/ready || exit 1
ENTRYPOINT ["dotnet", "${SERVICE}.dll"]
```
`provisioning-svc` adds a `step-ca` sidecar (named volume `provisioning-ca-data`, T-02); `tcp-adapter`
uses `mcr.microsoft.com/dotnet/runtime:10.0-alpine` (no ASP.NET) and exposes 5023–5026.

---

## 3. Docker Compose — Full Local Dev   [NEW] (NY had none — resolves `[UNVERIFIED]`)

```yaml
name: mageride
networks: { mr: {} }
volumes: { pgdata: {}, redisdata: {}, redpandadata: {}, emqxdata: {}, provisioning-ca-data: {} }
services:
  postgres:                                   # TimescaleDB-HA pg16 (PostGIS + TimescaleDB) — T-06
    image: timescale/timescaledb-ha:pg16
    environment: { POSTGRES_PASSWORD: ${PG_PASSWORD}, POSTGRES_DB: mageride }
    command: ["-c","shared_preload_libraries=timescaledb,postgis-3"]
    volumes: [pgdata:/var/lib/postgresql/data]
    healthcheck: { test: ["CMD","pg_isready","-U","postgres"], interval: 5s, retries: 10 }
    networks: [mr]
  pgbouncer:
    image: edoburu/pgbouncer
    environment: { DATABASE_URL: "postgres://postgres:${PG_PASSWORD}@postgres:5432/mageride", POOL_MODE: transaction }
    depends_on: { postgres: { condition: service_healthy } }
    networks: [mr]
  redis:
    image: redis:7-alpine
    command: ["redis-server","--appendonly","yes","--appendfsync","everysec"]
    volumes: [redisdata:/data]
    healthcheck: { test: ["CMD","redis-cli","ping"], interval: 5s, retries: 10 }
    networks: [mr]
  redpanda:
    image: redpandadata/redpanda:v24.2
    command: ["redpanda","start","--smp","1","--overprovisioned","--node-id","0","--set","redpanda.auto_create_topics_enabled=true"]
    volumes: [redpandadata:/var/lib/redpanda/data]
    healthcheck: { test: ["CMD","rpk","cluster","health"], interval: 10s, retries: 10 }
    networks: [mr]
  emqx:
    image: emqx/emqx:5.8
    environment: { EMQX_AUTHENTICATION__1__TYPE: jwt }
    volumes: [emqxdata:/opt/emqx/data]
    ports: ["8883:8883","8084:8084","18083:18083"]
    healthcheck: { test: ["CMD","emqx","ctl","status"], interval: 10s, retries: 10 }
    networks: [mr]
  hot-path:                                   # mqtt-bridge + position-processor + persistence-writer + fleet-health
    build: { context: ., args: { SERVICE: HotPath } }
    env_file: [.env.common]
    depends_on: { redpanda: {condition: service_healthy}, redis: {condition: service_healthy}, postgres: {condition: service_healthy}, emqx: {condition: service_healthy} }
    networks: [mr]
  app-services:                               # 21 domain services behind YARP (incl. transit-svc + public-bff) + embedded step-ca
    build: { context: ., args: { SERVICE: AppServices } }
    env_file: [.env.common, .env.app]
    volumes: [provisioning-ca-data:/var/step]
    depends_on: { pgbouncer: {condition: service_started}, redis: {condition: service_healthy}, redpanda: {condition: service_healthy} }
    networks: [mr]
  fanout:
    build: { context: ., args: { SERVICE: Fanout } }
    env_file: [.env.common]
    depends_on: { redis: {condition: service_healthy} }
    networks: [mr]
  tcp-adapter:
    build: { context: ., args: { SERVICE: TcpAdapter } }
    env_file: [.env.common]
    ports: ["5023:5023","5024:5024","5025:5025","5026:5026/udp"]
    depends_on: { emqx: {condition: service_healthy}, redis: {condition: service_healthy} }
    networks: [mr]
  haproxy:
    image: haproxy:2.9-alpine
    volumes: ["./deploy/haproxy.cfg:/usr/local/etc/haproxy/haproxy.cfg:ro"]
    ports: ["443:443","8883:8883","8084:8084","5023:5023","5024:5024","5025:5025","5026:5026/udp"]
    depends_on: [app-services, fanout, emqx]
    networks: [mr]
  # optional: voip (network_mode: host for UDP 50000-50100), admin-portal (3001), fleet-portal (3002), monitoring (9090/3000)
```
**Startup order** (resolves NY sequential-exe race `[ADAPT]`): infra (postgres→pgbouncer, redis,
redpanda, emqx) healthy → `hot-path`, `app-services`, `fanout`, `tcp-adapter` (depends_on healthchecks)
→ `haproxy`. **SQL-script migrations** (DbUp/Grate — versioned `.sql` files, **not** EF Core) run as a
one-shot `migrate` service before `app-services`. **Network:** single bridge `mr`; HAProxy is the only public
edge. **K3s single-node uses the same images/manifests** (§5).

---

## 4. Environment Variables   [NEW] (NY used Dhall, not env — `[DELTA:NIX]` resolved)

### 4.1 Common (`.env.common`, all .NET services)
| Variable | Description | Req | Default | Secret |
|---|---|---|---|---|
| `ASPNETCORE_ENVIRONMENT` | env name | yes | Production | no |
| `ConnectionStrings__Postgres` | pgbouncer DSN | yes | — | **yes** |
| `ConnectionStrings__Redis` | redis://redis:6379 | yes | — | no |
| `Kafka__BootstrapServers` | redpanda:9092 | yes | — | no |
| `Jwt__JwksUrl` | iam-svc JWKS endpoint | yes | — | no |
| `Otel__Endpoint` | OTLP collector | no | — | no |
| `Region__Timezone` | `Asia/Colombo` (D-38) | yes | Asia/Colombo | no |

### 4.2 Per-service additions (`.env.app`)
| Service | Variable | Description | Req | Default | Secret |
|---|---|---|---|---|---|
| iam-svc | `Sms__NotifyLkApiKey` · `Jwt__SigningKeyPem` (RS256) · `Otp__ResendCooldownSec`=60 · `Otp__MaxPerHour`=5 (D-32) | OTP/JWT (D-29) | yes | — | **yes** |
| iam-svc | `Google__ClientId` · `Apple__ClientId` | Admin Portal (Password/Google — **no MFA, AL-37**; `Mfa__IssuerName` removed) + Fleet Portal (Email/Google/Apple); apps = Phone OTP only (AL-07) | no | — | yes |
| registry-svc | `Ocr__Endpoint` | reg + payout profile | yes | — | yes |
| payout-svc | `Payout__Enabled` · `Payout__Cron` (weekly) · `Payout__RetainMinor`=0 · `Payout__BankBaseUrl` · `Payout__BankApiKey` (**AL-58**) | payout | yes | — | **yes** |
| provisioning-svc | `StepCa__Url` · `StepCa__RootKeyPath`=/var/step · `Cred__RotationDays`=90 (T-02) | tracker certs | yes | — | **yes** |
| query-svc | `Tiles__BaseUrl` · `Nominatim__Url` (D-14) | map data | yes | — | no |
| trip-state-svc | `Session__IdleTimeoutMin`=30 · `Geofence__AutoEndM`=100 | A/B sessions | yes | — | no |
| ride-svc | `Otp__PepperKey` (HMAC, P-07) · `Quartz__SchedulerName` · `Outbox__Channel`=ride_outbox (E-09) | ride aggregate | yes | — | **yes** |
| dispatch-svc | `Dispatch__OfferTtlSec`=15 · `Dispatch__GlobalTimeoutSec`=120 · `JobBoard__RadiusKm`=30 (D-06) · `Wallet__CacheTtlSec`=5 (D-08) | dispatch | yes | — | no |
| fare-svc | `Onepay__ApiKey` · `Onepay__WebhookSecret` · `LankaQr__MerchantId` | payment (D-10) | yes | — | **yes** |
| subscription-svc | `Fee__FirstTripFree`=true · `Tz`=Asia/Colombo (D-13) | daily fee | yes | — | no |
| wallet-svc | `Onepay__ApiKey` · `ComBankIpg__WebhookSecret` (D-12) · `LowBalance__ThresholdMinor`=20000 | wallet | yes | — | **yes** |
| notification-svc | `Fcm__ServiceAccountJson` · `Apns__P8Key` · `Apns__KeyId` · `Apns__TeamId` (E-01) · `Sms__SecondaryGateway` (D-33) | push/SMS | yes | — | **yes** |
| safety-svc | `Sos__SloMs`=5000 (D-33) · `TripShare__TtlGraceMin`=60 (D-34) | SOS | yes | — | no |
| reputation-svc | `Grpc__ListenPort`=5005 (block_status/level) | gRPC | yes | — | no |
| content-svc | `Cache__Ttl`=300 | Si/Ta/En templates (D-26); **public `GET /config/cities`** from `config.operating_cities`, cacheable (Change 6/22) | yes | — | no |
| support-svc | `Storage__ScreenshotBucket` | tickets | yes | — | no |
| fleet-svc | `Fleet__RlsEnabled`=true · `Fleet__VerificationGate`=true (US-13.A7) | **Phase 1** (AL-03) — Fleet Portal | yes | — | no |
| admin-bff | `Audit__Topic`=audit.events (D-35) · `Pdpa__DueDays`=30 (E-06) · `Rbac__DenyByDefault`=true · `Login__MaxFailedAttempts`=5 · `Login__LockoutMinutes`=15 · `Login__IpAllowList` (optional, CSV) | Admin Portal, nine-role RBAC (AL-02/06). **`Mfa__RequiredForInternal` removed — no second factor for internal roles (AL-37)**; the lock-out + optional IP allow-list are the compensating controls | yes | — | no |
| ocr-svc | `Gemini__ApiKey` · `Gemini__Model`=`gemini-flash-3.0` (Change 6/22) · `Redaction__Enabled`=true (D-36) | OCR + Mode-C auto-verify | yes | — | **yes** |
| voip-svc | `LiveKit__ApiKey` · `LiveKit__Secret` · `Turn__Realm` (D-24) | VoIP | no | — | **yes** |
| position-processor-svc | `Plausibility__MaxAccuracyM`=200 (D-18) · `Replay__MaxPerSec`=20 (T-05) | hot-path | yes | — | no |
| persistence-writer-svc | `Timescale__BatchRows`=1000 · `Timescale__FlushMs`=500 (T-06) | hot-path | yes | — | no |
| mqtt-bridge-svc | `Emqx__SharedSub`=`$share/posGroup/veh/+/pos/live` (E-08) | bridge | yes | — | no |
| fleet-health-svc | `Health__OfflinePct`=10 · `Health__WindowMin`=5 | rollups | yes | — | no |
| fanout-svc | `SignalR__BackplaneRedis` · `Geocell__Res`=7 (R-06) | SignalR | yes | — | no |
| tcp-adapter | `Adapter__Ports`=5023,5024,5025,5026 · `Provisioning__ImeiCacheKey`=imei: (T-03) | tracker ingest | yes | — | no |

---

## 5. K3s Manifest Templates   [NEW] (NY had no in-repo k8s — resolves `[UNVERIFIED]`)

**Deployment** (per stateless service; example `ride-svc`):
```yaml
apiVersion: apps/v1
kind: Deployment
metadata: { name: ride-svc, namespace: mageride }
spec:
  replicas: 2
  selector: { matchLabels: { app: ride-svc } }
  template:
    metadata: { labels: { app: ride-svc } }
    spec:
      containers:
      - name: ride-svc
        image: ghcr.io/mageride/ride-svc:${SHA}
        ports: [{ containerPort: 5000 }]
        envFrom: [{ configMapRef: { name: common-config } }, { secretRef: { name: ride-svc-secret } }]
        resources: { requests: { cpu: "500m", memory: "1Gi" }, limits: { cpu: "1", memory: "2Gi" } }
        readinessProbe: { httpGet: { path: /health/ready, port: 5000 }, initialDelaySeconds: 15, periodSeconds: 10, failureThreshold: 3 }
        livenessProbe:  { httpGet: { path: /health/live,  port: 5000 }, initialDelaySeconds: 30, periodSeconds: 15, failureThreshold: 6 }
---
apiVersion: v1
kind: Service
metadata: { name: ride-svc, namespace: mageride }
spec: { selector: { app: ride-svc }, ports: [{ port: 80, targetPort: 5000 }] }
```
**HPA:**
```yaml
apiVersion: autoscaling/v2
kind: HorizontalPodAutoscaler
metadata: { name: ride-svc-hpa }
spec:
  scaleTargetRef: { apiVersion: apps/v1, kind: Deployment, name: ride-svc }
  minReplicas: 2; maxReplicas: 10
  metrics: [{ type: Resource, resource: { name: cpu, target: { type: Utilization, averageUtilization: 70 } } }]
```
**StatefulSet** (EMQX / Redpanda / Postgres / tcp-adapter — stable identity + PVC):
```yaml
apiVersion: apps/v1
kind: StatefulSet
metadata: { name: emqx }
spec:
  serviceName: emqx; replicas: 1            # dev; 2-node cluster prod
  template: { spec: { containers: [{ name: emqx, image: emqx/emqx:5.8, ports: [{containerPort: 8883},{containerPort: 8084}] }] } }
  volumeClaimTemplates: [{ metadata: { name: emqxdata }, spec: { accessModes: [ReadWriteOnce], resources: { requests: { storage: 5Gi } } } }]
```
**Ingress** (Traefik/NGINX; WSS for SignalR, gateway for REST):
```yaml
apiVersion: networking.k8s.io/v1
kind: Ingress
metadata: { name: mr-ingress, annotations: { nginx.ingress.kubernetes.io/proxy-read-timeout: "3600" } }
spec:
  tls: [{ hosts: [api.mageride.lk], secretName: mr-tls }]
  rules:
  - host: api.mageride.lk
    http: { paths:
      - { path: /hubs, pathType: Prefix, backend: { service: { name: fanout-svc, port: { number: 80 } } } }
      - { path: /,     pathType: Prefix, backend: { service: { name: api-gateway, port: { number: 80 } } } } }
```
**ConfigMap/Secret:** `common-config` (ConfigMap, §4.1 non-secret), `<svc>-secret` (Secret, §4.2
secrets) — sourced from Vault via External Secrets Operator (§13). **PVC:** `provisioning-ca-data`
(step-ca, T-02), `pgdata`, `redpandadata`, `emqxdata`.

### 5.1 Health-check table
| Service | Liveness | Readiness | Interval | Init delay | Failure threshold |
|---|---|---|---|---|---|
| stateless .NET (ride/dispatch/fare/…) | `/health/live` | `/health/ready` (DB+Redis+Kafka ping) | 10–15 s | 15/30 s | 3/6 |
| fanout-svc | `/health/live` | `/health/ready` | 10 s | 15 s | 6 |
| tcp-adapter | TCP socket on 5023 | Redis `imei:` cache reachable | 10 s | 20 s | 5 |
| emqx | `emqx ctl status` | listener up | 10 s | 30 s | 6 |
| redpanda | `rpk cluster health` | broker ready | 10 s | 30 s | 6 |
| postgres | `pg_isready` | accepting connections | 5 s | 20 s | 10 |

---

## 6. KMP / Mobile Build Pipeline   [NEW]
- **Shared:** `./gradlew :shared:build` → JVM (Android) + `assembleXCFramework` (iOS, via CocoaPods/SPM).
- **Android:** `./gradlew :driver-android:assembleRelease :passenger-android:assembleRelease` → signed
  `.apk`/`.aab` (single keystore, replaces NY per-merchant Juspay variants `[DELTA:JUSPAY]`).
- **iOS:** KMP `XCFramework` → `xcodebuild archive` → `exportArchive` → `.ipa` (App Store / TestFlight).
- **Portal:** `npm ci && npm run build` (Next.js + Tailwind CSS — CSS compiled at build via PostCSS, no runtime styling dependency, AL-52) → Vercel/Cloudflare Pages or container.

---

## 7. GitHub Actions CI/CD (matrix)   [REPLACE] (NY Nix devour-flake/crane/Fastlane)
```yaml
name: ci
on: { push: { branches: [main] }, pull_request: {} }
jobs:
  build:
    strategy:
      matrix:
        target: [backend, android, ios, portal]
    runs-on: ${{ matrix.target == 'ios' && 'macos-14' || 'ubuntu-latest' }}
    steps:
      - uses: actions/checkout@v4
      - name: backend
        if: matrix.target == 'backend'
        run: |
          dotnet test --configuration Release
          for s in iam registry ride dispatch fare wallet ...; do
            docker build --build-arg SERVICE=$s -t ghcr.io/mageride/$s:${GITHUB_SHA::7} .
            docker push ghcr.io/mageride/$s:${GITHUB_SHA::7}
          done
      - name: android
        if: matrix.target == 'android'
        run: ./gradlew :shared:build :driver-android:assembleRelease :passenger-android:assembleRelease
      - name: ios
        if: matrix.target == 'ios'
        run: ./gradlew :shared:assembleXCFramework && xcodebuild -scheme DriverApp archive ...
      - name: portal
        if: matrix.target == 'portal'
        run: npm ci && npm run build
  deploy:
    needs: build
    if: github.ref == 'refs/heads/main'
    runs-on: ubuntu-latest
    steps:
      - run: |                                    # ArgoCD/kubectl rolling update
          kubectl -n mageride set image deploy/ride-svc ride-svc=ghcr.io/mageride/ride-svc:${GITHUB_SHA::7}
          # ... per service; rollout strategy = RollingUpdate (maxSurge 1, maxUnavailable 0)
```
**Testing:** `dotnet test` (xUnit unit/integration), Testcontainers (PG/Redis/Redpanda), **SQL-migration
apply/idempotency check** (DbUp/Grate against a throwaway PG container — replaces NY `db-check.yaml`),
Playwright (portal), instrumented Android/iOS tests.
**Rollout (resolves NY `[UNVERIFIED]`):** `RollingUpdate` (maxUnavailable 0, maxSurge 1) via ArgoCD;
dev→staging→prod promotion by image SHA tag + ArgoCD app-of-apps; DB migrations gated pre-deploy.

---

## 8. Deployment Targets   [REPLACE] (ADD §19 roadmap)
| Stage | Substrate | Notes |
|---|---|---|
| **Dev / replica** | 24 GB/6 vCPU **Contabo VPS-30**, Docker Compose **or K3s single-node** | core 10 + opt; SPOF accepted |
| **Prod P1–P2** | **DigitalOcean DOKS** 3 nodes (4 vCPU/8 GB, **Singapore — decided 2026-07-05**) | EMQX 2-node, Redpanda 3-node RF=3, Redis Sentinel, Postgres Patroni 1P+2R + PgBouncer, HAProxy+Keepalived; LiveKit+coturn pinned SGP (Colombo TURN evaluated in pilot) |
| **Prod P3** | **AWS EKS** (Mumbai/Singapore) | 1M+ WSS, Aurora, MSK, warm DR second region |
| Map tiles | **Cloudflare R2 + CDN** (free) → **Cloudflare Pro** at >50 GB/mo → **Bunny.net** fallback (D-16) | PMTiles, signed offline bundles |
| Backups | **Wasabi** nightly `pg_dump` (+ WAL archiving prod) | RPO 5 min / RTO 30 min (NFR-12/13) |

---

## 9. Infrastructure Dependencies   [REPLACE] (NY Kafka/ZK/Passetto/OSRM-India)
| Component | Version / image | Purpose | Tag |
|---|---|---|---|
| PostgreSQL + PostGIS + TimescaleDB | `timescale/timescaledb-ha:pg16` | OLTP + spatial + hypertable (T-06) | [REPLACE] |
| Redis | `redis:7-alpine` (Cluster/Sentinel prod) | live geo + caches + locks | [ADAPT] |
| EMQX | `emqx/emqx:5.8` | MQTT ingest (replaces Kafka-as-ingest) | [NEW] |
| Redpanda | `redpandadata/redpanda:v24.2` | event backbone (replaces Kafka+ZK) | [ADAPT] |
| PgBouncer | `edoburu/pgbouncer` | tx-mode pooler | [NEW] |
| step-ca | `smallstep/step-ca` (embedded in provisioning) | device PKI (T-02) | [NEW] |
| LiveKit + coturn | `livekit/livekit-server`, `coturn/coturn` | VoIP (replaces Exotel) | [NEW] |
| Nominatim | `mediagis/nominatim:4.4` | geocoding (replaces Google) | [NEW] |
| HAProxy + Keepalived | `haproxy:2.9-alpine` | edge LB / VRRP | [KEEP] |
| OTLP/LGTM | Prometheus, Loki, Grafana, Tempo | observability | [ADAPT] |

**External SaaS:** OnePay + LankaQR (payments — **no bank-transfer IPG, AL-05**), Fit SMS + Dialog/Mobitel (SMS, AL-60),
FCM + APNs (push), Gemini Flash (OCR), Cloudflare R2 (tiles). **Removed `[DELTA:INDIA]`/`[DELTA:JUSPAY]`:**
Juspay/Stripe, Idfy/HyperVerge/DigiLocker/Aadhaar, Google Maps, Exotel, InfoBIP, Passetto, Beckn/ONDC.

---

## 10. Scheduled / Infra Jobs   [NEW] (D-14, D-15)
**`osm-pipeline` CronJob (weekly, D-15):**
```yaml
apiVersion: batch/v1
kind: CronJob
metadata: { name: osm-pipeline, namespace: mageride }
spec:
  schedule: "0 3 * * 0"                         # weekly Sun 03:00
  jobTemplate: { spec: { template: { spec: {
    restartPolicy: OnFailure,
    containers: [{ name: osm-pipeline, image: ghcr.io/mageride/osm-pipeline:latest,
      command: ["/bin/sh","-c","download-sl-osm-diff && osm2pgsql && tippecanoe && pmtiles && aws s3 sync --endpoint $R2 ... && emit tiles.refreshed"],
      resources: { requests: { cpu: "1", memory: "2Gi" } } }] } } } }
```
**`tile-cdn` (D-14):** Cloudflare R2 bucket `mageride-tiles` + Worker (range-byte PMTiles, signed
offline-bundle URLs). **`nominatim-svc` (D-14):** dedicated 8 GB Postgres on SL extract, weekly refresh
by osm-pipeline; +1 read replica from Phase 2. **Other CronJobs:** `document-expiry` nightly (E-03),
`credential-rotation` (90-day, T-02), `pdpa-fulfillment` (E-06), `daily-fee-reset` (Asia/Colombo D-13),
**`gtfs-import`** (triggered by **SCR-AP-016 GTFS Dataset Manager** activation, AL-54 — loads the stored validated zip into `transit_staging.gtfs_*`, then a single transaction swaps live tables and `NOTIFY`s `transit-svc` to reload caches; also runs the async validation pass on upload. Versions in `transit.gtfs_feed_versions`; rollback re-runs the job from an archived version's zip. **Day-0: runs before go-live with the full national feed, AL-55**).

---

## 11. Capacity & SLO   [NEW] (D-19, D-20, T-10)
| Metric | Value | Item |
|---|---|---|
| Blended mobile ingest | **0.12 msg/s/vehicle** (1/4s move, 1/10s stationary, 1/60s idle) | D-20 |
| +Hardware trackers | **+100k @ 0.2 Hz = +20k msg/s sustained, 60k burst** → +1 EMQX node, +2 position-processor pods, +1 Timescale tablespace | T-10 |
| Latency SLO | **p95 < 5 s, p99 < 8 s** (device→passenger) | D-19 |
| Launch target | 10k vehicles / 100k passengers | §10.2 |
| Scale ceiling | 100k vehicles / 1M passengers | §10.3 |

---

## 12. Monitoring, Alerting & Runbooks (R-20)   [ADAPT]/[NEW]
- **Metrics:** Prometheus scrape `/metrics` (OpenTelemetry) per service; Grafana dashboards (ingest
  rate, p95/p99 latency, consumer lag, offer-accept rate, wallet ledger balance).
- **Logs/traces:** Loki + Tempo (OTLP); structured JSON. (NY had Prometheus+Grafana only; alert rules
  absent `[UNVERIFIED]` → **defined here**.)
- **Alert rules (Prometheus/Alertmanager):** consumer lag warn>10k/page>100k; **stuck-state runbooks
  (R-20):** `Matching>60s`, `Offered>20s`, `Accepted no-pos>60s`, `DriverArrived>10min`, `InProgress
  no-GPS>5min`, `Completed+PaymentPending>10min` → PagerDuty + runbook (driver-availability
  reconciliation, saga replay). SLO burn-rate alerts on p95/p99 (D-19). SOS dispatch > 5 s page (D-33).

---

## 13. Secrets Management & Rotation   [REPLACE] (NY Dhall placeholders + Passetto + GH secrets)
- **At rest:** **HashiCorp Vault** (transit engine for app secrets; PKI for step-ca intermediates) +
  **`pgcrypto`** for column encryption (replaces NY Passetto `[DELTA:JUSPAY]`). K8s **External Secrets
  Operator** syncs Vault → K8s `Secret`; sealed-secrets at K3s/MVP.
- **CI/CD:** GitHub Actions secrets — `GHCR_TOKEN`, `ANDROID_KEYSTORE` (base64), `KEYSTORE_PASSWORD`,
  `APPLE_API_KEY`, `R2_ACCESS_KEY`, `VAULT_TOKEN` (single keystore, no per-merchant bundle).
- **Rotation (resolves NY `[UNVERIFIED]`):** JWT signing key 90 d (JWKS overlap); MQTT device certs 90 d
  (T-02); OnePay/IPG webhook secrets 180 d; DB creds 90 d (Vault dynamic); step-ca root quarterly
  offline. Rotation via Vault leases + rolling restart (ESO re-sync).

---

## Traceability Addendum

| ADD §/Item | D7′ section | Tag | Notes |
|---|---|---|---|
| §18 stack | §1 build | [REPLACE] | .NET/Gradle/Xcode/Next.js |
| §10.1 dev topology | §2.1 / §3 compose | [ADAPT] | 24 GB VPS, 10 core containers |
| §10.2/10.3 prod | §8 targets | [REPLACE] | DOKS → EKS |
| §19 roadmap | §8 / §10 | [ADAPT] | phase substrates, CronJobs |
| canonical replica (all 25+ svcs) | §2.1, §4, §5 | [ADAPT] | Dockerfile/env/manifest each |
| D-09/10/11/12/13 (billing/payment) | §4.2 fare/wallet/subscription/registry env | [NEW] | OnePay/IPG/fee config |
| D-14 tile-cdn + nominatim | §2.1, §9, §10 | [NEW] | R2 Worker + Nominatim VPS |
| D-15 osm-pipeline | §10 CronJob | [NEW] | weekly tile refresh |
| D-16 CF Pro + Bunny fallback | §8 targets | [NEW] | tile egress trigger |
| D-18 plausibility | §4.2 position-processor env | [NEW] | accuracy>200m |
| D-19 SLO p95<5s | §11 / §12 | [NEW] | latency targets + burn alerts |
| D-20 blended ingest | §11 | [NEW] | 0.12 msg/s sizing |
| D-26 content-svc | §2.1, §4.2 | [NEW] | Si/Ta/En |
| D-29/32 auth | §4.2 iam env | [NEW] | JWT, OTP rate-limit |
| D-33 SOS SLO | §4.2 / §12 | [NEW] | dual SMS, 5s page |
| D-35 audit | §4.2 admin-bff env | [NEW] | audit.events |
| D-36 redaction | §4.2 ocr-svc env | [NEW] | pre-pass |
| E-01 offer push | §4.2 notification env | [NEW] | FCM-hi/APNs |
| E-06 PDPA | §10 CronJob | [NEW] | fulfillment job |
| E-08 shared sub | §4.2 mqtt-bridge env | [NEW] | $share/posGroup |
| E-09 outbox | §4.2 ride-svc env | [NEW] | LISTEN/NOTIFY |
| R-20 stuck-state runbooks | §12 | [NEW] | alert rules + PagerDuty |
| T-01 tcp-adapter | §2.1, §3, §5 StatefulSet | [NEW] | 4 protocol workers |
| T-02 provisioning + step-ca | §2.2, §4.2, §5 PVC, §13 | [NEW] | embedded step-ca volume |
| T-05 replay | §4.2 position-processor | [NEW] | 20/s seq dedup |
| T-06 TimescaleDB infra | §2.1 postgres, §3, §9 | [NEW] | hypertable image |
| T-10 +20k msg/s | §11 | [NEW] | tracker capacity |
| D-03/R-01/R-02/.. (services) | §2.1 app-services + §4.2 | [ADAPT] | env/manifest per service |

**Coverage:** every deployable service in the canonical replica (haproxy, emqx, redpanda, redis,
postgres, pgbouncer, hot-path [mqtt-bridge/position-processor/persistence-writer/fleet-health],
app-services [21 domain svcs incl. fleet-svc (Phase 1), transit-svc (AL-18), public-bff (AL-44)], fanout, tcp-adapter, + voip/admin-portal/fleet-portal/nominatim/osm-pipeline/
monitoring) → ≥1 row across Dockerfile/env/manifest sections.

## Mandatory ADD Critique-Item Coverage (D7′ scope)

| Item | §where | ✅ |
|---|---|---|
| D-14 tile-cdn + nominatim | §2.1, §9, §10 | ✅ |
| D-15 osm-pipeline CronJob | §10 | ✅ |
| D-16 CF Pro + Bunny fallback | §8 | ✅ |
| D-19 SLO p95<5s/p99<8s | §11, §12 | ✅ |
| D-20 blended ingest sizing | §11 | ✅ |
| R-20 stuck-state runbooks | §12 | ✅ |
| T-01 tcp-adapter deploy | §2.1, §3, §5 | ✅ |
| T-02 provisioning-svc + step-ca volume | §2.2, §4.2, §5 PVC, §13 | ✅ |
| T-06 TimescaleDB hypertable infra | §2.1, §3, §9 | ✅ |
| T-10 +20k msg/s capacity | §11 | ✅ |

All in-scope items ✅ — **document NOT `[INCOMPLETE]`.**

---

## Verification & Caveats Summary

- Build commands per component; container spec table (cites canonical 10-core + optional layout);
  full Docker Compose (all services, healthcheck-ordered startup, volumes, single bridge network);
  .NET 10 Dockerfile template; per-service env-var tables (common + 25 service deltas); K3s manifests
  (Deployment/Service/HPA/StatefulSet/Ingress/ConfigMap/Secret/PVC) + health-check table; GitHub
  Actions matrix (backend/android/ios/portal) + deploy; infra-dependency versions; secrets + rotation.
- **Resolved `[DELTA:NIX]`:** Nix flakes/process-compose/dockerTools/Dhall → Docker Compose+K3s/
  per-service alpine images/env-vars + SQL-script migrations (DbUp/Grate, no EF Core). **Resolved `[DELTA:JUSPAY]`:** Passetto→Vault+
  pgcrypto; per-merchant HyperSDK Android variants→single keystore; Juspay→OnePay.
- **Resolved Phase-A `[UNVERIFIED]` (6):** image size (alpine per-service ~120 MB), **k8s manifests now
  provided**, rollout strategy (RollingUpdate + ArgoCD promotion), **Prometheus alert rules/R-20
  runbooks defined**, secret-injection + **rotation schedule defined**, iOS/APNs now first-class.
- All in-scope ADD critique items ✅; every deployable replica service has Dockerfile/env/manifest.

---

## Δ Addendum — 2026-07-23 (micro-change-set: Node 24 LTS)

| Item | Change |
|---|---|
| Portal build toolchain (§1) | Next.js portals build on **Node 24 LTS** (was Node 20 — Node 20 reached EOL April 2026, no longer receives security patches) |
| Portal container images (§2.1) | `admin-portal` / `fleet-portal` base image → `node:24-alpine` (was `node:20-alpine`) |

No other runtime, image, manifest, or resource-budget changes. Mirrored the same day in
`lightweight-production-replica.md` (portal image rows) and `phase_c_step_by_step_guide.md` (Step 0 toolchain).

---

## Δ Addendum — 2026-08-10 (micro-change-set: the image namespace is the repository owner's)

| Item | Change |
|---|---|
| Image namespace (§5, §7, §12) | `ghcr.io/mageride/<service>` → **`ghcr.io/<repository owner>/<service>`**, which is `ghcr.io/ramitha-silva` for `Ramitha-Silva/mageride`. Overridable by the `IMAGE_NAMESPACE` repository variable, which is what a future organisation account sets. |

**Why the spec's namespace cannot be used.** `mageride` is an existing GitHub **user** account and is
not this repository's owner. A workflow's built-in `GITHUB_TOKEN` is scoped to its own owner's GHCR
namespace and to nothing else, so `docker push ghcr.io/mageride/...` is refused — not misconfigured,
refused — with `denied: permission_denied: The requested installation does not exist`. §7's push step
was therefore unrunnable as written from the first commit that had it. It surfaced on 2026-08-10, the
first time `ci` went green and `cd` reached the `images` job: all 28 images built, all 28 pushes failed.

Reaching `ghcr.io/mageride` would need a long-lived personal access token belonging to that account,
held as an Actions secret and rotated by hand — the opposite of §13's Vault/ESO discipline, for no
gain over a namespace the built-in token already owns.

**One value, two trees.** The manifests carry `<registry>/<service>` rendered from
`infra/k8s/service-catalog.yaml`; the workflows push to `${IMAGE_NAMESPACE}/<service>`. Nothing
generates one from the other, so they can disagree while every check is green — the push succeeds, the
promotion commit lands, the deploy reports success, and the first symptom is `ImagePullBackOff` in a
cluster. `infra/scripts/k8s-verify.sh` §8 now fails when the catalog's `registry:` and the workflow
fallbacks name different namespaces.

Unchanged: the Kubernetes namespace is still `mageride` (§5's `kubectl -n mageride`) and the Compose
project is still `mageride`. Only the registry namespace moves.

*End of D7′. 0 `[INCOMPLETE]` markers; all in-scope ADD critique items ✅.*
