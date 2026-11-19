#!/bin/sh
# Nightly PostgreSQL backups for Ecosys Payroll.
#   - pg_dump custom format (restorable with pg_restore), encrypted with AES-256 when BACKUP_PASSPHRASE is set
#   - runs daily at BACKUP_AT (24h, Africa/Nairobi), keeps BACKUP_KEEP_DAYS daily files and the first backup of
#     each month for BACKUP_KEEP_MONTHS months
#   - RUN_ONCE=1 takes one backup and exits (for testing or a pre-upgrade snapshot)
# Copy /backups off this server (another region or provider) — a backup on the same disk is not a backup.
set -eu
: "${PGHOST:=db}" "${PGUSER:=payroll}" "${PGDATABASE:=ecosys_payroll}"
: "${BACKUP_DIR:=/backups}" "${BACKUP_AT:=01:30}" "${BACKUP_KEEP_DAYS:=14}" "${BACKUP_KEEP_MONTHS:=12}"
export PGHOST PGUSER PGDATABASE TZ="${TZ:-Africa/Nairobi}"
mkdir -p "$BACKUP_DIR/daily" "$BACKUP_DIR/monthly"

backup() {
  stamp=$(date +%Y%m%d-%H%M)
  tmp="$BACKUP_DIR/.in-progress-$stamp"
  pg_dump --format=custom --no-owner --compress=6 --file="$tmp"
  if [ -n "${BACKUP_PASSPHRASE:-}" ]; then
    openssl enc -aes-256-cbc -pbkdf2 -iter 200000 -salt -pass env:BACKUP_PASSPHRASE -in "$tmp" -out "$tmp.enc"
    rm -f "$tmp"; tmp="$tmp.enc"; ext="dump.enc"
  else
    echo "WARNING: BACKUP_PASSPHRASE is not set; backup is unencrypted." >&2; ext="dump"
  fi
  file="$BACKUP_DIR/daily/$PGDATABASE-$stamp.$ext"
  mv "$tmp" "$file"
  sha256sum "$file" > "$file.sha256"
  # First backup of the month is kept as the monthly copy.
  month=$(date +%Y%m)
  if ! ls "$BACKUP_DIR/monthly/$PGDATABASE-$month"* >/dev/null 2>&1; then
    cp "$file" "$BACKUP_DIR/monthly/$PGDATABASE-$month.$ext"; sha256sum "$BACKUP_DIR/monthly/$PGDATABASE-$month.$ext" > "$BACKUP_DIR/monthly/$PGDATABASE-$month.$ext.sha256"
  fi
  find "$BACKUP_DIR/daily" -type f -mtime "+$BACKUP_KEEP_DAYS" -delete
  find "$BACKUP_DIR/monthly" -type f -mtime "+$((BACKUP_KEEP_MONTHS * 31))" -delete
  echo "$(date -Iseconds) backup ok: $file ($(du -h "$file" | cut -f1))"
}

until pg_isready -q; do echo "waiting for database..."; sleep 5; done
if [ "${RUN_ONCE:-0}" = "1" ]; then backup; exit 0; fi

while true; do
  now=$(date +%s)
  next=$(date -d "$(date +%Y-%m-%d) $BACKUP_AT" +%s 2>/dev/null || date -D "%Y-%m-%d %H:%M" -d "$(date +%Y-%m-%d) $BACKUP_AT" +%s)
  [ "$next" -le "$now" ] && next=$((next + 86400))
  sleep $((next - now))
  backup || echo "$(date -Iseconds) BACKUP FAILED" >&2
done
