using VoxArchive.Transcription;
using VoxArchive.Transcription.Abstractions;
using VoxArchive.Transcription.ReazonSpeech;

namespace VoxArchive.IntegrationTests;

/// <summary>
/// ReazonSpeechモデルの表示状態がファイル存在だけでなくnative load可能性を反映することを確認する
/// </summary>
public sealed class ReazonSpeechModelAvailabilityTests
{
    [Test]
    public void Inspect_WhenAllFilesExistButNativeLoadFails_ReturnsCorrupt()
    {
        var root = Path.Combine(Path.GetTempPath(), "VoxArchive.Tests", Guid.NewGuid().ToString("N"));
        try
        {
            var package = ReazonSpeechModelCatalog.Packages.Single(
                x => x.PackageId == ReazonSpeechModelCatalog.JapaneseInt8Fp32PackageId);
            var packageDirectory = Path.Combine(root, ReazonSpeechEngineIdentity.EngineId.Value, package.PackageId.Value);
            Directory.CreateDirectory(packageDirectory);

            // ファイルの有無だけならInstalledと判定される状態を作り、native loadに失敗する内容を配置する。
            // この状態を「取得済み」と公開すると実行時の利用可能性とUI表示が食い違うため、Corruptへ落ちることを固定する。
            foreach (var file in package.Files)
            {
                File.WriteAllText(Path.Combine(packageDirectory, file.DestinationName), "invalid-model-data");
            }

            var provider = new ReazonSpeechModelProvider(
                new ManagedModelFileTransaction(new HttpClient()),
                root);

            var inspection = provider.Inspect(
                package.PackageId,
                TranscriptionModelInspectionLevel.Existence);

            Assert.Multiple(() =>
            {
                Assert.That(inspection.State, Is.EqualTo(TranscriptionModelPackageState.Corrupt));
                Assert.That(provider.IsReady(package.PackageId), Is.False);
            });
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }
}
