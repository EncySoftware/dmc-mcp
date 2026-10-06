#!/bin/sh
# On the DMC server, as root: build the image for a released version and replace the dmc-mcp container.
#   sh update.sh 0.8.0
# First time only (see README, "Hosted server"): /opt/dmc-mcp/dmc-mcp.env with DMC_MCP_KEY, the data folder owned
# by UID 1654, then sign in once with the login command the container prints.
set -eu
V="${1:?usage: update.sh <version>, e.g. 0.8.0}"
docker build --build-arg VERSION="$V" -t "dmc-mcp:$V" "https://github.com/EncySoftware/dmc-mcp.git#v$V"
# The data folder holds the sign-in (an offline refresh token) and the uploads: the container's user only.
chmod 700 /opt/dmc-mcp/data
docker rm -f dmc-mcp 2>/dev/null || true
docker run -d --name dmc-mcp --restart unless-stopped \
  -p 127.0.0.1:8095:8080 \
  --env-file /opt/dmc-mcp/dmc-mcp.env \
  -v /opt/dmc-mcp/data:/data \
  "dmc-mcp:$V"
sleep 5
docker logs --tail 20 dmc-mcp
