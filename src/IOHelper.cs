using System.IO;
using System.Reflection;

namespace OptimizedRemix;

internal class IOHelper
{
    private static string modDirectory = string.Empty;
    public static string ModDirectory
    {
        get
        {
            if (modDirectory == string.Empty)
            {
                modDirectory = Path.GetDirectoryName(Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location));
            }
            return modDirectory;
        }
    }
}