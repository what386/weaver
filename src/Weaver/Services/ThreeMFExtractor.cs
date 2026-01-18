using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Weaver.Models;

namespace Weaver.Services;

public sealed record GCodeEntry(
    string Name,
    string Content
);

public sealed record ExtractionResult(
    ThreeMFJob[] Jobs,
    IReadOnlyList<ExtractionDiagnostic> Diagnostics)
{
    public bool HasErrors =>
        Diagnostics.Any(d => d.Severity == ExtractionSeverity.Error);
    public bool HasWarnings =>
        Diagnostics.Any(d => d.Severity == ExtractionSeverity.Warning);
    public bool IsSuccess => Jobs.Length > 0 && !HasErrors;
}

public enum ExtractionSeverity
{
    Info,
    Warning,
    Error
}

public sealed record ExtractionDiagnostic(
    ExtractionSeverity Severity,
    string Message)
{
    public override string ToString() => $"[{Severity}] {Message}";
}

public sealed class ThreeMFExtractor
{
    private readonly GCodeParser _parser;

    public ThreeMFExtractor(GCodeParser parser)
    {
        _parser = parser;
    }

    /// <summary>
    /// Extracts all jobs from a 3MF file and parses their metadata.
    /// Stores the original 3MF file bytes for later modification.
    /// </summary>
    public async Task<ExtractionResult> ExtractJobs(Stream stream)
    {
        var diagnostics = new List<ExtractionDiagnostic>();
        var jobs = new List<ThreeMFJob>();

        // Read entire 3MF into memory
        byte[] source3MFData;
        using (var ms = new MemoryStream())
        {
            await stream.CopyToAsync(ms);
            source3MFData = ms.ToArray();
        }

        try
        {
            using var archiveStream = new MemoryStream(source3MFData);
            using var archive = new ZipArchive(archiveStream, ZipArchiveMode.Read);

            // Find all G-code files
            var gcodeEntries = archive.Entries
                .Where(e => e.Name.EndsWith(".gcode", StringComparison.OrdinalIgnoreCase))
                .ToList();

            if (gcodeEntries.Count == 0)
            {
                diagnostics.Add(new ExtractionDiagnostic(
                    ExtractionSeverity.Error,
                    "No G-code files found in 3MF archive"
                ));
                return new ExtractionResult(Array.Empty<ThreeMFJob>(), diagnostics);
            }

            // Extract printer information
            var printer = await ExtractPrinterInfo(archive, diagnostics);

            // Process each G-code file (typically just one: plate_1.gcode)
            foreach (var entry in gcodeEntries)
            {
                try
                {
                    var job = await ExtractJob(archive, entry, printer, source3MFData, diagnostics);
                    if (job != null)
                        jobs.Add(job);
                }
                catch (Exception ex)
                {
                    diagnostics.Add(new ExtractionDiagnostic(
                        ExtractionSeverity.Error,
                        $"Failed to extract job from '{entry.Name}': {ex.Message}"
                    ));
                }
            }

            if (jobs.Count == 0)
            {
                diagnostics.Add(new ExtractionDiagnostic(
                    ExtractionSeverity.Error,
                    "No valid jobs could be extracted from 3MF file"
                ));
            }
        }
        catch (Exception ex)
        {
            diagnostics.Add(new ExtractionDiagnostic(
                ExtractionSeverity.Error,
                $"Failed to read 3MF archive: {ex.Message}"
            ));
        }

        return new ExtractionResult(jobs.ToArray(), diagnostics);
    }

    /// <summary>
    /// Extracts jobs from a file path.
    /// </summary>
    public async Task<ExtractionResult> ExtractFromFile(string path)
    {
        if (!File.Exists(path))
        {
            return new ExtractionResult(
                Array.Empty<ThreeMFJob>(),
                new[] { new ExtractionDiagnostic(ExtractionSeverity.Error, $"File not found: {path}") }
            );
        }

        using var fs = File.OpenRead(path);
        return await ExtractJobs(fs);
    }

