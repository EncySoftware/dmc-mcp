using DmcMcp;
using Xunit;

/** Who may call the hosted server: the key, by header or as the first path segment for URL-only clients. */
public class KeyAuthTests
{
    private const string Key = "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";

    [Fact] public void BearerHeaderLetsIn() => Assert.Equal((true, "/mcp"), KeyAuth.Check("/mcp", "Bearer " + Key, Key));
    [Fact] public void KeyInPathLetsInAndIsStripped() => Assert.Equal((true, "/mcp"), KeyAuth.Check("/mcp/" + Key, null, Key));
    [Fact] public void KeyInPathBeforeUpload() => Assert.Equal((true, "/mcp/upload"), KeyAuth.Check("/mcp/" + Key + "/upload", null, Key));
    [Fact] public void UploadWithHeader() => Assert.Equal((true, "/mcp/upload"), KeyAuth.Check("/mcp/upload", "Bearer " + Key, Key));
    [Fact] public void NoKeyIsOut() => Assert.False(KeyAuth.Check("/mcp", null, Key).Ok);
    [Fact] public void WrongHeaderIsOut() => Assert.False(KeyAuth.Check("/mcp", "Bearer nope", Key).Ok);
    [Fact] public void WrongPathKeyIsOut() => Assert.False(KeyAuth.Check("/mcp/" + Key[..^1] + "0", null, Key).Ok);
    [Fact] public void KeyPrefixIsNotTheKey() => Assert.False(KeyAuth.Check("/mcp/" + Key + "x", null, Key).Ok);
    [Fact] public void OtherPathsAreNotTheServers() => Assert.False(KeyAuth.Check("/api/products", "Bearer " + Key, Key).Ok);
}
