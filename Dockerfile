# syntax=docker/dockerfile:1

# ---------------------------------------------------------------------------
# Build + test stage. The test suite runs here: a red test means no image.
# Restore is a separate layer keyed on the project files so code edits do not
# invalidate the package cache.
# ---------------------------------------------------------------------------
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
ENV DOTNET_CLI_TELEMETRY_OPTOUT=1 \
    DOTNET_NOLOGO=1 \
    DOTNET_SKIP_FIRST_TIME_EXPERIENCE=1

COPY ClaimsEngine.sln Directory.Build.props Directory.Packages.props .editorconfig ./
COPY tests/Directory.Build.props tests/
COPY src/ClaimsEngine.Domain/ClaimsEngine.Domain.csproj src/ClaimsEngine.Domain/
COPY src/ClaimsEngine.Application/ClaimsEngine.Application.csproj src/ClaimsEngine.Application/
COPY src/ClaimsEngine.Infrastructure/ClaimsEngine.Infrastructure.csproj src/ClaimsEngine.Infrastructure/
COPY src/ClaimsEngine.Api/ClaimsEngine.Api.csproj src/ClaimsEngine.Api/
COPY tests/ClaimsEngine.Domain.Tests/ClaimsEngine.Domain.Tests.csproj tests/ClaimsEngine.Domain.Tests/
COPY tests/ClaimsEngine.Application.Tests/ClaimsEngine.Application.Tests.csproj tests/ClaimsEngine.Application.Tests/
COPY tests/ClaimsEngine.Api.Tests/ClaimsEngine.Api.Tests.csproj tests/ClaimsEngine.Api.Tests/
RUN dotnet restore ClaimsEngine.sln

COPY . .
RUN dotnet build ClaimsEngine.sln -c Release --no-restore

# No Docker daemon inside "docker build", so the API tests use the SQLite profile here;
# the PostgreSQL (Testcontainers) profile is the source of truth and runs in CI.
# Coverage gates are enforced in CI, not here.
ENV CLAIMS_TESTS_DB=sqlite
RUN dotnet test ClaimsEngine.sln -c Release --no-build -p:CollectCoverage=false --logger "console;verbosity=minimal"

RUN dotnet publish src/ClaimsEngine.Api/ClaimsEngine.Api.csproj -c Release --no-build -o /app/publish

# ---------------------------------------------------------------------------
# Runtime stage: ASP.NET Core runtime only, non-root, workstation GC.
# ---------------------------------------------------------------------------
FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
WORKDIR /app
ENV ASPNETCORE_HTTP_PORTS=8080 \
    DOTNET_gcServer=0 \
    DOTNET_EnableDiagnostics=0
COPY --from=build /app/publish .
USER app
EXPOSE 8080

# Readiness probe without extra packages: bash's /dev/tcp against /health/ready.
HEALTHCHECK --interval=15s --timeout=3s --start-period=30s --retries=5 \
  CMD bash -c 'exec 3<>/dev/tcp/127.0.0.1/8080 && printf "GET /health/ready HTTP/1.0\r\nHost: localhost\r\n\r\n" >&3 && head -n 1 <&3 | grep -q " 200 "'

ENTRYPOINT ["dotnet", "ClaimsEngine.Api.dll"]
