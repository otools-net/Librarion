#!/usr/bin/env bash
# Starts (or reuses) a local PostgreSQL in Docker for development and tests, then prints the connection string.
# Usage: agents/tools/postgres-dev/start.sh [port]   (default 55432; data kept in the volume librarion-pg)
set -euo pipefail

port="${1:-55432}"
name="librarion-pg"
image="postgres:17"

if [ -z "$(docker ps -q -f "name=^${name}$")" ]; then
    if [ -n "$(docker ps -aq -f "name=^${name}$")" ]; then
        docker start "$name" > /dev/null
    else
        docker run -d --name "$name" -p "127.0.0.1:${port}:5432" \
            -e POSTGRES_USER=librarion -e POSTGRES_PASSWORD=librarion -e POSTGRES_DB=postgres \
            -v librarion-pg:/var/lib/postgresql/data "$image"             -c shared_buffers=1GB -c effective_cache_size=4GB -c random_page_cost=1.1 -c work_mem=16MB > /dev/null
    fi
fi

for _ in $(seq 1 60); do
    if docker exec "$name" pg_isready -U librarion > /dev/null 2>&1; then
        port_now="$(docker port "$name" 5432/tcp | head -1 | sed 's/.*://')"
        echo "Host=127.0.0.1;Port=${port_now};Username=librarion;Password=librarion;Database=postgres"
        exit 0
    fi
    sleep 1
done

echo "PostgreSQL did not become ready (docker logs $name)" >&2
exit 1
