using System.Buffers.Binary;

namespace V5Sharp.Core;

/// <summary>判断磁盘上的 exe 是 32 位还是 64 位（用于选注入模块 / 选注入方式）</summary>
public static class PeInfo
{
    public static bool Is32Bit(string exePath)
    {
        using var fs = new FileStream(exePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        Span<byte> buf = stackalloc byte[0x400];
        int read = fs.Read(buf);
        if (read < 0x40) return true;

        int peOff = BinaryPrimitives.ReadInt32LittleEndian(buf[0x3C..]);
        if (peOff + 26 > read) return true;
        if (BinaryPrimitives.ReadUInt32LittleEndian(buf[peOff..]) != 0x00004550) return true;

        int opt = peOff + 24;
        ushort magic = BinaryPrimitives.ReadUInt16LittleEndian(buf[opt..]);
        return magic != 0x20B;   // 0x10B = PE32, 0x20B = PE32+
    }
}
