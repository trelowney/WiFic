// wific - Wi-Fi network scanner for Windows
// Copyright (C) 2026 trelowney
// SPDX-License-Identifier: GPL-3.0-or-later

using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Text;
using System.Text.Json;

namespace Wific.Wifi;

/// <summary>One scan as stored in a recording.</summary>
public sealed class RecordedFrame
{
    public DateTime Time { get; set; }
    public string? ConnectedBssid { get; set; }
    public List<BssInfo> Networks { get; set; } = new();
}

/// <summary>
/// Writes scans to a .wificrec file: gzip-compressed JSON, one frame per line. The file is flushed after every
/// frame, so a recording survives a crash or a power loss.
/// </summary>
public sealed class Recorder : IDisposable
{
    private static readonly JsonSerializerOptions Options = new();

    private readonly FileStream _file;
    private readonly GZipStream _gzip;
    private readonly StreamWriter _writer;

    public Recorder(string path)
    {
        Path = path;
        _file = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.Read);
        _gzip = new GZipStream(_file, CompressionLevel.Optimal);
        _writer = new StreamWriter(_gzip, new UTF8Encoding(false));
        Started = DateTime.Now;
    }

    public string Path { get; }
    public DateTime Started { get; }
    public int FrameCount { get; private set; }

    public void Write(RecordedFrame frame)
    {
        _writer.WriteLine(JsonSerializer.Serialize(frame, Options));
        _writer.Flush();
        _gzip.Flush();
        FrameCount++;
    }

    public static List<RecordedFrame> Load(string path)
    {
        var frames = new List<RecordedFrame>();
        using var file = File.OpenRead(path);
        using var gzip = new GZipStream(file, CompressionMode.Decompress);
        using var reader = new StreamReader(gzip, Encoding.UTF8);
        try
        {
            string? line;
            while ((line = reader.ReadLine()) != null)
            {
                if (line.Length == 0) continue;
                var frame = JsonSerializer.Deserialize<RecordedFrame>(line, Options);
                if (frame != null) frames.Add(frame);
            }
        }
        catch (Exception ex) when (ex is JsonException or InvalidDataException or EndOfStreamException)
        {
            // A recording that was cut off (crash, power loss) still plays up to the last complete frame.
        }
        frames.Sort((a, b) => a.Time.CompareTo(b.Time));
        return frames;
    }

    public void Dispose()
    {
        _writer.Dispose();
        _gzip.Dispose();
        _file.Dispose();
    }
}
