namespace Weaver.Models;

using System;
using System.ComponentModel;
using System.Runtime.CompilerServices;

public record ThreeMFJob(
    string PlateName,
    Filament[] Filaments,
    GCodeRoutine EmbeddedGCode,
    Printer Printer,
    PlateChangeRoutine? Routine,
    TimeSpan PrintTime,
    string? ModelImage,
    byte[]? Source3MFFile  // The original 3MF file bytes (null for standalone .gcode)
) : INotifyPropertyChanged
{
    private bool _isSelected = true;

    public bool IsSelected
    {
        get => _isSelected;
        set
        {
            if (_isSelected != value)
            {
                _isSelected = value;
                OnPropertyChanged();
            }
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    protected virtual void OnPropertyChanged([CallerMemberName] string? propertyName = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}
