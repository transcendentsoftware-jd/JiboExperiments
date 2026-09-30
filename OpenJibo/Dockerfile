# Set to "false" for managed/hosted builds that rely on Azure Speech instead, to keep image size and build time down.
ARG ENABLE_LOCAL_WHISPER=true
# Pinned ggml-org/whisper.cpp release tag.
ARG WHISPER_CPP_REF=v1.9.3
# Any model name accepted by whisper.cpp's download-ggml-model.sh, e.g. base.en, small.en.
ARG WHISPER_MODEL=base.en

FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

COPY ["OpenJibo.slnx", "./"]
COPY ["src/Jibo.Runtime.Abstractions/Jibo.Runtime.Abstractions.csproj", "src/Jibo.Runtime.Abstractions/"]
COPY ["src/Jibo.Cloud/dotnet/src/Jibo.Cloud.Domain/Jibo.Cloud.Domain.csproj", "src/Jibo.Cloud/dotnet/src/Jibo.Cloud.Domain/"]
COPY ["src/Jibo.Cloud/dotnet/src/Jibo.Cloud.Application/Jibo.Cloud.Application.csproj", "src/Jibo.Cloud/dotnet/src/Jibo.Cloud.Application/"]
COPY ["src/Jibo.Cloud/dotnet/src/Jibo.Cloud.Infrastructure/Jibo.Cloud.Infrastructure.csproj", "src/Jibo.Cloud/dotnet/src/Jibo.Cloud.Infrastructure/"]
COPY ["src/Jibo.Cloud/dotnet/src/Jibo.Cloud.Api/Jibo.Cloud.Api.csproj", "src/Jibo.Cloud/dotnet/src/Jibo.Cloud.Api/"]
COPY ["src/Jibo.Cloud/dotnet/src/Jibo.Cloud.Migrations/Jibo.Cloud.Migrations.csproj", "src/Jibo.Cloud/dotnet/src/Jibo.Cloud.Migrations/"]

RUN dotnet restore "src/Jibo.Cloud/dotnet/src/Jibo.Cloud.Api/Jibo.Cloud.Api.csproj"
RUN dotnet restore "src/Jibo.Cloud/dotnet/src/Jibo.Cloud.Migrations/Jibo.Cloud.Migrations.csproj"

COPY . .
RUN dotnet publish "src/Jibo.Cloud/dotnet/src/Jibo.Cloud.Api/Jibo.Cloud.Api.csproj" \
    -c Release \
    -o /app/publish/api \
    /p:UseAppHost=false
RUN dotnet publish "src/Jibo.Cloud/dotnet/src/Jibo.Cloud.Migrations/Jibo.Cloud.Migrations.csproj" \
    -c Release \
    -o /app/publish/migrations \
    /p:UseAppHost=false

# Builds whisper.cpp and downloads its model at the legacy Linux paths the
# .NET STT path resolver already falls back to, so self-hosted containers
# get working local transcription with zero extra environment wiring.
FROM build AS whisper-true
ARG WHISPER_CPP_REF
ARG WHISPER_MODEL
# Keep the default safe for memory-limited builders; override only with sufficient RAM.
ARG WHISPER_BUILD_JOBS=2
ARG WHISPER_CPU_PROFILE=portable
# Conservative x86 baseline avoids AVX-era requirements on older CPUs, at a speed cost.
RUN case "${WHISPER_BUILD_JOBS}" in ''|*[!0-9]*) echo 'WHISPER_BUILD_JOBS must be an integer from 1 to 32' >&2; exit 1;; esac \
    && if ! [ "${WHISPER_BUILD_JOBS}" -ge 1 ] || ! [ "${WHISPER_BUILD_JOBS}" -le 32 ]; then echo 'WHISPER_BUILD_JOBS must be an integer from 1 to 32' >&2; exit 1; fi \
    && case "${WHISPER_CPU_PROFILE}" in \
        portable) whisper_simd=OFF ;; \
        avx2) test "$(uname -m)" = x86_64 || { echo 'avx2 profile requires an x86_64 build target' >&2; exit 1; }; whisper_simd=ON ;; \
        *) echo 'WHISPER_CPU_PROFILE must be portable or avx2' >&2; exit 1 ;; \
       esac \
    && apt-get update \
    && apt-get install -y --no-install-recommends build-essential cmake git curl \
    && rm -rf /var/lib/apt/lists/* \
    && git clone --branch "${WHISPER_CPP_REF}" --depth 1 https://github.com/ggml-org/whisper.cpp.git /usr/bin/whisper.cpp \
    && cmake -S /usr/bin/whisper.cpp -B /usr/bin/whisper.cpp/build -DCMAKE_BUILD_TYPE=Release \
        -DGGML_NATIVE=OFF -DGGML_SSE42="$whisper_simd" -DGGML_AVX="$whisper_simd" -DGGML_AVX2="$whisper_simd" \
        -DGGML_FMA="$whisper_simd" -DGGML_F16C="$whisper_simd" -DGGML_BMI2="$whisper_simd" \
    && cmake --build /usr/bin/whisper.cpp/build --config Release -j"${WHISPER_BUILD_JOBS}" \
    && bash /usr/bin/whisper.cpp/models/download-ggml-model.sh "${WHISPER_MODEL}" /usr/bin/whisper.cpp/models

# Placeholder stage so COPY --from below always has a source, even when local Whisper is disabled.
FROM debian:bookworm-slim AS whisper-false
RUN mkdir -p /usr/bin/whisper.cpp/build/bin /usr/bin/whisper.cpp/models

FROM whisper-${ENABLE_LOCAL_WHISPER} AS whisper-stage

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
WORKDIR /app
ENV ASPNETCORE_URLS=http://+:8080
EXPOSE 8080

RUN apt-get update \
    && apt-get install -y --no-install-recommends ffmpeg libgomp1 \
    && rm -rf /var/lib/apt/lists/*

COPY --from=whisper-stage /usr/bin/whisper.cpp /usr/bin/whisper.cpp
COPY --from=build /app/publish/api ./api
COPY --from=build /app/publish/migrations ./migrations
COPY scripts/docker-api-entrypoint.sh /app/docker-api-entrypoint.sh
RUN chmod +x /app/docker-api-entrypoint.sh
ENTRYPOINT ["/app/docker-api-entrypoint.sh"]
