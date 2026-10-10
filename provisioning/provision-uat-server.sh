#!/usr/bin/env bash
set -euo pipefail

# Run the existing backend CLI inside its container: no host SDK or rebuild needed.
script_dir="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd)"
test -r "$script_dir/uat-accounts.json"
test -r "$script_dir/uat-organizations.sql"
test -r "$script_dir/uat-assignment-start.sql"
test -r "$script_dir/facilities.sql"
sudo docker inspect --format '{{.State.Running}}' hackathon-backend | grep -qx true
sudo docker inspect --format '{{.State.Running}}' hackathon-postgres | grep -qx true

printf 'Provision 14 UAT accounts in the SERVER database. Existing passwords stay unchanged.\n'
read -r -s -p 'Initial password (use the password agreed in chat): ' SIGNIT_UAT_PASSWORD
printf '\n'
trap 'unset SIGNIT_UAT_PASSWORD' EXIT
if (( ${#SIGNIT_UAT_PASSWORD} < 12 || ${#SIGNIT_UAT_PASSWORD} > 128 )); then
  printf 'Password must contain 12-128 characters.\n' >&2
  exit 1
fi
read -r -p 'Database backup ready? Type CREATE to continue: ' uat_confirmation
[[ "$uat_confirmation" == CREATE ]] || exit 1

sudo docker exec -i hackathon-postgres sh -c \
  'psql -X -v ON_ERROR_STOP=1 -U "$POSTGRES_USER" -d "$POSTGRES_DB"' \
  < "$script_dir/facilities.sql"
sudo docker exec -i hackathon-postgres sh -c \
  'psql -X -v ON_ERROR_STOP=1 -U "$POSTGRES_USER" -d "$POSTGRES_DB"' \
  < "$script_dir/uat-organizations.sql"
sudo docker exec -i hackathon-postgres sh -c \
  'psql -X -v ON_ERROR_STOP=1 -U "$POSTGRES_USER" -d "$POSTGRES_DB"' \
  < "$script_dir/uat-assignment-start.sql"
sudo docker cp "$script_dir/uat-accounts.json" hackathon-backend:/tmp/signit-uat-accounts.json
sudo env SIGNIT_UAT_PASSWORD="$SIGNIT_UAT_PASSWORD" docker exec -e SIGNIT_UAT_PASSWORD \
  hackathon-backend dotnet /app/SignIt.Api.dll --provision-auth /tmp/signit-uat-accounts.json

sudo docker exec -i hackathon-postgres sh -c \
  'psql -X -v ON_ERROR_STOP=1 -U "$POSTGRES_USER" -d "$POSTGRES_DB"' <<'SQL'
SELECT "Email", "IsActive" FROM public.auth_users
WHERE "Email" LIKE 'uat.%@demo.signit.example' ORDER BY "Email";
SQL
