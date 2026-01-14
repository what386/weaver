using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Weaver.Models;
using Weaver.Services;

namespace Weaver.ViewModels;

public partial class MainWindowViewModel : ViewModelBase
{
    private readonly IFileService _fileService;
    private readonly GCodeCompiler _compiler;
    private readonly ThreeMFCompiler _threeMFCompiler;

    [ObservableProperty]
    private ObservableCollection<ThreeMFJob> _jobs = new();

    [ObservableProperty]
    private ObservableCollection<string> _logs = new();

    [ObservableProperty]
    private Printer _selectedPrinter;

    [ObservableProperty]
    private PlateChangeRoutine? _selectedPlateChangeRoutine;

    [ObservableProperty]
    private bool _isBusy;

    [ObservableProperty]
    private string _statusMessage = "Ready";

    public ObservableCollection<Printer> AvailablePrinters { get; }
    public ObservableCollection<PlateChangeRoutine> AvailablePlateChangeRoutines { get; }

    public TimeSpan TotalPrintTime =>
        TimeSpan.FromSeconds(Jobs.Where(j => j.IsSelected).Sum(j => j.PrintTime.TotalSeconds));

    public int SelectedJobCount => Jobs.Count(j => j.IsSelected);

    public MainWindowViewModel()
    {
        _fileService = GetService<IFileService>();
        _compiler = GetService<GCodeCompiler>();
        _threeMFCompiler = GetService<ThreeMFCompiler>();

        // Initialize available printers
        AvailablePrinters = new ObservableCollection<Printer>
        {
            Printers.A1M,
            Printers.A1
        };

        // Initialize plate change routines
        AvailablePlateChangeRoutines = new ObservableCollection<PlateChangeRoutine>
        {
            new PlateChangeRoutine(
                Name: "A1 Mini - SwapMod",
                Description: "Plate change routine for the 'SwapMod' by SwapSystems.",
                Model: PrinterModel.A1M,
                GCode: PlateChangeRoutines.A1M_SwapMod
            ),
            new PlateChangeRoutine(
                Name: "A1 Mini - AutoPlateChanger",
                Description: "Plate change routine for the open-source 'AutoPlateChanger'.",
                Model: PrinterModel.A1M,
                GCode: PlateChangeRoutines.A1M_APC
            )
        };

        // Set defaults
        _selectedPrinter = AvailablePrinters[0];
        _selectedPlateChangeRoutine = AvailablePlateChangeRoutines[0];

        // Watch for job collection changes to update properties
        Jobs.CollectionChanged += (_, _) =>
        {
            OnPropertyChanged(nameof(TotalPrintTime));
            OnPropertyChanged(nameof(SelectedJobCount));
        };
    }

