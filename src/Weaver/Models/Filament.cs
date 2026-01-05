namespace Weaver.Models;

public enum FilamentKind
{
    PLA,
    PETG,
    ABS,
    TPU,
    ASA,
    HIPS,
    OTHER
}

public record Filament(
    string ColorHex,
    double CostPerKg,
    double WeightGrams,
    FilamentKind FilamentKind
) {
    public static FilamentKind ParseKind(string value) =>
        FilamentKind.TryParse<FilamentKind>(value, ignoreCase: true, out var result)
            ? result
            : FilamentKind.OTHER;
}
