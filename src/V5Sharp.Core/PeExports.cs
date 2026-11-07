using System.Buffers.Binary;

namespace V5Sharp.Core;

/// <summary>
/// 极简 PE 导出表解析：只需要"某个导出函数的 RVA"。
///
/// 用途：64 位宿主向 32 位(WOW64)目标注入时，必须使用 32 位地址空间里
/// 的 LoadLibraryW。做法是
///   1) 从 SysWOW64\kernel32.dll 上读出 LoadLibraryW 的 RVA（跨版本稳定）；
///   2) 用 EnumProcessModulesEx(LIST_MODULES_32BIT) 拿到目标里 32 位
///      kernel32 的加载基址；
///   3) 基址 + RVA 即为可在目标内调用的地址。这样就无需再带一个 32 位进程。
/// </summary>
public static class PeExports
{
    public static uint GetExportRva(string filePath, string exportName)
    {
        byte[] data = File.ReadAllBytes(filePath);
        return GetExportRva(data, exportName);
    }

    public static uint GetExportRva(byte[] data, string exportName)
    {
        int peOff = BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(0x3C));
        if (BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(peOff)) != 0x00004550)
            throw new BadImageFormatException("不是有效的 PE 文件");

        int coff = peOff + 4;
        ushort numSections = BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(coff + 2));
        ushort optSize = BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(coff + 16));
        int opt = coff + 20;
        ushort magic = BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(opt));
        int ddOff = opt + (magic == 0x20B ? 112 : 96);

        uint expRva = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(ddOff));
        if (expRva == 0) throw new InvalidOperationException("该模块没有导出表");

        var sections = new List<(uint Va, uint VSize, uint RawSize, uint RawPtr)>();
        int secOff = opt + optSize;
        for (int i = 0; i < numSections; i++)
        {
            int o = secOff + i * 40;
            sections.Add((
                BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(o + 12)),
                BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(o + 8)),
                BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(o + 16)),
                BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(o + 20))));
        }

        int RvaToOff(uint rva)
        {
            foreach (var s in sections)
            {
                uint size = Math.Max(s.VSize, s.RawSize);
                if (rva >= s.Va && rva < s.Va + size)
                {
                    uint delta = rva - s.Va;
                    if (delta >= s.RawSize) break;
                    return (int)(s.RawPtr + delta);
                }
            }
            throw new BadImageFormatException($"RVA 0x{rva:X} 无法映射到文件偏移");
        }

        int expOff = RvaToOff(expRva);
        uint nameCount = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(expOff + 24));
        uint funcsRva = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(expOff + 28));
        uint namesRva = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(expOff + 32));
        uint ordsRva = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(expOff + 36));

        int namesOff = RvaToOff(namesRva);
        int ordsOff = RvaToOff(ordsRva);
        int funcsOff = RvaToOff(funcsRva);

        for (int i = 0; i < nameCount; i++)
        {
            uint nameRva = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(namesOff + i * 4));
            int nameOff = RvaToOff(nameRva);
            int end = Array.IndexOf(data, (byte)0, nameOff);
            string name = System.Text.Encoding.ASCII.GetString(data, nameOff, end - nameOff);
            if (!string.Equals(name, exportName, StringComparison.Ordinal)) continue;

            ushort ord = BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(ordsOff + i * 2));
            return BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(funcsOff + ord * 4));
        }
        throw new EntryPointNotFoundException($"导出 {exportName} 不存在");
    }
}
