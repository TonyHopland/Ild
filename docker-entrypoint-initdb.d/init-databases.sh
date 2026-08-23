#!/bin/sh
#
# Runs once, from the postgres image's init hook, against an empty PGDATA.
#
# The two login roles get per-deployment passwords, supplied by compose as
# environment variables. They reach SQL as psql query variables (`-v name=...`,
# read back as `:'name'`) so psql does the quoting and escaping: splicing them
# into the statement text from the shell would turn any password containing a
# quote into a syntax error at best and an injection at worst.
set -eu

psql -v ON_ERROR_STOP=1 \
    --username "$POSTGRES_USER" \
    --dbname "${POSTGRES_DB:-postgres}" \
    -v core_password="${ILD_DB_PASSWORD:?ILD_DB_PASSWORD is required to create the ild_core role}" \
    -v workitems_password="${WORKITEM_DB_PASSWORD:?WORKITEM_DB_PASSWORD is required to create the ild_workitems role}" <<'SQL'
-- Create dedicated roles for each database
CREATE ROLE ild_core WITH LOGIN PASSWORD :'core_password';
CREATE ROLE ild_workitems WITH LOGIN PASSWORD :'workitems_password';

-- Create databases
CREATE DATABASE "IldCore";
CREATE DATABASE "IldWorkitems";

-- Grant connect privileges
GRANT CONNECT ON DATABASE "IldCore" TO ild_core;
GRANT CONNECT ON DATABASE "IldWorkitems" TO ild_workitems;

-- Grant schema privileges in each database
\c "IldCore"
GRANT ALL ON SCHEMA public TO ild_core;

\c "IldWorkitems"
GRANT ALL ON SCHEMA public TO ild_workitems;
SQL
