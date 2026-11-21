using System.Security.Cryptography;
using System.Text.Json;
using Dnf90.Launcher;
using ServerS5A3;

internal static class GameArchiveChecks
{
    internal static void Run(PlayerProfile root, RSA key, Action<bool, string> check)
    {
        byte[] Sign(Bootstrap value)
        {
            var payload = JsonSerializer.SerializeToUtf8Bytes(value, WireJson.Default.Bootstrap);
            return JsonSerializer.SerializeToUtf8Bytes(
                new SignedEnvelope(Convert.ToBase64String(payload),
                    Convert.ToBase64String(key.SignData(payload, HashAlgorithmName.SHA256, RSASignaturePadding.Pss))),
                CoreJson.Default.SignedEnvelope);
        }
        GameArchive Archive(string exeHash, string pvfHash, string? badFileHash = null, string? badPath = null) =>
            new("http://a.example.com/link?d=token", "http://b.example.com/link?d=token", new string('1', 64), 1024,
            [
                new(badPath ?? "DNF.exe", 100, badFileHash ?? exeHash),
                new("Script.pvf", 200, pvfHash),
                new("ImagePacks2/sprite.NPK", 50, new string('3', 64)),
            ]);

        var valid = Archive(root.ExeSha256, root.PvfSha256);
        var verified = Trust.Verify(Sign(new Bootstrap("DNF90-Flutter", 1, 2, root, [], null, null, valid)), root);
        check(verified.Archive is not null && verified.Archive.Files.Length == 3 &&
              verified.Archive.PrimaryUrl.Contains("?d="), "signed whole-archive metadata survives serialization");

        void Reject(GameArchive archive, string name)
        {
            try { Trust.Verify(Sign(new Bootstrap("DNF90-Flutter", 1, 2, root, [], null, null, archive)), root); throw new Exception("accepted: " + name); }
            catch (InvalidDataException) { check(true, name); }
        }
        Reject(Archive(root.ExeSha256, root.PvfSha256, badFileHash: new string('0', 64)), "tampered archive file digest rejected");
        Reject(Archive(new string('9', 64), root.PvfSha256), "archive EXE mismatching trust root rejected");
        Reject(Archive(root.ExeSha256, new string('8', 64)), "archive PVF mismatching trust root rejected");
        Reject(Archive(root.ExeSha256, root.PvfSha256, badPath: "../evil.exe"), "archive path traversal rejected");
        Reject(valid with { Sha256 = "nothex" }, "archive invalid zip digest rejected");
        Reject(valid with { PrimaryUrl = "file:///c:/evil", FallbackUrl = "" }, "archive non-http url rejected");
        Reject(valid with { PrimaryUrl = "http://a.example.com/x", FallbackUrl = "http://a.example.com/x" }, "archive identical links rejected");

        // Disabled channel (null archive) is valid.
        Trust.Verify(Sign(new Bootstrap("DNF90-Flutter", 1, 2, root, [], null, null, null)), root);
        check(true, "disabled archive channel accepted");
    }
}
