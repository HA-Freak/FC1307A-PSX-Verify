using System.Buffers.Binary;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Text;

namespace FC1307AVerify;

static class Program
{
    const int ExitPass = 0, ExitFail = 1, ExitError = 2;

    static int Main(string[] args)
    {
        Console.OutputEncoding = Encoding.UTF8;
        Console.WriteLine("FC1307A PSX DVR firmware verification");
        Console.WriteLine("Reference: " + Reference.ReleaseName);
        Console.WriteLine();

        try
        {
            if (args.Length == 0 || args[0] is "-h" or "--help" or "help")
                return Usage();

            var opts = Options.Parse(args.Skip(1).ToArray());
            return args[0].ToLowerInvariant() switch
            {
                "list" => ListDrives(),
                "verify" => Verify(opts),
                "dump" => VerifyDump(opts),
                "probe" => Probe(opts),
                _ => Usage(),
            };
        }
        catch (Exception ex)
        {
            Console.ForegroundColor = ConsoleColor.Red;
            Console.WriteLine("ERROR: " + ex.Message);
            Console.ResetColor();
            return ExitError;
        }
    }

    static int Usage()
    {
        Console.WriteLine("""
            Usage:
              FC1307AVerify list
                  Lists physical drives.

              FC1307AVerify verify <N> [--transport auto|ata|sat16|sat12|jmicron] [--firmware <bin>] [--yes] [--save <file>]
                  Tests the adapter at \\.\PhysicalDrive<N> directly on the hardware:
                    1. IDENTIFY DEVICE (model, firmware revision, capacity)
                    2. Sony command 0x8E (Features=0xEC), sent twice as during PSX boot
                    3. Compares the 512-byte response with the reference firmware
                  No data is written to the SD card.
                  --firmware  use another firmware image as reference (e.g. a newer release)
                  --save      save the received 0x8E response to a file
                  --yes       skip the confirmation prompt

              FC1307AVerify probe <N>
                  Shows which ATA commands the USB bridge/driver passes through, per transport.

              FC1307AVerify dump <spi-readback.bin> [--firmware <bin>]
                  Checks a flash image read back with an SPI programmer (e.g. CH341A).

            Exit code: 0 = passed, 1 = failed, 2 = error or cannot be tested
            """);
        return ExitError;
    }

    // ================================================================== list

    static int ListDrives()
    {
        int sys = SystemDiskNumber();
        Console.WriteLine(" No  Bus      Size        Device");
        Console.WriteLine(" --  -------  ----------  ------------------------------------------");
        for (int n = 0; n < 32; n++)
        {
            var info = DriveInfo.Query(n);
            if (info == null) continue;
            string mark = n == sys ? "  [SYSTEM]" : "";
            Console.WriteLine($" {n,2}  {info.Bus,-7}  {FormatSize(info.SizeBytes),10}  {info.Name}{mark}");
        }
        Console.WriteLine();
        Console.WriteLine("Connect the FC1307A adapter through an IDE->USB or IDE->SATA bridge,");
        Console.WriteLine("then run 'FC1307AVerify verify <No>'.");
        return ExitPass;
    }

    // ================================================================== verify (hardware)

