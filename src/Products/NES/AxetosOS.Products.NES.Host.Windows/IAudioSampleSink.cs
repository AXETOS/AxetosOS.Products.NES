namespace AxetosOS.Products.NES.Host.Windows;

public interface IAudioSampleSink : IDisposable
{
    int SampleRate { get; }
    int Channels { get; }
    void Start();
    void Submit(ReadOnlySpan<float> samples);
    void Stop();
}
