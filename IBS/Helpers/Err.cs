using System;
using System.Threading.Tasks;

using IBS.Common;

using Spectre.Console;

namespace IBS.Helpers;

public static class Err
{

    public static void WriteEx(Exception ex)
    {
        lock (Utils.OutLock)
            AnsiConsole.MarkupLineInterpolated($"[red]Error: [{ex.GetType()}] {ex.Message}\n{ex.StackTrace}[/]");
    }

    public static void Handle(Action act)
    {
        try
        {
            act();
        }
        catch (Exception ex)
        {
            WriteEx(ex);
        }
    }

    public static void TaskWithHandling(Func<Task> act)
    {
        Task.Run(async () =>
        {
            try
            {
                await act();
            }
            catch (Exception ex)
            {
                WriteEx(ex);
            }
        });
    }

}
