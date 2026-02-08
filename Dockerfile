# --- Stage 1: Build the Vue frontend ---
FROM node:22-alpine AS frontend-build
WORKDIR /app

COPY src/fe/package.json src/fe/package-lock.json* ./
RUN npm ci 2>/dev/null || npm install

COPY src/fe/ .
RUN npm run build

# --- Stage 2: Build the .NET backend ---
FROM mcr.microsoft.com/dotnet/sdk:9.0 AS backend-build
WORKDIR /src

COPY src/be/Mergician/Mergician/Mergician.csproj Mergician/
RUN dotnet restore Mergician/Mergician.csproj

COPY src/be/Mergician/Mergician/ Mergician/
RUN dotnet publish Mergician/Mergician.csproj -c Release -o /app/publish

# --- Stage 3: Production runtime ---
FROM mcr.microsoft.com/dotnet/aspnet:9.0
RUN apt-get update && apt-get install -y --no-install-recommends curl && rm -rf /var/lib/apt/lists/*
WORKDIR /app

COPY --from=backend-build /app/publish .
COPY --from=frontend-build /app/dist ./wwwroot/

ENV ASPNETCORE_URLS=http://0.0.0.0:5000
ENV ASPNETCORE_ENVIRONMENT=Production

EXPOSE 5000

HEALTHCHECK --interval=10s --timeout=5s --retries=5 --start-period=5s \
    CMD curl -f http://localhost:5000/api/health || exit 1

ENTRYPOINT ["dotnet", "Mergician.dll"]