    static int Verify(Options o)
    {
        if (o.Positional.Count < 1)
            throw new ArgumentException("Drive number missing, e.g. 'verify 2'.");

        string arg = o.Positional[0];
        int diskNo = -1;
        string path = int.TryParse(arg, out diskNo) ? $@"\\.\PhysicalDrive{diskNo}" : arg;
        if (diskNo < 0 && arg.StartsWith(@"\\.\PhysicalDrive", StringComparison.OrdinalIgnoreCase))
            int.TryParse(arg[17..], out diskNo);

        if (!IsAdmin())
            throw new UnauthorizedAccessException("Direct ATA access requires running as administrator.");

        if (diskNo >= 0 && diskNo == SystemDiskNumber())
            throw new InvalidOperationException($"Drive {diskNo} is the Windows system disk – aborted.");

        byte[] expected = Reference.ExpectedResponse();
        if (o.Firmware != null)
        {
            var img = File.ReadAllBytes(o.Firmware);
            expected = Reference.ResponseFromImage(img, out int off);
            Console.WriteLine($"Reference response from {Path.GetFileName(o.Firmware)} @0x{off:X5} (image SHA-256 {Reference.Sha256Hex(img)[..16]}…)");
        }

        var dinfo = diskNo >= 0 ? DriveInfo.Query(diskNo) : null;
        Console.WriteLine($"Target: {path}" + (dinfo != null ? $"  –  {dinfo.Name} ({dinfo.Bus}, {FormatSize(dinfo.SizeBytes)})" : ""));

        if (!o.Yes)
        {
            Console.WriteLine();
            Console.WriteLine("This sends IDENTIFY DEVICE and the vendor-specific command 0x8E.");
            Console.WriteLine("Use only with the FC1307A adapter, not with regular hard drives.");
            Console.Write("Continue? [y/N] ");
            var a = Console.ReadLine()?.Trim().ToLowerInvariant();
            if (a is not ("y" or "yes"))
                return ExitError;
        }
        Console.WriteLine();

        var checks = new List<(string Name, Verdict V, string Text)>();
        using var dev = AtaDevice.Open(path);

        // ---- 1. IDENTIFY DEVICE
        var log = new List<string>();
        var id = dev.PioIn(o.Transport, 0xEC, 0x00, 1, 512, log);
        if (!id.Ok)
        {
            Console.WriteLine("IDENTIFY DEVICE failed:");
            log.ForEach(Console.WriteLine);
            Console.WriteLine($"  {id.Transport}: {id.Detail}");
            Console.WriteLine();
            Console.WriteLine("The bridge/driver in use does not pass ATA commands through.");
            Console.WriteLine("Fix: use another IDE-USB adapter (e.g. JMicron JM20337) or an IDE->SATA adapter");
            Console.WriteLine("on a motherboard SATA port.");
            return ExitError;
        }
        Transport transport = id.Transport;
        var ident = IdentifyInfo.Parse(id.Data);
        Console.WriteLine($"Transport       : {Describe(transport)}");
        Console.WriteLine($"Model           : {ident.Model}");
        Console.WriteLine($"Serial number   : {ident.Serial}");
        Console.WriteLine($"Firmware rev.   : {ident.Firmware}");
        Console.WriteLine($"Capacity        : {ident.Sectors:N0} sectors = {FormatSize(ident.Sectors * 512)}{(ident.Lba48 ? " (LBA48)" : " (LBA28)")}");
        checks.Add(("IDENTIFY DEVICE", Verdict.Pass, $"{ident.Model} / {ident.Firmware}"));

        // The original firmware is limited to 128 GB. A reported capacity clearly above that means the
        // limit removal is active; below it the check is inconclusive without knowing the card size.
        const ulong Limit128 = 128UL * 1000 * 1000 * 1000 / 512;
        if (ident.Sectors > Limit128 + Limit128 / 20)
            checks.Add(("Capacity limit", Verdict.Pass, $"{FormatSize(ident.Sectors * 512)} > 128 GB – limit removed"));
        else
            checks.Add(("Capacity limit", Verdict.Info, "card ≤128 GB – limit removal cannot be checked (insert a >128 GB card to test)"));

        // ---- 2. Sony 0x8E, twice as during PSX startup
        Console.WriteLine();
        byte[]? firstResponse = null;
        for (int run = 1; run <= 2; run++)
        {
            var r = dev.PioIn(transport, 0x8E, 0xEC, 1, 512);
            string st = r.Status.HasValue ? $"Status=0x{r.Status:X2}" : "Status=n/a";
            if (r.Error.HasValue) st += $" Error=0x{r.Error:X2}";
            Console.WriteLine($"0x8E run {run}: {(r.Ok ? "OK" : "FAILED")}  {st}{(r.Detail != null ? "  " + r.Detail : "")}");

            if (!r.Ok && r.BridgeRejected)
            {
                // IDENTIFY went through the same transport -> the bridge filters 0x8E (command whitelist)
                checks.Add(("0x8E", Verdict.Untestable,
                    "USB bridge filters the command (whitelist), it never reaches the adapter. " +
                    "Use another bridge (e.g. JMicron JM20337) or an IDE->SATA adapter on a motherboard SATA port."));
                break;
            }
            if (!r.Ok)
            {
                bool aborted = r.Error.HasValue && (r.Error.Value & 0x04) != 0;
                checks.Add(($"0x8E run {run}", Verdict.Fail, aborted
                    ? "command rejected (ABRT) – original firmware, 0x8E not supported"
                    : "command failed: " + r.Detail));
                break;
            }
            if (r.Status.HasValue && (r.Status.Value & 0x08) != 0)
                checks.Add(($"0x8E run {run}", Verdict.Warn, $"DRQ still set after completion (status 0x{r.Status:X2}, expected 0x50)"));

            var cmp = CompareResponse(r.Data, expected);
            checks.Add(($"0x8E run {run} data", cmp.V, cmp.Text));
            if (run == 1)
            {
                firstResponse = r.Data;
                PrintHexHead(r.Data, 128);
                if (o.Save != null)
                {
                    File.WriteAllBytes(o.Save, r.Data);
                    Console.WriteLine($"Response saved: {o.Save}");
                }
            }
            else if (firstResponse != null && !firstResponse.AsSpan().SequenceEqual(r.Data))
                checks.Add(("0x8E consistency", Verdict.Fail, "responses of run 1 and 2 differ"));
        }

        // ---- 3. Sanity check: normal commands still work after 0x8E
        var id2 = dev.PioIn(transport, 0xEC, 0x00, 1, 512);
        checks.Add(("IDENTIFY after 0x8E", id2.Ok ? Verdict.Pass : Verdict.Fail,
            id2.Ok ? "adapter still responds normally" : "adapter no longer responds after 0x8E: " + id2.Detail));

        return PrintSummary(checks);
    }

