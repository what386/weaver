using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using Weaver.Models;

namespace Weaver.Services;

public sealed record ParseResult(
    ParsedMetadata Metadata,
    IReadOnlyList<ParseDiagnostic> Diagnostics)
{
    public bool HasErrors =>
        Diagnostics.Any(d => d.Severity == ParseDiagnosticSeverity.Error);

    public bool HasWarnings =>
        Diagnostics.Any(d => d.Severity == ParseDiagnosticSeverity.Warning);
}

public sealed record ParsedMetadata(
    string PlateName,
    Filament[] Filaments,
    TimeSpan PrintTime,
    string? ModelImage,
    PrinterModel PrinterModel,
    GCodeRoutine GCode
);

public enum ParseDiagnosticSeverity
{
    Info,
    Warning,
    Error
}

public sealed record ParseDiagnostic(
    ParseDiagnosticSeverity Severity,
    string Message)
{
    public override string ToString() => $"[{Severity}] {Message}";
}

public sealed class GCodeParser
{
    private static readonly string[] RequiredHeaders =
    {
        "; model printing time:",
        "; filament_colour",
        "; filament used [g]"
    };

    /// <summary>
    /// Validates that the G-code content contains the minimum required metadata.
    /// </summary>
    public bool ValidateGCodeFile(string content)
    {
        if (string.IsNullOrWhiteSpace(content))
            return false;

        return RequiredHeaders.All(header =>
            content.Contains(header, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// Parses G-code file content and extracts metadata.
    /// </summary>
    public ParseResult ParseGCodeFile(string content, string fileName)
    {
        var diagnostics = new List<ParseDiagnostic>();
        var lines = content.Split('\n', StringSplitOptions.RemoveEmptyEntries);

        // Extract basic metadata
        var plateName = ExtractPlateName(fileName);
        var printerModel = DeterminePrinterModel(lines, diagnostics);
        var printTime = ParsePrintTime(lines, diagnostics);

        // Extract filament data
        var colors = ParseColorList(lines, diagnostics);
        var weights = ParseWeightList(lines, diagnostics);
        var costs = ParseCostList(lines, diagnostics);
        var kinds = ParseFilamentKinds(lines, diagnostics);

        // Build filament array
        var filaments = BuildFilaments(colors, weights, costs, kinds, diagnostics);

        // Extract G-code routine
        var gcode = new GCodeRoutine(lines);

        // Try to find model image (thumbnail)
        var modelImage = ExtractThumbnail(lines);

        var metadata = new ParsedMetadata(
            PlateName: plateName,
            Filaments: filaments,
            PrintTime: printTime,
            ModelImage: modelImage,
            PrinterModel: printerModel,
            GCode: gcode
        );

        return new ParseResult(metadata, diagnostics);
    }

    // ---------- Parsing Methods ----------

    private static string ExtractPlateName(string fileName)
    {
        var name = fileName;

        // Remove common extensions
        if (name.EndsWith(".gcode", StringComparison.OrdinalIgnoreCase))
            name = name[..^6];
        else if (name.EndsWith(".gco", StringComparison.OrdinalIgnoreCase))
            name = name[..^4];

        return name.Trim();
    }

    private static PrinterModel DeterminePrinterModel(
        string[] lines,
        List<ParseDiagnostic> diagnostics)
    {
        var modelLine = FindHeaderValue(lines, "; printer_model =");

        if (string.IsNullOrWhiteSpace(modelLine))
        {
            diagnostics.Add(new ParseDiagnostic(
                ParseDiagnosticSeverity.Warning,
                "Printer model not found in G-code header"
            ));
            return PrinterModel.Unknown;
        }

        // Try to match known printer models
        if (modelLine.Contains("A1 Mini", StringComparison.OrdinalIgnoreCase) ||
            modelLine.Contains("A1M", StringComparison.OrdinalIgnoreCase))
        {
            return PrinterModel.A1M;
        }

        if (modelLine.Contains("A1", StringComparison.OrdinalIgnoreCase))
        {
            return PrinterModel.A1;
        }

        diagnostics.Add(new ParseDiagnostic(
            ParseDiagnosticSeverity.Info,
            $"Unknown printer model: {modelLine}"
        ));

        return PrinterModel.Unknown;
    }

    private static TimeSpan ParsePrintTime(
        string[] lines,
        List<ParseDiagnostic> diagnostics)
    {
        var timeLine = lines.FirstOrDefault(l =>
            l.Contains("; model printing time:", StringComparison.OrdinalIgnoreCase));

        if (timeLine == null)
        {
            diagnostics.Add(new ParseDiagnostic(
                ParseDiagnosticSeverity.Warning,
                "Print time not found in G-code"
            ));
            return TimeSpan.Zero;
        }

        // Try to parse "model printing time: 1h 23m 45s" format
        var match = Regex.Match(timeLine, @"model printing time:\s*(\d+)h\s*(\d+)m\s*(\d+)s",
            RegexOptions.IgnoreCase);

        if (match.Success)
        {
            var hours = int.Parse(match.Groups[1].Value);
            var minutes = int.Parse(match.Groups[2].Value);
            var seconds = int.Parse(match.Groups[3].Value);
            return new TimeSpan(hours, minutes, seconds);
        }

        // Try alternate format: "1h23m45s" (no spaces)
        match = Regex.Match(timeLine, @"(\d+)h(\d+)m(\d+)s", RegexOptions.IgnoreCase);
        if (match.Success)
        {
            var hours = int.Parse(match.Groups[1].Value);
            var minutes = int.Parse(match.Groups[2].Value);
            var seconds = int.Parse(match.Groups[3].Value);
            return new TimeSpan(hours, minutes, seconds);
        }

        diagnostics.Add(new ParseDiagnostic(
            ParseDiagnosticSeverity.Warning,
            "Could not parse print time format"
        ));

        return TimeSpan.Zero;
    }

    private static List<string> ParseColorList(
        string[] lines,
        List<ParseDiagnostic> diagnostics)
    {
        var raw = FindHeaderValue(lines, "; filament_colour");

        if (string.IsNullOrWhiteSpace(raw))
        {
            diagnostics.Add(new ParseDiagnostic(
                ParseDiagnosticSeverity.Warning,
                "Filament colors not found"
            ));
            return new List<string>();
        }

        return ParseList(raw, ';', color =>
        {
            color = color.Trim();
            // Ensure color starts with #
            return color.StartsWith("#") ? color : $"#{color}";
        });
    }

    private static List<double> ParseWeightList(
        string[] lines,
        List<ParseDiagnostic> diagnostics)
    {
        var raw = FindHeaderValue(lines, "; filament used [g]");

        if (string.IsNullOrWhiteSpace(raw))
        {
            diagnostics.Add(new ParseDiagnostic(
                ParseDiagnosticSeverity.Warning,
                "Filament weights not found"
            ));
            return new List<double>();
        }

        return ParseList(raw, ',', weightStr =>
        {
            if (double.TryParse(weightStr, out var weight))
                return weight;

            diagnostics.Add(new ParseDiagnostic(
                ParseDiagnosticSeverity.Warning,
                $"Invalid weight value: {weightStr}"
            ));
            return 0.0;
        });
    }

    private static List<double> ParseCostList(
        string[] lines,
        List<ParseDiagnostic> diagnostics)
    {
        var raw = FindHeaderValue(lines, "; filament cost");

        if (string.IsNullOrWhiteSpace(raw))
        {
            // Cost is optional
            return new List<double>();
        }

        return ParseList(raw, ',', costStr =>
        {
            if (double.TryParse(costStr, out var cost))
                return cost;
            return 0.0;
        });
    }

    private static List<string> ParseFilamentKinds(
        string[] lines,
        List<ParseDiagnostic> diagnostics)
    {
        var raw = FindHeaderValue(lines, "; filament_type");

        if (string.IsNullOrWhiteSpace(raw))
        {
            diagnostics.Add(new ParseDiagnostic(
                ParseDiagnosticSeverity.Info,
                "Filament types not found, defaulting to PLA"
            ));
            return new List<string>();
        }

        return ParseList(raw, ';', kind => kind.Trim());
    }

    private static string? ExtractThumbnail(string[] lines)
    {
        // Look for base64-encoded thumbnails in comments
        // Format: ; thumbnail begin 200x200 [size]
        var inThumbnail = false;
        var thumbnailData = new List<string>();

        foreach (var line in lines)
        {
            if (line.Contains("; thumbnail begin", StringComparison.OrdinalIgnoreCase))
            {
                inThumbnail = true;
                continue;
            }

            if (line.Contains("; thumbnail end", StringComparison.OrdinalIgnoreCase))
            {
                break;
            }

            if (inThumbnail && line.StartsWith(";"))
            {
                // Extract base64 data after the semicolon and space
                var data = line.TrimStart(';', ' ');
                if (!string.IsNullOrWhiteSpace(data))
                    thumbnailData.Add(data);
            }
        }

        return thumbnailData.Count > 0
            ? string.Join("", thumbnailData)
            : null;
    }

    // ---------- Helper Methods ----------

    private static string? FindHeaderValue(IEnumerable<string> lines, string key)
    {
        var line = lines.FirstOrDefault(l =>
            l.StartsWith(key, StringComparison.OrdinalIgnoreCase));

        if (line == null)
            return null;

        var idx = line.IndexOf('=');
        return idx >= 0 ? line[(idx + 1)..].Trim() : null;
    }

    private static List<T> ParseList<T>(
        string raw,
        char separator,
        Func<string, T> convert)
    {
        if (string.IsNullOrWhiteSpace(raw))
            return new List<T>();

        return raw
            .Split(separator, StringSplitOptions.RemoveEmptyEntries)
            .Select(s => convert(s.Trim()))
            .ToList();
    }

    private static Filament[] BuildFilaments(
        IReadOnlyList<string> colors,
        IReadOnlyList<double> weights,
        IReadOnlyList<double> costs,
        IReadOnlyList<string> kinds,
        List<ParseDiagnostic> diagnostics)
    {
        var maxCount = new[] { colors.Count, weights.Count, kinds.Count }
            .Where(c => c > 0)
            .DefaultIfEmpty(0)
            .Max();

        if (maxCount == 0)
        {
            diagnostics.Add(new ParseDiagnostic(
                ParseDiagnosticSeverity.Warning,
                "No filament data found, using default single filament"
            ));

            return new[]
            {
                new Filament(
                    ColorHex: "#FFFFFF",
                    CostPerKg: 0.0,
                    WeightGrams: 0.0,
                    FilamentKind: FilamentKind.PLA
                )
            };
        }

        var filaments = new Filament[maxCount];

        for (int i = 0; i < maxCount; i++)
        {
            var color = i < colors.Count ? colors[i] : "#FFFFFF";
            var weight = i < weights.Count ? weights[i] : 0.0;
            var costPerKg = i < costs.Count && costs[i] > 0
                ? costs[i] * 1000 / weight  // Convert cost per gram to cost per kg
                : 0.0;
            var kindStr = i < kinds.Count ? kinds[i] : "PLA";
            var kind = Filament.ParseKind(kindStr);

            filaments[i] = new Filament(
                ColorHex: color,
                CostPerKg: costPerKg,
                WeightGrams: weight,
                FilamentKind: kind
            );
        }

        return filaments;
    }
}
