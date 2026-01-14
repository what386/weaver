namespace Weaver.Models;
using System;

public record ThreeMFJob(
    string PlateName,
    Filament[] Filaments,
    GCodeRoutine EmbeddedGCode,
    Printer Printer,
    PlateChangeRoutine? Routine,
    TimeSpan PrintTime,
    string? ModelImage
)
{
    // Make IsSelected mutable for UI binding
    public bool IsSelected { get; set; } = true;
}
