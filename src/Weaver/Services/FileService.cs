using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Platform.Storage;
using Weaver.Models;

namespace Weaver.Services;

public sealed record FileLoadResult(
    ThreeMFJob[] Jobs,
    IReadOnlyList<FileLoadDiagnostic> Diagnostics)
{
    public bool HasErrors =>
        Diagnostics.Any(d => d.Severity == FileLoadSeverity.Error);
    public bool HasWarnings =>
        Diagnostics.Any(d => d.Severity == FileLoadSeverity.Warning);
    public bool IsSuccess => Jobs.Length > 0 && !HasErrors;
    public IEnumerable<FileLoadDiagnostic> Errors =>
        Diagnostics.Where(d => d.Severity == FileLoadSeverity.Error);
    public IEnumerable<FileLoadDiagnostic> Warnings =>
        Diagnostics.Where(d => d.Severity == FileLoadSeverity.Warning);
}

public enum FileLoadSeverity
{
    Info,
    Warning,
    Error
}

public sealed record FileLoadDiagnostic(
    FileLoadSeverity Severity,
    string FileName,
    string Message)
{
    public override string ToString() => $"[{Severity}] {FileName}: {Message}";
}

public interface IFileService
{
    Task<FileLoadResult?> LoadFilesAsync(Window parentWindow);
    Task<FileLoadResult?> LoadFileFromPathAsync(string path);
    Task<bool> Save3MFAsync(Window parentWindow, byte[] content, string suggestedFileName);
}

public sealed class FileService : IFileService
{
    private readonly ThreeMFExtractor _extractor;
    private readonly GCodeParser _parser;

    public FileService(ThreeMFExtractor extractor, GCodeParser parser)
    {
        _extractor = extractor;
        _parser = parser;
    }

    public async Task<FileLoadResult?> LoadFilesAsync(Window parentWindow)
    {
        var storageProvider = parentWindow.StorageProvider;
        if (!storageProvider.CanOpen)
            return null;

        var options = new FilePickerOpenOptions
        {
            Title = "Select 3MF or G-code Files",
            AllowMultiple = true,
            FileTypeFilter = new[]
            {
                new FilePickerFileType("3D Print Files")
                {
                    Patterns = new[] { "*.3mf", "*.gcode" }
                },
                FilePickerFileTypes.All
            }
        };

        var files = await storageProvider.OpenFilePickerAsync(options);
        if (files == null || files.Count == 0)
            return null;

        return await ProcessFilesAsync(files);
    }

    public async Task<FileLoadResult?> LoadFileFromPathAsync(string path)
    {
        if (!File.Exists(path))
        {
            return new FileLoadResult(
                Array.Empty<ThreeMFJob>(),
                new[] { new FileLoadDiagnostic(FileLoadSeverity.Error, Path.GetFileName(path), "File not found") }
            );
        }

        var fileName = Path.GetFileName(path);
        var extension = Path.GetExtension(fileName).ToLowerInvariant();
        var diagnostics = new List<FileLoadDiagnostic>();
        var jobs = new List<ThreeMFJob>();

        try
        {
            switch (extension)
            {
                case ".3mf":
                    using (var stream = File.OpenRead(path))
                    {
                        var result = await _extractor.ExtractJobs(stream);

                        foreach (var diag in result.Diagnostics)
                        {
                            diagnostics.Add(new FileLoadDiagnostic(
                                diag.Severity switch
                                {
                                    ExtractionSeverity.Error => FileLoadSeverity.Error,
                                    ExtractionSeverity.Warning => FileLoadSeverity.Warning,
                                    _ => FileLoadSeverity.Info
                                },
                                fileName,
                                diag.Message
                            ));
                        }

                        if (result.IsSuccess)
                            jobs.AddRange(result.Jobs);
                    }
                    break;

                case ".gcode":
                    var content = await File.ReadAllTextAsync(path);
                    var job = ProcessGCodeContent(content, fileName, diagnostics);
                    if (job != null)
                        jobs.Add(job);
                    break;

                default:
                    diagnostics.Add(new FileLoadDiagnostic(
                        FileLoadSeverity.Warning,
                        fileName,
                        $"Unsupported file type: {extension}"
                    ));
                    break;
            }
        }
        catch (Exception ex)
        {
            diagnostics.Add(new FileLoadDiagnostic(
                FileLoadSeverity.Error,
                fileName,
                $"Failed to load file: {ex.Message}"
            ));
        }

        return new FileLoadResult(jobs.ToArray(), diagnostics);
    }

