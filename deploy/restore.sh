#!/bin/sh
# Restore an Ecosys Payroll backup into a database. Usage (inside the backup container):
#   docker compose run --rm -e RESTORE_DB=ecosys_payroll_restore backup-restore /backups/daily/<file>
# Restore into a scratch database first, check it, then switch the application over (see docs/operations.md).
set -eu
file="$1"; target="${RESTORE_DB:?set RESTORE_DB to the database to restore into}"
sha256sum -c "$file.sha256"
src="$file"
case "$file" in *.enc)
  : "${BACKUP_PASSPHRASE:?BACKUP_PASSPHRASE is needed to decrypt}"
  src="/tmp/restore.dump"
  openssl enc -d -aes-256-cbc -pbkdf2 -iter 200000 -pass env:BACKUP_PASSPHRASE -in "$file" -out "$src";;
esac
createdb "$target" 2>/dev/null || echo "database $target exists; restoring over it"
pg_restore --no-owner --clean --if-exists --dbname="$target" "$src"
[ "$src" = "/tmp/restore.dump" ] && rm -f "$src"
echo "Restored $file into $target"
