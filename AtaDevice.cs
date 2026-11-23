using System.Buffers.Binary;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace FC1307AVerify;

enum Transport { Auto, Ata, Sat16, Sat12, JMicron }

/// <summary>Result of an ATA command (PIO Data-In).</summary>
sealed class AtaResult
{
    public required Transport Transport { get; init; }
    public required bool Ok { get; init; }
    /// <summary>ATA status register, if returned by the driver/bridge.</summary>
    public byte? Status { get; init; }
    /// <summary>ATA error register, if returned by the driver/bridge.</summary>
    public byte? Error { get; init; }
    public byte[] Data { get; init; } = Array.Empty<byte>();
    public string? Detail { get; init; }
    /// <summary>The USB bridge itself rejected the command (sense 5/20/00); it never reached the device.</summary>
    public bool BridgeRejected { get; init; }
}

/// <summary>
/// Access to a physical drive and sending raw ATA commands:
///  - IOCTL_ATA_PASS_THROUGH (IDE/SATA directly on the controller, incl. IDE-&gt;SATA adapters)
///  - SCSI/ATA Translation (SAT) ATA PASS-THROUGH(16/12) for USB-IDE bridges
///  - JMicron vendor pass-through for JM20329/JM2033x USB bridges
/// </summary>
sealed class AtaDevice : IDisposable
{
    const uint GENERIC_READ = 0x80000000, GENERIC_WRITE = 0x40000000;
    const uint FILE_SHARE_READ = 1, FILE_SHARE_WRITE = 2, OPEN_EXISTING = 3;

    const uint IOCTL_ATA_PASS_THROUGH = 0x0004D02C;
    const uint IOCTL_SCSI_PASS_THROUGH = 0x0004D004;
    public const uint IOCTL_STORAGE_QUERY_PROPERTY = 0x002D1400;
    public const uint IOCTL_DISK_GET_DRIVE_GEOMETRY_EX = 0x000700A0;
    public const uint IOCTL_VOLUME_GET_VOLUME_DISK_EXTENTS = 0x00560000;

    const ushort ATA_FLAGS_DRDY_REQUIRED = 0x01, ATA_FLAGS_DATA_IN = 0x02;
    const byte SCSI_IOCTL_DATA_IN = 1;

    const int TimeoutSeconds = 10;
    const int SenseLength = 32;

    readonly SafeFileHandle _h;
    public string Path { get; }

    AtaDevice(SafeFileHandle h, string path) { _h = h; Path = path; }

    public static AtaDevice Open(string path)
    {
        var h = CreateFile(path, GENERIC_READ | GENERIC_WRITE, FILE_SHARE_READ | FILE_SHARE_WRITE,
            IntPtr.Zero, OPEN_EXISTING, 0, IntPtr.Zero);
        if (h.IsInvalid)
        {
            int err = Marshal.GetLastWin32Error();
            h.Dispose();
            if (err == 5)
                throw new UnauthorizedAccessException($"Access to {path} denied – run the program as administrator.");
            throw new Win32Exception(err, $"Cannot open {path}");
        }
        return new AtaDevice(h, path);
    }

    public void Dispose() => _h.Dispose();

    /// <summary>
    /// Sends a 28-bit ATA command with PIO Data-In over the chosen transport.
    /// Auto tries ATA, SAT16, SAT12 and JMicron in that order.
    /// </summary>
    public AtaResult PioIn(Transport transport, byte command, byte features, byte count, int bytes,
        List<string>? log = null)
    {
        if (transport != Transport.Auto)
            return Send(transport, command, features, count, bytes);

        AtaResult? last = null;
        foreach (var t in new[] { Transport.Ata, Transport.Sat16, Transport.Sat12, Transport.JMicron })
        {
            last = Send(t, command, features, count, bytes);
            // A result with a status register is a real device answer (even with ERR) -> use it.
            if (last.Ok || last.Status.HasValue)
                return last;
            log?.Add($"  {t}: {last.Detail}");
        }
        return last!;
    }

    AtaResult Send(Transport t, byte command, byte features, byte count, int bytes) => t switch
    {
        Transport.Ata => SendAta(command, features, count, bytes),
        Transport.Sat16 => SendSat(16, command, features, count, bytes),
        Transport.Sat12 => SendSat(12, command, features, count, bytes),
        Transport.JMicron => SendSat(0, command, features, count, bytes),
        _ => throw new ArgumentOutOfRangeException(nameof(t)),
    };

    // ---------------------------------------------------------------- ATA_PASS_THROUGH_EX

