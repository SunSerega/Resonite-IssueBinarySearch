using System;

namespace IBS.Common;

public static class Constants
{
    public const String HeadlessFullPath = @"C:\Program Files (x86)\Steam\steamapps\common\Resonite\Headless\Resonite.exe";
    public const String HeadfullFullPath = @"C:\Program Files (x86)\Steam\steamapps\common\Resonite\Resonite.exe";
    public const Int32 ResoLinkPort = 4567;
    public const Int32 FluxPort = 4568;
    public const String SessionFolder = @"Session";
    public const String ConfigPath = @$"{SessionFolder}\config.dat";
    public const String SuccessPath = @$"{SessionFolder}\SuccessfulModifications";
    public const String HeadlessConfigPath = @$"{SessionFolder}\headless_config.json";
    public const String ResoniteFoldersRoot = @$"{SessionFolder}\Resonite";
    public const String SessionInitItemResRec = @"resrec:///U-1j4841f40i8/R-019e8c20-b2e9-7db4-8988-98f2e18008ee";
    //public const String HeadlessSpawnedItemName = @"Headless Spawned Item";
}
