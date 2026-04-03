# ── Stage 1: Build React UI ───────────────────────────────────────────────────
FROM node:20-alpine AS ui-build
WORKDIR /ui
COPY src/abook-ui/package*.json ./
RUN npm ci
COPY src/abook-ui/ ./
# Build to ./dist (override the dev outDir pointing to ASP.NET wwwroot)
RUN npx vite build --outDir ./dist --emptyOutDir

# ── Stage 2: Build .NET API ───────────────────────────────────────────────────
FROM mcr.microsoft.com/dotnet/sdk:8.0 AS api-build
WORKDIR /src

# Copy solution and project files first (layer caching)
COPY ABook.sln ./
COPY src/ABook.Core/ABook.Core.csproj src/ABook.Core/
COPY src/ABook.Infrastructure/ABook.Infrastructure.csproj src/ABook.Infrastructure/
COPY src/ABook.Agents/ABook.Agents.csproj src/ABook.Agents/
COPY src/ABook.Api/ABook.Api.csproj src/ABook.Api/
RUN dotnet restore

# Copy source and build
COPY src/ src/
RUN dotnet publish src/ABook.Api/ABook.Api.csproj -c Release -o /app/publish --no-restore

# ── Stage 3: Runtime ──────────────────────────────────────────────────────────
FROM mcr.microsoft.com/dotnet/aspnet:8.0 AS runtime
WORKDIR /app
COPY --from=api-build /app/publish ./

# Copy React build output into wwwroot
COPY --from=ui-build /ui/dist ./wwwroot

EXPOSE 8080
ENV ASPNETCORE_URLS=http://+:8080
ENTRYPOINT ["dotnet", "ABook.Api.dll"]
