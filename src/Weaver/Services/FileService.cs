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

public static class WeaverFileTypes
{
    public static FilePickerFileType PrintFiles => new("3D Print Files")
    {
        Patterns = new[] { "*.3mf", "*.gcode" },
        MimeTypes = new[] { "application/vnd.ms-package.3dmanufacturing-3dmodel+xml", "text/x.gcode" }
    };

    public static FilePickerFileType ThreeMF => new("3MF Files")
    {
        Patterns = new[] { "*.3mf" },
        MimeTypes = new[] { "application/vnd.ms-package.3dmanufacturing-3dmodel+xml" }
    };

    public static FilePickerFileType GCode => new("G-code Files")
    {
        Patterns = new[] { "*.gcode" },
        MimeTypes = new[] { "text/x.gcode" }
    };
}

public interface IFileService
{
    Task<FileLoadResult?> LoadFilesAsync(Window parentWindow);
    Task<bool> SaveGCodeAsync(Window parentWindow, string content, string suggestedFileName);
    Task<bool> Save3MFAsync(Window parentWindow, byte[] content, string suggestedFileName);
    Task<string?> PickSaveLocationAsync(Window parentWindow, string suggestedFileName, FilePickerFileType fileType);
    Task<IReadOnlyList<IStorageFile>?> PickFilesAsync(Window parentWindow, FilePickerOpenOptions options);
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

