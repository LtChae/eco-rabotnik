// Extracts files from a .NET single-file bundle (apphost). Usage: bundlex <exe> <outDir> [nameFilterSubstring]
using System;
using System.IO;
using System.IO.Compression;
using System.Linq;

var exe = args[0]; var outDir = args[1]; var filter = args.Length > 2 ? args[2] : null;
byte[] sig = { 0x8b, 0x12, 0x02, 0xb9, 0x6a, 0x61, 0x20, 0x38, 0x72, 0x7b, 0x93, 0x02, 0x14, 0xd7, 0xa0, 0x32,
               0x13, 0xf5, 0xb9, 0xe6, 0xef, 0xae, 0x33, 0x18, 0xee, 0x3b, 0x2d, 0xce, 0x24, 0xb3, 0x6a, 0xae };

using var fs = File.OpenRead(exe);
// The signature lives in the apphost portion near the start; scan the first 16 MB.
var scanLen = (int)Math.Min(fs.Length, 16 * 1024 * 1024);
var buf = new byte[scanLen]; fs.ReadExactly(buf, 0, scanLen);
int pos = -1;
for (int i = 8; i <= scanLen - sig.Length; i++)
{
    if (buf[i] != sig[0]) continue;
    if (buf.AsSpan(i, sig.Length).SequenceEqual(sig)) { pos = i; break; }
}
if (pos < 0) { Console.WriteLine("Bundle signature not found"); return 1; }
long headerOffset = BitConverter.ToInt64(buf, pos - 8);
Console.WriteLine($"Header offset: {headerOffset}");

fs.Position = headerOffset;
using var br = new BinaryReader(fs);
uint major = br.ReadUInt32(); uint minor = br.ReadUInt32(); int count = br.ReadInt32(); string bundleId = br.ReadString();
if (major >= 2) { br.ReadInt64(); br.ReadInt64(); br.ReadInt64(); br.ReadInt64(); br.ReadUInt64(); }
Console.WriteLine($"Bundle v{major}.{minor}, {count} files, id {bundleId}");

var entries = Enumerable.Range(0, count).Select(_ =>
{
    long off = br.ReadInt64(); long size = br.ReadInt64(); long csize = major >= 6 ? br.ReadInt64() : 0;
    byte type = br.ReadByte(); string path = br.ReadString();
    return (off, size, csize, type, path);
}).ToList();

int written = 0;
foreach (var e in entries)
{
    if (filter != null && !e.path.Contains(filter, StringComparison.OrdinalIgnoreCase)) continue;
    var dest = Path.Combine(outDir, e.path);
    Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
    fs.Position = e.off;
    using var o = File.Create(dest);
    if (e.csize > 0) { using var ds = new DeflateStream(new SubStream(fs, e.csize), CompressionMode.Decompress); ds.CopyTo(o); }
    else new SubStream(fs, e.size).CopyTo(o);
    written++;
}
Console.WriteLine($"Extracted {written} files to {outDir}");
return 0;

class SubStream(Stream inner, long length) : Stream
{
    long remaining = length;
    public override int Read(byte[] buffer, int offset, int count)
    {
        if (remaining <= 0) return 0;
        int n = inner.Read(buffer, offset, (int)Math.Min(count, remaining)); remaining -= n; return n;
    }
    public override bool CanRead => true; public override bool CanSeek => false; public override bool CanWrite => false;
    public override long Length => length; public override long Position { get => length - remaining; set => throw new NotSupportedException(); }
    public override void Flush() { } public override long Seek(long o, SeekOrigin s) => throw new NotSupportedException();
    public override void SetLength(long v) => throw new NotSupportedException(); public override void Write(byte[] b, int o, int c) => throw new NotSupportedException();
}
