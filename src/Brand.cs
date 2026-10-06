namespace DmcMcp;

/// <summary>The only file with VALUES. The rest of the code reads them from here.</summary>
public static class Brand
{
    public const string Product = "Digital Machine Center";
    public const string Cli = "dmc-mcp";
    public const string McpServerName = "dmc";

    public const string Api = "https://dmc.encycam.com/api";
    public const string Site = "https://dmc.encycam.com";

    /// <summary>Checked 2026-09-16: this client on the encycam Keycloak accepts a loopback redirect,
    /// and the DMC backend does not check `aud`, so its token is accepted.</summary>
    public const string KeycloakUrl = "https://webservices.encycam.com/keycloak/";
    public const string KeycloakRealm = "licsys";
    public const string KeycloakClient = "dealer-space";

    /// <summary>The %APPDATA% folder for the refresh token — our own, not shared with the extension store.</summary>
    public const string AuthFolder = "dmc-mcp";
}
