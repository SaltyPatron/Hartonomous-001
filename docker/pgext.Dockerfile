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

# ----- libhartonomous (icx for C numerical kernels, g++ for C++) -----
# icpx 2025.3.2 SIGSEGVs inside its hir-ssa-deconstruction optimization pass
# when compiling either of the two C++ sources (laplacian_eigenmap.cpp,
# delaunay_4d.cpp) — even at -O0. The pass is integral to icpx's pipeline
# and isn't toggleable via flags. The .cpp files only #include Eigen + Spectra
# (header-only) and call MKL via standard linkage; gcc compiles them
# correctly. C numerical kernels stay on icx where it actually wins.
COPY ext/libhartonomous /src/libhartonomous
# libhartonomous's CMakeLists references the generated UCD .c/.h files
# under ../hartonomous_pg/src/generated relative to its source root.
# Copy that subtree into place BEFORE the libhartonomous configure so the
# add_library() call sees the source files.
COPY ext/hartonomous_pg/src/generated /src/hartonomous_pg/src/generated
WORKDIR /src/libhartonomous/build
# RelWithDebInfo + frame pointers + non-stripped DWARF: when a SIGSEGV in the
# numerical kernels unwinds back through libhartonomous, addr2line against the
# .so file needs the .debug_info / .debug_line sections to resolve offsets
# to file:line. Plain Release ships stripped binaries and any backtrace lands
# at "??:0". -fno-omit-frame-pointer is belt-and-suspenders for any unwinder
# that does fall back to rbp walking (most do _Unwind_Backtrace via .eh_frame
# which is independent of frame pointers, but some libc/glibc paths probe rbp).
RUN source ${ONEAPI_ROOT}/setvars.sh --force && \
    CC=icx CXX=g++ \
    cmake .. \
        -DCMAKE_BUILD_TYPE=RelWithDebInfo \
        -DCMAKE_C_FLAGS_RELWITHDEBINFO="-O2 -g3 -DNDEBUG -fno-omit-frame-pointer" \
        -DCMAKE_CXX_FLAGS_RELWITHDEBINFO="-O2 -g3 -DNDEBUG -fno-omit-frame-pointer" \
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

# gdb + binutils-debuginfod let us pull a backtrace from a core file when one
# lands. addr2line resolves symbol+offset (from the in-extension signal handler)
# back to file:line for the dev backtrace path that doesn't have core access.
RUN apt-get update && apt-get install -y --no-install-recommends \
        gdb binutils \
    && rm -rf /var/lib/apt/lists/*

COPY --from=builder /usr/local/lib/libhartonomous.so /usr/local/lib/libhartonomous.so
COPY --from=builder /opt/pg18/lib/postgresql/hartonomous.so /opt/pg18/lib/postgresql/hartonomous.so
COPY --from=builder /opt/pg18/share/postgresql/extension/hartonomous.control /opt/pg18/share/postgresql/extension/hartonomous.control
COPY --from=builder /opt/pg18/share/postgresql/extension/hartonomous--*.sql /opt/pg18/share/postgresql/extension/
# UCD/UCA atom blob: per-block math files + index + global reverse table.
# Built by `make install-ucd-blob` (PGXS hook in Makefile) into builder's
# $datadir/extension/hartonomous-ucd/ from src/generated/. Backend mmaps
# these on _PG_init; without them, substrate.cp_hash/centroid/hilbert
# return NULL and the determinism gate fails.
COPY --from=builder /opt/pg18/share/postgresql/extension/hartonomous-ucd /opt/pg18/share/postgresql/extension/hartonomous-ucd

RUN ldconfig

USER postgres
