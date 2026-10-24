# OpenBook as a container: build with the .NET SDK, run on the ASP.NET runtime as a non-root user.
#   docker build -t openbook .
#   docker run -p 8080:8080 -v openbook-data:/data openbook
# Data (SQLite company file, backups, email folder, Data Protection keys) lives in /data. GET /healthz for probes.
# For PostgreSQL, set ConnectionStrings__OpenBook to "Host=...;Database=...;Username=...;Password=..." (see docker-compose.yml).

FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
# .editorconfig marks EF migrations as generated code, which the analyzers (warnings as errors) skip.
COPY .editorconfig Directory.Build.props OpenBook.slnx ./
COPY src/OpenBook.Domain/OpenBook.Domain.csproj src/OpenBook.Domain/
COPY src/OpenBook.Application/OpenBook.Application.csproj src/OpenBook.Application/
COPY src/OpenBook.Infrastructure/OpenBook.Infrastructure.csproj src/OpenBook.Infrastructure/
COPY src/OpenBook.Infrastructure.Postgres/OpenBook.Infrastructure.Postgres.csproj src/OpenBook.Infrastructure.Postgres/
COPY src/OpenBook.Api/OpenBook.Api.csproj src/OpenBook.Api/
COPY src/OpenBook.Web/OpenBook.Web.csproj src/OpenBook.Web/
RUN dotnet restore src/OpenBook.Web/OpenBook.Web.csproj
COPY src/ src/
RUN dotnet publish src/OpenBook.Web/OpenBook.Web.csproj -c Release -o /app --no-restore

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
# PDF forms need a sans-serif font (see SystemFontResolver); Npgsql loads the Kerberos (GSSAPI) library when it connects.
RUN apt-get update \
    && apt-get install -y --no-install-recommends fonts-dejavu-core libgssapi-krb5-2 \
    && rm -rf /var/lib/apt/lists/* \
    && mkdir -p /data && chown app:app /data
WORKDIR /app
COPY --from=build /app .
USER app
ENV ASPNETCORE_URLS=http://+:8080 \
    ASPNETCORE_ENVIRONMENT=Production \
    ConnectionStrings__OpenBook="Data Source=/data/openbook.db" \
    OpenBook__BackupDirectory=/data/backups \
    OpenBook__DataProtectionKeys=/data/keys
VOLUME /data
EXPOSE 8080
ENTRYPOINT ["dotnet", "OpenBook.Web.dll"]
