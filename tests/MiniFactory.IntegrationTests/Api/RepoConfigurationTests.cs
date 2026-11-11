using System.Text.RegularExpressions;
using MiniFactory.IntegrationTests.Support;

namespace MiniFactory.IntegrationTests.Api;

/// <summary>B6 / S8 / S9：repo 設定檔層級的防呆檢查（不需資料庫）。</summary>
public partial class RepoConfigurationTests
{
    private static string Read(params string[] parts) => File.ReadAllText(Path.Combine([RepoPaths.Root, .. parts]));

    [Fact]
    public void B6_AppSettings_DoNotHardcodeUrls()
    {
        foreach (var file in Directory.GetFiles(Path.Combine(RepoPaths.Root, "src", "MiniFactory.Api"), "appsettings*.json"))
        {
            Assert.DoesNotContain("\"Urls\"", File.ReadAllText(file), StringComparison.OrdinalIgnoreCase);
        }
    }

    [Fact]
    public void B6_LaunchSettings_ListensOnPort5080()
    {
        var text = Read("src", "MiniFactory.Api", "Properties", "launchSettings.json");
        Assert.Contains("\"applicationUrl\": \"http://localhost:5080\"", text, StringComparison.Ordinal);
    }

    [Fact]
    public void S8_GitIgnore_IgnoresEnvVariantsAndKeyFiles_ButKeepsEnvExample()
    {
        var lines = Read(".gitignore").Split('\n').Select(l => l.Trim()).ToArray();
        foreach (var rule in new[] { ".env", ".env.*", "!.env.example", "*.pem", "*.key", "*.pfx" })
        {
            Assert.Contains(rule, lines);
        }

        // 否定規則必須在 .env.* 之後才有效
        Assert.True(Array.IndexOf(lines, "!.env.example") > Array.IndexOf(lines, ".env.*"));
    }

    [Fact]
    public void R8_GitIgnore_IgnoresClaudeLocalSettings_ButNotSharedSettings()
    {
        var lines = Read(".gitignore").Split('\n').Select(l => l.Trim()).ToArray();

        Assert.Contains(".claude/settings.local.json", lines);
        // 團隊共用的 settings.json 不可被忽略
        Assert.DoesNotContain(".claude/", lines);
        Assert.DoesNotContain(".claude/settings.json", lines);
    }

    [Fact]
    public void Gitattributes_ForcesLfForShellScripts_AndNormalizesTextFiles()
    {
        var lines = Read(".gitattributes").Split('\n').Select(l => l.Trim()).ToArray();

        // initdb 的 *.sh 在 Linux 容器內執行，CRLF 會讓 `set -eu\r` 失敗
        Assert.Contains("*.sh text eol=lf", lines);
        Assert.Contains("* text=auto", lines);
    }

    [Fact]
    public void InitDbShellScripts_InWorkingTree_ContainNoCarriageReturn()
    {
        var scripts = Directory.GetFiles(Path.Combine(RepoPaths.Root, "deploy", "mariadb", "initdb"), "*.sh");
        Assert.NotEmpty(scripts);
        foreach (var script in scripts)
        {
            Assert.DoesNotContain((byte)'\r', File.ReadAllBytes(script));
        }
    }

    [Fact]
    public void S9_WorkflowActions_ArePinnedToFullCommitSha()
    {
        var files = Directory.GetFiles(Path.Combine(RepoPaths.Root, ".github", "workflows"), "*.yml");
        Assert.NotEmpty(files);
        var uses = files.SelectMany(f => File.ReadAllLines(f))
            .Select(l => UsesRegex().Match(l)).Where(m => m.Success).Select(m => m.Groups[1].Value).ToArray();
        Assert.NotEmpty(uses);
        foreach (var reference in uses.Where(u => !u.StartsWith("./", StringComparison.Ordinal)))
        {
            Assert.Matches(@"@[0-9a-f]{40}$", reference);
        }
    }

    [GeneratedRegex(@"uses:\s*(\S+)")]
    private static partial Regex UsesRegex();
}
