namespace DmcMcp;

/// <summary>Единственный файл со ЗНАЧЕНИЯМИ. Остальной код читает их отсюда.</summary>
public static class Brand
{
    public const string Product = "Digital Machine Center";
    public const string Cli = "dmc-mcp";
    public const string McpServerName = "dmc";

    public const string Api = "https://dmc.encycam.com/api";
    public const string Site = "https://dmc.encycam.com";

    /// <summary>Проверено 2026-09-16: этот клиент на encycam-Keycloak принимает loopback-redirect,
    /// а бэкенд DMC не проверяет `aud`, так что его токен принимается.</summary>
    public const string KeycloakUrl = "https://webservices.encycam.com/keycloak/";
    public const string KeycloakRealm = "licsys";
    public const string KeycloakClient = "dealer-space";

    /// <summary>Папка под %APPDATA% для refresh-токена — своя, не общая с магазином расширений.</summary>
    public const string AuthFolder = "dmc-mcp";
}
