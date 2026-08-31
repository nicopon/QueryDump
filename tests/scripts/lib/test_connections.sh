# shellcheck shell=bash
#
# test_connections.sh — the endpoints of the local test infrastructure.
#
# Sourcing this file IS a script's declaration that it needs `tests/infra` running. The CI
# validator job selects what it can run on that basis, so a new script that needs a database is
# excluded from CI by the same line that gives it a connection string. Nothing to keep in sync.
#
# Connect timeouts are deliberately far above the drivers' 15 s defaults. Eight containers on one
# developer machine take longer than that to accept a connection under load, and the whole catalog
# used to fail on it — SQL Server reported "[Pre-Login] handshake=14993", one millisecond inside
# the default. This buys the handshake time; it does not retry, so a genuinely unreachable server
# still fails.

TEST_CONNECT_TIMEOUT="${TEST_CONNECT_TIMEOUT:-60}"

PG="pg:Host=localhost;Port=5440;Database=integration;Username=postgres;Password=password;Timeout=${TEST_CONNECT_TIMEOUT}"
MSSQL="mssql:Server=localhost,1434;Database=master;User Id=sa;Password=Password123!;Encrypt=False;Connect Timeout=${TEST_CONNECT_TIMEOUT}"
ORA="ora:Data Source=localhost:1522/FREEPDB1;User Id=testuser;Password=password;Connection Timeout=${TEST_CONNECT_TIMEOUT}"
MYSQL="mysql:Server=127.0.0.1;Port=3306;Database=integration;User ID=testuser;Password=password;Connection Timeout=${TEST_CONNECT_TIMEOUT}"

# Object storage (MinIO, Azurite). No timeout key: these are HTTP endpoints, not ADO.NET.
MINIO_ENDPOINT="http://127.0.0.1:9000"
MINIO_ACCESS_KEY="minioadmin"
MINIO_SECRET_KEY="minioadmin"
AZURITE_CONNECTION_STRING="DefaultEndpointsProtocol=http;AccountName=devstoreaccount1;AccountKey=Eby8vdM02xNOcqFlqUwJPLlmEtlCDXJ1OUzFT50uSRZ6IFsuFq2UVErCz4I6tq/K1SZFPTOtr/KBHBeksoGMGw==;BlobEndpoint=http://127.0.0.1:10000/devstoreaccount1;"