    public async Task<bool> Save3MFAsync(
        Window parentWindow,
        byte[] content,
        string suggestedFileName)
    {
        var storageProvider = parentWindow.StorageProvider;
        if (!storageProvider.CanSave)
            return false;

        var options = new FilePickerSaveOptions
        {
            Title = "Save 3MF File",
            SuggestedFileName = suggestedFileName,
            FileTypeChoices = new[]
            {
                new FilePickerFileType("3MF File") { Patterns = new[] { "*.3mf" } },
                FilePickerFileTypes.All
            },
            ShowOverwritePrompt = true
        };

        var file = await storageProvider.SaveFilePickerAsync(options);
        if (file == null)
            return false;

        try
        {
            await File.WriteAllBytesAsync(file.Path.LocalPath, content);
            return true;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Failed to save 3MF: {ex.Message}");
            return false;
        }
    }

    private async Task<FileLoadResult> ProcessFilesAsync(IReadOnlyList<IStorageFile> files)
    {
        var jobs = new List<ThreeMFJob>();
        var diagnostics = new List<FileLoadDiagnostic>();

        foreach (var file in files)
        {
            var fileName = file.Name;
            var extension = Path.GetExtension(fileName).ToLowerInvariant();

            try
            {
                switch (extension)
                {
                    case ".3mf":
                        await using (var stream = await file.OpenReadAsync())
                        {
                            var result = await _extractor.ExtractJobs(stream);

                            foreach (var diag in result.Diagnostics)
                            {
                                diagnostics.Add(new FileLoadDiagnostic(
                                    diag.Severity switch
                                    {
                                        ExtractionSeverity.Error => FileLoadSeverity.Error,
                                        ExtractionSeverity.Warning => FileLoadSeverity.Warning,
                                        _ => FileLoadSeverity.Info
                                    },
                                    fileName,
                                    diag.Message
                                ));
                            }

                            if (result.IsSuccess)
                            {
                                jobs.AddRange(result.Jobs);
                                diagnostics.Add(new FileLoadDiagnostic(
                                    FileLoadSeverity.Info,
                                    fileName,
                                    $"Successfully loaded {result.Jobs.Length} job(s)"
                                ));
                            }
                        }
                        break;

                    case ".gcode":
                        await using (var stream = await file.OpenReadAsync())
                        using (var reader = new StreamReader(stream))
                        {
                            var content = await reader.ReadToEndAsync();
                            var job = ProcessGCodeContent(content, fileName, diagnostics);
                            if (job != null)
                            {
                                jobs.Add(job);
                                diagnostics.Add(new FileLoadDiagnostic(
                                    FileLoadSeverity.Info,
                                    fileName,
                                    "Successfully loaded G-code file"
                                ));
                            }
                        }
                        break;

                    default:
                        diagnostics.Add(new FileLoadDiagnostic(
                            FileLoadSeverity.Warning,
                            fileName,
                            $"Unsupported file type: {extension}"
                        ));
                        break;
                }
            }
            catch (Exception ex)
            {
                diagnostics.Add(new FileLoadDiagnostic(
                    FileLoadSeverity.Error,
                    fileName,
                    $"Failed to load file: {ex.Message}"
                ));
            }
        }

        return new FileLoadResult(jobs.ToArray(), diagnostics);
    }

    private ThreeMFJob? ProcessGCodeContent(
        string content,
        string fileName,
        List<FileLoadDiagnostic> diagnostics)
    {
        if (!_parser.ValidateGCodeFile(content))
        {
            diagnostics.Add(new FileLoadDiagnostic(
                FileLoadSeverity.Error,
                fileName,
                "File does not contain valid G-code metadata"
            ));
            return null;
        }

        var parseResult = _parser.ParseGCodeFile(content, fileName);

        foreach (var diag in parseResult.Diagnostics)
        {
            diagnostics.Add(new FileLoadDiagnostic(
                diag.Severity switch
                {
                    ParseDiagnosticSeverity.Error => FileLoadSeverity.Error,
                    ParseDiagnosticSeverity.Warning => FileLoadSeverity.Warning,
                    _ => FileLoadSeverity.Info
                },
                fileName,
                diag.Message
            ));
        }

        if (parseResult.HasErrors)
            return null;

        var metadata = parseResult.Metadata;

        return new ThreeMFJob(
            PlateName: metadata.PlateName,
            Filaments: metadata.Filaments,
            EmbeddedGCode: metadata.GCode,
            Printer: DeterminePrinterFromMetadata(metadata.PrinterModel),
            Routine: null,
            PrintTime: metadata.PrintTime,
            ModelImage: metadata.ModelImage,
            Source3MFFile: null  // Standalone G-code has no source 3MF
        );
    }

    private static Printer DeterminePrinterFromMetadata(PrinterModel model)
    {
        return model switch
        {
            PrinterModel.A1M => Printers.A1M,
            PrinterModel.A1 => Printers.A1,
            _ => Printers.A1M
        };
    }
}
