# syntax=docker/dockerfile:1
# M0T4 — multi-stage, non-root build (§17 outline + SQ-M0-2/SQ-M0-3 deviations).

# ---- Stage 1: build the API ----
FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src
COPY . .
RUN dotnet publish src/EmailAgent.Api/EmailAgent.Api.csproj \
        -c Release \
        -o /app/publish

# ---- Stage 2: publish the SPA ----
FROM mcr.microsoft.com/dotnet/sdk:8.0 AS spa
WORKDIR /src
COPY . .
RUN dotnet publish src/EmailAgent.Spa/EmailAgent.Spa.csproj \
        -c Release \
        -o /app/spa

# ---- Stage 3: API runtime (non-root) ----
FROM mcr.microsoft.com/dotnet/aspnet:8.0 AS api-runtime
# SQ-M0-3: curl is required for the compose healthcheck (aspnet image ships none).
RUN apt-get update \
    && apt-get install -y --no-install-recommends curl \
    && rm -rf /var/lib/apt/lists/*

RUN useradd -u 10001 -m emailagent

WORKDIR /app
COPY --from=build /app/publish .

# SQ-M0-2: pre-create the configured blob path owned by the runtime user so a
# fresh named volume inherits ownership on first mount.
RUN mkdir -p /data/blobs && chown -R emailagent:emailagent /data/blobs

USER emailagent
ENV ASPNETCORE_HTTP_PORTS=8080
EXPOSE 8080
CMD ["dotnet", "EmailAgent.Api.dll"]

# ---- Stage 4: web (nginx + SPA payload) ----
FROM nginx:alpine AS web
COPY --from=spa /app/spa/wwwroot /usr/share/nginx/html
COPY docker/nginx.conf /etc/nginx/conf.d/default.conf