    AtaResult SendAta(byte command, byte features, byte count, int bytes)
    {
        bool x64 = IntPtr.Size == 8;
        int offDataBufOffset = x64 ? 24 : 20;
        int offPrevTf = x64 ? 32 : 24;
        int offCurTf = offPrevTf + 8;
        int headerSize = offCurTf + 8;          // 48 (x64) / 40 (x86)
        int dataOffset = Align(headerSize, 8);

        var buf = new byte[dataOffset + bytes];
        var s = buf.AsSpan();
        BinaryPrimitives.WriteUInt16LittleEndian(s[0..], (ushort)headerSize);
        BinaryPrimitives.WriteUInt16LittleEndian(s[2..], ATA_FLAGS_DRDY_REQUIRED | ATA_FLAGS_DATA_IN);
        BinaryPrimitives.WriteUInt32LittleEndian(s[8..], (uint)bytes);
        BinaryPrimitives.WriteUInt32LittleEndian(s[12..], TimeoutSeconds);
        if (x64) BinaryPrimitives.WriteUInt64LittleEndian(s[offDataBufOffset..], (ulong)dataOffset);
        else BinaryPrimitives.WriteUInt32LittleEndian(s[offDataBufOffset..], (uint)dataOffset);

        // CurrentTaskFile: Features, SectorCount, LBA low, LBA mid, LBA high, Device, Command, Reserved
        s[offCurTf + 0] = features;
        s[offCurTf + 1] = count;
        s[offCurTf + 5] = 0xA0;
        s[offCurTf + 6] = command;

        if (!Ioctl(IOCTL_ATA_PASS_THROUGH, buf, buf, out _, out int err))
            return new AtaResult { Transport = Transport.Ata, Ok = false, Detail = Win32Msg(err) };

        byte error = s[offCurTf + 0];
        byte status = s[offCurTf + 6];
        bool ok = (status & 0x01) == 0 && (status & 0x80) == 0;
        return new AtaResult
        {
            Transport = Transport.Ata, Ok = ok, Status = status, Error = error,
            Data = buf.AsSpan(dataOffset, bytes).ToArray(),
            Detail = ok ? null : "device reported ERR/BSY",
        };
    }

    // ---------------------------------------------------------------- SCSI_PASS_THROUGH + SAT

    /// <param name="cdbLen">16/12 = SAT ATA PASS-THROUGH, 0 = JMicron vendor CDB 0xDF (JM20329/JM20335-39)</param>
    AtaResult SendSat(int cdbLen, byte command, byte features, byte count, int bytes)
    {
        var tr = cdbLen switch { 16 => Transport.Sat16, 12 => Transport.Sat12, _ => Transport.JMicron };
        bool jmicron = cdbLen == 0;
        if (jmicron) cdbLen = 12;
        bool x64 = IntPtr.Size == 8;
        int offDataBufOffset = x64 ? 24 : 20;
        int offSenseOffset = offDataBufOffset + IntPtr.Size;
        int offCdb = offSenseOffset + 4;
        int headerSize = Align(offCdb + 16, IntPtr.Size);   // 56 (x64) / 44 (x86)
        int senseOffset = headerSize;
        int dataOffset = Align(senseOffset + SenseLength, 8);

        var buf = new byte[dataOffset + bytes];
        var s = buf.AsSpan();
        BinaryPrimitives.WriteUInt16LittleEndian(s[0..], (ushort)headerSize);
        s[6] = (byte)cdbLen;
        s[7] = SenseLength;
        s[8] = SCSI_IOCTL_DATA_IN;
        BinaryPrimitives.WriteUInt32LittleEndian(s[12..], (uint)bytes);
        BinaryPrimitives.WriteUInt32LittleEndian(s[16..], TimeoutSeconds);
        if (x64) BinaryPrimitives.WriteUInt64LittleEndian(s[offDataBufOffset..], (ulong)dataOffset);
        else BinaryPrimitives.WriteUInt32LittleEndian(s[offDataBufOffset..], (uint)dataOffset);
        BinaryPrimitives.WriteUInt32LittleEndian(s[offSenseOffset..], (uint)senseOffset);

        // Protocol 4 = PIO Data-In. Byte 2: T_DIR=1 (in), BYTE_BLOCK=1, T_LENGTH=2 (sector count field)
        var cdb = s.Slice(offCdb, 16);
        if (jmicron)
        {
            // Layout as in smartmontools "-d usbjmicron": DF, R/W, 0, length (BE16), taskfile, device, command
            cdb[0] = 0xDF; cdb[1] = 0x10; // 0x10 = data from device
            BinaryPrimitives.WriteUInt16BigEndian(cdb[3..], (ushort)bytes);
            cdb[5] = features; cdb[6] = count; cdb[10] = 0xA0; cdb[11] = command;
        }
        else if (cdbLen == 16)
        {
            cdb[0] = 0x85; cdb[1] = 4 << 1; cdb[2] = 0x0E;
            cdb[4] = features; cdb[6] = count; cdb[13] = 0xA0; cdb[14] = command;
        }
        else
        {
            cdb[0] = 0xA1; cdb[1] = 4 << 1; cdb[2] = 0x0E;
            cdb[3] = features; cdb[4] = count; cdb[8] = 0xA0; cdb[9] = command;
        }

        if (!Ioctl(IOCTL_SCSI_PASS_THROUGH, buf, buf, out _, out int err))
            return new AtaResult { Transport = tr, Ok = false, Detail = Win32Msg(err) };

        byte scsiStatus = s[2];
        var sense = buf.AsSpan(senseOffset, SenseLength);
        if (scsiStatus == 0)
            return new AtaResult { Transport = tr, Ok = true, Data = buf.AsSpan(dataOffset, bytes).ToArray() };

        // CHECK CONDITION: read ATA status from the ATA Return Descriptor (0x09), if present
        var (key, asc, ascq, ataErr, ataStatus) = ParseSense(sense);
        string detail = $"SCSI status 0x{scsiStatus:X2}, Sense {key:X}/{asc:X2}/{ascq:X2}";
        if (key == 0x5 && asc == 0x20)
            detail += " (rejected by the bridge: command/CDB not supported)";
        else if (key == 0x5 && asc == 0x24)
            detail += " (bridge rejects CDB)";
        return new AtaResult
        {
            Transport = tr, Ok = false, Status = ataStatus, Error = ataErr,
            Data = buf.AsSpan(dataOffset, bytes).ToArray(), Detail = detail,
            BridgeRejected = key == 0x5 && asc == 0x20 && ataStatus == null,
        };
    }