    [RelayCommand]
    private async Task LoadFiles()
    {
        try
        {
            IsBusy = true;
            StatusMessage = "Loading files...";

            var topLevel = App.Current?.ApplicationLifetime is
                Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime desktop
                ? desktop.MainWindow
                : null;

            if (topLevel == null)
            {
                AddLog("Error: Could not get main window reference");
                return;
            }

            var result = await _fileService.LoadFilesAsync(topLevel);
            if (result == null)
            {
                StatusMessage = "File loading cancelled";
                return;
            }

            if (result.IsSuccess)
            {
                foreach (var job in result.Jobs)
                {
                    Jobs.Add(job);
                }

                AddLog($"✓ Loaded {result.Jobs.Length} job(s)");
                StatusMessage = $"Loaded {result.Jobs.Length} jobs";

                // Show warnings if any
                foreach (var warning in result.Warnings)
                {
                    AddLog($"⚠ {warning}");
                }
            }
            else
            {
                AddLog("✗ Failed to load files");
                foreach (var error in result.Errors)
                {
                    AddLog($"✗ {error}");
                }
                StatusMessage = "Failed to load files";
            }
        }
        catch (Exception ex)
        {
            AddLog($"✗ Error: {ex.Message}");
            StatusMessage = "Error loading files";
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private async Task CompileJobs()
    {
        var selectedJobs = Jobs.Where(j => j.IsSelected).ToList();

        if (selectedJobs.Count == 0)
        {
            AddLog("⚠ No jobs selected for compilation");
            return;
        }

        if (SelectedPlateChangeRoutine == null)
        {
            AddLog("⚠ No plate change routine selected");
            return;
        }

        try
        {
            IsBusy = true;
            StatusMessage = "Compiling jobs...";
            AddLog($"Compiling {selectedJobs.Count} selected job(s)...");

            var result = await Task.Run(() =>
                _compiler.Compile(selectedJobs, SelectedPrinter, SelectedPlateChangeRoutine)
            );

            if (result.HasErrors)
            {
                AddLog("✗ Compilation failed with errors:");
                foreach (var error in result.Errors)
                    AddLog($"  ✗ {error}");
                StatusMessage = "Compilation failed";
                return;
            }

            if (result.HasWarnings)
            {
                AddLog("⚠ Compilation succeeded with warnings:");
                foreach (var warning in result.Warnings)
                    AddLog($"  ⚠ {warning}");
            }

            // Package into 3MF on background thread
            AddLog("Packaging into 3MF format...");
            var threeMFData = await Task.Run(() =>
                _threeMFCompiler.CompileMultiJob(result.Output, selectedJobs.ToArray(), SelectedPrinter)
            );

            // Export compiled 3MF
            var topLevel = App.Current?.ApplicationLifetime is
                Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime desktop
                ? desktop.MainWindow
                : null;

            if (topLevel == null)
            {
                AddLog("✗ Could not get main window reference");
                return;
            }

            var suggestedFileName = selectedJobs.Count == 1
                ? $"{selectedJobs[0].PlateName}_compiled.gcode.3mf"
                : $"multi_job_{selectedJobs.Count}plates_{DateTime.Now:yyyyMMdd_HHmmss}.gcode.3mf";

            var saved = await _fileService.Save3MFAsync(
                topLevel,
                threeMFData,
                suggestedFileName
            );

            if (saved)
            {
                AddLog($"✓ Compilation successful! Total time: {result.TotalPrintTime:hh\\:mm\\:ss}");
                AddLog($"✓ Exported as 3MF package: {suggestedFileName}");
                StatusMessage = "Compilation successful";
            }
            else
            {
                AddLog("⚠ Compilation succeeded but save was cancelled");
                StatusMessage = "Save cancelled";
            }
        }
        catch (Exception ex)
        {
            AddLog($"✗ Error: {ex.Message}");
            StatusMessage = "Compilation error";
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private void ClearJobs()
    {
        Jobs.Clear();
        Logs.Clear();
        AddLog("✓ Cleared all jobs");
        StatusMessage = "Ready";
    }

    [RelayCommand]
    private void RemoveJob(ThreeMFJob job)
    {
        if (Jobs.Remove(job))
        {
            AddLog($"✓ Removed job: {job.PlateName}");
            StatusMessage = $"Removed {job.PlateName}";
        }
    }

    [RelayCommand]
    private void MoveJobUp(ThreeMFJob job)
    {
        var index = Jobs.IndexOf(job);
        if (index > 0)
        {
            Jobs.Move(index, index - 1);
            AddLog($"↑ Moved '{job.PlateName}' up");
        }
    }

    [RelayCommand]
    private void MoveJobDown(ThreeMFJob job)
    {
        var index = Jobs.IndexOf(job);
        if (index < Jobs.Count - 1)
        {
            Jobs.Move(index, index + 1);
            AddLog($"↓ Moved '{job.PlateName}' down");
        }
    }

    [RelayCommand]
    private void SelectAllJobs()
    {
        foreach (var job in Jobs)
            job.IsSelected = true;

        // Force UI update
        OnPropertyChanged(nameof(TotalPrintTime));
        OnPropertyChanged(nameof(SelectedJobCount));
        AddLog($"✓ Selected all {Jobs.Count} jobs");
    }

    [RelayCommand]
    private void DeselectAllJobs()
    {
        foreach (var job in Jobs)
            job.IsSelected = false;

        // Force UI update
        OnPropertyChanged(nameof(TotalPrintTime));
        OnPropertyChanged(nameof(SelectedJobCount));
        AddLog($"○ Deselected all jobs");
    }

    [RelayCommand]
    private void RefreshSelectionStats()
    {
        OnPropertyChanged(nameof(TotalPrintTime));
        OnPropertyChanged(nameof(SelectedJobCount));
    }

    public async Task LoadFilesFromPathsAsync(IEnumerable<string> paths)
    {
        try
        {
            IsBusy = true;
            StatusMessage = "Loading dropped files...";

            var pathList = paths.ToList();
            AddLog($"Processing {pathList.Count} dropped file(s)...");

            // Process each file
            var loadedCount = 0;
            foreach (var path in pathList)
            {
                try
                {
                    var result = await _fileService.LoadFileFromPathAsync(path);
                    if (result != null && result.IsSuccess)
                    {
                        foreach (var job in result.Jobs)
                        {
                            Jobs.Add(job);
                        }
                        loadedCount += result.Jobs.Length;
                    }
                }
                catch (Exception ex)
                {
                    AddLog($"✗ Failed to load {System.IO.Path.GetFileName(path)}: {ex.Message}");
                }
            }

            if (loadedCount > 0)
            {
                AddLog($"✓ Loaded {loadedCount} job(s) from dropped files");
                StatusMessage = $"Loaded {loadedCount} jobs";
            }
            else
            {
                AddLog("⚠ No valid jobs found in dropped files");
                StatusMessage = "No jobs loaded";
            }
        }
        catch (Exception ex)
        {
            AddLog($"✗ Error processing dropped files: {ex.Message}");
            StatusMessage = "Error loading files";
        }
        finally
        {
            IsBusy = false;
        }
    }

    private void AddLog(string message)
    {
        var timestamp = DateTime.Now.ToString("HH:mm:ss");
        Logs.Insert(0, $"[{timestamp}] {message}");

        // Keep only last 100 log entries
        while (Logs.Count > 100)
        {
            Logs.RemoveAt(Logs.Count - 1);
        }
    }
}
