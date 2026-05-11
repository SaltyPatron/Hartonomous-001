#!/usr/bin/env bash
set -Eeuo pipefail

source "$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd)/common.sh"

require_cmd python3

check=false
while (($#)); do
    case "$1" in
        --check)
            check=true
            shift
            ;;
        -h|--help)
            cat <<'USAGE'
Usage: scripts/linux/build-extension-sql.sh [--check]
Assemble ext/hartonomous_pg/sql/hartonomous--1.0.sql from sql/schema/bootstrap.sql.
USAGE
            exit 0
            ;;
        *)
            die "unknown argument: $1"
            ;;
    esac
done

args=()
if [[ "$check" == true ]]; then
    args+=(--check)
fi
python3 scripts/build/concat_extension_sql.py "${args[@]}"
