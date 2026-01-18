using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

using Weaver.Models;

namespace Weaver.Services;

public sealed class GCodeCompiler
{
    private readonly AppSettings _settings;

    public GCodeCompiler(AppSettings settings)
    {
        _settings = settings;
    }

    public CompileResult Compile(
        IEnumerable<ThreeMFJob> jobs,
        Printer printer,
        PlateChangeRoutine? routine)
    {
        var diagnostics = new List<CompileDiagnostic>();
        var sb = new StringBuilder();
        var jobList = jobs.ToList();
        var totalTime = CalculateTotalPrintTime(jobList);

        // Header
        sb.AppendLine($"; WEAVER: Total Jobs: {jobList.Count}");
        sb.AppendLine($"; WEAVER: Estimated Time: {totalTime:hh\\:mm\\:ss}");
        sb.AppendLine($"; WEAVER: Generated: {DateTime.UtcNow:O}");
        sb.AppendLine($"; WEAVER: Printer: {printer.DisplayName}");
        sb.AppendLine();

        for (int jobIndex = 0; jobIndex < jobList.Count; jobIndex++)
        {
            var job = jobList[jobIndex];
            var isLastJob = jobIndex == jobList.Count - 1;

            sb.AppendLine($"; WEAVER: Start of Job {jobIndex + 1}/{jobList.Count}: '{job.PlateName}'");
            sb.AppendLine($"; WEAVER: Print Time: {job.PrintTime:hh\\:mm\\:ss}");

            // Add filament info
            if (job.Filaments.Length > 0)
            {
                sb.AppendLine($"; WEAVER: Filaments: {string.Join(", ", job.Filaments.Select(f => f.FilamentKind))}");
            }
            sb.AppendLine();

            // Validate before mutation
            ValidateGCode(job.EmbeddedGCode, printer, job.PlateName, diagnostics);
            ValidateJobCompatibility(job, printer, diagnostics);

            // Inject plate swap routine before print finish marker (if not last job)
            GCodeRoutine compiled;

            // Use the routine parameter if available, otherwise use the job's routine
            var activeRoutine = routine ?? job.Routine;

            if (!isLastJob && activeRoutine != null)
            {
                compiled = job.EmbeddedGCode.InsertBefore(
                    printer.PrintFinishedPredicate,
                    activeRoutine.GCode
                );
                diagnostics.Add(new CompileDiagnostic(
                    DiagnosticSeverity.Info,
                    $"Injected plate change routine '{activeRoutine.Name}' for job '{job.PlateName}'"
                ));
            }
            else
            {
                compiled = job.EmbeddedGCode;
                if (!isLastJob && activeRoutine == null)
                {
                    diagnostics.Add(new CompileDiagnostic(
                        DiagnosticSeverity.Error,
                        $"No plate change routine specified for job '{job.PlateName}' (not last job)"
                    ));

                    throw new Exception();
                }
            }

            sb.AppendLine(compiled.ToContentString());
            sb.AppendLine();
            sb.AppendLine($"; WEAVER: End of Job {jobIndex + 1}/{jobList.Count}: '{job.PlateName}'");
            sb.AppendLine();
        }

        // Footer
        sb.AppendLine("; WEAVER: Compilation Complete");

        return new CompileResult(
            sb.ToString(),
            totalTime,
            diagnostics
        );
    }

    // ---------- Validation ----------

