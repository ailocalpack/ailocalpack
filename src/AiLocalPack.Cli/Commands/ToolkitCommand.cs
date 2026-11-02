using System.CommandLine;
using System.Text.Json;
using System.Text.Json.Serialization;
using AiLocalPack.Toolkit.Engines;

namespace AiLocalPack.Cli.Commands;

/// <summary><c>toolkit detect &lt;folder&gt; [--json]</c>: what the toolkit finds in a game folder.</summary>
internal static class ToolkitCommand
{
    public static Command Build()
    {
        var folder = new Argument<string>("folder") { Description = "Game folder (or a macOS .app bundle)." };
        var json = new Option<bool>("--json") { Description = "Print JSON instead of text." };
        var detect = new Command("detect", "Detect the engine of a game folder: engine, confidence, marker file and main executable.")
        {
            folder,
            json,
        };
        detect.SetAction(result =>
        {
            var path = result.GetValue(folder)!;
            if (!Directory.Exists(path))
            {
                result.InvocationConfiguration.Error.WriteLine($"Folder not found: {path}");
                return 1;
            }

            var detection = new EngineDetector().Detect(path);
            var output = result.InvocationConfiguration.Output;
            if (result.GetValue(json))
            {
                WriteJson(output, detection);
            }
            else
            {
                WriteText(output, detection);
            }

            return 0;
        });

        return new Command("toolkit", "Engine toolkit.") { detect };
    }

    private static void WriteText(TextWriter output, EngineDetection detection)
    {
        if (detection.Match is { } match)
        {
            output.WriteLine($"engine: {match.Kind.DisplayName}");
            output.WriteLine($"confidence: {Confidence(match.Confidence)}");
            output.WriteLine($"marker: {match.Marker}");
            output.WriteLine($"executable: {match.MainExecutable ?? "-"}");
            return;
        }

        if (!detection.IsAmbiguous)
        {
            output.WriteLine("engine: unknown");
            return;
        }

        output.WriteLine("engine: unknown (ambiguous)");
        output.WriteLine("candidates:");
        foreach (var candidate in detection.Candidates)
        {
            output.WriteLine($"  {candidate.Kind.DisplayName}, {Confidence(candidate.Confidence)}, {candidate.Marker}");
        }
    }

    private static void WriteJson(TextWriter output, EngineDetection detection)
    {
        var dto = new DetectJson(
            detection.Kind.ToString(),
            detection.Kind.DisplayName,
            detection.Match is null ? null : Confidence(detection.Match.Confidence),
            detection.Match?.Marker,
            detection.Match?.MainExecutable,
            detection.IsAmbiguous,
            [.. detection.Candidates.Select(c => new CandidateJson(
                c.Kind.ToString(), c.Kind.DisplayName, Confidence(c.Confidence), c.Marker, c.MainExecutable))]);
        output.WriteLine(JsonSerializer.Serialize(dto, ToolkitJsonContext.Default.DetectJson));
    }

    private static string Confidence(EngineConfidence confidence) => confidence.ToString().ToLowerInvariant();
}

internal sealed record DetectJson(
    string Engine,
    string Name,
    string? Confidence,
    string? Marker,
    string? Executable,
    bool Ambiguous,
    IReadOnlyList<CandidateJson> Candidates);

internal sealed record CandidateJson(string Engine, string Name, string Confidence, string Marker, string? Executable);

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, WriteIndented = true)]
[JsonSerializable(typeof(DetectJson))]
internal sealed partial class ToolkitJsonContext : JsonSerializerContext;