    // ================================================================== probe (diagnostics)

    /// <summary>
    /// Sends a set of harmless read commands and 0x8E over every transport individually
    /// to find out which commands the bridge passes through.
    /// </summary>
    static int Probe(Options o)
    {
        if (o.Positional.Count < 1 || !int.TryParse(o.Positional[0], out int diskNo))
            throw new ArgumentException("Drive number missing, e.g. 'probe 2'.");
        if (!IsAdmin())
            throw new UnauthorizedAccessException("Direct ATA access requires running as administrator.");
        if (diskNo == SystemDiskNumber())
            throw new InvalidOperationException($"Drive {diskNo} is the Windows system disk – aborted.");

        using var dev = AtaDevice.Open($@"\\.\PhysicalDrive{diskNo}");
        var cmds = new (string Name, byte Cmd, byte Feat)[]
        {
            ("IDENTIFY 0xEC", 0xEC, 0x00), ("READ LBA0 0x20", 0x20, 0x00), ("READ BUF 0xE4", 0xE4, 0x00),
            ("SMART RD 0xB0", 0xB0, 0xD0), ("Sony 0x8E/EC", 0x8E, 0xEC), ("Sony 0x8E/00", 0x8E, 0x00),
        };
        foreach (var t in new[] { Transport.Ata, Transport.Sat16, Transport.Sat12, Transport.JMicron })
            foreach (var (name, cmd, feat) in cmds)
            {
                var r = dev.PioIn(t, cmd, feat, 1, 512);
                string st = r.Status.HasValue ? $" Status=0x{r.Status:X2}" : "";
                if (r.Error.HasValue) st += $" Error=0x{r.Error:X2}";
                string head = r.Ok ? "  Data: " + Encoding.ASCII.GetString(r.Data, 0, 48).Replace('\0', '.') : "";
                Console.WriteLine($"{t,-7} {name,-15} {(r.Ok ? "OK    " : "FAILED")}{st} {r.Detail}{head}");
            }
        return ExitPass;
    }

    static (Verdict V, string Text) CompareResponse(byte[] data, byte[] expected)
    {
        if (data.AsSpan().SequenceEqual(expected))
            return (Verdict.Pass, "512 bytes identical to reference");

        var swapped = SwapWords(data);
        if (swapped.AsSpan().SequenceEqual(expected))
            return (Verdict.Pass, "identical to reference (bridge swaps byte order within 16-bit words)");

        if (data.All(b => b == 0))
            return (Verdict.Fail, "only zero bytes received – bridge transferred no data or 0x8E not supported");

        bool sig = data.AsSpan().StartsWith(Reference.ResponseSignature) || swapped.AsSpan().StartsWith(Reference.ResponseSignature);
        int diff = 0, first = -1;
        for (int i = 0; i < expected.Length; i++)
            if (data[i] != expected[i]) { diff++; if (first < 0) first = i; }
        return sig
            ? (Verdict.Warn, $"Sony signature present, but {diff} bytes differ (from 0x{first:X3}) – different firmware version? use --firmware if so")
            : (Verdict.Fail, $"no Sony signature, {diff} bytes differ");
    }

