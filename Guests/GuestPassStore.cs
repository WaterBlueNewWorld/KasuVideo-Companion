// SPDX-License-Identifier: AGPL-3.0-only
// Copyright (C) 2026 WaterBlueNewWorld

using System.Text.Json;

namespace Jellyfin.Plugin.KasuVideoCompanion.Guests;

/// <summary>
/// Every guest pass, in one JSON file in the plugin's data folder (<c>guest-passes.json</c>).
/// Reads and changes happen under one lock, and every change is written to a temporary file
/// first and then moved over the old one, so a crash never leaves half a file.
/// </summary>
public sealed class GuestPassStore
{
    /// <summary>The file's name in the plugin's data folder.</summary>
    public const string FileName = "guest-passes.json";

    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    private readonly string _path;
    private readonly object _lock = new();
    private readonly List<GuestPass> _passes;

    /// <summary>Opens the store in <paramref name="folder"/>, made if missing.</summary>
    /// <param name="folder">The plugin's data folder.</param>
    public GuestPassStore(string folder)
    {
        Directory.CreateDirectory(folder);
        _path = Path.Combine(folder, FileName);
        _passes = File.Exists(_path)
            ? JsonSerializer.Deserialize<List<GuestPass>>(File.ReadAllText(_path), Json) ?? []
            : [];
    }

    /// <summary>Reads the passes, under the lock.</summary>
    /// <typeparam name="T">What is read.</typeparam>
    /// <param name="read">What to read; it must not keep the list.</param>
    /// <returns>What <paramref name="read"/> returned.</returns>
    public T Read<T>(Func<IReadOnlyList<GuestPass>, T> read)
    {
        lock (_lock)
        {
            return read(_passes);
        }
    }

    /// <summary>Changes the passes under the lock, then saves them.</summary>
    /// <typeparam name="T">What the change answers.</typeparam>
    /// <param name="change">The change; it must not keep the list.</param>
    /// <returns>What <paramref name="change"/> returned.</returns>
    public T Update<T>(Func<List<GuestPass>, T> change)
    {
        lock (_lock)
        {
            var result = change(_passes);
            Save();
            return result;
        }
    }

    private void Save()
    {
        var temporary = _path + ".tmp";
        File.WriteAllText(temporary, JsonSerializer.Serialize(_passes, Json));
        File.Move(temporary, _path, overwrite: true);
    }
}
