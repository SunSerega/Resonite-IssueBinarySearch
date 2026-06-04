using System;
using System.IO;
using System.Threading.Tasks;

using IBS.Common;
using IBS.Managers;

using Spectre.Console;

//TODO So far I've done everything for 1 form of reproduction:
// - Client crash without -Screen flag, with headless running the world in the background
//TODO Add more reproduction types:
// - Item behavior
// - Server crash

try
{
    AnsiConsole.MarkupLineInterpolated($"[green]Starting Resonite IssueBinarySearch (IBS) from:\n{Environment.CurrentDirectory}[/]");
    AnsiConsole.MarkupLineInterpolated($"[green]This program allows you to take a world appart to find minimal reproducible example (MRE) for an issue[/]");
    AnsiConsole.MarkupLineInterpolated($"[green]To use it you need to be on a stream branch with headless included (requires donation) and a world that contains something causing the issue[/]");
    AnsiConsole.MarkupLineInterpolated($"[green]You also need to resave the auto-joiner world to your account and set it as home. You can find that world in public folder: resrec:///U-1j4841f40i8/R-A7AC9249A2BB74B8D47B3B2AABE4383B8D0D81C8E9AD52D854580D77FBA13010[/]");

    while (!File.Exists(Constants.HeadlessFullPath))
    {
        AnsiConsole.MarkupLineInterpolated($"[yellow]Waiting for Resonite headless to be installed at {Constants.HeadlessFullPath}...[/]");
        await Task.Delay(1000);
    }
    AnsiConsole.MarkupLineInterpolated($"[green]Found Resonite headless at {Constants.HeadlessFullPath}[/]");

    AnsiConsole.MarkupLineInterpolated($"[green]Starting with {ReproductionManager.SuccessfulModCount} successful modifications already discovered on previous run[/]");

    Directory.CreateDirectory(Constants.SessionFolder);

    WsManager.RunFluxServer();

    HeadlessManager.KillOldInstances(); //TODO Also auto-kill the headfull?
    HeadlessManager.EnsureRunning();
    HeadfullManager.EnsureRunning();

    await ReproductionManager.RunExploration();

    HeadlessManager.KillIfRunning();
    HeadfullManager.KillIfRunning();
    AnsiConsole.MarkupLineInterpolated($"[green]Finished[/]");
}
catch (Exception ex)
{
    lock (Utils.OutLock)
    {
        Console.WriteLine();
        Console.WriteLine($"Critical error");
        Console.WriteLine(ex);
    }
    Console.ReadLine();
    HeadlessManager.KillIfRunning();
    HeadfullManager.KillIfRunning();
    Environment.Exit(-1);
}
