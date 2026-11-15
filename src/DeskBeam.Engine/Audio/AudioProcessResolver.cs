using System.Diagnostics;
using System.Runtime.InteropServices;

namespace DeskBeam.Engine.Audio;

/// <summary>
/// Finds the root process of an application whose audio must be excluded from the stream.
/// WASAPI process loopback can exclude one process tree, so picking the tree root also excludes
/// helper/child processes such as Discord's audio and overlay processes.
/// </summary>
public static class AudioProcessResolver
{
    public readonly record struct ExclusionTarget(int ProcessId, string Name, bool IsFallback);

    public static ExclusionTarget FindExclusionTarget(string excludedProcessNames, int fallbackProcessId)
    {
        var names = ParseNames(excludedProcessNames);
        if (names.Count > 0)
        {
            var nameSet = new HashSet<string>(names, StringComparer.OrdinalIgnoreCase);
            var entries = Snapshot();
            if (entries.Count > 0)
            {
                var matches = entries.Where(e => nameSet.Contains(NormalizeName(e.Name))).ToList();
                if (matches.Count > 0)
                {
                    // Keep only tree roots: a Discord helper whose parent is also Discord belongs
                    // to the main process's tree and is excluded through that root.
                    var roots = matches
                        .Where(e => !entries.Any(parent =>
                            parent.ProcessId == e.ParentProcessId &&
                            nameSet.Contains(NormalizeName(parent.Name))))
                        .ToList();
                    if (roots.Count == 0)
                        roots = matches;

                    // Several disjoint instances can match (a stale helper, a second install).
                    // Process loopback only excludes one tree, so pick the largest tree: that is
                    // the running app the user actually hears.
                    var best = roots
                        .Select(root => (Root: root, Descendants: CountDescendants(entries, root.ProcessId)))
                        .OrderByDescending(x => x.Descendants)
                        .ThenBy(x => x.Root.ProcessId)
                        .First();

                    return new ExclusionTarget((int)best.Root.ProcessId, best.Root.Name, false);
                }
            }
        }

        // Excluding our own tree is the safe neutral target: DeskBeam itself produces no audio,
        // and we deliberately never fall back to a full system loopback that would leak Discord.
        return new ExclusionTarget(fallbackProcessId, "DeskBeam", true);
    }

    public static IReadOnlyList<string> ParseNames(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return [];

        return value
            .Split([',', ';', '\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(NormalizeName)
            .Where(n => n.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static int CountDescendants(List<ProcessEntry> entries, uint rootProcessId)
    {
        var childrenByParent = new Dictionary<uint, List<uint>>();
        foreach (var entry in entries)
        {
            if (!childrenByParent.TryGetValue(entry.ParentProcessId, out var children))
                childrenByParent[entry.ParentProcessId] = children = [];
            children.Add(entry.ProcessId);
        }

        var seen = new HashSet<uint> { rootProcessId };
        var pending = new Stack<uint>();
        pending.Push(rootProcessId);
        var count = 0;

        while (pending.Count > 0)
        {
            if (!childrenByParent.TryGetValue(pending.Pop(), out var children))
                continue;

            foreach (var child in children)
            {
                if (!seen.Add(child))
                    continue;

                count++;
                pending.Push(child);
            }
        }

        return count;
    }

    private static string NormalizeName(string name)
    {
        var trimmed = name.Trim();
        return trimmed.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)
            ? trimmed[..^4]
            : trimmed;
    }

    private sealed record ProcessEntry(uint ProcessId, uint ParentProcessId, string Name);

    private static List<ProcessEntry> Snapshot()
    {
        var result = new List<ProcessEntry>();
        var snapshot = CreateToolhelp32Snapshot(TH32CS_SNAPPROCESS, 0);
        if (snapshot == IntPtr.Zero || snapshot == InvalidHandleValue)
            return result;

        try
        {
            var entry = new PROCESSENTRY32W { dwSize = (uint)Marshal.SizeOf<PROCESSENTRY32W>() };
            if (!Process32FirstW(snapshot, ref entry))
                return result;

            do
            {
                if (entry.th32ProcessID == 0)
                    continue;

                result.Add(new ProcessEntry(entry.th32ProcessID, entry.th32ParentProcessID,
                    entry.szExeFile ?? string.Empty));
            } while (Process32NextW(snapshot, ref entry));
        }
        catch (Exception)
        {
            // The process list is best-effort; falling back to our own PID is always safe.
        }
        finally
        {
            CloseHandle(snapshot);
        }

        return result;
    }

    /// <summary>Convenience for diagnostics; returns a friendly name for the caller's own PID.</summary>
    public static string DescribeCurrentProcess()
    {
        try
        {
            using var process = Process.GetCurrentProcess();
            return process.ProcessName;
        }
        catch (Exception)
        {
            return "DeskBeam";
        }
    }

    private const uint TH32CS_SNAPPROCESS = 0x00000002;
    private static readonly IntPtr InvalidHandleValue = new(-1);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct PROCESSENTRY32W
    {
        public uint dwSize;
        public uint cntUsage;
        public uint th32ProcessID;
        public IntPtr th32DefaultHeapID;
        public uint th32ModuleID;
        public uint cntThreads;
        public uint th32ParentProcessID;
        public int pcPriClassBase;
        public uint dwFlags;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)]
        public string? szExeFile;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr CreateToolhelp32Snapshot(uint dwFlags, uint th32ProcessID);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool Process32FirstW(IntPtr hSnapshot, ref PROCESSENTRY32W lppe);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool Process32NextW(IntPtr hSnapshot, ref PROCESSENTRY32W lppe);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(IntPtr hObject);
}
