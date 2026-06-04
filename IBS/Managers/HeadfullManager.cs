using System.Diagnostics;

using IBS.Common;

namespace IBS.Managers;

public static class HeadfullManager
{
    private static Process? running_process = null;

    public static Process EnsureRunning()
    {
        if (running_process is { } && !running_process.HasExited)
            return running_process;
        running_process = Run();
        return running_process;
    }

    public static void KillIfRunning()
    {
        if (running_process is { } && !running_process.HasExited)
        {
            running_process.Kill(entireProcessTree: true);
            running_process.WaitForExit();
        }
    }

    private static Process Run()
    {
        var psi = new ProcessStartInfo
        {
            FileName = Constants.HeadfullFullPath,
        };
        //psi.ArgumentList.Add("-Screen");

        var p = new Process
        {
            StartInfo = psi,
        };

        p.Start();

        return p;
    }

}