    static (byte key, byte asc, byte ascq, byte? err, byte? status) ParseSense(ReadOnlySpan<byte> sense)
    {
        byte rc = (byte)(sense[0] & 0x7F);
        if (rc is 0x72 or 0x73)
        {
            byte key = (byte)(sense[1] & 0x0F), asc = sense[2], ascq = sense[3];
            int addLen = sense[7];
            for (int i = 8; i + 1 < Math.Min(8 + addLen, sense.Length);)
            {
                byte code = sense[i], len = sense[i + 1];
                if (code == 0x09 && len >= 12 && i + 13 < sense.Length)
                    return (key, asc, ascq, sense[i + 3], sense[i + 13]);
                i += 2 + len;
            }
            return (key, asc, ascq, null, null);
        }
        if (rc is 0x70 or 0x71)
        {
            // Fixed format: for ATA PASS-THROUGH, error is in byte 3, status in byte 4 (information field)
            byte key = (byte)(sense[2] & 0x0F), asc = sense[12], ascq = sense[13];
            bool ataInfo = asc == 0x00 && ascq == 0x1D;
            return (key, asc, ascq, ataInfo ? sense[3] : null, ataInfo ? sense[4] : null);
        }
        return (0, 0, 0, null, null);
    }

    // ---------------------------------------------------------------- Helpers

    bool Ioctl(uint code, byte[] inBuf, byte[] outBuf, out int returned, out int err)
    {
        bool ok = DeviceIoControl(_h, code, inBuf, inBuf.Length, outBuf, outBuf.Length, out returned, IntPtr.Zero);
        err = ok ? 0 : Marshal.GetLastWin32Error();
        return ok;
    }

    static int Align(int v, int a) => (v + a - 1) / a * a;

    static string Win32Msg(int err) => $"Win32 error {err}: {new Win32Exception(err).Message}";

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    internal static extern SafeFileHandle CreateFile(string name, uint access, uint share, IntPtr sa,
        uint disposition, uint flags, IntPtr template);

    [DllImport("kernel32.dll", SetLastError = true)]
    internal static extern bool DeviceIoControl(SafeFileHandle h, uint code, byte[]? inBuf, int inSize,
        byte[]? outBuf, int outSize, out int returned, IntPtr overlapped);
}

/// <summary>Parser for the IDENTIFY DEVICE (0xEC) data block.</summary>
sealed record IdentifyInfo(string Model, string Serial, string Firmware, ulong Sectors, bool Lba48)
{
    public static IdentifyInfo Parse(byte[] d)
    {
        ushort W(int i) => BinaryPrimitives.ReadUInt16LittleEndian(d.AsSpan(i * 2));
        string Str(int first, int last)
        {
            var sb = new StringBuilder();
            for (int i = first; i <= last; i++)
            {
                ushort w = W(i);
                sb.Append((char)(w >> 8)).Append((char)(w & 0xFF));
            }
            return sb.ToString().Trim(' ', '\0');
        }

        bool lba48 = (W(83) & (1 << 10)) != 0;
        ulong lba28 = W(60) | ((ulong)W(61) << 16);
        ulong lba48n = W(100) | ((ulong)W(101) << 16) | ((ulong)W(102) << 32) | ((ulong)W(103) << 48);
        return new IdentifyInfo(Str(27, 46), Str(10, 19), Str(23, 26), lba48 && lba48n > 0 ? lba48n : lba28, lba48);
    }
}
