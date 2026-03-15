using System;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace StrongestVwap.Replay
{
    /// <summary>
    /// Exports ReplayResult to JSON format using System.Text.Json.
    /// Compact output (no indentation) to keep file sizes manageable for large replay sessions.
    /// </summary>
    public static class ReplayExporter
    {
        /// <summary>
        /// Serialize a ReplayResult to a JSON file.
        /// </summary>
        /// <param name="result">The replay result containing all snapshots.</param>
        /// <param name="outputPath">Full path for the output JSON file.</param>
        public static void ExportJson(ReplayResult result, string outputPath)
        {
            var options = new JsonSerializerOptions
            {
                WriteIndented = false, // compact for large files
                PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
                DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
            };

            string json = JsonSerializer.Serialize(result, options);

            string? dir = Path.GetDirectoryName(outputPath);
            if (!string.IsNullOrEmpty(dir))
                Directory.CreateDirectory(dir);

            File.WriteAllText(outputPath, json, Encoding.UTF8);
            Console.WriteLine($"[REPLAY] Exported {result.Snapshots.Count} snapshots to {outputPath}");
        }

        /// <summary>
        /// Serialize a ReplayResult to a pretty-printed JSON file (for debugging).
        /// </summary>
        /// <param name="result">The replay result containing all snapshots.</param>
        /// <param name="outputPath">Full path for the output JSON file.</param>
        public static void ExportJsonPretty(ReplayResult result, string outputPath)
        {
            var options = new JsonSerializerOptions
            {
                WriteIndented = true,
                PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
                DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
            };

            string json = JsonSerializer.Serialize(result, options);

            string? dir = Path.GetDirectoryName(outputPath);
            if (!string.IsNullOrEmpty(dir))
                Directory.CreateDirectory(dir);

            File.WriteAllText(outputPath, json, Encoding.UTF8);
            Console.WriteLine($"[REPLAY] Exported {result.Snapshots.Count} snapshots (pretty) to {outputPath}");
        }
    }
}
