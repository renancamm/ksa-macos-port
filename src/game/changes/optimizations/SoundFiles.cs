using System;
using System.Collections.Generic;
using System.IO;
using System.IO.MemoryMappedFiles;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using Brutal.FmodApi;
using HarmonyLib;
using KSA;

namespace KsaMetal.Hooks.Optimizations;

// Sound file mapping. The game loads its sounds that aren't streamed from disk (GameAudio.CreateFmodSound) as FMOD
// samples: FMOD decodes each file into PCM held in memory of its own for the session, about 145 MB in all (the
// engines', the wind's and the interior's loops, decoded from Ogg Vorbis at about ten times their size). A prefix
// decodes such a sound once as the game does, keeps its PCM in a copy in KSA Metal's cache folder (sounds/, named after
// the file's path, size and date and FMOD's version), and gives FMOD that copy mapped from the file
// (Mode.OpenMemoryPoint and OpenRaw, with the PCM's format, channels and rate): FMOD plays it in place, the same
// samples, and macOS can let go of the pages and read them back. Later starts map the copy without decoding. A sound
// the prefix can't do that for loads as the game loads it, and its copy is made again at the next start. Copies
// unused for 60 days (an older game) are deleted when the game starts, as the KTX shim's are.
[Hook(Phase.Default, Switch = nameof(Settings.SoundFileMapping))]
internal static class SoundFiles
{
    private const int HeaderBytes = 32;
    // Zeros after the PCM, inside the mapping, for whatever FMOD reads past the last sample when it interpolates
    private const int TailBytes = 64;
    private static readonly List<(MemoryMappedFile File, MemoryMappedViewAccessor View)> Maps = new();
    private static string _folder = "";
    private static uint _version;
    private static long _bytes;
    private static int _count;

    public static string Apply(Harmony harmony)
    {
        var create = Find.Method(typeof(GameAudio), nameof(GameAudio.CreateFmodSound));
        harmony.Patch(create, prefix: new HarmonyMethod(typeof(SoundFiles), nameof(Creating)));
        var root = Settings.CacheDir is { Length: > 0 } dir ? dir
            : Path.Combine(Environment.GetEnvironmentVariable("HOME") ?? "", "Library", "Caches", "ksa-metal");
        _folder = Path.Combine(root, "sounds");
        Task.Run(Clean);
        return "on";
    }

