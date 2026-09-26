using System.Threading;
using System.Windows.Forms;

namespace INTVoiceToText.Services;

/// <summary>Prevents two portable copies from downloading/using the same model simultaneously.</summary>
public sealed class SingleInstanceGuard : IDisposable
{
    private readonly Mutex _mutex;
    public bool IsFirstInstance { get; }

    public SingleInstanceGuard()
    {
        _mutex = new Mutex(true, @"Local\INT_VoiceToText.SingleInstance", out var created);
        IsFirstInstance = created;
        if (!created)
        {
            MessageBox.Show(
                "INT VoiceToText уже запущен. Откройте его через значок в системном трее.",
                "INT VoiceToText",
                MessageBoxButtons.OK,
                MessageBoxIcon.Information);
        }
    }

    public void Dispose()
    {
        if (!IsFirstInstance) { _mutex.Dispose(); return; }
        try { _mutex.ReleaseMutex(); } catch { }
        _mutex.Dispose();
    }
}
