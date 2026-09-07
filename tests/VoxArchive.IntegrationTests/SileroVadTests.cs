using System.Text.Json;
using VoxArchive.Transcription.Abstractions;
using VoxArchive.Transcription.SileroVad;

namespace VoxArchive.IntegrationTests;

/// <summary>
/// Silero VAD固有設定とraw regionからSpeechRegionへの変換規則を確認する
/// </summary>
public sealed class SileroVadTests
{
    [Test]
    public void FromSnapshot_WhenSettingsAreMissing_UsesVoxArchiveDefaultProfile()
    {
        var snapshot = new SpeechRegionDetectorSettingsSnapshot(1, default);

        var options = SileroVadOptions.FromSnapshot(snapshot);

        Assert.That(options, Is.EqualTo(SileroVadOptions.Default));
    }

    [Test]
    public void FromSnapshot_ReadsJobSnapshotValues()
    {
        using var document = JsonDocument.Parse("""
            {
              "Threshold": 0.65,
              "MinimumSpeechDurationMilliseconds": 120,
              "MinimumSilenceDurationMilliseconds": 650,
              "PrePaddingMilliseconds": 400,
              "PostPaddingMilliseconds": 250
            }
            """);
        var snapshot = new SpeechRegionDetectorSettingsSnapshot(1, document.RootElement.Clone());

        var options = SileroVadOptions.FromSnapshot(snapshot);

        Assert.Multiple(() =>
        {
            Assert.That(options.Threshold, Is.EqualTo(0.65d));
            Assert.That(options.MinimumSpeechDurationMilliseconds, Is.EqualTo(120));
            Assert.That(options.MinimumSilenceDurationMilliseconds, Is.EqualTo(650));
            Assert.That(options.PrePaddingMilliseconds, Is.EqualTo(400));
            Assert.That(options.PostPaddingMilliseconds, Is.EqualTo(250));
        });
    }

    [Test]
    public void Build_AppliesAsymmetricPaddingAndClampsToPreparedAudio()
    {
        var options = new SileroVadOptions(0.5d, 100, 500, 300, 200);
        var rawRegions = new[]
        {
            new AudioSampleRange(1_000, 3_000),
            new AudioSampleRange(15_000, 15_900),
        };

        var regions = SileroVadRegionBuilder.Build(rawRegions, 16_000, 16_000, options);

        Assert.That(regions, Has.Count.EqualTo(2));
        Assert.Multiple(() =>
        {
            Assert.That(regions[0].StartSample, Is.EqualTo(0));
            Assert.That(regions[0].EndSample, Is.EqualTo(6_200));
            Assert.That(regions[0].CoreRanges, Is.EqualTo(new[] { new AudioSampleRange(1_000, 3_000) }));
            Assert.That(regions[0].SourceRawSpeechRegionIds, Is.EqualTo(new[] { 0 }));

            Assert.That(regions[1].StartSample, Is.EqualTo(10_200));
            Assert.That(regions[1].EndSample, Is.EqualTo(16_000));
            Assert.That(regions[1].CoreRanges, Is.EqualTo(new[] { new AudioSampleRange(15_000, 15_900) }));
            Assert.That(regions[1].SourceRawSpeechRegionIds, Is.EqualTo(new[] { 1 }));
        });
    }

    [Test]
    public void Build_WhenPaddedRegionsTouch_MergesWithoutLosingCoreRangesOrLineage()
    {
        var options = new SileroVadOptions(0.5d, 100, 500, 300, 200);
        var rawRegions = new[]
        {
            new AudioSampleRange(10_000, 12_000),
            new AudioSampleRange(19_000, 21_000),
        };

        var regions = SileroVadRegionBuilder.Build(rawRegions, 40_000, 16_000, options);

        Assert.That(regions, Has.Count.EqualTo(1));
        var region = regions[0];
        Assert.Multiple(() =>
        {
            Assert.That(region.SpeechRegionId, Is.EqualTo(0));
            Assert.That(region.StartSample, Is.EqualTo(5_200));
            Assert.That(region.EndSample, Is.EqualTo(24_200));
            Assert.That(region.CoreRanges, Is.EqualTo(new[]
            {
                new AudioSampleRange(10_000, 12_000),
                new AudioSampleRange(19_000, 21_000),
            }));
            Assert.That(region.SourceRawSpeechRegionIds, Is.EqualTo(new[] { 0, 1 }));
        });
    }

    [Test]
    public void Build_WhenPaddingDoesNotTouch_KeepsRegionsSeparateAndAssignsSequentialIds()
    {
        var options = new SileroVadOptions(0.5d, 100, 500, 100, 100);
        var rawRegions = new[]
        {
            new AudioSampleRange(5_000, 6_000),
            new AudioSampleRange(12_000, 13_000),
        };

        var regions = SileroVadRegionBuilder.Build(rawRegions, 20_000, 16_000, options);

        Assert.That(regions, Has.Count.EqualTo(2));
        Assert.Multiple(() =>
        {
            Assert.That(regions[0].SpeechRegionId, Is.EqualTo(0));
            Assert.That(regions[1].SpeechRegionId, Is.EqualTo(1));
            Assert.That(regions[0].SourceRawSpeechRegionIds, Is.EqualTo(new[] { 0 }));
            Assert.That(regions[1].SourceRawSpeechRegionIds, Is.EqualTo(new[] { 1 }));
        });
    }
}
