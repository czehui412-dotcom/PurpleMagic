using System.Security.Cryptography;
using System.Text.Json;
using Dnf90.Launcher;
using ServerS5A3;

// P4b: format=2 bootstraps carry a unified GameArtifact. Verify must validate
// it against the trust root and project it onto the legacy Archive/Content
// fields so the update engine consumes it without a parallel code path.
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

        // format=2 with archive Game: validated and projected to Archive.
        var v = Trust.Verify(Sign(new Bootstrap("DNF90-Flutter", 2, 2, root, [], null, null, null, ArchiveGame(root.ExeSha256, root.PvfSha256))), root);
        check(v.Game is not null && v.Archive is not null && v.Archive.Files.Length == 2 &&
              v.Archive.PrimaryUrl.Contains("?d=") && v.Archive.Sha256 == new string('1', 64),
            "format=2 archive Game validated and projected to legacy Archive");

        // format=2 with blob Game: projected to Content when no explicit Content.
        var blob = new GameArtifact("1.2.27", "src", 32,
            [new("DNF.exe", 100, root.ExeSha256), new("Script.pvf", 200, root.PvfSha256, BlobSha256: new string('5', 64), BlobLength: 50, Compression: "gzip")],
            new GameDelivery("blob", Endpoint: "https://blob.example.com/", CertificateSha256: ""));
        var vb = Trust.Verify(Sign(new Bootstrap("DNF90-Flutter", 2, 2, root, [], null, null, null, blob)), root);
        check(vb.Content is not null && vb.Content.Files.Length == 2 && vb.Content.Version == "1.2.27",
            "format=2 blob Game projected to legacy Content");

        void Reject(GameArtifact game, string name)
        {
            try { Trust.Verify(Sign(new Bootstrap("DNF90-Flutter", 2, 2, root, [], null, null, null, game)), root); throw new Exception("accepted: " + name); }
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

        // format=1 still accepted (transition), no Game field.
        Trust.Verify(Sign(new Bootstrap("DNF90-Flutter", 1, 2, root, [], null, null, null)), root);
        check(true, "format=1 bootstrap still accepted during transition");

        // Unsupported format rejected.
        try { Trust.Verify(Sign(new Bootstrap("DNF90-Flutter", 3, 2, root, [], null, null, null)), root); throw new Exception("accepted format 3"); }
        catch (InvalidDataException) { check(true, "unsupported format rejected"); }
    }
}
