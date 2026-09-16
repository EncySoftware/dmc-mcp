using System.IO.Compression;
using DmcMcp;
using Xunit;

/**
 * Факты об архиве для агента — локально, без сервера и без ИИ: что внутри, как называется станок,
 * какие оси, какая оснастка, есть ли картинка, какие стойки упоминаются. По ним агент пишет
 * описание сам и заполняет поля, если автор не хочет серверного ИИ.
 */
public class InspectArchiveTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "dmc-insp-" + Guid.NewGuid().ToString("N")[..8]);
    public InspectArchiveTests() => Directory.CreateDirectory(_dir);
    public void Dispose() { try { Directory.Delete(_dir, true); } catch { } }

    private const string SchemaXml = """
        <?xml version="1.0" encoding="UTF-8" ?>
        <SCCollection>
          <SCNameSpace ID="MachineRegistrator">
            <SCType ID="RegHaas" Caption="Haas VF-2" type="TRegisterMachineRecord" Enabled="True">
              <TypeName DefaultValue="HaasVF2"/>
            </SCType>
          </SCNameSpace>
          <SCType ID="Machine" Caption="Haas VF-2" type="TMachine">
            <MachineStateParameters>
              <SCType ID="X" Caption="Axis X"><Group DefaultValue="LinearAxis"/><Address DefaultValue="X"/><Min DefaultValue="0"/><Max DefaultValue="762"/></SCType>
              <SCType ID="Y" Caption="Axis Y"><Group DefaultValue="LinearAxis"/><Address DefaultValue="Y"/><Min DefaultValue="0"/><Max DefaultValue="406"/></SCType>
              <SCType ID="Z" Caption="Axis Z"><Group DefaultValue="LinearAxis"/><Address DefaultValue="Z"/><Min DefaultValue="0"/><Max DefaultValue="508"/></SCType>
              <SCType ID="A" Caption="Axis A"><Group DefaultValue="RotaryAxis"/><Address DefaultValue="A"/></SCType>
            </MachineStateParameters>
            <Schema>
              <SCType ID="HRT" Caption="HAAS HRT160 Selector" Type="TCaseNode"/>
            </Schema>
          </SCType>
        </SCCollection>
        """;

    private string ZipWith(params (string Name, string Text)[] entries)
    {
        var path = Path.Combine(_dir, "component.zip");
        using var zip = ZipFile.Open(path, ZipArchiveMode.Create);
        foreach (var (name, text) in entries)
        {
            var e = zip.CreateEntry(name);
            using var w = new StreamWriter(e.Open());
            w.Write(text);
        }
        return path;
    }

    [Fact]
    public void ReadsTheSchemaInsideAZip()
    {
        var zip = ZipWith(("machine.xml", SchemaXml), ("Images/preview.png", "not really png"),
            ("post.sppx", "FANUC 0i-MF post ; G-code M6 T"));

        var res = ArchiveInspector.Inspect(zip);

        Assert.Contains("Haas VF-2", res);
        Assert.Contains("X, Y, Z", res);
        Assert.Contains("A", res);
        Assert.Contains("HAAS HRT160", res);
        Assert.Contains("Images/preview.png", res);
        Assert.Contains("post.sppx", res);
        Assert.Contains("Fanuc", res);
        Assert.Contains("762", res); // ход по X попал в отчёт
    }

    /** Не zip — всё равно полезно: размер и какие стойки упоминаются в тексте. */
    [Fact]
    public void ScansAPlainFileForKeywords()
    {
        var sppx = Path.Combine(_dir, "post.sppx");
        File.WriteAllText(sppx, "; Siemens Sinumerik 840D post for DMG MORI\nG0 X0 Y0");

        var res = ArchiveInspector.Inspect(sppx);

        Assert.Contains("не zip", res);
        Assert.Contains("Siemens", res);
        Assert.Contains("DMG", res);
        Assert.DoesNotContain("Fanuc", res);
    }

    [Fact]
    public void MissingFileIsAnError()
    {
        Assert.StartsWith("ОШИБКА", ArchiveInspector.Inspect(Path.Combine(_dir, "nope.zip")));
    }

    [Fact]
    public async Task ToolWrapsTheInspector()
    {
        var zip = ZipWith(("machine.xml", SchemaXml));
        var tools = new DmcTools(new FakeDmcClient(), new FakeTokens(null)); // входа не нужно
        Assert.Contains("Haas VF-2", await tools.InspectArchive(zip));
    }
}
