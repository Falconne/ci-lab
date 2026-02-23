FROM mcr.microsoft.com/dotnet/sdk:9.0 AS build
WORKDIR /src

# Copy project file and restore dependencies
COPY Mergician/Mergician.csproj Mergician/
RUN dotnet restore Mergician/Mergician.csproj

# Copy source and build
COPY Mergician/ Mergician/
RUN dotnet publish Mergician/Mergician.csproj -c Release -o /app/publish

# Runtime image
FROM mcr.microsoft.com/dotnet/aspnet:9.0
RUN apt-get update && apt-get install -y --no-install-recommends curl && rm -rf /var/lib/apt/lists/*

# Create a dedicated non-root runtime user. This prevents the app from
# running as root inside the container and ensures files created at runtime
# have predictable ownership.
RUN groupadd -g 1000 mergician && \
    useradd -u 1000 -g 1000 -m -s /bin/bash mergician

WORKDIR /app
# Ensure the published app files are owned by the non-root `mergician` user
# so the process can access them without elevated privileges.
COPY --from=build --chown=mergician:mergician /app/publish .

ENV ASPNETCORE_URLS=http://0.0.0.0:5000
ENV ASPNETCORE_ENVIRONMENT=Production

EXPOSE 5000

USER mergician

ENTRYPOINT ["dotnet", "Mergician.dll"]
