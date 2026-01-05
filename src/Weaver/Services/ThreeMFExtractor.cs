using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using System.Xml.Linq;
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
    /// </summary>
    public async Task<ExtractionResult> ExtractJobs(Stream stream)
    {
        var diagnostics = new List<ExtractionDiagnostic>();
        var jobs = new List<ThreeMFJob>();

        try
        {
            using var archive = new ZipArchive(stream, ZipArchiveMode.Read, leaveOpen: true);

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

            // Extract printer information from metadata
            var printer = await ExtractPrinterInfo(archive, diagnostics);

            // Process each G-code file
            foreach (var entry in gcodeEntries)
            {
                try
                {
                    var job = await ExtractJob(archive, entry, printer, diagnostics);
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
    /// Extracts G-code content only (legacy method).
    /// </summary>
    public async Task<IReadOnlyList<GCodeEntry>> ExtractGCodeFiles(Stream stream)
    {
        using var archive = new ZipArchive(stream, ZipArchiveMode.Read, leaveOpen: true);

        var gcodeFiles = archive.Entries
            .Where(e => e.Name.EndsWith(".gcode", StringComparison.OrdinalIgnoreCase))
            .ToList();

        if (gcodeFiles.Count == 0)
            throw new InvalidOperationException("No G-code files found in 3MF archive");

        var result = new List<GCodeEntry>();

        foreach (var entry in gcodeFiles)
        {
            using var reader = new StreamReader(entry.Open(), Encoding.UTF8);
            var content = await reader.ReadToEndAsync();

            result.Add(new GCodeEntry(
                Name: Path.GetFileNameWithoutExtension(entry.Name),
                Content: content
            ));
        }

        return result;
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

    /// <summary>
    /// Extracts jobs from byte array.
    /// </summary>
    public async Task<ExtractionResult> ExtractFromBytes(byte[] data)
    {
        using var ms = new MemoryStream(data);
        return await ExtractJobs(ms);
    }

    // ---------- Private extraction methods ----------

    private async Task<ThreeMFJob?> ExtractJob(
        ZipArchive archive,
        ZipArchiveEntry gcodeEntry,
        Printer printer,
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

        // Extract thumbnail for this plate
        var thumbnail = await ExtractThumbnail(archive, gcodeEntry.Name);

        // Determine appropriate plate change routine
        var routine = DeterminePlateChangeRoutine(printer.Model, diagnostics);

        return new ThreeMFJob(
            PlateName: metadata.PlateName,
            Filaments: metadata.Filaments,
            EmbeddedGCode: metadata.GCode,
            Printer: printer,
            Routine: routine,
            PrintTime: metadata.PrintTime,
            ModelImage: thumbnail ?? metadata.ModelImage
        );
    }

    private async Task<Printer> ExtractPrinterInfo(
        ZipArchive archive,
        List<ExtractionDiagnostic> diagnostics)
    {
        // Try to extract printer info from project settings
        var projectSettingsEntry = archive.Entries
            .FirstOrDefault(e => e.FullName.Contains("project_settings.config"));

        if (projectSettingsEntry != null)
        {
            try
            {
                using var reader = new StreamReader(projectSettingsEntry.Open(), Encoding.UTF8);
                var json = await reader.ReadToEndAsync();
                var doc = JsonDocument.Parse(json);

                if (doc.RootElement.TryGetProperty("project", out var project))
                {
                    if (project.TryGetProperty("printer_model", out var modelProp))
                    {
                        var modelStr = modelProp.GetString();
                        if (Enum.TryParse<PrinterModel>(modelStr, true, out var model))
                        {
                            return model switch
                            {
                                PrinterModel.A1M => Printers.A1M,
                                PrinterModel.A1 => Printers.A1,
                                _ => Printers.A1M
                            };
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                diagnostics.Add(new ExtractionDiagnostic(
                    ExtractionSeverity.Warning,
                    $"Failed to parse project settings: {ex.Message}"
                ));
            }
        }

        // Try to extract from 3D model metadata
        var modelEntry = archive.Entries
            .FirstOrDefault(e => e.FullName.EndsWith("3dmodel.model"));

        if (modelEntry != null)
        {
            try
            {
                using var reader = new StreamReader(modelEntry.Open(), Encoding.UTF8);
                var xml = await reader.ReadToEndAsync();
                var doc = XDocument.Parse(xml);

                var printerMeta = doc.Descendants()
                    .FirstOrDefault(e =>
                        e.Name.LocalName == "metadata" &&
                        e.Attribute("name")?.Value.Contains("Printer") == true);

                if (printerMeta != null)
                {
                    var printerName = printerMeta.Value;

                    if (printerName.Contains("A1 Mini", StringComparison.OrdinalIgnoreCase))
                        return Printers.A1M;
                    if (printerName.Contains("A1", StringComparison.OrdinalIgnoreCase))
                        return Printers.A1;
                }
            }
            catch (Exception ex)
            {
                diagnostics.Add(new ExtractionDiagnostic(
                    ExtractionSeverity.Warning,
                    $"Failed to parse 3D model metadata: {ex.Message}"
                ));
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
        // Determine thumbnail filename from G-code filename
        // e.g., "plate_1.gcode" -> "plate_1.png"
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
        // Default to the most common plate change routine for each printer
        var routine = model switch
        {
            PrinterModel.A1M => new PlateChangeRoutine(
                Name: "A1 Mini - Default",
                Description: "Default plate change routine",
                Model: PrinterModel.A1M,
                GCode: PlateChangeRoutines.A1M_SwapMod
            ),
            PrinterModel.A1 => null, // A1 doesn't have automatic plate changing by default
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

