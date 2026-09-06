#!/bin/bash
# Applies the schema, once, when the database volume is first created.
#
# Postgres runs everything in /docker-entrypoint-initdb.d on a fresh data
# directory only, over a local socket, BEFORE it opens the TCP port. So by the
# time the healthcheck passes and the API is allowed to connect, the schema is
# already there — which is what stops the API crash-looping on an empty
# database (SeedOwnerAsync queries users.email, a citext column, and citext
# does not exist until schema_v1.sql creates the extension).
#
# ORDER IS NOT ALPHABETICAL, which is exactly why this script exists rather
# than mounting sqlfiles/ directly. Postgres would run 002 before schema_v1.sql
# because digits sort ahead of letters, and 004 is a CREATE OR REPLACE VIEW
# over a view schema_v1.sql defines. The list below is the one in CLAUDE.md.
set -euo pipefail

FILES=(
  schema_v1.sql
  002_refresh_tokens.sql
  003_plan_services.sql
  004_member_overview.sql
  005_attendance_module.sql
  006_dashboard.sql
  007_member_overview_joined.sql
  008_public_site.sql
  009_messaging.sql
  010_payments_module.sql
)

for f in "${FILES[@]}"; do
  echo "initdb: applying $f"
  psql -v ON_ERROR_STOP=1 --username "$POSTGRES_USER" --dbname "$POSTGRES_DB" \
       -f "/sqlfiles/$f"
done

echo "initdb: schema applied (${#FILES[@]} files)"
