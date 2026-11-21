using System.Security.Cryptography;
using System.Text.Json;
using Dnf90.Launcher;
using ServerS5A3;

// format=2 only: the unified GameArtifact is the sole game-resource authority.
// There is no format=1 path, no legacy Archive/Content projection.
internal static class GameArtifactChecks
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
        GameArtifact ArchiveGame(string exe, string pvf) => new("v", "src", 32,
            [new("DNF.exe", 100, exe), new("Script.pvf", 200, pvf)],
            new GameDelivery("archive", URLs: ["http://a.example.com/x?d=t", "http://b.example.com/y?d=t"],
                PackageSha256: new string('1', 64), PackageSize: 1024));

        var v = Trust.Verify(Sign(new Bootstrap("DNF90-Flutter", 2, 2, root, [], null, ArchiveGame(root.ExeSha256, root.PvfSha256))), root);
        check(v.Game.Files.Length == 2 && v.Game.Delivery.Strategy == "archive" &&
              v.Game.Delivery.URLs![0].Contains("?d=") && v.Game.Delivery.PackageSha256 == new string('1', 64),
            "format=2 archive Game validated as sole authority");

        var blob = new GameArtifact("1.2.27", "src", 32,
            [new("DNF.exe", 100, root.ExeSha256), new("Script.pvf", 200, root.PvfSha256, BlobSha256: new string('5', 64), BlobLength: 50, Compression: "gzip")],
            new GameDelivery("blob", Endpoint: "https://blob.example.com/", CertificateSha256: ""));
        var vb = Trust.Verify(Sign(new Bootstrap("DNF90-Flutter", 2, 2, root, [], null, blob)), root);
        check(vb.Game.Version == "1.2.27" && vb.Game.Files.Length == 2 && vb.Game.Delivery.Strategy == "blob",
            "format=2 blob Game validated as sole authority");

        void Reject(GameArtifact game, string name)
        {
            try { Trust.Verify(Sign(new Bootstrap("DNF90-Flutter", 2, 2, root, [], null, game)), root); throw new Exception("accepted: " + name); }
            catch (InvalidDataException) { check(true, name); }
        }
        Reject(ArchiveGame(new string('0', 64), root.PvfSha256), "format=2 Game EXE mismatch rejected");
        Reject(ArchiveGame(root.ExeSha256, new string('8', 64)), "format=2 Game PVF mismatch rejected");
        Reject(ArchiveGame(root.ExeSha256, root.PvfSha256) with
        {
            Delivery = new GameDelivery("archive", URLs: ["file:///c:/evil", ""], PackageSha256: new string('1', 64), PackageSize: 1024)
        }, "format=2 Game bad delivery url rejected");
        Reject(ArchiveGame(root.ExeSha256, root.PvfSha256) with
        {
            Files = [new("DNF.exe", 100, root.ExeSha256), new("../evil.pvf", 200, root.PvfSha256)]
        }, "format=2 Game path traversal rejected");
        Reject(ArchiveGame(root.ExeSha256, root.PvfSha256) with
        {
            Delivery = new GameDelivery("unknown", URLs: ["http://a/x", "http://b/y"], PackageSha256: new string('1', 64), PackageSize: 1024)
        }, "unknown delivery strategy rejected");

        // format=1 and unsupported formats are both rejected: format=2 is mandatory.
        foreach (var format in new[] { 1, 3 })
            try { Trust.Verify(Sign(new Bootstrap("DNF90-Flutter", format, 2, root, [], null, ArchiveGame(root.ExeSha256, root.PvfSha256))), root); throw new Exception("accepted format " + format); }
            catch (InvalidDataException) { check(true, "non-2 format rejected: " + format); }
    }
}
