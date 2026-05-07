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
            Guid sessionId = await store.CreateSessionAsync(description, null, CancellationToken.None);
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
            Console.WriteLine($"{"ID",-36} {"Label",-30} {"State",-10} {"Comparisons",12} {"Started",-22}");
            Console.WriteLine(new string('-', 116));
            foreach (SessionSummary s in sessions)
            {
                string state = s.EndedAt.HasValue ? "closed" : "open";
                Console.WriteLine(
                    $"{s.SessionId,-36} {(s.Label ?? "-"),-30} {state,-10} {s.ComparisonEventCount,12:N0} {s.StartedAt.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture),-22}");
            }
        });

        Command archive = new("archive", "Archive a closed session (deletes session-scoped substrate rows).");
        Argument<Guid> archiveSessionIdArg = new("session-id", "Session UUID to archive.");
        archive.AddArgument(archiveSessionIdArg);
        archive.SetHandler(async (InvocationContext ic) =>
        {
            Guid sessionId = ic.ParseResult.GetValueForArgument(archiveSessionIdArg);
            NpgsqlSessionStore store = new(dataSource);
            await store.ArchiveSessionAsync(sessionId, CancellationToken.None);
            Console.WriteLine($"Session {sessionId} archived.");
        });

        Command show = new("show", "Show session details.");
        Argument<Guid> showSessionIdArg = new("session-id", "Session UUID to show.");
        show.AddArgument(showSessionIdArg);
        show.SetHandler(async (InvocationContext ic) =>
        {
            Guid sessionId = ic.ParseResult.GetValueForArgument(showSessionIdArg);
            NpgsqlSessionStore store = new(dataSource);
            SessionDetail? detail = await store.GetSessionDetailAsync(sessionId, CancellationToken.None);
            if (detail is null)
            {
                Console.Error.WriteLine($"Session {sessionId} not found.");
                ic.ExitCode = 2;
                return;
            }
            Console.WriteLine($"ID:           {detail.SessionId}");
            Console.WriteLine($"Label:        {detail.Label}");
            Console.WriteLine($"Notes:        {detail.Notes ?? "-"}");
            Console.WriteLine($"State:        {(detail.EndedAt.HasValue ? "closed" : "open")}");
            Console.WriteLine($"Started:      {detail.StartedAt:yyyy-MM-dd HH:mm:ss}");
            Console.WriteLine($"Ended:        {detail.EndedAt?.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture) ?? "-"}");
            Console.WriteLine($"Comparisons:  {detail.ComparisonEventCount:N0}");
        });

        session.AddCommand(create);
        session.AddCommand(close);
        session.AddCommand(list);
        session.AddCommand(archive);
        session.AddCommand(show);
        return session;
    }
}
