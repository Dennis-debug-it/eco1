# syntax=docker/dockerfile:1.7
# Ecosys Payroll — single image serving the REST API and the React app.

# ---- Front-end ---------------------------------------------------------------------------
FROM node:22-alpine AS web
WORKDIR /web
COPY web/package.json web/package-lock.json ./
RUN npm ci --no-audit --no-fund
COPY web/ ./
RUN npm run build

# ---- Back-end ----------------------------------------------------------------------------
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS api
WORKDIR /src
COPY Directory.Build.props Ecosys.Payroll.slnx ./
COPY src/Ecosys.Payroll.Engine/Ecosys.Payroll.Engine.csproj src/Ecosys.Payroll.Engine/
COPY src/Ecosys.Payroll.Domain/Ecosys.Payroll.Domain.csproj src/Ecosys.Payroll.Domain/
COPY src/Ecosys.Payroll.Infrastructure/Ecosys.Payroll.Infrastructure.csproj src/Ecosys.Payroll.Infrastructure/
COPY src/Ecosys.Payroll.Api/Ecosys.Payroll.Api.csproj src/Ecosys.Payroll.Api/
RUN dotnet restore src/Ecosys.Payroll.Api/Ecosys.Payroll.Api.csproj
COPY src/ src/
RUN dotnet publish src/Ecosys.Payroll.Api/Ecosys.Payroll.Api.csproj -c Release -o /app --no-restore /p:UseAppHost=false

# ---- Runtime -----------------------------------------------------------------------------
FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
WORKDIR /app
ENV ASPNETCORE_URLS=http://+:8080 \
    ASPNETCORE_ENVIRONMENT=Production \
    DOTNET_RUNNING_IN_CONTAINER=true
COPY --from=api /app ./
COPY --from=web /web/dist ./wwwroot
USER $APP_UID
EXPOSE 8080
HEALTHCHECK --interval=30s --timeout=5s --start-period=40s --retries=3 \
  CMD ["dotnet", "Ecosys.Payroll.Api.dll", "--healthcheck"]
ENTRYPOINT ["dotnet", "Ecosys.Payroll.Api.dll"]
