namespace ScreenTask
{
    internal static class Constants
    {
        public static readonly string AppSettingsFileName = System.IO.Path.Combine(System.AppDomain.CurrentDomain.BaseDirectory, "appsettings.xml");
        public const string ScreenshotFileName = "ScreenTask.jpg";
        public const string WebServerDirectory = "/WebServer";
        public const string ScreenshotFilePath = WebServerDirectory + "/" + ScreenshotFileName;
        public const string GitHubApiLatestReleaseUrl = "https://api.github.com/repos/snoopeh/ScreenTask/releases/latest";
        public const string WebsiteUrl = "https://github.com/snoopeh/ScreenTask";
        public const string TwitterUrl = "http://twitter.com/EslaMx7";
        public const string UserAgent = "ScreenTask";
    }
}
