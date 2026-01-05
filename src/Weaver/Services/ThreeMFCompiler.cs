using System;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Weaver.Models;

namespace Weaver.Services;

public sealed record ThreeMFMetadata(
    string PlateName,
    Printer Printer,
    TimeSpan PrintTime,
    Filament[] Filaments,
    string? ModelImage = null,
    int JobCount = 1
);

public sealed record ExportOptions(
    bool IncludeThumbnails = true,
    bool OptimizeFileSize = true
)
{
    public static ExportOptions Default => new();
}

public sealed class ThreeMFCompiler
{
    private const string PlateId = "plate_1";
    private const string GCodePath = "Metadata/plate_1.gcode";

    /// <summary>
    /// Compiles G-code and metadata into a 3MF file package.
    /// </summary>
    public byte[] Compile3MF(
        string gcode,
        ThreeMFMetadata metadata)
    {
        using var memoryStream = new MemoryStream();
        using (var archive = new ZipArchive(memoryStream, ZipArchiveMode.Create, true))
        {
            var currentDate = DateTime.UtcNow.ToString("yyyy-MM-dd HH:mm:ss");

            // Add thumbnails
            AddThumbnails(archive, metadata.ModelImage);

            // Add relationship files
            AddText(archive, "_rels/.rels", GetRelsContent());
            AddText(archive, "Metadata/_rels/model_settings.config.rels", GetModelSettingsRelsContent());

            // Add 3D model
            AddText(archive, "3D/3dmodel.model", Get3DModelContent(currentDate, metadata));

            // Add G-code and its MD5 hash
            AddText(archive, GCodePath, gcode);
            AddText(archive, $"Metadata/{PlateId}.gcode.md5", ComputeMd5Hex(gcode));

            // Add metadata files
            AddText(archive, "Metadata/model_settings.config", GetModelSettingsContent(metadata));
            AddText(archive, "Metadata/plate_1.json", GetPlateJsonContent());
            AddText(archive, "Metadata/project_settings.config", GetProjectSettingsContent(metadata));
            AddText(archive, "Metadata/slice_info.config", GetSliceInfoContent(metadata));

            // Add content types
            AddText(archive, "[Content_Types].xml", GetContentTypesContent());
        }

        return memoryStream.ToArray();
    }

    /// <summary>
    /// Compiles multiple jobs into a single 3MF with combined G-code.
    /// </summary>
    public byte[] CompileMultiJob(
        string combinedGCode,
        ThreeMFJob[] jobs,
        Printer printer)
    {
        var totalTime = jobs.Aggregate(TimeSpan.Zero, (acc, job) => acc + job.PrintTime);
        var firstJob = jobs.First();

        var metadata = new ThreeMFMetadata(
            PlateName: $"Multi-Job ({jobs.Length} plates)",
            Printer: printer,
            PrintTime: totalTime,
            Filaments: firstJob.Filaments, // Use first job's filaments
            ModelImage: firstJob.ModelImage,
            JobCount: jobs.Length
        );

        return Compile3MF(combinedGCode, metadata);
    }

    // ZIP helpers

    private static void AddText(
        ZipArchive archive,
        string entryName,
        string content)
    {
        var entry = archive.CreateEntry(entryName, CompressionLevel.Optimal);
        using var writer = new StreamWriter(entry.Open(), Encoding.UTF8);
        writer.Write(content);
    }

    private static void AddBinary(
        ZipArchive archive,
        string entryName,
        byte[] content)
    {
        var entry = archive.CreateEntry(entryName, CompressionLevel.Optimal);
        using var stream = entry.Open();
        stream.Write(content, 0, content.Length);
    }

    // Thumbnail handling

    private void AddThumbnails(ZipArchive archive, string? base64Image)
    {
        byte[] imageData;

        if (!string.IsNullOrWhiteSpace(base64Image))
        {
            try
            {
                imageData = Convert.FromBase64String(base64Image);
            }
            catch
            {
                // If base64 decoding fails, use placeholder
                imageData = GenerateMinimalPNG();
            }
        }
        else
        {
            imageData = GenerateMinimalPNG();
        }

        // Add same image for all thumbnail variants
        AddBinary(archive, $"Metadata/{PlateId}.png", imageData);
        AddBinary(archive, $"Metadata/{PlateId}_small.png", imageData);
        AddBinary(archive, "Metadata/top_1.png", imageData);
        AddBinary(archive, "Metadata/pick_1.png", imageData);
    }

