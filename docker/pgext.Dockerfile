# syntax=docker/dockerfile:1.7
# ==============================================================================
# Layer 3: libhartonomous + hartonomous_pg extension.
# Builds against pg headers from layer 1 (via layer 2 image which inherits them).
# Result image:  hartonomous/pgext:dev
# ==============================================================================

ARG ONEAPI_HPCKIT=2025.3.1-0-devel-ubuntu22.04
ARG IMG_NS=hartonomous
ARG POSTGIS_VERSION=3.6.3

# ---------- builder ----------
FROM ${IMG_NS}/postgis:${POSTGIS_VERSION} AS pgisbase

FROM intel/oneapi-hpckit:${ONEAPI_HPCKIT} AS builder
ENV DEBIAN_FRONTEND=noninteractive TZ=Etc/UTC

# Complete dev toolset for libhartonomous + hartonomous_pg.
# Includes everything PG_CONFIG/pgxs Makefiles transitively need.
RUN apt-get update && apt-get install -y --no-install-recommends \
        build-essential pkg-config cmake git ca-certificates \
        bison flex autoconf automake libtool m4 perl \
        libreadline-dev zlib1g-dev libssl-dev libicu-dev libxml2-dev \
        liblz4-dev libzstd-dev uuid-dev \
    && rm -rf /var/lib/apt/lists/*

SHELL ["/bin/bash", "-lc"]
ENV ONEAPI_ROOT=/opt/intel/oneapi

# Bring postgres + postgis + geo from prior layer.
COPY --from=pgisbase /opt/pg18 /opt/pg18
COPY --from=pgisbase /opt/geo /opt/geo
ENV PATH=/opt/pg18/bin:$PATH

# ----- BLAKE3 (built with gcc, installed to /usr/local) -----
# BLAKE3 1.5.4's CMake only knows GNU/Clang/AppleClang/MSVC for SIMD-flag tables.
# icx identifies as IntelLLVM and falls through every branch, asserting at
# configure. BLAKE3 is one hash function with hand-written AVX intrinsics; it
# gains nothing measurable from icx vs gcc. Build it once with gcc here and let
# libhartonomous's CMakeLists pick it up via find_package(BLAKE3 CONFIG). icx
# remains the toolchain for the libhartonomous numerical kernels (Eigen, MKL
# glue) where it actually wins.
RUN git clone --depth 1 --branch 1.5.4 https://github.com/BLAKE3-team/BLAKE3.git /src/blake3
WORKDIR /src/blake3/c/build
RUN cmake .. \
        -DCMAKE_C_COMPILER=gcc \
        -DCMAKE_CXX_COMPILER=g++ \
        -DCMAKE_BUILD_TYPE=Release \
        -DCMAKE_INSTALL_PREFIX=/usr/local \
        -DBLAKE3_TESTING=OFF \
        -DBUILD_SHARED_LIBS=OFF && \
    cmake --build . -j"$(nproc)" && \
    cmake --install .

# ----- libhartonomous (native, icx for numerical kernels) -----
COPY ext/libhartonomous /src/libhartonomous
WORKDIR /src/libhartonomous/build
RUN source ${ONEAPI_ROOT}/setvars.sh --force && \
    CC=icx CXX=icpx \
    cmake .. \
        -DCMAKE_BUILD_TYPE=Release \
        -DHARTONOMOUS_BUILD_TESTS=OFF \
        -DHARTONOMOUS_BUILD_SHARED=ON && \
    cmake --build . -j"$(nproc)" && \
    cp bin/libhartonomous.so /usr/local/lib/ && \
    ldconfig

# ----- hartonomous_pg (extension) -----
COPY ext/hartonomous_pg /src/hartonomous_pg
WORKDIR /src/hartonomous_pg
RUN source ${ONEAPI_ROOT}/setvars.sh --force && \
    PG_CONFIG=/opt/pg18/bin/pg_config make && \
    PG_CONFIG=/opt/pg18/bin/pg_config make install

# ---------- runtime ----------
FROM ${IMG_NS}/postgis:${POSTGIS_VERSION} AS runtime
USER root

COPY --from=builder /usr/local/lib/libhartonomous.so /usr/local/lib/libhartonomous.so
COPY --from=builder /opt/pg18/lib/postgresql/hartonomous.so /opt/pg18/lib/postgresql/hartonomous.so
COPY --from=builder /opt/pg18/share/postgresql/extension/hartonomous.control /opt/pg18/share/postgresql/extension/hartonomous.control
COPY --from=builder /opt/pg18/share/postgresql/extension/hartonomous--*.sql /opt/pg18/share/postgresql/extension/

RUN ldconfig

USER postgres
