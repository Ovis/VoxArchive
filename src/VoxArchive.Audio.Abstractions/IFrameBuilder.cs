namespace VoxArchive.Audio.Abstractions;

public interface IFrameBuilder
{
    FrameBuildResult BuildFrame(int frameSamples, double micRatio);
}