    private static byte[] GenerateMinimalPNG()
    {
        // 1x1 white PNG (minimal valid PNG file)
        return Convert.FromBase64String(
            "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mP8/5+hHgAHggJ/PchI7wAAAABJRU5ErkJggg=="
        );
    }

    // Hashing

    private static string ComputeMd5Hex(string content)
    {
        using var md5 = MD5.Create();
        var bytes = Encoding.UTF8.GetBytes(content);
        var hash = md5.ComputeHash(bytes);
        return BitConverter.ToString(hash).Replace("-", "").ToLowerInvariant();
    }

    // Content generation

    private static string GetRelsContent()
    {
        return @"<?xml version=""1.0"" encoding=""UTF-8""?>
<Relationships xmlns=""http://schemas.openxmlformats.org/package/2006/relationships"">
  <Relationship Target=""/3D/3dmodel.model"" Id=""rel-1"" Type=""http://schemas.microsoft.com/3dmanufacturing/2013/01/3dmodel""/>
  <Relationship Target=""/Metadata/plate_1.png"" Id=""rel-2"" Type=""http://schemas.openxmlformats.org/package/2006/relationships/metadata/thumbnail""/>
  <Relationship Target=""/Metadata/plate_1.png"" Id=""rel-4"" Type=""http://schemas.bambulab.com/package/2021/cover-thumbnail-middle""/>
  <Relationship Target=""/Metadata/plate_1_small.png"" Id=""rel-5"" Type=""http://schemas.bambulab.com/package/2021/cover-thumbnail-small""/>
</Relationships>";
    }

    private static string GetModelSettingsRelsContent()
    {
        return @"<?xml version=""1.0"" encoding=""UTF-8""?>
<Relationships xmlns=""http://schemas.openxmlformats.org/package/2006/relationships"">
  <Relationship Target=""/Metadata/plate_1.gcode"" Id=""rel-1"" Type=""http://schemas.bambulab.com/package/2021/gcode""/>
</Relationships>";
    }

    private static string Get3DModelContent(string date, ThreeMFMetadata metadata)
    {
        var printerName = metadata.Printer.DisplayName;

        return $@"<?xml version=""1.0"" encoding=""UTF-8""?>
<model unit=""millimeter"" xml:lang=""en-US""
       xmlns=""http://schemas.microsoft.com/3dmanufacturing/core/2015/02""
       xmlns:BambuStudio=""http://schemas.bambulab.com/package/2021""
       xmlns:p=""http://schemas.microsoft.com/3dmanufacturing/production/2015/06""
       requiredextensions=""p"">
  <metadata name=""Application"">Weaver</metadata>
  <metadata name=""BambuStudio:3mfVersion"">1</metadata>
  <metadata name=""CreationDate"">{date}</metadata>
  <metadata name=""ModificationDate"">{date}</metadata>
  <metadata name=""Title"">{EscapeXml(metadata.PlateName)}</metadata>
  <metadata name=""Designer"">Weaver Compiler</metadata>
  <metadata name=""BambuStudio:Printer"">{EscapeXml(printerName)}</metadata>
  <resources/>
  <build/>
</model>";
    }

    private static string GetModelSettingsContent(ThreeMFMetadata metadata)
    {
        var plateName = EscapeXml(metadata.PlateName);

        return $@"<?xml version=""1.0"" encoding=""UTF-8""?>
<config>
  <plate>
    <metadata key=""plater_id"" value=""1""/>
    <metadata key=""plater_name"" value=""{plateName}""/>
    <metadata key=""locked"" value=""false""/>
    <metadata key=""gcode_file"" value=""Metadata/plate_1.gcode""/>
    <metadata key=""thumbnail_file"" value=""Metadata/plate_1.png""/>
    <metadata key=""top_file"" value=""Metadata/top_1.png""/>
    <metadata key=""pick_file"" value=""Metadata/pick_1.png""/>
    <metadata key=""pattern_bbox_file"" value=""Metadata/plate_1.json""/>
    <metadata key=""print_time"" value=""{FormatTimeSeconds(metadata.PrintTime)}""/>
  </plate>
</config>";
    }

