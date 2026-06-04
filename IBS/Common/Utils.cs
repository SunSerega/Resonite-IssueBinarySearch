using System;
using System.IO;
using System.Threading;

namespace IBS.Common;

public static class Utils
{
    public static Lock OutLock { get; } = new();
    public static String ResoniteRootFolderFullPath { get; } = Path.GetFullPath(Constants.ResoniteFoldersRoot);
}
