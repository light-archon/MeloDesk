using System.Diagnostics;
using System.Text;
using MeloDesk.Music;
using NAudio.CoreAudioApi;

static class MixerChecks
{
    public static async Task Switching()
    {
        var folder = Path.Combine(AppContext.BaseDirectory, "mixer-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        var a = Path.Combine(folder, "a.wav"); var b = Path.Combine(folder, "b.wav");
        Wave(a, 440); Wave(b, 880);
        using var output = new AudioPlayback { Volume = 20 };
        var streams = new[] { a, b }.Select(path => new AudioStream(new Uri(path).AbsoluteUri, new Dictionary<string, string>())).ToArray();
        output.Start(streams[0], output.BeginSwitch());
        await Until(() => output.Time > 300);
        SetFixtureMute(false);
        await Task.Delay(150);
        var baseline = Read();
        for (var index = 0; index < 8; index++)
        {
            var next = streams[(index + 1) % streams.Length];
            await output.PrepareAsync(next);
            Check(Read(), baseline.Volume);
            var watch = Stopwatch.StartNew();
            var generation = output.BeginSwitch();
            Check(Read(), baseline.Volume);
            if (!output.Start(next, generation)) throw new Exception("Prepared track did not start.");
            await Until(() => output.Time > 300);
            var state = Read(); Check(state, baseline.Volume);
            if (state.Peak <= 0) throw new Exception("The audio clock advanced but Windows received no audio samples.");
            Console.WriteLine($"Mixer switch {index + 1}: muted={state.Muted}; name={state.Name}; decoded in {watch.ElapsedMilliseconds} ms.");
        }
        SetFixtureMute(true);
        output.Start(streams[0], output.BeginSwitch());
        await Until(() => output.Time > 300);
        if (!Read().Muted) throw new Exception("Switching overrode a user-selected Windows mixer mute.");
        SetFixtureMute(false);
        output.Dispose(); File.Delete(a); File.Delete(b); Directory.Delete(folder);
    }
    private sealed record State(string Name, bool Muted, float Volume, float Peak);
    private static State Read()
    {
        State? found = null;
        Visit(session => found = new(session.DisplayName, session.SimpleAudioVolume.Mute,
            session.SimpleAudioVolume.Volume, session.AudioMeterInformation.MasterPeakValue));
        return found ?? throw new Exception("The Windows audio session was not created.");
    }
    private static void SetFixtureMute(bool muted) => Visit(session => session.SimpleAudioVolume.Mute = muted);
    private static void Visit(Action<AudioSessionControl> action)
    {
        using var devices = new MMDeviceEnumerator();
        foreach (var device in devices.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active))
        using (device)
        {
            var sessions = device.AudioSessionManager.Sessions;
            for (var index = 0; index < sessions.Count; index++)
            {
                using var session = sessions[index];
                if (session.GetProcessID == Environment.ProcessId && session.DisplayName == "MeloDesk") action(session);
            }
        }
    }
    private static void Check(State state, float volume)
    {
        if (state.Muted) throw new Exception("Preparing or switching a track muted the Windows audio session.");
        if (Math.Abs(state.Volume - volume) > .0001f) throw new Exception("Preparation changed the Windows audio session volume.");
        if (state.Name != "MeloDesk") throw new Exception("The mixer session is not labelled MeloDesk.");
    }
    private static async Task Until(Func<bool> test)
    {
        var end = DateTime.UtcNow.AddSeconds(8);
        while (!test()) { if (DateTime.UtcNow > end) throw new TimeoutException("Playback did not advance."); await Task.Delay(25); }
    }
    private static void Wave(string path, int frequency)
    {
        const int rate = 22050, samples = rate * 12;
        using var writer = new BinaryWriter(File.Create(path));
        writer.Write(Encoding.ASCII.GetBytes("RIFF")); writer.Write(36 + samples * 2); writer.Write(Encoding.ASCII.GetBytes("WAVEfmt "));
        writer.Write(16); writer.Write((short)1); writer.Write((short)1); writer.Write(rate); writer.Write(rate * 2);
        writer.Write((short)2); writer.Write((short)16); writer.Write(Encoding.ASCII.GetBytes("data")); writer.Write(samples * 2);
        for (var index = 0; index < samples; index++) writer.Write((short)(Math.Sin(index * 2 * Math.PI * frequency / rate) * 1000));
    }
}
