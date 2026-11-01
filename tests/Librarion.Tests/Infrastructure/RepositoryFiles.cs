namespace Librarion.Tests.Infrastructure;

/// <summary>Files of the repository the tests read (the built web client, configuration files).</summary>
public static class RepositoryFiles
{
    /// <summary>The folder holding <c>Librarion.slnx</c>, found upwards from the test binaries.</summary>
    public static string Root { get; } = FindRoot();

    public static string PathOf(string relative) => Path.Combine(Root, relative.Replace('/', Path.DirectorySeparatorChar));

    /// <summary>
    /// The built client (<c>src/Librarion.Api/wwwroot</c>). Built by the Api project unless
    /// <c>-p:LibrarionSkipWebClient=true</c>: then a test reading it is inconclusive locally and fails on CI.
    /// </summary>
    public static string WebRoot()
    {
        var root = PathOf("src/Librarion.Api/wwwroot");
        if (!File.Exists(Path.Combine(root, "index.html")))
        {
            const string message = "The web client is not built (src/Librarion.Api/wwwroot): build without -p:LibrarionSkipWebClient=true.";
            if (Environment.GetEnvironmentVariable("CI") == "true")
                Assert.Fail(message);
            Assert.Inconclusive(message);
        }

        return root;
    }

    private static string FindRoot()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
            if (File.Exists(Path.Combine(directory.FullName, "Librarion.slnx")))
                return directory.FullName;
        throw new InvalidOperationException("Librarion.slnx not found above " + AppContext.BaseDirectory);
    }
}