    private static unsafe bool Creating(SoundFileReference soundReference, ref Sound sound, ref Result result, ref bool __result)
    {
        if (soundReference.StreamFromDisk)
            return true;
        string? file = null;
        try
        {
            if (_version == 0 && GameAudio.System.TryGetVersion(out var version, out _) == Result.Ok)
                _version = version;
            var path = soundReference.ModPath;
            file = CopyPath(path);
            if (file == null || !(File.Exists(file) || Decode(path, soundReference.Mode, file)))
                return true;
            var (pcm, header) = Map(file);
            var info = new CreateSoundExInfo
            {
                Length = header.Bytes, NumChannels = header.Channels, DefaultFrequency = header.Frequency,
                Format = (SoundFormat)header.Format,
            };
            var mode = soundReference.Mode | Mode.OpenMemoryPoint | Mode.OpenRaw;
            var made = GameAudio.System.TryCreateSound(new ReadOnlySpan<byte>((void*)pcm, (int)header.Bytes), mode, in info,
                out var mapped);
            if (made != Result.Ok)
                throw new InvalidDataException($"FMOD: {made}");
            try
            {
                soundReference.LoopData?.Apply(mapped);
            }
            catch
            {
                mapped.TryRelease();
                throw;
            }
            sound = mapped;
            result = made;
            lock (Maps)
            {
                _bytes += header.Bytes;
                if (++_count % 10 == 0)
                    Hooks.Log($"{nameof(SoundFiles)}: {_count} sounds mapped, {_bytes >> 20} MB");
            }
            __result = true;
            return false;
        }
        catch (Exception e)
        {
            Hooks.Log($"{nameof(SoundFiles)}: {Path.GetFileName(soundReference.ModPath)} loads as the game loads it: {Hooks.Describe(e)}");
            try
            {
                if (file != null)
                    File.Delete(file);
            }
            catch (IOException)
            {
            }
            return true;
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Header
    {
        public uint Magic, Bytes;
        public int Format, Channels, Frequency, Reserved0, Reserved1, Reserved2;
    }

    private const uint Magic = 0x444E534B;  // "KSND"

    // The copy's name: the file's path, size and date, and FMOD's version
    private static string? CopyPath(string path)
    {
        var info = new FileInfo(path);
        if (!info.Exists)
            return null;
        ulong h = 1469598103934665603ul;  // FNV-1a
        foreach (char c in Path.GetFullPath(path))
            h = (h ^ c) * 1099511628211ul;
        h = (h ^ (ulong)info.Length) * 1099511628211ul;
        h = (h ^ (ulong)info.LastWriteTimeUtc.Ticks) * 1099511628211ul;
        h = (h ^ _version) * 1099511628211ul;
        return Path.Combine(_folder, $"{h:x16}");
    }

    // FMOD decodes the file as the game has it decode it (a sample in memory); its PCM goes to the copy
    private static unsafe bool Decode(string path, Mode mode, string file)
    {
        var data = File.ReadAllBytes(path);
        var info = new CreateSoundExInfo { Length = (uint)data.Length };
        if (GameAudio.System.TryCreateSound(new ReadOnlySpan<byte>(data), mode | Mode.OpenMemory, in info, out var sound) != Result.Ok)
            return false;
        try
        {
            if (sound.TryGetFormat(out _, out var format, out var channels, out _) != Result.Ok ||
                sound.TryGetDefaults(out var frequency, out _) != Result.Ok ||
                sound.TryGetLength(out var bytes, Brutal.FmodApi.TimeUnit.PcmBytes) != Result.Ok || bytes == 0 ||
                format is not (SoundFormat.Pcm8 or SoundFormat.Pcm16 or SoundFormat.Pcm24 or SoundFormat.Pcm32 or SoundFormat.PcmFloat) ||
                sound.TryLock(0, bytes, out var p1, out var p2, out var l1, out var l2) != Result.Ok)
                return false;
            try
            {
                if (l1 + l2 != bytes)
                    return false;
                var header = new Header
                {
                    Magic = Magic, Bytes = bytes, Format = (int)format, Channels = channels, Frequency = (int)frequency,
                };
                Directory.CreateDirectory(_folder);
                var temp = $"{file}.{Environment.ProcessId}.{Environment.CurrentManagedThreadId}";
                using (var stream = new FileStream(temp, FileMode.Create, FileAccess.Write))
                {
                    stream.Write(new ReadOnlySpan<byte>(&header, HeaderBytes));
                    stream.Write(new ReadOnlySpan<byte>((void*)p1, (int)l1));
                    if (l2 > 0)
                        stream.Write(new ReadOnlySpan<byte>((void*)p2, (int)l2));
                    stream.Write(stackalloc byte[TailBytes]);
                }
                File.Move(temp, file, overwrite: true);
                return true;
            }
            finally
            {
                sound.TryUnlock(p1, p2, l1, l2);
            }
        }
        finally
        {
            sound.TryRelease();
        }
    }

    // The copy mapped (private, copy on write) for the session: the PCM's start and the header, checked
    private static unsafe (nint Pcm, Header Header) Map(string file)
    {
        var length = new FileInfo(file).Length;
        var mapped = MemoryMappedFile.CreateFromFile(file, FileMode.Open, null, 0, MemoryMappedFileAccess.CopyOnWrite);
        var view = mapped.CreateViewAccessor(0, length, MemoryMappedFileAccess.CopyOnWrite);
        byte* start = null;
        view.SafeMemoryMappedViewHandle.AcquirePointer(ref start);
        var header = *(Header*)start;
        if (header.Magic != Magic || header.Bytes + (long)HeaderBytes + TailBytes != length ||
            header.Channels is < 1 or > 32 || header.Frequency <= 0 ||
            (SoundFormat)header.Format is not (SoundFormat.Pcm8 or SoundFormat.Pcm16 or SoundFormat.Pcm24 or SoundFormat.Pcm32 or SoundFormat.PcmFloat))
        {
            view.SafeMemoryMappedViewHandle.ReleasePointer();
            view.Dispose();
            mapped.Dispose();
            File.Delete(file);
            throw new InvalidDataException("its copy doesn't match, made again next start");
        }
        File.SetLastWriteTimeUtc(file, DateTime.UtcNow);  // used now
        lock (Maps)
            Maps.Add((mapped, view));  // mapped while the game runs: FMOD plays from it
        return ((nint)(start + HeaderBytes), header);
    }

    // Copies no start used in 60 days, and temporary files a failed write left a day ago
    private static void Clean()
    {
        try
        {
            if (!Directory.Exists(_folder))
                return;
            foreach (var file in Directory.EnumerateFiles(_folder))
            {
                var age = DateTime.UtcNow - File.GetLastWriteTimeUtc(file);
                if (age > TimeSpan.FromDays(60) || Path.GetFileName(file).Contains('.') && age > TimeSpan.FromDays(1))
                    File.Delete(file);
            }
        }
        catch (Exception e)
        {
            Hooks.Log($"{nameof(SoundFiles)}: cleaning the copies, {Hooks.Describe(e)}");
        }
    }
}
