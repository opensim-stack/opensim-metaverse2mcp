FROM --platform=$BUILDPLATFORM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src

COPY src/opensim-metaverse2mcp.csproj src/
RUN dotnet restore src/opensim-metaverse2mcp.csproj

COPY src/ src/
RUN dotnet publish src/opensim-metaverse2mcp.csproj -c Release -o /out /p:UseAppHost=false --no-restore

FROM mcr.microsoft.com/dotnet/aspnet:8.0 AS runtime
WORKDIR /app

COPY --from=build /out/ /app/
COPY lsl/ /app/lsl/
COPY docker/entrypoint.sh /entrypoint.sh

RUN apt-get update \
    && apt-get install -y --no-install-recommends procps iputils-ping \
    && rm -rf /var/lib/apt/lists/*

EXPOSE 8999
ENTRYPOINT ["/entrypoint.sh"]
