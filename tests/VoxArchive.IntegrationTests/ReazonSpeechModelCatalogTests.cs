using VoxArchive.Transcription.ReazonSpeech;

namespace VoxArchive.IntegrationTests;

/// <summary>
/// ReazonSpeechモデル定義が固定revisionのprecision別必要ファイルだけを指すことを確認する
/// </summary>
public sealed class ReazonSpeechModelCatalogTests
{
    [Test]
    public void Packages_ArePinnedAndContainOnlyRequiredFilesForEachPrecision()
    {
        Assert.That(ReazonSpeechModelCatalog.Packages, Has.Count.EqualTo(3));

        AssertPackage(
            ReazonSpeechPrecision.Fp32,
            ReazonSpeechModelCatalog.JapaneseFp32PackageId.Value,
            "encoder-epoch-99-avg-1.onnx",
            "decoder-epoch-99-avg-1.onnx",
            "joiner-epoch-99-avg-1.onnx");
        AssertPackage(
            ReazonSpeechPrecision.Int8,
            ReazonSpeechModelCatalog.JapaneseInt8PackageId.Value,
            "encoder-epoch-99-avg-1.int8.onnx",
            "decoder-epoch-99-avg-1.int8.onnx",
            "joiner-epoch-99-avg-1.int8.onnx");
        AssertPackage(
            ReazonSpeechPrecision.Int8Fp32,
            ReazonSpeechModelCatalog.JapaneseInt8Fp32PackageId.Value,
            "encoder-epoch-99-avg-1.int8.onnx",
            "decoder-epoch-99-avg-1.onnx",
            "joiner-epoch-99-avg-1.int8.onnx");
    }

    private static void AssertPackage(
        ReazonSpeechPrecision precision,
        string expectedPackageId,
        string encoder,
        string decoder,
        string joiner)
    {
        var package = ReazonSpeechModelCatalog.Packages.Single(x => x.Precision == precision);
        Assert.Multiple(() =>
        {
            Assert.That(package.PackageId.Value, Is.EqualTo(expectedPackageId));
            Assert.That(package.Files.Select(x => x.DestinationName), Is.EquivalentTo(new[]
            {
                encoder,
                decoder,
                joiner,
                "tokens.txt"
            }));
            Assert.That(package.Files.All(x =>
                x.SourceUrl.AbsoluteUri.Contains(ReazonSpeechModelCatalog.Revision, StringComparison.Ordinal)
                && x.SourceUrl.AbsoluteUri.EndsWith($"/{x.DestinationName}?download=true", StringComparison.Ordinal)), Is.True);
            Assert.That(package.Files.All(x => x.ExpectedSizeBytes is null), Is.True);
        });
    }
}