    /// <summary>
    /// Opens a file picker and loads selected .3mf or .gcode files.
    /// </summary>
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
                    Patterns = new[] { "*.3mf", "*.gcode" },
                    MimeTypes = new[] { "application/vnd.ms-package.3dmanufacturing-3dmodel+xml", "text/x.gcode" }
                },
                new FilePickerFileType("3MF Files")
                {
                    Patterns = new[] { "*.3mf" },
                    MimeTypes = new[] { "application/vnd.ms-package.3dmanufacturing-3dmodel+xml" }
                },
                new FilePickerFileType("G-code Files")
                {
                    Patterns = new[] { "*.gcode" },
                    MimeTypes = new[] { "text/x.gcode" }
                },
                FilePickerFileTypes.All
            }
        };

        var files = await storageProvider.OpenFilePickerAsync(options);

        if (files == null || files.Count == 0)
            return null;

        return await ProcessFilesAsync(files);
    }

    /// <summary>
    /// Opens a save dialog for G-code files.
    /// </summary>
    public async Task<bool> SaveGCodeAsync(
        Window parentWindow,
        string content,
        string suggestedFileName)
    {
        var location = await PickSaveLocationAsync(
            parentWindow,
            suggestedFileName,
            new FilePickerFileType("G-code File")
            {
                Patterns = new[] { "*.gcode" },
                MimeTypes = new[] { "text/x.gcode" }
            }
        );

        if (location == null)
            return false;

        try
        {
            await File.WriteAllTextAsync(location, content);
            return true;
        }
        catch (Exception ex)
        {
            // Log error or show dialog
            Console.WriteLine($"Failed to save G-code: {ex.Message}");
            return false;
        }
    }

    /// <summary>
    /// Opens a save dialog for 3MF files.
    /// </summary>
    public async Task<bool> Save3MFAsync(
        Window parentWindow,
        byte[] content,
        string suggestedFileName)
    {
        var location = await PickSaveLocationAsync(
            parentWindow,
            suggestedFileName,
            new FilePickerFileType("3MF File")
            {
                Patterns = new[] { "*.3mf" },
                MimeTypes = new[] { "application/vnd.ms-package.3dmanufacturing-3dmodel+xml" }
            }
        );

        if (location == null)
            return false;

        try
        {
            await File.WriteAllBytesAsync(location, content);
            return true;
        }
        catch (Exception ex)
        {
            // Log error or show dialog
            Console.WriteLine($"Failed to save 3MF: {ex.Message}");
            return false;
        }
    }

    /// <summary>
    /// Opens a save file picker and returns the selected path.
    /// </summary>
    public async Task<string?> PickSaveLocationAsync(
        Window parentWindow,
        string suggestedFileName,
        FilePickerFileType fileType)
    {
        var storageProvider = parentWindow.StorageProvider;

        if (!storageProvider.CanSave)
            return null;

        var options = new FilePickerSaveOptions
        {
            Title = "Save File",
            SuggestedFileName = suggestedFileName,
            FileTypeChoices = new[] { fileType, FilePickerFileTypes.All },
            ShowOverwritePrompt = true
        };

        var file = await storageProvider.SaveFilePickerAsync(options);

        return file?.Path.LocalPath;
    }

    /// <summary>
    /// Opens a file picker with custom options.
    /// </summary>
    public async Task<IReadOnlyList<IStorageFile>?> PickFilesAsync(
        Window parentWindow,
        FilePickerOpenOptions options)
    {
        var storageProvider = parentWindow.StorageProvider;

        if (!storageProvider.CanOpen)
            return null;

        return await storageProvider.OpenFilePickerAsync(options);
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
                        await Process3MFFileAsync(file, jobs, diagnostics);
                        break;

                    case ".gcode":
                        await ProcessGCodeFileAsync(file, jobs, diagnostics);
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

    private async Task Process3MFFileAsync(
        IStorageFile file,
        List<ThreeMFJob> jobs,
        List<FileLoadDiagnostic> diagnostics)
    {
        await using var stream = await file.OpenReadAsync();
        var result = await _extractor.ExtractJobs(stream);

        // Add extraction diagnostics
        foreach (var diag in result.Diagnostics)
        {
            diagnostics.Add(new FileLoadDiagnostic(
                diag.Severity switch
                {
                    ExtractionSeverity.Error => FileLoadSeverity.Error,
                    ExtractionSeverity.Warning => FileLoadSeverity.Warning,
                    _ => FileLoadSeverity.Info
                },
                file.Name,
                diag.Message
            ));
        }

        if (result.IsSuccess)
        {
            jobs.AddRange(result.Jobs);
            diagnostics.Add(new FileLoadDiagnostic(
                FileLoadSeverity.Info,
                file.Name,
                $"Successfully loaded {result.Jobs.Length} job(s)"
            ));
        }
    }

    private async Task ProcessGCodeFileAsync(
        IStorageFile file,
        List<ThreeMFJob> jobs,
        List<FileLoadDiagnostic> diagnostics)
    {
        await using var stream = await file.OpenReadAsync();
        using var reader = new StreamReader(stream);
        var content = await reader.ReadToEndAsync();

        // Validate G-code
        if (!_parser.ValidateGCodeFile(content))
        {
            diagnostics.Add(new FileLoadDiagnostic(
                FileLoadSeverity.Error,
                file.Name,
                "File does not contain valid G-code metadata"
            ));
            return;
        }

        // Parse G-code
        var parseResult = _parser.ParseGCodeFile(content, file.Name);

        // Add parse diagnostics
        foreach (var diag in parseResult.Diagnostics)
        {
            diagnostics.Add(new FileLoadDiagnostic(
                diag.Severity switch
                {
                    ParseDiagnosticSeverity.Error => FileLoadSeverity.Error,
                    ParseDiagnosticSeverity.Warning => FileLoadSeverity.Warning,
                    _ => FileLoadSeverity.Info
                },
                file.Name,
                diag.Message
            ));
        }

        if (!parseResult.HasErrors)
        {
            var metadata = parseResult.Metadata;

            // Create a job from the parsed metadata
            // Note: Printer and Routine need to be set by the user
            var job = new ThreeMFJob(
                PlateName: metadata.PlateName,
                Filaments: metadata.Filaments,
                EmbeddedGCode: metadata.GCode,
                Printer: DeterminePrinterFromMetadata(metadata.PrinterModel),
                Routine: null, // User must assign
                PrintTime: metadata.PrintTime,
                ModelImage: metadata.ModelImage
            );

            jobs.Add(job);
            diagnostics.Add(new FileLoadDiagnostic(
                FileLoadSeverity.Info,
                file.Name,
                "Successfully loaded G-code file"
            ));
        }
    }

    private static Printer DeterminePrinterFromMetadata(PrinterModel model)
    {
        return model switch
        {
            PrinterModel.A1M => Printers.A1M,
            PrinterModel.A1 => Printers.A1,
            _ => Printers.A1M // Default fallback
        };
    }
}
