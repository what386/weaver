using System;
using System.IO;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using Weaver.Models;

namespace Weaver.Services;

public sealed class ThreeMFCompiler
{
    /// <summary>
    /// Creates a new 3MF file by copying the source and replacing the G-code.
    /// </summary>
    public byte[] CompileMultiJob(
        string compiledGCode,
        ThreeMFJob[] jobs)
    {
        // Use the first job's source 3MF as the template
        var source3MF = jobs[0].Source3MFFile;

        if (source3MF == null)
        {
            throw new InvalidOperationException(
                "First job must be from a 3MF file (not standalone G-code)");
        }

        return InjectGCode(source3MF, compiledGCode);
    }

    /// <summary>
    /// Creates a copy of the source 3MF and replaces plate_1.gcode with new content.
    /// </summary>
    private byte[] InjectGCode(byte[] source3MF, string newGCode)
    {
        using var sourceStream = new MemoryStream(source3MF);
        using var outputStream = new MemoryStream();

        using (var sourceArchive = new ZipArchive(sourceStream, ZipArchiveMode.Read))
        using (var outputArchive = new ZipArchive(outputStream, ZipArchiveMode.Create, true))
        {
            // Copy all entries except G-code and its MD5
            foreach (var entry in sourceArchive.Entries)
            {
                if (entry.FullName == "Metadata/plate_1.gcode" ||
                    entry.FullName == "Metadata/plate_1.gcode.md5")
                {
                    continue; // Skip - we'll replace these
                }

                // Copy entry as-is
                var newEntry = outputArchive.CreateEntry(entry.FullName, CompressionLevel.Optimal);
                using var sourceEntryStream = entry.Open();
                using var newEntryStream = newEntry.Open();
                sourceEntryStream.CopyTo(newEntryStream);
            }

            // Add our compiled G-code
            AddTextEntry(outputArchive, "Metadata/plate_1.gcode", newGCode);
            AddTextEntry(outputArchive, "Metadata/plate_1.gcode.md5", ComputeMd5(newGCode));
        }

        return outputStream.ToArray();
    }

    private void AddTextEntry(ZipArchive archive, string path, string content)
    {
        var entry = archive.CreateEntry(path, CompressionLevel.Optimal);
        using var writer = new StreamWriter(entry.Open(), Encoding.UTF8);
        writer.Write(content);
    }

    private string ComputeMd5(string content)
    {
        using var md5 = MD5.Create();
        var bytes = Encoding.UTF8.GetBytes(content);
        var hash = md5.ComputeHash(bytes);
        return BitConverter.ToString(hash).Replace("-", "").ToLowerInvariant();
    }
}
