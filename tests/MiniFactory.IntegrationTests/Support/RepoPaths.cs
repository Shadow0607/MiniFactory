namespace MiniFactory.IntegrationTests.Support;

/// <summary>從測試執行目錄往上找 MiniFactory.slnx，定位 repo 根目錄。</summary>
public static class RepoPaths
{
    private static readonly Lazy<string> RootLazy = new(FindRoot);

    public static string Root => RootLazy.Value;

    private static string FindRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "MiniFactory.slnx")))
            {
                return dir.FullName;
            }

            dir = dir.Parent;
        }

        throw new DirectoryNotFoundException("找不到 repo 根目錄（MiniFactory.slnx）。");
    }
}
