namespace DbvoStudio.Core.ElevenLabs;

/// <summary>
/// Runs work with at most N in flight (the user's plan limit). When the service answers "busy" (HTTP 429),
/// the job waits with exponential backoff and the effective limit is lowered temporarily, then recovers.
/// </summary>
public sealed class AdaptiveLimiter
{
    private readonly object _gate = new();
    private readonly int _max;
    private int _limit;
    private int _inFlight;
    private int _successStreak;
    private readonly Queue<TaskCompletionSource> _waiters = new();

    public AdaptiveLimiter(int maxConcurrency)
    {
        _max = Math.Max(1, maxConcurrency);
        _limit = _max;
    }

    public int CurrentLimit { get { lock (_gate) return _limit; } }

    public async Task<T> RunAsync<T>(Func<CancellationToken, Task<T>> work, Func<Exception, bool> isBusy, int maxAttempts = 8, CancellationToken ct = default)
    {
        for (int attempt = 1; ; attempt++)
        {
            await AcquireAsync(ct);
            try
            {
                var result = await work(ct);
                OnSuccess();
                return result;
            }
            catch (Exception ex) when (isBusy(ex) && attempt < maxAttempts && !ct.IsCancellationRequested)
            {
                OnBusy();
                Release();
                var delay = TimeSpan.FromMilliseconds(Math.Min(30_000, 500 * Math.Pow(2, attempt - 1)) + Random.Shared.Next(0, 400));
                await Task.Delay(delay, ct);
                continue;
            }
            catch
            {
                Release();
                throw;
            }
        }
    }

    private void OnSuccess()
    {
        lock (_gate)
        {
            _inFlight--;
            if (_limit < _max && ++_successStreak >= 10)
            {
                _limit++;
                _successStreak = 0;
            }
            Pump();
        }
    }

    private void OnBusy()
    {
        lock (_gate)
        {
            _successStreak = 0;
            _limit = Math.Max(1, _limit - 1);
        }
    }

    private Task AcquireAsync(CancellationToken ct)
    {
        lock (_gate)
        {
            if (_inFlight < _limit)
            {
                _inFlight++;
                return Task.CompletedTask;
            }
            var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            ct.Register(() => tcs.TrySetCanceled(ct));
            _waiters.Enqueue(tcs);
            return tcs.Task;
        }
    }

    private void Release()
    {
        lock (_gate)
        {
            _inFlight--;
            Pump();
        }
    }

    private void Pump()
    {
        while (_inFlight < _limit && _waiters.Count > 0)
        {
            var w = _waiters.Dequeue();
            if (w.TrySetResult()) _inFlight++;
        }
    }
}
