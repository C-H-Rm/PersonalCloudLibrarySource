#!/bin/bash
set -euo pipefail
RCLONE=${PCLS_RCLONE:-rclone}
ROOT=$(mktemp -d "${TMPDIR:-${HOME}/.cache}/pcls-migration-native-proof-XXXXXX")
trap 'rm -rf "$ROOT"' EXIT
export RCLONE_CONFIG="$ROOT/rclone.conf"
printf '[fixture]\ntype = local\n' > "$RCLONE_CONFIG"
LOCAL="$ROOT/game/Mods"; CLOUD="$ROOT/cloud/mods"; A="$ROOT/state-a/hash"; B="$ROOT/state-b/hash"
mkdir -p "$LOCAL" "$CLOUD" "$A"
printf 'one\n' > "$LOCAL/local.txt"; printf 'two\n' > "$CLOUD/cloud.txt"
PAIR="$LOCAL\n:local:$CLOUD"
printf '%b' "$PAIR" > "$A/pair.txt"; mkdir -p "$A/bisync"
MARKER=.pcls-modsync-proof
printf fixture > "$LOCAL/$MARKER"
"$RCLONE" copyto "$LOCAL/$MARKER" ":local:$CLOUD/$MARKER"
common=(--workdir "$A/bisync" --check-access --check-filename "$MARKER" --compare size,modtime,checksum --download-hash --conflict-resolve none --conflict-loser num --conflict-suffix pcls-conflict --max-delete 100 --resilient --recover)
"$RCLONE" bisync "$LOCAL" ":local:$CLOUD" "${common[@]}" --resync
printf ready > "$A/ready"
mkdir -p "$B"; cp -a "$A/." "$B/"
# The exact pair identity, ready marker, and native rclone work history move with the copy.
[ "$(cat "$A/pair.txt")" = "$(cat "$B/pair.txt")" ]
cmp "$A/ready" "$B/ready"
cp -a "$A/bisync/." "$B/bisync/"
common=(--workdir "$B/bisync" --check-access --check-filename "$MARKER" --compare size,modtime,checksum --download-hash --conflict-resolve none --conflict-loser num --conflict-suffix pcls-conflict --max-delete 100 --resilient --recover)
"$RCLONE" bisync "$LOCAL" ":local:$CLOUD" "${common[@]}"
[ -f "$LOCAL/cloud.txt" ] && [ -f "$CLOUD/local.txt" ]
printf 'edit-local\n' > "$LOCAL/local.txt"
"$RCLONE" bisync "$LOCAL" ":local:$CLOUD" "${common[@]}"
grep -q edit-local "$CLOUD/local.txt"
printf edit-cloud > "$CLOUD/cloud.txt"
"$RCLONE" bisync "$LOCAL" ":local:$CLOUD" "${common[@]}"
grep -q edit-cloud "$LOCAL/cloud.txt"
rm "$LOCAL/local.txt"
"$RCLONE" bisync "$LOCAL" ":local:$CLOUD" "${common[@]}"
[ ! -e "$CLOUD/local.txt" ]
rm "$CLOUD/cloud.txt"
"$RCLONE" bisync "$LOCAL" ":local:$CLOUD" "${common[@]}"
[ ! -e "$LOCAL/cloud.txt" ]
# Equal-size/equal-time edits must be detected, and conflict policy retains both variants.
printf same > "$LOCAL/equal.txt"
"$RCLONE" bisync "$LOCAL" ":local:$CLOUD" "${common[@]}"
stamp=$(stat -c %y "$LOCAL/equal.txt"); printf edit > "$LOCAL/equal.txt"; touch -d "$stamp" "$LOCAL/equal.txt"
"$RCLONE" bisync "$LOCAL" ":local:$CLOUD" "${common[@]}"
grep -q edit "$CLOUD/equal.txt"
printf baseline > "$LOCAL/conflict.txt"
"$RCLONE" bisync "$LOCAL" ":local:$CLOUD" "${common[@]}"
printf local-version > "$LOCAL/conflict.txt"; printf cloud-version > "$CLOUD/conflict.txt"
"$RCLONE" bisync "$LOCAL" ":local:$CLOUD" "${common[@]}"
ls "$LOCAL" "$CLOUD" | grep -q 'pcls-conflict'
grep -q local-version "$LOCAL"/conflict.txt* "$CLOUD"/conflict.txt* && grep -q cloud-version "$LOCAL"/conflict.txt* "$CLOUD"/conflict.txt*
printf 'PASS native rclone completed baseline relocated A->B; no post-copy --resync; unchanged, bidirectional edit/delete, metadata-equal edit and conflicts verified\n'
printf 'Proof fixture: %s\n' "$ROOT"
