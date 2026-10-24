using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Excalibur.Core.Models;

namespace Excalibur.Core.Services;

public static class TestStateStore
{
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter() },
    };

    public static string DefaultStateDir
    {
        get
        {
            var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            var dir = Path.Combine(local, "Excalibur", "test-state");
            Directory.CreateDirectory(dir);
            return dir;
        }
    }

    public static string DefaultStatePath(PhysicalDisk disk)
    {
        var key = string.IsNullOrEmpty(disk.SerialNumber)
            ? $"disk{disk.DiskNumber}"
            : disk.SerialNumber;
        // sanitize for filesystem
        foreach (var c in Path.GetInvalidFileNameChars())
            key = key.Replace(c, '_');
        return Path.Combine(DefaultStateDir, key + ".json");
    }

    public static TestState? TryLoad(string path)
    {
        if (!File.Exists(path)) return null;
        try
        {
            var text = File.ReadAllText(path, Encoding.UTF8);
            return JsonSerializer.Deserialize<TestState>(text, JsonOpts);
        }
        catch
        {
            return null;
        }
    }

    public static void Save(string path, TestState state)
    {
        var dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

        // atomic write via temp file
        var tmp = path + ".tmp";
        var json = JsonSerializer.Serialize(state, JsonOpts);
        File.WriteAllText(tmp, json, new UTF8Encoding(false));

        // Include file content hash inside state? No — store in a sidecar for tamper detection
        if (File.Exists(path))
        {
            try { File.Delete(path); } catch { /* ignore */ }
        }
        File.Move(tmp, path);
    }

    public static void Delete(string path)
    {
        if (File.Exists(path))
        {
            try { File.Delete(path); } catch { }
        }
    }

    public static string ComputeStateHash(TestState state)
    {
        // quick hash of key fields (used in HTML footer)
        var sb = new StringBuilder();
        sb.Append(state.DiskNumber).Append('|')
          .Append(state.SerialNumber).Append('|')
          .Append(state.StartLba).Append('|')
          .Append(state.EndLba).Append('|')
          .Append(state.BlockSizeBytes).Append('|')
          .Append(state.LastCompletedLba).Append('|')
          .Append(state.BytesReadBeforeResume).Append('|')
          .Append(state.BadBlocks.Count).Append('|')
          .Append(state.Buckets.Count);
        using var sha = SHA256.Create();
        return Convert.ToHexString(sha.ComputeHash(Encoding.UTF8.GetBytes(sb.ToString()))).ToLowerInvariant();
    }
}