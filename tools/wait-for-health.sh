#!/bin/sh
# Waits until a container's OpenBook answers its health check and its sign-in or set-up page (CI, Linux).
#   tools/wait-for-health.sh <container> <address>
container="$1"
address="$2"
i=0
while [ "$i" -lt 60 ]; do
    if curl -fsS -o /dev/null "$address/healthz" 2>/dev/null && curl -fsSL -o /dev/null "$address/" 2>/dev/null; then
        echo "$container is healthy after $((i * 2)) seconds."
        exit 0
    fi
    i=$((i + 1))
    sleep 2
done
echo "$container didn't become healthy in two minutes. Its log:"
docker logs "$container" 2>&1 | tail -80
exit 1
