using DtPipe.Core.Abstractions;

namespace DtPipe.Sessions;

/// <summary>
/// The informed opt-in: the first time a session materialises anything, say where the data is
/// going, how long it stays, that it is encrypted, and how to remove it.
///
/// <b>Once per session, not once per run.</b> A warning repeated every run is a warning nobody
/// reads, and one that has stopped being read is worse than none — it converts a real notice
/// into noise while leaving the impression the user was told.
/// </summary>
public static class OptInNotice
{
    public static void ShowOnce(SessionStore session, IExportObserver observer, bool silenced)
    {
        if (silenced) return;

        var meta = session.EnsureCreated();
        if (meta.NoticeShown) return;

        observer.LogMessage(
            $"[yellow]dtpipe is materialising pipeline data on disk.[/]{Environment.NewLine}" +
            $"[grey]   session   : {session.Identity.Name} ({session.Identity.Origin})[/]{Environment.NewLine}" +
            $"[grey]   location  : {session.SessionPath}[/]{Environment.NewLine}" +
            $"[grey]   encryption: AES-GCM; the key is held apart, in {UserStatePaths.KeysDirectory()}[/]{Environment.NewLine}" +
            $"[grey]   retention : {meta.TtlDays} days, then purged automatically[/]{Environment.NewLine}" +
            $"[grey]   remove now: dtpipe session purge {session.Identity.Name}[/]");

        meta.NoticeShown = true;
        session.WriteMetadata(meta);
    }
}
