namespace Weaver.Models;

using System;
using System.Collections.Generic;
using System.Linq;

public class GCodeRoutine
{
    public List<string> Lines { get; }

    public GCodeRoutine()
    {
        Lines = new List<string>();
    }

    public GCodeRoutine(IEnumerable<string> lines)
    {
        Lines = new List<string>(lines);
    }

    public static GCodeRoutine FromString(string content) =>
        new GCodeRoutine(
            content
                .Split('\n')
                .Select(l => l.TrimEnd())
        );

    public void Append(GCodeRoutine other) =>
        Lines.AddRange(other.Lines);

    public void Insert(int index, GCodeRoutine other) =>
        Lines.InsertRange(index, other.Lines);

    public GCodeRoutine InsertBefore(
        Predicate<string> match,
        GCodeRoutine other)
    {
        var lines = new List<string>(Lines);
        var index = lines.FindIndex(match);

        if (index >= 0)
            lines.InsertRange(index, other.Lines);

        if (index < 0)
            throw new Exception("Marker not found!");

        return new GCodeRoutine(lines);
    }

    public string ToContentString() =>
        string.Join(Environment.NewLine, Lines);

    public IEnumerable<string> WithoutComments() =>
        Lines.Where(l => !l.TrimStart().StartsWith(";"));
}

