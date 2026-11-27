// SPDX-License-Identifier: AGPL-3.0-only
// Copyright (C) 2026 WaterBlueNewWorld

using System.Globalization;
using System.Text.Json;

namespace Jellyfin.Plugin.KasuVideoCompanion.Guests;

/// <summary>
/// Every guest pass, in one JSON file in the plugin's data folder (<c>guest-passes.json</c>).
/// Reads and changes happen under one lock, and every change is written to a temporary file
/// first and then moved over the old one, so a crash never leaves half a file. A file that can't
/// be read is set aside (<c>guest-passes.json.unreadable-…</c>) and the store starts empty: the
/// server keeps working, and every guest account is refused by the guard (it knows them by their
/// provider) until the sweeps delete it. Before G8 an unreadable file stopped every request on the
/// server, and Jellyfin then marked the plugin as malfunctioned.
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
        _passes = Load(_path, out var setAside);
        SetAside = setAside;
    }

    /// <summary>Gets where an unreadable file was moved when the store opened, or null.</summary>
    public string? SetAside { get; }

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

    /// <summary>
    /// Like <see cref="Update{T}"/>, but saves only when <paramref name="change"/> found something
    /// (answered non-null): a code that matches nothing costs no write (G8: every attempt used to
    /// rewrite the file, under the lock every request's guard waits on).
    /// </summary>
    /// <typeparam name="T">What the change answers.</typeparam>
    /// <param name="change">The change; null when it changed nothing.</param>
    /// <returns>What <paramref name="change"/> returned.</returns>
    public T? UpdateIfFound<T>(Func<List<GuestPass>, T?> change)
        where T : class
    {
        lock (_lock)
        {
            var result = change(_passes);
            if (result is not null)
            {
                Save();
            }

            return result;
        }
    }

    private static List<GuestPass> Load(string path, out string? setAside)
    {
        setAside = null;
        if (!File.Exists(path))
        {
            return [];
        }

        try
        {
            return JsonSerializer.Deserialize<List<GuestPass>>(File.ReadAllText(path), Json) ?? [];
        }
        catch (Exception e) when (e is JsonException or IOException or NotSupportedException or UnauthorizedAccessException)
        {
            setAside = $"{path}.unreadable-{DateTime.UtcNow.ToString("yyyyMMddHHmmss", CultureInfo.InvariantCulture)}";
            try
            {
                File.Move(path, setAside, overwrite: true);
            }
            catch (Exception moveFailed) when (moveFailed is IOException or UnauthorizedAccessException)
            {
                // Left where it is; the first save writes over it.
            }

            return [];
        }
    }

    private void Save()
    {
        var temporary = _path + ".tmp";
        File.WriteAllText(temporary, JsonSerializer.Serialize(_passes, Json));
        File.Move(temporary, _path, overwrite: true);
    }
}