    private static string GetPlateJsonContent()
    {
        return JsonSerializer.Serialize(new
        {
            plate_index = 1,
            thumbnail = "plate_1.png",
            small_thumbnail = "plate_1_small.png",
            top_thumbnail = "top_1.png",
            pick_thumbnail = "pick_1.png"
        }, new JsonSerializerOptions { WriteIndented = true });
    }

    private static string GetProjectSettingsContent(ThreeMFMetadata metadata)
    {
        var filamentInfo = metadata.Filaments
            .Select((f, i) => new
            {
                index = i,
                type = f.FilamentKind.ToString(),
                color = f.ColorHex,
                weight_grams = f.WeightGrams,
                cost_per_kg = f.CostPerKg
            })
            .ToArray();

        return JsonSerializer.Serialize(new
        {
            version = "1.0.0",
            project = new
            {
                name = metadata.PlateName,
                created_at = DateTime.UtcNow.ToString("O"),
                printer = metadata.Printer.DisplayName,
                printer_model = metadata.Printer.Model.ToString(),
                print_time_seconds = (int)metadata.PrintTime.TotalSeconds,
                job_count = metadata.JobCount
            },
            filaments = filamentInfo
        }, new JsonSerializerOptions { WriteIndented = true });
    }

    private static string GetSliceInfoContent(ThreeMFMetadata metadata)
    {
        return JsonSerializer.Serialize(new
        {
            version = "1.0.0",
            printer = new
            {
                model = metadata.Printer.Model.ToString(),
                name = metadata.Printer.DisplayName,
                bed_size = new
                {
                    width = metadata.Printer.PrintableBedSize.Width,
                    length = metadata.Printer.PrintableBedSize.Length,
                    height = metadata.Printer.PrintableBedSize.Height
                }
            },
            plate_info = new[]
            {
                new
                {
                    plate_index = 1,
                    gcode_file = "plate_1.gcode",
                    print_time = FormatTimeHMS(metadata.PrintTime)
                }
            },
            total_print_time = FormatTimeHMS(metadata.PrintTime),
            total_filament_weight = metadata.Filaments.Sum(f => f.WeightGrams)
        }, new JsonSerializerOptions { WriteIndented = true });
    }

    private static string GetContentTypesContent()
    {
        return @"<?xml version=""1.0"" encoding=""UTF-8""?>
<Types xmlns=""http://schemas.openxmlformats.org/package/2006/content-types"">
  <Default Extension=""rels"" ContentType=""application/vnd.openxmlformats-package.relationships+xml""/>
  <Default Extension=""model"" ContentType=""application/vnd.ms-package.3dmanufacturing-3dmodel+xml""/>
  <Default Extension=""png"" ContentType=""image/png""/>
  <Default Extension=""gcode"" ContentType=""text/x.gcode""/>
  <Default Extension=""json"" ContentType=""application/json""/>
  <Default Extension=""config"" ContentType=""application/xml""/>
  <Default Extension=""xml"" ContentType=""application/xml""/>
  <Default Extension=""md5"" ContentType=""text/plain""/>
</Types>";
    }

    // Utility methods

    private static string EscapeXml(string text)
    {
        return text
            .Replace("&", "&amp;")
            .Replace("<", "&lt;")
            .Replace(">", "&gt;")
            .Replace("\"", "&quot;")
            .Replace("'", "&apos;");
    }

    private static string FormatTimeSeconds(TimeSpan time)
    {
        return ((int)time.TotalSeconds).ToString();
    }

    private static string FormatTimeHMS(TimeSpan time)
    {
        return $"{(int)time.TotalHours}h {time.Minutes}m {time.Seconds}s";
    }
}
