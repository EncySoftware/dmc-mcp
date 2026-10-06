# The hosted dmc-mcp (`dmc-mcp serve`): the dotnet tool exactly as published on nuget.org, on the ASP.NET Core
# runtime. Nothing is compiled — the DMC VPS has two cores and its memory belongs to the backend.
#   docker build --build-arg VERSION=0.8.0 -t dmc-mcp:0.8.0 https://github.com/EncySoftware/dmc-mcp.git#v0.8.0
FROM alpine:3.20 AS package
ARG VERSION
RUN test -n "$VERSION" \
 && wget -qO /p.nupkg "https://api.nuget.org/v3-flatcontainer/encysoftware.dmcmcp/${VERSION}/encysoftware.dmcmcp.${VERSION}.nupkg" \
 && mkdir /x && unzip -q /p.nupkg 'tools/net8.0/any/*' -d /x \
 && mv /x/tools/net8.0/any /app

# The tool is built for net8.0 and rolls forward (RollForward=Major): it runs on a runtime that is still supported.
FROM mcr.microsoft.com/dotnet/aspnet:10.0
COPY --from=package /app /app
# XDG_CONFIG_HOME puts the sign-in (auth.json) on the volume; uploads go there too.
ENV XDG_CONFIG_HOME=/data DMC_MCP_DATA=/data ASPNETCORE_HTTP_PORTS=8080 DOTNET_CLI_TELEMETRY_OPTOUT=1
RUN mkdir -p /data && chown app /data
VOLUME /data
USER app
WORKDIR /app
EXPOSE 8080
ENTRYPOINT ["dotnet", "/app/dmc-mcp.dll"]
CMD ["serve"]
