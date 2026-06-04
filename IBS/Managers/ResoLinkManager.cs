using System;
using System.Threading;
using System.Threading.Tasks;

using IBS.Common;

using ResoniteLink;

using Spectre.Console;

namespace IBS.Managers;

public static class ResoLinkManager
{

    public static async Task<LinkInterface> Connect(CancellationToken req_cancel_token)
    {
        var port = Constants.ResoLinkPort;
        //lock (Utils.OutLock)
        //AnsiConsole.MarkupLineInterpolated($"[aqua]Connecting to Resonite Link on port {port}[/]");
        while (true)
        {
            try
            {
                HeadlessManager.EnsureRunning();
                var reso_link = new LinkInterface();
                await reso_link.Connect(new Uri($"ws://localhost:{port}"), req_cancel_token);
                return reso_link;
            }
            catch (Exception ex)
            {
                var err_message = $"Failed to connect to Resonite Link on port {port}: [{ex.GetType()}] {ex.Message}";
                lock (Utils.OutLock)
                    AnsiConsole.MarkupLineInterpolated($"[red]{err_message}[/]");
                await Task.Delay(1000, CancellationToken.None);
            }
        }

    }

}
