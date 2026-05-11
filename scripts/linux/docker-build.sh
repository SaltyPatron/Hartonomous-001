#!/usr/bin/env bash
set -Eeuo pipefail

source "$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd)/common.sh"

layer=all
no_cache=()

while (($#)); do
    case "$1" in
        --layer)
            layer="${2:?missing layer}"
            shift 2
            ;;
        --no-cache)
            no_cache+=(--no-cache)
            shift
            ;;
        -h|--help)
            cat <<'USAGE'
Usage: scripts/linux/docker-build.sh [--layer all|postgres|postgis|pgext|final] [--no-cache]
Build the Hartonomous Docker image stack on Linux. No PowerShell.
USAGE
            exit 0
            ;;
        *)
            die "unknown argument: $1"
            ;;
    esac
done

case "$layer" in
    all|postgres|postgis|pgext|final) ;;
    *) die "invalid layer: $layer" ;;
esac

require_cmd docker

if [[ "$layer" == all || "$layer" == pgext ]]; then
    generated_unicode_tables_present || die "generated Unicode extension assets missing; run explicit codegen with scripts/hart codegen unicode --ucd-root PATH"
fi

set -a
source docker/versions.env
set +a

build_layer() {
    local name="$1"
    local dockerfile="$2"
    local tag="$3"
    shift 3
    info "Building $name -> $tag"
    docker build --progress=plain -f "$dockerfile" -t "$tag" "${no_cache[@]}" "$@" .
}

if [[ "$layer" == all || "$layer" == postgres ]]; then
    build_layer postgres docker/postgres.Dockerfile "${IMG_NS}/postgres:${POSTGRES_VERSION}" \
        --build-arg "ONEAPI_HPCKIT=${ONEAPI_HPCKIT}" \
        --build-arg "ONEAPI_RUNTIME=${ONEAPI_RUNTIME}" \
        --build-arg "POSTGRES_VERSION=${POSTGRES_VERSION}"
    docker tag "${IMG_NS}/postgres:${POSTGRES_VERSION}" "${IMG_NS}/postgres:latest"
fi

if [[ "$layer" == all || "$layer" == postgis ]]; then
    build_layer postgis docker/postgis.Dockerfile "${IMG_NS}/postgis:${POSTGIS_VERSION}" \
        --build-arg "ONEAPI_HPCKIT=${ONEAPI_HPCKIT}" \
        --build-arg "IMG_NS=${IMG_NS}" \
        --build-arg "POSTGRES_VERSION=${POSTGRES_VERSION}" \
        --build-arg "POSTGIS_VERSION=${POSTGIS_VERSION}" \
        --build-arg "PROJ_VERSION=${PROJ_VERSION}" \
        --build-arg "GEOS_VERSION=${GEOS_VERSION}"
    docker tag "${IMG_NS}/postgis:${POSTGIS_VERSION}" "${IMG_NS}/postgis:latest"
fi

if [[ "$layer" == all || "$layer" == pgext ]]; then
    build_layer pgext docker/pgext.Dockerfile "${IMG_NS}/pgext:dev" \
        --build-arg "ONEAPI_HPCKIT=${ONEAPI_HPCKIT}" \
        --build-arg "IMG_NS=${IMG_NS}" \
        --build-arg "POSTGIS_VERSION=${POSTGIS_VERSION}"
fi

if [[ "$layer" == all || "$layer" == final ]]; then
    build_layer final docker/final.Dockerfile hartonomous-postgres:latest \
        --build-arg "IMG_NS=${IMG_NS}"
fi

docker images | grep -E '(^hartonomous|hartonomous-postgres)' || true
