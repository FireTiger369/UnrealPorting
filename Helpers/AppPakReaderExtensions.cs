using CUE4Parse.FileProvider;

namespace UnrealPorting.Helpers
{
    public static class AppPakReaderExtensions
    {
        public static bool FileExistsInProvider(this DefaultFileProvider provider, string path)
        {
            if (path == null) return false;

            // Normalize path
            string p = path.Replace("\\", "/");

            return provider.Files.ContainsKey(p);
        }

        public static bool HasUexp(this DefaultFileProvider provider, string assetPath)
        {
            if (string.IsNullOrEmpty(assetPath)) return false;

            string basePath = assetPath.Replace(".uasset", "");

            return provider.FileExistsInProvider(basePath + ".uexp");
        }

        public static bool HasUbulk(this DefaultFileProvider provider, string assetPath)
        {
            if (string.IsNullOrEmpty(assetPath)) return false;

            string basePath = assetPath.Replace(".uasset", "");

            return provider.FileExistsInProvider(basePath + ".ubulk")
                || provider.FileExistsInProvider(basePath + ".ubm");
        }
    }
}
