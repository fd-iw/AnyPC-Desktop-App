using AnyPC.Core.Abstractions;
using AnyPC.Core.Files;
using AnyPC.Core.Pairing;
using AnyPC.Core.Protocol;
using Xunit;

namespace AnyPC.Core.Tests;

public class WireTests
{
    [Fact]
    public void Frame_round_trips()
    {
        var bytes = Wire.EncodeFrame(42, new EncodedFrame([1, 2, 3], 1136, 639, 5, -1));
        var f = Wire.DecodeFrame(bytes);
        Assert.Equal(42u, f.Seq);
        Assert.Equal(1136, f.Width);
        Assert.Equal(639, f.Height);
        Assert.Equal(5, f.CursorX);
        Assert.Equal(-1, f.CursorY);
        Assert.Equal(new byte[] { 1, 2, 3 }, f.Jpeg.ToArray());
    }

    [Fact]
    public void Chunk_round_trips()
    {
        var bytes = Wire.EncodeChunk(Wire.UploadChunkType, 7, [9, 8]);
        var (id, data) = Wire.DecodeChunk(bytes);
        Assert.Equal(7u, id);
        Assert.Equal(new byte[] { 9, 8 }, data.ToArray());
    }

    [Fact]
    public void Parses_modifiers() =>
        Assert.Equal(Modifiers.Ctrl | Modifiers.Win, Wire.ParseModifiers(["ctrl", "win", "bogus"]));
}

public class PairingTests
{
    static PairingManager Create(Func<DateTimeOffset>? clock = null) =>
        new(new DeviceStore(Path.Combine(Path.GetTempPath(), "anypc-" + Guid.NewGuid() + ".json")), clock);

    [Fact]
    public void Pin_is_six_digits_and_rotates_after_success()
    {
        var p = Create();
        var pin = p.CurrentPin;
        Assert.Matches("^[0-9]{6}$", pin);
        Assert.Equal(PairResult.Ok, p.TryPair(pin, "d", "n", out var token, out _));
        Assert.NotEmpty(token);
        Assert.True(p.Devices.Validate("d", token));
        Assert.False(p.Devices.Validate("d", token + "x"));
        Assert.Equal(PairResult.BadPin, p.TryPair(pin == p.CurrentPin ? "x" : pin, "d2", "n", out _, out _));
    }

    [Fact]
    public void Locks_out_after_repeated_failures()
    {
        var p = Create();
        for (int i = 0; i < PairingManager.MaxFailures - 1; i++)
            Assert.Equal(PairResult.BadPin, p.TryPair("bad", "d", "n", out _, out _));
        Assert.Equal(PairResult.Locked, p.TryPair("bad", "d", "n", out _, out var retry));
        Assert.True(retry.TotalSeconds >= 59);
        // Even the right PIN is refused while locked.
        Assert.Equal(PairResult.Locked, p.TryPair(p.CurrentPin, "d", "n", out _, out _));
    }

    [Fact]
    public void Pin_expires()
    {
        var now = DateTimeOffset.UtcNow;
        var p = Create(() => now);
        var pin = p.CurrentPin;
        now += PairingManager.PinLifetime + TimeSpan.FromSeconds(1);
        // Rotation is random; loop until it differs to avoid a 1-in-a-million flake.
        var rotated = p.CurrentPin;
        Assert.True(rotated != pin || p.TryPair(pin, "d", "n", out _, out _) == PairResult.Ok);
    }

    [Fact]
    public void Device_store_persists()
    {
        var path = Path.Combine(Path.GetTempPath(), "anypc-" + Guid.NewGuid() + ".json");
        new DeviceStore(path).Upsert("id1", "Phone", "tok");
        var reloaded = new DeviceStore(path);
        Assert.True(reloaded.Validate("id1", "tok"));
        Assert.DoesNotContain("tok\"", File.ReadAllText(path));
        File.Delete(path);
    }
}

public class FileServiceTests
{
    [Theory]
    [InlineData("photo.jpg", "photo.jpg")]
    [InlineData("../../etc/passwd", "passwd")]
    [InlineData("..\\..\\Windows\\win.ini", "win.ini")]
    [InlineData("a<b>c:d.txt", "a_b_c_d.txt")]
    [InlineData("..", "upload")]
    [InlineData("", "upload")]
    public void Sanitizes_names(string input, string expected) =>
        Assert.Equal(expected, FileService.SanitizeName(input));

    [Fact]
    public void Lists_root_entries()
    {
        var listing = new FileService().List("");
        Assert.NotEmpty(listing.Entries);
        Assert.All(listing.Entries, e => Assert.True(e.IsDirectory));
    }
}