    private async Task<ThreeMFJob?> ExtractJob(
        ZipArchive archive,
        ZipArchiveEntry gcodeEntry,
        Printer printer,
        byte[] source3MFData,
        List<ExtractionDiagnostic> diagnostics)
    {
        // Read G-code content
        string gcodeContent;
        using (var reader = new StreamReader(gcodeEntry.Open(), Encoding.UTF8))
        {
            gcodeContent = await reader.ReadToEndAsync();
        }

        // Parse G-code to extract metadata
        var fileName = Path.GetFileNameWithoutExtension(gcodeEntry.Name);
        var parseResult = _parser.ParseGCodeFile(gcodeContent, fileName);

        // Add parse diagnostics
        foreach (var diag in parseResult.Diagnostics)
        {
            diagnostics.Add(new ExtractionDiagnostic(
                diag.Severity switch
                {
                    ParseDiagnosticSeverity.Error => ExtractionSeverity.Error,
                    ParseDiagnosticSeverity.Warning => ExtractionSeverity.Warning,
                    _ => ExtractionSeverity.Info
                },
                $"[{fileName}] {diag.Message}"
            ));
        }

        if (parseResult.HasErrors)
        {
            diagnostics.Add(new ExtractionDiagnostic(
                ExtractionSeverity.Warning,
                $"Skipping job '{fileName}' due to parse errors"
            ));
            return null;
        }

        var metadata = parseResult.Metadata;

        // Extract thumbnail
        var thumbnail = await ExtractThumbnail(archive, gcodeEntry.Name);

        // Determine plate change routine
        var routine = DeterminePlateChangeRoutine(printer.Model, diagnostics);

        return new ThreeMFJob(
            PlateName: metadata.PlateName,
            Filaments: metadata.Filaments,
            EmbeddedGCode: metadata.GCode,
            Printer: printer,
            Routine: routine,
            PrintTime: metadata.PrintTime,
            ModelImage: thumbnail ?? metadata.ModelImage,
            Source3MFFile: source3MFData  // Store the original 3MF for later use
        );
    }

    private async Task<Printer> ExtractPrinterInfo(
        ZipArchive archive,
        List<ExtractionDiagnostic> diagnostics)
    {
        // Try to extract from G-code metadata first
        var gcodeEntry = archive.Entries
            .FirstOrDefault(e => e.Name.EndsWith(".gcode", StringComparison.OrdinalIgnoreCase));

        if (gcodeEntry != null)
        {
            using var reader = new StreamReader(gcodeEntry.Open(), Encoding.UTF8);
            var content = await reader.ReadToEndAsync();
            var lines = content.Split('\n');

            var modelLine = lines.FirstOrDefault(l => l.StartsWith("; printer_model =", StringComparison.OrdinalIgnoreCase));
            if (modelLine != null)
            {
                if (modelLine.Contains("A1 Mini", StringComparison.OrdinalIgnoreCase))
                    return Printers.A1M;
                if (modelLine.Contains("A1", StringComparison.OrdinalIgnoreCase))
                    return Printers.A1;
            }
        }

        diagnostics.Add(new ExtractionDiagnostic(
            ExtractionSeverity.Info,
            "Printer model not found in metadata, defaulting to A1 Mini"
        ));
        return Printers.A1M;
    }

    private async Task<string?> ExtractThumbnail(ZipArchive archive, string gcodeFileName)
    {
        var baseName = Path.GetFileNameWithoutExtension(gcodeFileName);
        var thumbnailName = $"{baseName}.png";

        var thumbnailEntry = archive.Entries
            .FirstOrDefault(e =>
                e.FullName.Contains("Metadata/") &&
                e.Name.Equals(thumbnailName, StringComparison.OrdinalIgnoreCase));

        if (thumbnailEntry == null)
            return null;

        try
        {
            using var stream = thumbnailEntry.Open();
            using var ms = new MemoryStream();
            await stream.CopyToAsync(ms);
            var bytes = ms.ToArray();
            return Convert.ToBase64String(bytes);
        }
        catch
        {
            return null;
        }
    }

    private PlateChangeRoutine? DeterminePlateChangeRoutine(
        PrinterModel model,
        List<ExtractionDiagnostic> diagnostics)
    {
        var routine = model switch
        {
            PrinterModel.A1M => new PlateChangeRoutine(
                Name: "A1 Mini - Default",
                Description: "Default plate change routine",
                Model: PrinterModel.A1M,
                GCode: PlateChangeRoutines.A1M_SwapMod
            ),
            PrinterModel.A1 => null,
            _ => null
        };

        if (routine == null && model == PrinterModel.A1M)
        {
            diagnostics.Add(new ExtractionDiagnostic(
                ExtractionSeverity.Info,
                "No plate change routine assigned. Multi-plate jobs will require manual configuration."
            ));
        }

        return routine;
    }
}
