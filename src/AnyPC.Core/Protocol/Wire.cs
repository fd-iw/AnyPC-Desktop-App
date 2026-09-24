using System.Buffers.Binary;
using AnyPC.Core.Abstractions;

namespace AnyPC.Core.Protocol;

public static class Wire
{
    public const int Version = 1;
    public const int DefaultPort = 47800;
    public const string Path = "/anypc";
    public const string ServiceType = "_anypc._tcp";

    public const byte FrameType = 0x01;
    public const byte DownloadChunkType = 0x02;
    public const byte UploadChunkType = 0x03;

    public const int FrameHeaderSize = 13;
    public const int ChunkHeaderSize = 5;
    public const int ChunkSize = 64 * 1024;
    public const int MaxMessageSize = 4 * 1024 * 1024;

    public static byte[] EncodeFrame(uint seq, EncodedFrame frame)
    {
        var buf = new byte[FrameHeaderSize + frame.Jpeg.Length];
        buf[0] = FrameType;
        BinaryPrimitives.WriteUInt32BigEndian(buf.AsSpan(1), seq);
        BinaryPrimitives.WriteUInt16BigEndian(buf.AsSpan(5), (ushort)frame.Width);
        BinaryPrimitives.WriteUInt16BigEndian(buf.AsSpan(7), (ushort)frame.Height);
        BinaryPrimitives.WriteUInt16BigEndian(buf.AsSpan(9), CursorCoord(frame.CursorX, frame.Width));
        BinaryPrimitives.WriteUInt16BigEndian(buf.AsSpan(11), CursorCoord(frame.CursorY, frame.Height));
        frame.Jpeg.CopyTo(buf, FrameHeaderSize);
        return buf;
    }

    static ushort CursorCoord(int v, int max) => v < 0 || v >= max ? (ushort)0xFFFF : (ushort)v;

    public static (uint Seq, int Width, int Height, int CursorX, int CursorY, ReadOnlyMemory<byte> Jpeg) DecodeFrame(ReadOnlyMemory<byte> data)
    {
        var s = data.Span;
        if (s.Length < FrameHeaderSize || s[0] != FrameType) throw new FormatException("not a frame");
        int cx = BinaryPrimitives.ReadUInt16BigEndian(s[9..]);
        int cy = BinaryPrimitives.ReadUInt16BigEndian(s[11..]);
        return (BinaryPrimitives.ReadUInt32BigEndian(s[1..]),
            BinaryPrimitives.ReadUInt16BigEndian(s[5..]),
            BinaryPrimitives.ReadUInt16BigEndian(s[7..]),
            cx == 0xFFFF ? -1 : cx, cy == 0xFFFF ? -1 : cy,
            data[FrameHeaderSize..]);
    }

    public static byte[] EncodeChunk(byte type, uint id, ReadOnlySpan<byte> data)
    {
        var buf = new byte[ChunkHeaderSize + data.Length];
        buf[0] = type;
        BinaryPrimitives.WriteUInt32BigEndian(buf.AsSpan(1), id);
        data.CopyTo(buf.AsSpan(ChunkHeaderSize));
        return buf;
    }

    public static (uint Id, ReadOnlyMemory<byte> Data) DecodeChunk(ReadOnlyMemory<byte> data)
    {
        if (data.Length < ChunkHeaderSize) throw new FormatException("chunk too short");
        return (BinaryPrimitives.ReadUInt32BigEndian(data.Span[1..]), data[ChunkHeaderSize..]);
    }

    public static Modifiers ParseModifiers(IEnumerable<string>? mods)
    {
        var m = Modifiers.None;
        if (mods is null) return m;
        foreach (var s in mods)
        {
            m |= s switch
            {
                "ctrl" => Modifiers.Ctrl,
                "alt" => Modifiers.Alt,
                "shift" => Modifiers.Shift,
                "win" => Modifiers.Win,
                _ => Modifiers.None,
            };
        }
        return m;
    }

    public static MouseButton? ParseButton(string? b) => b switch
    {
        "left" => MouseButton.Left,
        "right" => MouseButton.Right,
        "middle" => MouseButton.Middle,
        _ => null,
    };

    public static ButtonAction? ParseButtonAction(string? a) => a switch
    {
        "click" or null => ButtonAction.Click,
        "dblclick" => ButtonAction.DoubleClick,
        "down" => ButtonAction.Down,
        "up" => ButtonAction.Up,
        _ => null,
    };

    public static KeyAction? ParseKeyAction(string? a) => a switch
    {
        "press" or null => KeyAction.Press,
        "down" => KeyAction.Down,
        "up" => KeyAction.Up,
        _ => null,
    };
}
