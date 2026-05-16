#!/usr/bin/env bash
# Generic passthrough into Hartonomous.Cli for any subcommand.
# Mirrors phases.sh wiring (run_cli + HARTONOMOUS_DB) so any CLI subcommand
# inherits the same connection-string + configuration defaults as
# `scripts/hart phase run` / `scripts/hart seed`.
set -Eeuo pipefail

source "$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd)/common.sh"

cli_prefix=()
if [[ "${1:-}" == "--no-build" ]]; then
    cli_prefix+=(--no-build)
    shift
fi

if (($# == 0)); then
    die "scripts/linux/cli.sh: missing CLI subcommand"
fi

run_cli "${cli_prefix[@]}" "$@"
