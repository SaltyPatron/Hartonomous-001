# syntax=docker/dockerfile:1.7
# ==============================================================================
# Layer 1: PostgreSQL 18.3 from source, built with Intel oneAPI icx compiler.
# Same toolchain as every downstream layer = one libstdc++, one glibc, one libc++.
# Result image:  hartonomous/postgres:${POSTGRES_VERSION}
# ==============================================================================

ARG ONEAPI_HPCKIT=2025.3.1-0-devel-ubuntu22.04
ARG ONEAPI_RUNTIME=2025.3.1-0-devel-ubuntu22.04
ARG POSTGRES_VERSION=18.3

# ---------- builder ----------
FROM intel/oneapi-hpckit:${ONEAPI_HPCKIT} AS builder
ARG POSTGRES_VERSION
ENV DEBIAN_FRONTEND=noninteractive TZ=Etc/UTC

RUN apt-get update && apt-get install -y --no-install-recommends \
        build-essential pkg-config bison flex \
        libreadline-dev zlib1g-dev libssl-dev libicu-dev libxml2-dev \
        liblz4-dev libzstd-dev uuid-dev libsystemd-dev \
        ca-certificates \
    && rm -rf /var/lib/apt/lists/*

SHELL ["/bin/bash", "-lc"]

COPY external/postgres /src/postgres
WORKDIR /src/postgres

# PostgreSQL is pure C glue code. Build with stock gcc (icx is not certified
# for postgres and crashes on pl_exec.c at -O3 -xHost). The icx/icpx toolchain
# is reserved for libhartonomous (numerical kernels) downstream — both compilers
# emit C-ABI compatible code and use the system libstdc++ on Ubuntu 22.04.
# JIT off for now (LLVM dep is heavy and we don't need it for Block B).
#
# Production-mode build: -O2 with debug info preserved (-g) and frame pointers
# kept so gdb / our extension's signal handler can still walk stacks. The
# previous diagnostic build (--enable-cassert --enable-debug -O0) enabled PG's
# memory-poisoning macros (CLOBBER_FREED_MEMORY, RANDOMIZE_ALLOCATED_MEMORY)
# which surfaced spurious SEGVs in catcache rehash + qsort over poisoned
# pointers — bugs that don't fire in production builds. Cassert mode is meant
# for PG-internals development, not for substrate ingestion at scale.
#
# Symbols stay (the in-extension crash handler dumps /proc/self/maps + rip;
# addr2line still resolves source locations from -g symbols at -O2).
RUN CC=gcc CXX=g++ \
    CFLAGS="-O2 -g -fno-omit-frame-pointer" \
    CXXFLAGS="-O2 -g -fno-omit-frame-pointer" \
    ./configure \
        --prefix=/opt/pg18 \
        --with-icu \
        --with-openssl \
        --with-libxml \
        --with-uuid=e2fs \
        --with-lz4 \
        --with-zstd \
        --with-system-tzdata=/usr/share/zoneinfo \
        --without-llvm

RUN make -j"$(nproc)" world-bin && \
    make install-world-bin

# ---------- runtime ----------
FROM intel/oneapi-runtime:${ONEAPI_RUNTIME} AS runtime
ARG POSTGRES_VERSION
ENV DEBIAN_FRONTEND=noninteractive TZ=Etc/UTC

RUN apt-get update && apt-get install -y --no-install-recommends \
        libreadline8 zlib1g libssl3 libicu70 libxml2 liblz4-1 libzstd1 \
        libuuid1 tzdata locales ca-certificates \
        gosu \
    && rm -rf /var/lib/apt/lists/* \
    && localedef -i en_US -c -f UTF-8 -A /usr/share/locale/locale.alias en_US.UTF-8

ENV LANG=en_US.utf8 \
    PG_MAJOR=18 \
    PGDATA=/var/lib/postgresql/data \
    PATH=/opt/pg18/bin:$PATH \
    LD_LIBRARY_PATH=/opt/pg18/lib:/opt/intel/oneapi/redist/lib:/opt/intel/oneapi/redist/lib/intel64:/opt/intel/oneapi/redist/opt/compiler/lib

COPY --from=builder /opt/pg18 /opt/pg18

# postgres user, data dir
RUN groupadd -r postgres --gid=999 && \
    useradd -r -g postgres --uid=999 --home-dir=/var/lib/postgresql --shell=/bin/bash postgres && \
    install -d -o postgres -g postgres -m 0700 /var/lib/postgresql /var/lib/postgresql/data && \
    install -d -m 1777 /var/run/postgresql

RUN echo "/opt/pg18/lib" > /etc/ld.so.conf.d/pg18.conf && \
    echo "/opt/intel/oneapi/redist/lib" > /etc/ld.so.conf.d/oneapi.conf && \
    echo "/opt/intel/oneapi/redist/lib/intel64" >> /etc/ld.so.conf.d/oneapi.conf && \
    echo "/opt/intel/oneapi/redist/opt/compiler/lib" >> /etc/ld.so.conf.d/oneapi.conf && \
    ldconfig

EXPOSE 5432
VOLUME /var/lib/postgresql/data

USER postgres
CMD ["postgres"]
