namespace IcarusStarlink.App.Tests;

/// <summary>
/// Fake pak contents that pass the import's "is this a real pak" check (ModLints.CheckIsPak): the
/// given bytes, then a pak footer with the magic and version 11 (UE 4.27), as
/// PrebuiltPakImporterTests.WriteMinimalPak builds. Every pak of a download is checked since
/// 2026-10-28, so tests' stand-in bytes ("pak1", [1, 2, 3]) need the footer.
/// </summary>
internal static class TestPaks
{
    public static byte[] Bytes(params byte[] content) =>
        [.. content, .. new byte[64], 0xE1, 0x12, 0x6F, 0x5A, .. BitConverter.GetBytes(11), .. new byte[40]];

    public static byte[] Bytes(string content) => Bytes(System.Text.Encoding.UTF8.GetBytes(content));
}
