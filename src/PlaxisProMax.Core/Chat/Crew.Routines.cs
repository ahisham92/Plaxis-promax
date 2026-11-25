namespace PlaxisProMax.Chat;

/// <summary>
/// Scheduled routines in a chat With coordinator (Sketch ProMax's ChatPane.Routines, without its window): a routine
/// whose time has come, or whose run was missed while Plaxis ProMax or the PC was off (caught up once), runs as a job
/// of this chat, and its history keeps how each run ended.
/// </summary>
internal sealed partial class Crew
{
    /// <summary>Where routines.json is kept (the checks use their own folder).</summary>
    public static string RoutinesFolder => FolderOverride ?? ClaudeCli.Folder;

    /// <summary>True when <paramref name="routine"/> runs on the project with this title (or on whichever is open).</summary>
    public static bool RoutineFits(Routine routine, string projectTitle)
    {
        static string Bare(string title)
        {
            title = title.Trim();
            foreach (var ext in new[] { ".p2dx", ".p3dx", ".p2d", ".p3d" })
                if (title.EndsWith(ext, StringComparison.OrdinalIgnoreCase)) return title.Substring(0, title.Length - ext.Length);
            return title;
        }
        return routine.Model.Trim().Length == 0 || string.Equals(Bare(routine.Model), Bare(projectTitle), StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// The routine that is due now and fits the open project, started as a job; the caller saves the list when one
    /// started. One a tick: the next goes a minute later.
    /// </summary>
    public bool RoutineTick(List<Routine> list, DateTime now, string projectTitle)
    {
        foreach (var routine in list)
        {
            if (Routines.Due(routine, now) is not { } due || !RoutineFits(routine, projectTitle)) continue;
            return RunRoutine(routine, due, now, projectTitle);
        }
        return false;
    }

    /// <summary>Runs <paramref name="routine"/> for its time <paramref name="due"/> now, as a job; false when this chat can't take it.</summary>
    public bool RunRoutine(Routine routine, DateTime due, DateTime now, string projectTitle)
    {
        if (_stopped || !IsCoordinator || Run is { Active: true }) return false; // an autopilot's chat isn't disturbed
        var catchUp = now - due > Routines.Late;
        var job = Board.Start("Routine: " + routine.Name, Routines.JobTask(routine, due, catchUp), DateTime.UtcNow, force: true, out _);
        if (job is null) return false;
        job.Routine = routine.Id;
        ChatJobs.Assign(job, null);
        Routines.Started(routine, due, DateTime.UtcNow, now, _conv.Id, ChatHistory.NameFor(_conv), job.Number, projectTitle);
        _host.Note($"Routine \"{routine.Name}\" started as job {job.Number}" +
                   (catchUp ? $", catching up the run of {due:ddd HH:mm} missed while Plaxis ProMax was closed or busy." : "."));
        JobsChanged();
        _host.Save();
        return true;
    }

    /// <summary>A job that is a routine's run ended (done, a problem, stopped, paused): its history says how.</summary>
    private void RoutineEnded(ChatJob job)
    {
        if (job.Routine.Length == 0) return;
        var state = job.OverBudget ? JobBoard.Stopped : job.State;
        var summary = (job.Summary + (JobReview.ReportLine(job) is { Length: > 0 } check ? "\n\n" + check : "")).Trim();
        var folder = RoutinesFolder;
        var chat = _conv.Id;
        var id = job.Routine;
        var number = job.Number;
        RoutineSaved = Task.Run(() =>
        {
            try
            {
                var list = Routines.Load(folder);
                if (Routines.Ended(list, id, chat, number, state, summary, DateTime.UtcNow)) Routines.Save(folder, list);
            }
            catch (Exception) { /* the history keeps "running" for that run */ }
        });
    }

    /// <summary>The last write of a run's outcome (the checks wait for it).</summary>
    internal Task RoutineSaved { get; private set; } = Task.CompletedTask;
}