    // ================================================================== dump (SPI readback)

    static int VerifyDump(Options o)
    {
        if (o.Positional.Count < 1)
            throw new ArgumentException("SPI readback file name missing.");

        var dump = File.ReadAllBytes(o.Positional[0]);
        var checks = new List<(string Name, Verdict V, string Text)>();
        Console.WriteLine($"Readback: {o.Positional[0]} ({dump.Length:N0} bytes)");

        byte[]? refImg = o.Firmware != null ? File.ReadAllBytes(o.Firmware) : null;
        int size = refImg?.Length ?? Reference.ImageSize;
        string refSha = refImg != null ? Reference.Sha256Hex(refImg) : Reference.ReleaseSha256;

        if (dump.Length < size)
        {
            checks.Add(("Size", Verdict.Fail, $"readback smaller than {size:N0} bytes"));
            return PrintSummary(checks);
        }
        if (dump.Length > size)
        {
            bool restBlank = dump.AsSpan(size).IndexOfAnyExcept((byte)0xFF) < 0;
            checks.Add(("Size", restBlank ? Verdict.Info : Verdict.Warn,
                $"readback is {dump.Length:N0} bytes, comparing the first {size:N0}" + (restBlank ? " (rest empty/0xFF)" : " (rest NOT empty!)")));
        }

        var img = dump.AsSpan(0, size);
        if (img.IndexOfAnyExcept((byte)0xFF) < 0 || img.IndexOfAnyExcept((byte)0x00) < 0)
        {
            checks.Add(("Content", Verdict.Fail, "readback is all 0xFF or all 0x00 – chip empty or clip has no contact"));
            return PrintSummary(checks);
        }

        string sha = Reference.Sha256Hex(img);
        Console.WriteLine($"SHA-256 readback : {sha}");
        Console.WriteLine($"SHA-256 reference: {refSha}");
        Console.WriteLine();

        if (sha == refSha)
        {
            checks.Add(("SHA-256", Verdict.Pass, "readback is bit-identical to the reference firmware"));
            return PrintSummary(checks);
        }
        checks.Add(("SHA-256", Verdict.Fail, "readback differs from the reference firmware"));

        // Detailed analysis of the difference
        if (refImg != null)
            ReportDiffRanges(img, refImg, checks);

        int sigAt = img.IndexOf(Reference.ResponseSignature);
        if (sigAt < 0)
            checks.Add(("PSX patch", Verdict.Fail, "no 0x8E response in image – probably original firmware"));
        else
        {
            var expected = refImg != null ? Reference.ResponseFromImage(refImg, out _) : Reference.ExpectedResponse();
            bool respOk = sigAt + 512 <= img.Length && img.Slice(sigAt, 512).SequenceEqual(expected);
            checks.Add(("0x8E response block", respOk ? Verdict.Pass : Verdict.Fail, $"@0x{sigAt:X5} {(respOk ? "correct" : "differs")}"));

            if (refImg == null)
            {
                bool codeOk = img.Slice(Reference.HandlerOffset, Reference.HandlerCode.Length).SequenceEqual(Reference.HandlerCode);
                checks.Add(("0x8E handler code", codeOk ? Verdict.Pass : Verdict.Fail, $"@0x{Reference.HandlerOffset:X5} {(codeOk ? "correct" : "differs")}"));
            }
        }
        if (refImg == null)
            Console.WriteLine("Note: pass the reference file with --firmware for a byte-exact list of differences.");
        return PrintSummary(checks);
    }

    static void ReportDiffRanges(ReadOnlySpan<byte> img, byte[] refImg, List<(string, Verdict, string)> checks)
    {
        var ranges = new List<(int Start, int End)>();
        int total = 0;
        for (int i = 0; i < refImg.Length; i++)
        {
            if (img[i] == refImg[i]) continue;
            total++;
            if (ranges.Count > 0 && i - ranges[^1].End <= 16) ranges[^1] = (ranges[^1].Start, i);
            else ranges.Add((i, i));
        }
        Console.WriteLine($"{total:N0} differing bytes in {ranges.Count} ranges:");
        foreach (var (s, e) in ranges.Take(20))
            Console.WriteLine($"  0x{s:X5}–0x{e:X5} ({e - s + 1} bytes)");
        if (ranges.Count > 20) Console.WriteLine($"  … {ranges.Count - 20} more");
        Console.WriteLine();
        checks.Add(("Differences", Verdict.Info, $"{total:N0} bytes in {ranges.Count} ranges"));
    }

