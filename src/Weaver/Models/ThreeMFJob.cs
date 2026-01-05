namespace Weaver.Models;
using System;

// Updated to make Routine nullable since not all printers have plate changers
public record ThreeMFJob(
    string PlateName,
    Filament[] Filaments,
    GCodeRoutine EmbeddedGCode,
    Printer Printer,
    PlateChangeRoutine? Routine,  // Made nullable
    TimeSpan PrintTime,
    string? ModelImage
) {
}
