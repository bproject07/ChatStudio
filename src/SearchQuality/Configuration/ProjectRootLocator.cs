namespace SearchQuality.Configuration;

public static class ProjectRootLocator
{
    public static string Find(string startDirectory)
    {
        var directory = new DirectoryInfo(Path.GetFullPath(startDirectory));
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "appsettings.json"))
                && (File.Exists(Path.Combine(directory.FullName, "SearchQuality.slnx"))
                    || File.Exists(Path.Combine(directory.FullName, "ChatStudio.slnx"))))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        throw new DirectoryNotFoundException(
            "Could not locate the Search Quality Lab root. Run the application from the project directory.");
    }
}
