namespace Weaver.Models;

using System;

public record Printer(
    string DisplayName,
    PrinterModel Model,
    BedSize PrintableBedSize,
    ExtendedBedSize ExtendedBed,      // <- embedded directly
    string PrintFinishedMarker,
    double MaxBedTemp,
    double MaxNozzleTemp,
    bool HasAMS
)
{
    public Predicate<string> PrintFinishedPredicate =>
        line => string.Equals(line.Trim(), PrintFinishedMarker.Trim(), StringComparison.Ordinal);
}

public record BedSize(
    double Width,
    double Length,
    double Height
)
{
    public bool Contains(double? x, double? y, double? z)
    {
        if (x.HasValue && (x.Value < 0 || x.Value > Width))
            return false;
        if (y.HasValue && (y.Value < 0 || y.Value > Length))
            return false;
        if (z.HasValue && (z.Value < 0 || z.Value > Height))
            return false;
        return true;
    }

    public (double Min, double Max) GetXBounds() => (0, Width);
    public (double Min, double Max) GetYBounds() => (0, Length);
    public (double Min, double Max) GetZBounds() => (0, Height);
}

public record ExtendedBedSize(
    double MinX,
    double MaxX,
    double MinY,
    double MaxY,
    double MinZ,
    double MaxZ
)
{
    public bool Contains(double? x, double? y, double? z)
    {
        if (x.HasValue && (x.Value < MinX || x.Value > MaxX))
            return false;
        if (y.HasValue && (y.Value < MinY || y.Value > MaxY))
            return false;
        if (z.HasValue && (z.Value < MinZ || z.Value > MaxZ))
            return false;
        return true;
    }

    // Create from a standard BedSize plus optional margin
    public static ExtendedBedSize FromBedSize(BedSize bed, double margin = 0)
        => new ExtendedBedSize(
            MinX: -margin,
            MaxX: bed.Width + margin,
            MinY: -margin,
            MaxY: bed.Length + margin,
            MinZ: -margin,
            MaxZ: bed.Height + margin
        );
}

public enum PrinterModel
{
    Unknown,
    A1,
    A1M,
}

// ---------- Static printer definitions ----------
public static class Printers
{
    public static readonly Printer A1 = new(
        "Bambu Lab A1",
        PrinterModel.A1,
        PrintableBedSize: new BedSize(256, 256, 256),
        ExtendedBed: ExtendedBedSize.FromBedSize(new BedSize(256, 256, 256), margin: 20),
        PrintFinishedMarker: ";=====printer finish  sound=========",
        MaxBedTemp: 100,
        MaxNozzleTemp: 300,
        HasAMS: false
    );

    public static readonly Printer A1M = new(
        "Bambu Lab A1 Mini",
        PrinterModel.A1M,
        PrintableBedSize: new BedSize(180, 180, 180),
        ExtendedBed: new ExtendedBedSize(
            MinX: -30, MaxX: 190,
            MinY: -20, MaxY: 190,
            MinZ: -5,  MaxZ: 185
        ),
        PrintFinishedMarker: ";=====printer finish  sound=========",
        MaxBedTemp: 100,
        MaxNozzleTemp: 300,
        HasAMS: false
    );
}

