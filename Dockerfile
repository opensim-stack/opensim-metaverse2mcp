FROM --platform=$BUILDPLATFORM mcr.microsoft.com/dotnet/sdk:10.0 AS build
ARG USE_LIBREMETAVERSE_GIT=false
WORKDIR /src

# Clone LibreMetaverse fork if requested
RUN if [ "$USE_LIBREMETAVERSE_GIT" = "true" ] ; then \
    git clone https://github.com/cinderblocks/libremetaverse.git /libremetaverse ; \
    fi

COPY src/opensim-metaverse2mcp.csproj src/
RUN if [ "$USE_LIBREMETAVERSE_GIT" = "true" ] ; then \
    dotnet restore src/opensim-metaverse2mcp.csproj /p:UseLibreMetaverseGit=true ; \
    else \
    dotnet restore src/opensim-metaverse2mcp.csproj ; \
    fi

COPY src/ src/
RUN dotnet publish src/opensim-metaverse2mcp.csproj -c Release -o /out \
    /p:UseAppHost=false \
    /p:UseLibreMetaverseGit=$USE_LIBREMETAVERSE_GIT \
    --no-restore

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
WORKDIR /app

COPY --from=build /out/ /app/
COPY lsl/ /app/lsl/
COPY docker/entrypoint.sh /entrypoint.sh

RUN apt-get update \
    && apt-get install -y --no-install-recommends procps iputils-ping \
    && rm -rf /var/lib/apt/lists/*
    
RUN mkdir -p /app/linden/cache /workspace/state \
    && chmod -R a+rwx /app/linden /workspace/state

EXPOSE 8999
ENTRYPOINT ["/entrypoint.sh"]
