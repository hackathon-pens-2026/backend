# ==========================================
# Multi-stage Dockerfile for .NET 10 Web API
# ==========================================

# Stage 1: Runtime Base
# Uses Microsoft ASP.NET Core 10.0 Linux runtime
FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS base
RUN apt-get update \
    && apt-get install -y --no-install-recommends fonts-dejavu-core \
    && rm -rf /var/lib/apt/lists/*
WORKDIR /app
# Default ASP.NET Core port in non-root user mode is 8080
EXPOSE 8080
EXPOSE 8081
ENV ASPNETCORE_URLS=http://+:8080
ENV ASPNETCORE_ENVIRONMENT=Production

# Stage 2: SDK Build & Restore
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
ARG BUILD_CONFIGURATION=Release
WORKDIR /src

# Leverage Docker layer caching: copy solution / csproj files first and restore
COPY ["*.csproj", "./"]
RUN dotnet restore

# Copy all source files before build
COPY . .
RUN dotnet build -c $BUILD_CONFIGURATION -o /app/build --no-restore
# Stage 3: Publish
FROM build AS publish
ARG BUILD_CONFIGURATION=Release
RUN dotnet publish -c $BUILD_CONFIGURATION -o /app/publish /p:UseAppHost=false --no-restore

# Stage 4: Final Production Image
FROM base AS final
WORKDIR /app
# Copy published DLLs from publish stage
COPY --from=publish /app/publish .

# Use the built-in non-root 'app' user for enhanced container security
USER app

# Entrypoint matches <AssemblyName>SignIt.Api</AssemblyName>
ENTRYPOINT ["dotnet", "SignIt.Api.dll"]
