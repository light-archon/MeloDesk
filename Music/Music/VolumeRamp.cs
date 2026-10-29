namespace MeloDesk.Music;

public sealed class VolumeRamp
{
    public const int DurationMs = 180;
    private double _from = 70;
    private int _target = 70;
    private long _started;
    public int Target => _target;
    public void Reset(int value) { _from = _target = Math.Clamp(value, 0, 100); _started = 0; }
    public void Retarget(int value, long now)
    {
        _from = ValueAt(now);
        _target = Math.Clamp(value, 0, 100);
        _started = now;
    }
    public double ValueAt(long now)
    {
        var progress = Math.Clamp((now - _started) / (double)DurationMs, 0, 1);
        var smooth = progress * progress * (3 - 2 * progress);
        return _from + (_target - _from) * smooth;
    }
    public bool Finished(long now) => now - _started >= DurationMs;
}
