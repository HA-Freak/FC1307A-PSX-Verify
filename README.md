# FC1307AVerify

Companion tool for [DanielFergisz/FC1307A-PSX-DVR](https://github.com/DanielFergisz/FC1307A-PSX-DVR).

A Windows tool that checks whether the PSX DVR firmware (`FC1307A_RepairBox_PSX_v1.0.bin`) is correctly
flashed and running on an FC1307A SD-to-IDE adapter. It can check this in two ways:

- **On the hardware** (`verify`): sends the Sony ATA command `0x8E` to the adapter and compares the
  512-byte response with the release firmware.
- **From an SPI flash readback** (`dump`): compares a chip dump read with your programmer against the
  release image.

## 1. Hardware test (`verify`)

Connect the adapter with an SD card to the PC through an IDE-to-USB bridge or an IDE-to-SATA adapter.
Then run as **administrator**:

```
FC1307AVerify list
FC1307AVerify verify 2
```

What it does:

1. `IDENTIFY DEVICE` (0xEC): shows model, firmware revision and capacity. If the adapter reports more than
   128 GB, the firmware's removal of the capacity limit is active.
2. Sony command `0x8E` with Features = `0xEC`, sent twice, as the PSX does at boot. It expects a good
   status and the 512-byte response (`Sony Computer Entertainment Inc.RepairBox … 120` plus binary data,
   the rest 0x00). The response is compared byte by byte with the block at offset `0xC000` of the release
   firmware.
3. `IDENTIFY` again, to check that the adapter still works normally afterwards.

With the original firmware, the adapter rejects `0x8E` (ABRT) and the result is "FAILED".
The tool writes no data to the SD card and refuses to run on the Windows system disk.

Options:

| Option | Meaning |
|---|---|
| `--transport auto\|ata\|sat16\|sat12\|jmicron` | How ATA commands are sent (default: try all in order) |
| `--firmware <bin>` | Use another firmware image as the reference (e.g. a newer release) |
| `--save <file>` | Save the received `0x8E` response |
| `--yes` | Skip the confirmation prompt |

### Diagnostics (`probe`)

```
FC1307AVerify probe 2
```

Sends IDENTIFY, READ SECTOR, READ BUFFER, SMART READ and `0x8E` over every transport, and shows which
commands the connection passes through to the adapter.

### Which USB bridge to use

Many USB-to-IDE bridges only pass a fixed list of ATA commands and block `0x8E` before it reaches the
adapter. In that case the tool reports "CANNOT BE TESTED", not a firmware failure.

| Bridge | USB ID | `0x8E` |
|---|---|---|
| Innostor IS611 (many USB 3.0 IDE/SATA combo adapters, often with a backup button) | `VID_1F75&PID_0611` | **Blocked** (tested) |
| JMicron JM20337, e.g. StarTech USB2SATAIDE, StarTech UNIDOCK2U | `VID_152D&PID_2338` | **Works** (tested), through `--transport jmicron` |
| IDE on a motherboard | – | The most reliable option |

You can find a bridge's USB ID in Device Manager under Details → Hardware IDs.

## 2. SPI flash readback (`dump`)

After flashing, read the chip (BY25D40 / P25D40, 512 KB) back with your programmer, then run:

```
FC1307AVerify dump readback.bin
FC1307AVerify dump readback.bin --firmware FC1307A_RepairBox_PSX_v1.0.bin
```

The tool compares the SHA-256 with the release (`e98af32a…ad559a`). If they differ, it checks the `0x8E`
response block and the handler code separately. With `--firmware` it also lists every range that differs.
Readbacks larger than 512 KB (e.g. from a 1 MB read) are accepted if the rest is empty (0xFF). An all-0xFF
or all-0x00 readback is reported as an empty chip or a bad clip connection.

## Exit codes

| Code | Meaning |
|---|---|
| 0 | Passed |
| 1 | Failed |
| 2 | Error, or cannot be tested with this connection |

## Building

Requires the .NET 9 SDK:

```
dotnet build -c Release
```

Prebuilt binaries are attached to the [GitHub releases](../../releases) and need the [.NET 9 runtime](https://dotnet.microsoft.com/download/dotnet/9.0).

## License

[MIT](LICENSE). The reference values embedded in `Reference.cs` (the 0x8E response block, handler code
bytes and SHA-256) are taken from the
[FC1307A-PSX-DVR](https://github.com/DanielFergisz/FC1307A-PSX-DVR) firmware release; the firmware
itself is not included.
