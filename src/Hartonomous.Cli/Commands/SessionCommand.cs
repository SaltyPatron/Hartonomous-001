using System;
using System.Collections.Generic;
using System.CommandLine;
using System.CommandLine.Invocation;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;
using Hartonomous.Core.Data;
using Hartonomous.Engine.Data;
using Npgsql;

namespace Hartonomous.Cli.Commands;

/// <summary>
/// Manages inference sessions (create / close / list / archive / show).
/// </summary>
internal sealed class SessionCommand(NpgsqlDataSource dataSource)
{
    public Command Build()
    {
        Command session = new("session", "Manage inference sessions (create / close / list / archive / show).");

        Command create = new("create", "Create a new session.");
        Option<string?> labelOpt = new("--label", "Human-readable description for the session.");
        create.AddOption(labelOpt);
        create.SetHandler(async (InvocationContext ic) =>
        {
            string description = ic.ParseResult.GetValueForOption(labelOpt) ?? "cli session";
            NpgsqlSessionStore store = new(dataSource);
            long sessionId = await store.CreateSessionAsync(string.Empty, description, CancellationToken.None);
            Console.WriteLine($"Session created: {sessionId}");
        });

        Command close = new("close", "Close the current active session.");
        close.SetHandler(async () =>
        {
            NpgsqlSessionStore store = new(dataSource);
            bool closed = await store.CloseSessionAsync(CancellationToken.None);
            Console.WriteLine(closed ? "Session closed." : "No active session to close.");
        });

        Command list = new("list", "List all sessions.");
        list.SetHandler(async () =>
        {
            NpgsqlSessionStore store = new(dataSource);
            IReadOnlyList<SessionSummary> sessions = await store.ListSessionsAsync(CancellationToken.None);

            if (sessions.Count == 0)
            {
                Console.WriteLine("No sessions found.");
                return;
            }
            Console.WriteLine($"{"ID",-8} {"Description",-30} {"Phase",-20} {"Status",-12} {"Created",-22}");
            Console.WriteLine(new string('-', 95));
            foreach (SessionSummary s in sessions)
            {
                Console.WriteLine(
                    $"{s.SessionId,-8} {(s.Description ?? "-"),-30} {(s.PhaseCode ?? "-"),-20} {s.Status,-12} {s.CreatedAt.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture),-22}");
            }
        });

        Command archive = new("archive", "Archive a closed session (deletes session-scoped substrate rows).");
        Argument<long> archiveSessionIdArg = new("session-id", "Session ID to archive.");
        archive.AddArgument(archiveSessionIdArg);
        archive.SetHandler(async (InvocationContext ic) =>
        {
            long sessionId = ic.ParseResult.GetValueForArgument(archiveSessionIdArg);
            NpgsqlSessionStore store = new(dataSource);
            await store.ArchiveSessionAsync(sessionId, CancellationToken.None);
            Console.WriteLine($"Session {sessionId} archived.");
        });

        Command show = new("show", "Show session details.");
        Argument<long> showSessionIdArg = new("session-id", "Session ID to show.");
        show.AddArgument(showSessionIdArg);
        show.SetHandler(async (InvocationContext ic) =>
        {
            long sessionId = ic.ParseResult.GetValueForArgument(showSessionIdArg);
            NpgsqlSessionStore store = new(dataSource);
            SessionDetail? detail = await store.GetSessionDetailAsync(sessionId, CancellationToken.None);
            if (detail is null)
            {
                Console.Error.WriteLine($"Session {sessionId} not found.");
                ic.ExitCode = 2;
                return;
            }
            Console.WriteLine($"ID:           {detail.SessionId}");
            Console.WriteLine($"Description:  {detail.Description}");
            Console.WriteLine($"Phase:        {detail.PhaseCode}");
            Console.WriteLine($"Status:       {detail.Status}");
            Console.WriteLine($"Created:      {detail.CreatedAt:yyyy-MM-dd HH:mm:ss}");
            Console.WriteLine($"Closed:       {detail.ClosedAt?.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture) ?? "-"}");
            Console.WriteLine($"Comparisons:  {detail.ComparisonEventCount:N0}");
            Console.WriteLine($"Snapshots:    {detail.SignificanceSnapshotCount:N0}");
        });

        session.AddCommand(create);
        session.AddCommand(close);
        session.AddCommand(list);
        session.AddCommand(archive);
        session.AddCommand(show);
        return session;
    }
}
