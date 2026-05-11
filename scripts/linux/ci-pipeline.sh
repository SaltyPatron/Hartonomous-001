#!/usr/bin/env bash
set -Eeuo pipefail

source "$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd)/common.sh"

skip_seed=false
while (($#)); do
    case "$1" in
        --skip-seed) skip_seed=true; shift ;;
        -h|--help)
            cat <<'USAGE'
Usage: scripts/linux/ci-pipeline.sh [--skip-seed]
Linux-native local CI: preflight, build, native/unit tests, local PG extension install, DB bootstrap, optional seed floor.
USAGE
            exit 0
            ;;
        *) die "unknown argument: $1" ;;
    esac
done

scripts/linux/ci-preflight.sh
scripts/linux/build-native.sh
scripts/linux/build-extension-sql.sh
scripts/linux/build-dotnet.sh
scripts/linux/test-native.sh
scripts/linux/test-dotnet-unit.sh --no-build
scripts/linux/build-pg-extension.sh
scripts/linux/db-create.sh
scripts/linux/db-bootstrap.sh

if [[ "$skip_seed" == false ]]; then
    scripts/linux/seed-phase.sh UcdUca --no-build
    scripts/linux/seed-phase.sh Iso639 --no-build
    scripts/linux/seed-validate.sh
fi
