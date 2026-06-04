using System;
using System.Collections.Concurrent;
using System.Threading;

using IBS.Common;
using IBS.Helpers;

using Spectre.Console;

using WatsonWebserver;
using WatsonWebserver.Core;
using WatsonWebserver.Core.WebSockets;

namespace IBS.Managers;

public static class WsManager
{
    private static readonly ConcurrentDictionary<Int32, WebSocketSession> ws_sessions_on_wait_invite = [];

    public static void RunFluxServer()
    {
        var ws_settings = new WebserverSettings("localhost", Constants.FluxPort);
        ws_settings.WebSockets.Enable = true;
        ws_settings.IO.EnableKeepAlive = true;
        var ws_server = new Webserver(ws_settings, _ => throw new WebserverException(ApiResultEnum.BadRequest, "No route selected"));

        ws_server.Post("/invite", async req =>
        {
            var session_id = req.Http.Request.DataAsString;
            lock (Utils.OutLock)
                AnsiConsole.MarkupLineInterpolated($"[green]Received invite request for session {session_id}, forwarding to {ws_sessions_on_wait_invite.Count} sessions[/]");
            foreach (var ws_sess in ws_sessions_on_wait_invite.Values)
            {
                Err.TaskWithHandling(async () =>
                {
                    await ws_sess.SendTextAsync(session_id, req.CancellationToken);
                });
            }
            return null;
        });

        ws_server.WebSocket("/wait_invite", async (ctx, ws_sess) =>
        {
            var remote_port = ws_sess.RemotePort;
            lock (Utils.OutLock)
                AnsiConsole.MarkupLineInterpolated($"[green]New wait_invite WebSocket session from port {remote_port}[/]");
            ws_sessions_on_wait_invite[remote_port] = ws_sess;
            try
            {
                await foreach (WebSocketMessage message in ws_sess.ReadMessagesAsync(ctx.Token))
                {
                    lock (Utils.OutLock)
                        AnsiConsole.MarkupLineInterpolated($"[yellow]Received unexpected message from wait_invite: {message}[/]");
                }
            }
            finally
            {
                lock (Utils.OutLock)
                    AnsiConsole.MarkupLineInterpolated($"[green]wait_invite WebSocket session from port {remote_port} closed[/]");
                if (!ws_sessions_on_wait_invite.TryRemove(new(remote_port, ws_sess)))
                {
                    lock (Utils.OutLock)
                        AnsiConsole.MarkupLineInterpolated($"[red]Failed to remove wait_invite session for port {remote_port}[/]");
                }
            }
        });

        ws_server.Start();
    }

    public static void WaitForClientSession()
    {
        while (ws_sessions_on_wait_invite.IsEmpty)
            Thread.Sleep(100);
        //lock (Utils.OutLock)
        //    AnsiConsole.MarkupLineInterpolated($"[green]{ws_sessions_on_wait_invite.Count} session(s) waiting for invite[/]");
    }

}