    // ================================================================== output

    enum Verdict { Pass, Info, Warn, Untestable, Fail }

    static int PrintSummary(List<(string Name, Verdict V, string Text)> checks)
    {
        Console.WriteLine();
        Console.WriteLine("Result");
        Console.WriteLine("------");
        foreach (var (name, v, text) in checks)
        {
            Console.ForegroundColor = v switch
            {
                Verdict.Pass => ConsoleColor.Green, Verdict.Warn or Verdict.Untestable => ConsoleColor.Yellow,
                Verdict.Fail => ConsoleColor.Red, _ => ConsoleColor.Gray,
            };
            Console.Write($"[{(v == Verdict.Untestable ? "N/A" : v.ToString().ToUpperInvariant()),-4}] ");
            Console.ResetColor();
            Console.WriteLine($"{name}: {text}");
        }
        Console.WriteLine();

        bool fail = checks.Any(c => c.V == Verdict.Fail);
        bool untestable = checks.Any(c => c.V == Verdict.Untestable);
        bool warn = checks.Any(c => c.V == Verdict.Warn);
        Console.ForegroundColor = fail ? ConsoleColor.Red : untestable || warn ? ConsoleColor.Yellow : ConsoleColor.Green;
        Console.WriteLine(fail ? "==> FAILED"
            : untestable ? "==> CANNOT BE TESTED with this hardware connection"
            : warn ? "==> PASSED WITH WARNINGS" : "==> PASSED – PSX firmware verified");
        Console.ResetColor();
        return fail ? ExitFail : untestable ? ExitError : ExitPass;
    }

    static void PrintHexHead(byte[] data, int len)
    {
        Console.WriteLine($"Response (first {len} of {data.Length} bytes):");
        for (int i = 0; i < len; i += 16)
        {
            var row = data.AsSpan(i, 16);
            string hex = string.Join(" ", row.ToArray().Select(b => b.ToString("x2")));
            string asc = new(row.ToArray().Select(b => b is >= 0x20 and < 0x7F ? (char)b : '.').ToArray());
            Console.WriteLine($"  {i:X4}: {hex}  {asc}");
        }
        int tailNonZero = data.Skip(128).Count(b => b != 0);
        Console.WriteLine($"  Bytes 0x080–0x1FF: {(tailNonZero == 0 ? "all 0x00" : $"{tailNonZero} bytes non-zero")}");
    }

    static string Describe(Transport t) => t switch
    {
        Transport.Ata => "IOCTL_ATA_PASS_THROUGH (IDE/SATA)",
        Transport.Sat16 => "SAT ATA PASS-THROUGH(16) (USB bridge)",
        Transport.Sat12 => "SAT ATA PASS-THROUGH(12) (USB bridge)",
        Transport.JMicron => "JMicron vendor pass-through 0xDF (JM20329/JM2033x)",
        _ => t.ToString(),
    };

    static string FormatSize(ulong bytes) => bytes switch
    {
        0 => "?",
        >= 1_000_000_000_000 => $"{bytes / 1e12:0.00} TB",
        >= 1_000_000_000 => $"{bytes / 1e9:0.0} GB",
        _ => $"{bytes / 1e6:0.0} MB",
    };

    static byte[] SwapWords(byte[] d)
    {
        var r = (byte[])d.Clone();
        for (int i = 0; i + 1 < r.Length; i += 2) (r[i], r[i + 1]) = (r[i + 1], r[i]);
        return r;
    }

    static bool IsAdmin() =>
        new WindowsPrincipal(WindowsIdentity.GetCurrent()).IsInRole(WindowsBuiltInRole.Administrator);

