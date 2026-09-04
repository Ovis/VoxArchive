using VoxArchive.Domain;
using VoxArchive.Infrastructure;

namespace VoxArchive.IntegrationTests;

/// <summary>
/// ReazonSpeechモデル定義が、検証済みの固定配布物を指していることを確認する
/// </summary>
public sealed class ReazonSpeechModelCatalogTests
{
    [Test]
    public void Japanese_UsesPinnedHybridK2V2Artifacts()
    {
        var definition = ReazonSpeechModelCatalog.Japanese;

        Assert.Multiple(() =>
        {
            Assert.That(definition.EngineId, Is.EqualTo(TranscriptionEngineId.ReazonSpeech));
            Assert.That(definition.ModelId, Is.EqualTo(new TranscriptionModelId("ja")));
            Assert.That(definition.ArtifactVersion, Is.EqualTo("k2-v2"));
            Assert.That(definition.Revision, Is.EqualTo("291488c8151be24d7da4bf7af26e533fad96e407"));
            Assert.That(definition.License, Is.EqualTo("Apache-2.0"));
            Assert.That(definition.Files, Has.Count.EqualTo(4));
        });

        AssertFile(
            definition.Files[0],
            "encoder-epoch-99-avg-1.int8.onnx",
            154_670_139,
            "2c7bd08a8a99f9ddd0d9e458456577b1f6279214e51426f114f9eced44c54e1d");
        AssertFile(
            definition.Files[1],
            "decoder-epoch-99-avg-1.onnx",
            11_767_836,
            "58b18211ae06265466bfa17172dab574df94f76c8bcb61a3640c28ba860e4124");
        AssertFile(
            definition.Files[2],
            "joiner-epoch-99-avg-1.int8.onnx",
            2_696_970,
            "49cc7ea1d3d35a40a27442db5e89996da64bf0e683a903dce76e99e57a12e4de");
        AssertFile(
            definition.Files[3],
            "tokens.txt",
            45_754,
            "2c3ac659818a48a0c04010e0593bbc4d7c8a24a054340b01131499c05fd52def");
    }

    private static void AssertFile(
        TranscriptionModelFileDefinition file,
        string expectedName,
        long expectedSize,
        string expectedSha256)
    {
        Assert.Multiple(() =>
        {
            Assert.That(file.DestinationName, Is.EqualTo(expectedName));
            Assert.That(file.Size, Is.EqualTo(expectedSize));
            Assert.That(file.Sha256, Is.EqualTo(expectedSha256));
            Assert.That(file.SourceUrl.AbsoluteUri, Does.Contain("291488c8151be24d7da4bf7af26e533fad96e407"));
            Assert.That(file.SourceUrl.AbsoluteUri, Does.EndWith($"/{expectedName}?download=true"));
        });
    }
}