    private static void ValidateGCode(
        GCodeRoutine routine,
        Printer printer,
        string jobName,
        List<CompileDiagnostic> diagnostics)
    {
        bool foundFinishMarker = false;
        bool inPrintingPhase = false;
        double currentZ = 0;
        const double FIRST_LAYER_THRESHOLD = 1.0; // Z height below which we consider it early printing

        for (int i = 0; i < routine.Lines.Count; i++)
        {
            var line = routine.Lines[i].Trim();

            // Check for finish marker (including comments)
            if (printer.PrintFinishedPredicate(line))
                foundFinishMarker = true;

            // Skip validation for empty lines and comments
            if (string.IsNullOrWhiteSpace(line) || line.StartsWith(";"))
                continue;

            // Track Z position and determine if we're in printing phase
            if (line.StartsWith("G0 ", StringComparison.OrdinalIgnoreCase) ||
                line.StartsWith("G1 ", StringComparison.OrdinalIgnoreCase))
            {
                var z = ExtractAxisValue(line, 'Z');
                if (z.HasValue)
                {
                    currentZ = z.Value;
                }

                // Heuristic: We're in printing phase if Z is low and we have extrusion (E parameter)
                if (!inPrintingPhase && currentZ > 0 && currentZ < FIRST_LAYER_THRESHOLD)
                {
                    var e = ExtractAxisValue(line, 'E');
                    if (e.HasValue)
                    {
                        inPrintingPhase = true;
                    }
                }
            }

            // Validate nozzle temperature (M104 or M109)
            if (line.StartsWith("M104", StringComparison.OrdinalIgnoreCase) ||
                line.StartsWith("M109", StringComparison.OrdinalIgnoreCase))
            {
                var temp = ExtractTemperature(line);
                if (temp.HasValue && temp.Value > printer.MaxNozzleTemp)
                {
                    diagnostics.Add(new CompileDiagnostic(
                        DiagnosticSeverity.Warning,
                        $"[{jobName}] Nozzle temp {temp.Value}°C exceeds max {printer.MaxNozzleTemp}°C",
                        LineNumber: i + 1
                    ));
                }
            }

            // Validate bed temperature (M140 or M190)
            if (line.StartsWith("M140", StringComparison.OrdinalIgnoreCase) ||
                line.StartsWith("M190", StringComparison.OrdinalIgnoreCase))
            {
                var temp = ExtractTemperature(line);
                if (temp.HasValue && temp.Value > printer.MaxBedTemp)
                {
                    diagnostics.Add(new CompileDiagnostic(
                        DiagnosticSeverity.Warning,
                        $"[{jobName}] Bed temp {temp.Value}°C exceeds max {printer.MaxBedTemp}°C",
                        LineNumber: i + 1
                    ));
                }
            }

            // Validate bed boundaries (G0/G1 moves)
            if (line.StartsWith("G0 ", StringComparison.OrdinalIgnoreCase) ||
                line.StartsWith("G1 ", StringComparison.OrdinalIgnoreCase))
            {
                ValidateMoveCommand(line, printer, jobName, i + 1, inPrintingPhase, diagnostics);
            }
        }

        if (!foundFinishMarker)
        {
            diagnostics.Add(new CompileDiagnostic(
                DiagnosticSeverity.Error,
                $"[{jobName}] Print-finished marker '{printer.PrintFinishedMarker}' not found"
            ));
        }
    }

    private static void ValidateJobCompatibility(
        ThreeMFJob job,
        Printer printer,
        List<CompileDiagnostic> diagnostics)
    {
        // Check if routine matches printer model
        if (job.Routine != null && job.Routine.Model != printer.Model)
        {
            diagnostics.Add(new CompileDiagnostic(
                DiagnosticSeverity.Warning,
                $"[{job.PlateName}] Plate change routine '{job.Routine.Name}' is for {job.Routine.Model}, but printer is {printer.Model}"
            ));
        }

        // Check if printer has AMS and job uses multiple filaments
        if (!printer.HasAMS && job.Filaments.Length > 1)
        {
            diagnostics.Add(new CompileDiagnostic(
                DiagnosticSeverity.Warning,
                $"[{job.PlateName}] Job uses {job.Filaments.Length} filaments but printer has no AMS"
            ));
        }
    }