    /// <summary>Disk number holding the Windows drive (-1 if unknown).</summary>
    static int SystemDiskNumber()
    {
        string drive = Path.GetPathRoot(Environment.SystemDirectory)!.TrimEnd('\\');
        using var h = AtaDevice.CreateFile($@"\\.\{drive}", 0, 3, IntPtr.Zero, 3, 0, IntPtr.Zero);
        if (h.IsInvalid) return -1;
        var buf = new byte[64];
        if (!AtaDevice.DeviceIoControl(h, AtaDevice.IOCTL_VOLUME_GET_VOLUME_DISK_EXTENTS, null, 0, buf, buf.Length, out _, IntPtr.Zero))
            return -1;
        // VOLUME_DISK_EXTENTS: DWORD count, (pad), DISK_EXTENT { DWORD DiskNumber, LARGE_INTEGER Start, Length }
        return BinaryPrimitives.ReadInt32LittleEndian(buf.AsSpan(8));
    }
}

sealed record DriveInfo(int Number, string Name, string Bus, ulong SizeBytes)
{
    static readonly string[] BusNames =
    {
        "?", "SCSI", "ATAPI", "ATA", "1394", "SSA", "FC", "USB", "RAID", "iSCSI", "SAS", "SATA",
        "SD", "MMC", "Virtual", "VHD", "Spaces", "NVMe", "SCM", "UFS",
    };

    public static DriveInfo? Query(int n)
    {
        // Access 0 is enough for device properties and geometry, no admin rights needed
        using var h = AtaDevice.CreateFile($@"\\.\PhysicalDrive{n}", 0, 3, IntPtr.Zero, 3, 0, IntPtr.Zero);
        if (h.IsInvalid) return null;

        string name = "?", bus = "?";
        var q = new byte[12]; // STORAGE_PROPERTY_QUERY: StorageDeviceProperty(0), PropertyStandardQuery(0)
        var o = new byte[1024];
        if (AtaDevice.DeviceIoControl(h, AtaDevice.IOCTL_STORAGE_QUERY_PROPERTY, q, q.Length, o, o.Length, out _, IntPtr.Zero))
        {
            string Str(int off)
            {
                int p = BinaryPrimitives.ReadInt32LittleEndian(o.AsSpan(off));
                if (p <= 0 || p >= o.Length) return "";
                int e = Array.IndexOf(o, (byte)0, p);
                return Encoding.ASCII.GetString(o, p, (e < 0 ? o.Length : e) - p).Trim();
            }
            // STORAGE_DEVICE_DESCRIPTOR: VendorIdOffset@12, ProductIdOffset@16, ProductRevisionOffset@20, BusType@28
            name = string.Join(" ", new[] { Str(12), Str(16), Str(20) }.Where(s => s.Length > 0));
            int bt = BinaryPrimitives.ReadInt32LittleEndian(o.AsSpan(28));
            bus = bt >= 0 && bt < BusNames.Length ? BusNames[bt] : bt.ToString();
        }

        ulong size = 0;
        var g = new byte[256];
        if (AtaDevice.DeviceIoControl(h, AtaDevice.IOCTL_DISK_GET_DRIVE_GEOMETRY_EX, null, 0, g, g.Length, out _, IntPtr.Zero))
            size = BinaryPrimitives.ReadUInt64LittleEndian(g.AsSpan(24)); // DISK_GEOMETRY (24 bytes), then DiskSize

        return new DriveInfo(n, name, bus, size);
    }
}

sealed class Options
{
    public List<string> Positional { get; } = new();
    public Transport Transport { get; private set; } = Transport.Auto;
    public string? Firmware { get; private set; }
    public string? Save { get; private set; }
    public bool Yes { get; private set; }

    public static Options Parse(string[] a)
    {
        var o = new Options();
        for (int i = 0; i < a.Length; i++)
        {
            string Next() => i + 1 < a.Length ? a[++i] : throw new ArgumentException($"Value for {a[i]} missing");
            switch (a[i].ToLowerInvariant())
            {
                case "--transport": o.Transport = Enum.Parse<Transport>(Next(), true); break;
                case "--firmware": o.Firmware = Next(); break;
                case "--save": o.Save = Next(); break;
                case "--yes" or "-y": o.Yes = true; break;
                default:
                    if (a[i].StartsWith("--")) throw new ArgumentException($"Unknown option {a[i]}");
                    o.Positional.Add(a[i]);
                    break;
            }
        }
        return o;
    }
}
