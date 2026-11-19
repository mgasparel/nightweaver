using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Win32.SafeHandles;

namespace Nightweaver.Evidence
{
    // Keep all enumerated files read-locked and all directory components pinned
    // until inspection ends. A path check followed by an ordinary open would
    // otherwise allow a junction replacement to redirect a payload read.
    public sealed class UnattendedArchiveReader : IDisposable
    {
        private readonly string root;
        private readonly List<SafeFileHandle> directories = new List<SafeFileHandle>();
        private readonly Dictionary<string, FileStream> files = new Dictionary<string, FileStream>(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, string> hashes = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        public long TotalBytes { get; private set; }

        public UnattendedArchiveReader(string directory)
        {
            if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
                throw new PlatformNotSupportedException("Archive inspection requires Windows.");
            root = Path.GetFullPath(directory).TrimEnd(Path.DirectorySeparatorChar);
            if (root.Length < 3 || root.Length > 4096 || root[1] != ':' || root[2] != '\\')
                throw new InvalidDataException("Archive must be on a local drive.");
            try
            {
                var path = Path.GetPathRoot(root);
                PinDirectory(path);
                foreach (var segment in root.Substring(path.Length).Split('\\'))
                {
                    if (segment.Length == 0) continue;
                    path = Path.Combine(path, segment);
                    PinDirectory(path);
                }
                var pending = new Queue<string>();
                pending.Enqueue(root);
                var entries = 0;
                while (pending.Count > 0)
                {
                    foreach (var entry in Directory.EnumerateFileSystemEntries(pending.Dequeue()))
                    {
                        if (++entries > 16384) throw new InvalidDataException("Archive entry limit exceeded.");
                        var relative = Path.GetRelativePath(root, entry).Replace('\\', '/');
                        ValidateRelative(relative);
                        var attributes = File.GetAttributes(entry);
                        if ((attributes & FileAttributes.ReparsePoint) != 0)
                            throw new InvalidDataException("Archive links are unsupported.");
                        if ((attributes & FileAttributes.Directory) != 0)
                        {
                            if (relative != "payloads" && !relative.StartsWith("payloads/", StringComparison.Ordinal))
                                throw new InvalidDataException("Unexpected archive directory.");
                            PinDirectory(entry);
                            pending.Enqueue(entry);
                            continue;
                        }
                        if (relative != "manifest.json" && relative != "samples.jsonl" && !relative.StartsWith("payloads/", StringComparison.Ordinal))
                            throw new InvalidDataException("Unexpected archive file.");
                        var handle = Open(entry, 0x80000000, 1, false);
                        FileStream stream;
                        try { stream = new FileStream(handle, FileAccess.Read); }
                        catch { handle.Dispose(); throw; }
                        try { files.Add(relative, stream); }
                        catch { stream.Dispose(); throw; }
                        var maximum = relative == "samples.jsonl" ? 32L * 1024 * 1024 : 1024L * 1024;
                        if (stream.Length > maximum)
                            throw new InvalidDataException("Archive file size limit exceeded.");
                        TotalBytes = checked(TotalBytes + stream.Length);
                        if (TotalBytes > 256L * 1024 * 1024)
                            throw new InvalidDataException("Archive size limit exceeded.");
                    }
                }
            }
            catch { Dispose(); throw; }
        }

        public static void ValidateRelative(string relative)
        {
            if (String.IsNullOrEmpty(relative) || relative.Length > 240 || relative.Contains("\\") || relative.Contains(":"))
                throw new InvalidDataException("Invalid archive path.");
            foreach (var part in relative.Split('/'))
            {
                if (part.Length == 0 || part == "." || part == ".." || part.EndsWith(".", StringComparison.Ordinal))
                    throw new InvalidDataException("Invalid archive path.");
                foreach (var character in part)
                    if (!(character >= 'a' && character <= 'z') && !(character >= 'A' && character <= 'Z') &&
                        !(character >= '0' && character <= '9') && character != '.' && character != '_' && character != '-')
                        throw new InvalidDataException("Invalid archive path.");
                var stem = part.Split('.')[0].ToUpperInvariant();
                if (stem == "CON" || stem == "PRN" || stem == "AUX" || stem == "NUL" ||
                    (stem.Length == 4 && (stem.StartsWith("COM", StringComparison.Ordinal) || stem.StartsWith("LPT", StringComparison.Ordinal)) && stem[3] >= '1' && stem[3] <= '9'))
                    throw new InvalidDataException("Invalid archive path.");
            }
        }

        private FileStream FileFor(string relative)
        {
            ValidateRelative(relative);
            FileStream stream;
            if (!files.TryGetValue(relative, out stream)) throw new InvalidDataException("Archive file is missing.");
            return stream;
        }

        public byte[] Read(string relative, int maximum)
        {
            var stream = FileFor(relative);
            if (stream.Length > maximum) throw new InvalidDataException("Archive file size limit exceeded.");
            stream.Position = 0;
            var bytes = new byte[(int)stream.Length];
            var offset = 0;
            while (offset < bytes.Length)
            {
                var count = stream.Read(bytes, offset, bytes.Length - offset);
                if (count == 0) throw new InvalidDataException("Archive file is incomplete.");
                offset += count;
            }
            return bytes;
        }

        public long Length(string relative) { return FileFor(relative).Length; }

        public string Sha256(string relative)
        {
            var stream = FileFor(relative);
            string hash;
            if (!hashes.TryGetValue(relative, out hash))
            {
                stream.Position = 0;
                using (var sha = SHA256.Create()) hash = BitConverter.ToString(sha.ComputeHash(stream)).Replace("-", "").ToLowerInvariant();
                hashes.Add(relative, hash);
            }
            return hash;
        }

        public static object Json(byte[] bytes)
        {
            var text = new UTF8Encoding(false, true).GetString(bytes);
            using (var document = JsonDocument.Parse(text, new JsonDocumentOptions { MaxDepth = 16 }))
                return ConvertElement(document.RootElement);
        }

        private static object ConvertElement(JsonElement element)
        {
            switch (element.ValueKind)
            {
                case JsonValueKind.Object:
                    var values = new Dictionary<string, object>(StringComparer.Ordinal);
                    foreach (var property in element.EnumerateObject())
                    {
                        if (values.ContainsKey(property.Name)) throw new InvalidDataException("Duplicate JSON property.");
                        values.Add(property.Name, ConvertElement(property.Value));
                    }
                    return values;
                case JsonValueKind.Array:
                    var items = new List<object>();
                    foreach (var item in element.EnumerateArray()) items.Add(ConvertElement(item));
                    return items.ToArray();
                case JsonValueKind.String: return element.GetString();
                case JsonValueKind.Number:
                    long integer;
                    return element.TryGetInt64(out integer) ? (object)integer : element.GetDouble();
                case JsonValueKind.True: return true;
                case JsonValueKind.False: return false;
                case JsonValueKind.Null: return null;
                default: throw new InvalidDataException("Unsupported JSON value.");
            }
        }

        private void PinDirectory(string path)
        {
            if (directories.Count >= 128) throw new InvalidDataException("Archive directory limit exceeded.");
            directories.Add(Open(path, 0x80, 3, true));
        }

        private static SafeFileHandle Open(string path, uint access, uint share, bool directory)
        {
            var handle = CreateFileW("\\\\?\\" + path, access, share, IntPtr.Zero, 3,
                0x00200000u | (directory ? 0x02000000u : 0u), IntPtr.Zero);
            if (handle.IsInvalid) { handle.Dispose(); throw new IOException("Archive cannot be read."); }
            FileInformation information;
            if (!GetFileInformationByHandle(handle, out information) || GetFileType(handle) != 1 ||
                (information.Attributes & 0x400) != 0 || ((information.Attributes & 0x10) != 0) != directory ||
                (!directory && information.Links != 1))
            {
                handle.Dispose();
                throw new InvalidDataException("Archive entry is not a regular file or directory.");
            }
            return handle;
        }

        public void Dispose()
        {
            foreach (var file in files.Values) file.Dispose();
            files.Clear();
            for (var index = directories.Count - 1; index >= 0; index--) directories[index].Dispose();
            directories.Clear();
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct FileInformation
        {
            public uint Attributes;
            public System.Runtime.InteropServices.ComTypes.FILETIME Created, Accessed, Written;
            public uint Volume, SizeHigh, SizeLow, Links, IndexHigh, IndexLow;
        }

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern SafeFileHandle CreateFileW(string name, uint access, uint share, IntPtr security, uint disposition, uint flags, IntPtr template);
        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool GetFileInformationByHandle(SafeFileHandle file, out FileInformation information);
        [DllImport("kernel32.dll")]
        private static extern uint GetFileType(SafeFileHandle file);
    }
}