    private static void ValidateMoveCommand(
        string line,
        Printer printer,
        string jobName,
        int lineNumber,
        bool inPrintingPhase,
        List<CompileDiagnostic> diagnostics)
    {
        var x = ExtractAxisValue(line, 'X');
        var y = ExtractAxisValue(line, 'Y');
        var z = ExtractAxisValue(line, 'Z');

        // Use printer's embedded extended bed size
        var extendedBounds = printer.ExtendedBed;

        // First check: ensure coordinates are within extended bounds (including parking positions)
        if (!extendedBounds.Contains(x, y, z))
        {
            if (x.HasValue && (x.Value < extendedBounds.MinX || x.Value > extendedBounds.MaxX))
            {
                diagnostics.Add(new CompileDiagnostic(
                    DiagnosticSeverity.Error,
                    $"[{jobName}] X position {x.Value:F2} is outside safe bounds ({extendedBounds.MinX:F0} to {extendedBounds.MaxX:F0})",
                    LineNumber: lineNumber
                ));
            }
            if (y.HasValue && (y.Value < extendedBounds.MinY || y.Value > extendedBounds.MaxY))
            {
                diagnostics.Add(new CompileDiagnostic(
                    DiagnosticSeverity.Error,
                    $"[{jobName}] Y position {y.Value:F2} is outside safe bounds ({extendedBounds.MinY:F0} to {extendedBounds.MaxY:F0})",
                    LineNumber: lineNumber
                ));
            }
            if (z.HasValue && (z.Value < extendedBounds.MinZ || z.Value > extendedBounds.MaxZ))
            {
                diagnostics.Add(new CompileDiagnostic(
                    DiagnosticSeverity.Error,
                    $"[{jobName}] Z position {z.Value:F2} is outside safe bounds ({extendedBounds.MinZ:F0} to {extendedBounds.MaxZ:F0})",
                    LineNumber: lineNumber
                ));
            }
        }

        // Second check: if in printing phase, ensure coordinates are within actual printable bed
        if (inPrintingPhase && !printer.PrintableBedSize.Contains(x, y, z))
        {
            if (x.HasValue && (x.Value < 0 || x.Value > printer.PrintableBedSize.Width))
            {
                diagnostics.Add(new CompileDiagnostic(
                    DiagnosticSeverity.Warning,
                    $"[{jobName}] Print move X position {x.Value:F2} outside printable bed width (0-{printer.PrintableBedSize.Width})",
                    LineNumber: lineNumber
                ));
            }
            if (y.HasValue && (y.Value < 0 || y.Value > printer.PrintableBedSize.Length))
            {
                diagnostics.Add(new CompileDiagnostic(
                    DiagnosticSeverity.Warning,
                    $"[{jobName}] Print move Y position {y.Value:F2} outside printable bed length (0-{printer.PrintableBedSize.Length})",
                    LineNumber: lineNumber
                ));
            }
            if (z.HasValue && (z.Value < 0 || z.Value > printer.PrintableBedSize.Height))
            {
                diagnostics.Add(new CompileDiagnostic(
                    DiagnosticSeverity.Warning,
                    $"[{jobName}] Print move Z position {z.Value:F2} outside printable bed height (0-{printer.PrintableBedSize.Height})",
                    LineNumber: lineNumber
                ));
            }
        }
    }

    // ---------- Helpers ----------

    private static double? ExtractTemperature(string line)
    {
        var sIndex = line.IndexOf('S');
        if (sIndex < 0) return null;

        var valueStr = new string(
            line.Skip(sIndex + 1)
                .TakeWhile(c => char.IsDigit(c) || c == '.')
                .ToArray()
        );

        return double.TryParse(valueStr, out var temp) ? temp : null;
    }

    private static double? ExtractAxisValue(string line, char axis)
    {
        var axisIndex = line.IndexOf(axis);
        if (axisIndex < 0) return null;

        var valueStr = new string(
            line.Skip(axisIndex + 1)
                .TakeWhile(c => char.IsDigit(c) || c == '.' || c == '-')
                .ToArray()
        );

        return double.TryParse(valueStr, out var value) ? value : null;
    }

    private static TimeSpan CalculateTotalPrintTime(
        IEnumerable<ThreeMFJob> jobs) =>
        jobs.Aggregate(
            TimeSpan.Zero,
            (acc, j) => acc + j.PrintTime
        );
}

// ---------- Supporting types ----------

public sealed record CompileResult(
    string Output,
    TimeSpan TotalPrintTime,
    IReadOnlyList<CompileDiagnostic> Diagnostics)
{
    public bool HasErrors =>
        Diagnostics.Any(d => d.Severity == DiagnosticSeverity.Error);

    public bool HasWarnings =>
        Diagnostics.Any(d => d.Severity == DiagnosticSeverity.Warning);

    public IEnumerable<CompileDiagnostic> Errors =>
        Diagnostics.Where(d => d.Severity == DiagnosticSeverity.Error);

    public IEnumerable<CompileDiagnostic> Warnings =>
        Diagnostics.Where(d => d.Severity == DiagnosticSeverity.Warning);
}

public enum DiagnosticSeverity
{
    Info,
    Warning,
    Error
}

public sealed record CompileDiagnostic(
    DiagnosticSeverity Severity,
    string Message,
    int? LineNumber = null)
{
    public override string ToString() =>
        LineNumber.HasValue
            ? $"[{Severity}] Line {LineNumber}: {Message}"
            : $"[{Severity}] {Message}";
}
