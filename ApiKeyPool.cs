// ApiKeyPool.cs
using System.Linq;

namespace evren_cli;

/// <summary>Bir API anahtarının o anki durumu.</summary>
public enum ApiKeyState
{
    /// <summary>Kullanılabilir.</summary>
    Ready,
    /// <summary>Geçici doldu (429 / kota); <see cref="ApiKeyEntry.RetryAfterUtc"/> sonrası yeniden denenir.</summary>
    Cooling,
    /// <summary>Kullanılamaz (401 — anahtar geçersiz); bir daha denenmez.</summary>
    Disabled
}

/// <summary>Havuzdaki tek bir API anahtarı ve kullanım istatistikleri.</summary>
public sealed class ApiKeyEntry
{
    internal ApiKeyEntry(string key) => Key = key;

    public string Key { get; }
    public ApiKeyState State { get; internal set; } = ApiKeyState.Ready;
    public DateTime RetryAfterUtc { get; internal set; }
    public int Failures { get; internal set; }
    public long Uses { get; internal set; }
    public string? LastError { get; internal set; }

    /// <summary>Güvenli gösterim: yalnızca ilk 10 ve son 4 karakter görünür.</summary>
    public string Masked => ApiKeyPool.Mask(Key);
}

/// <summary>
/// Birden çok API anahtarını yöneten havuz. Bir anahtar kota/limit hatasında
/// geçici dinlenmeye ya da geçersizse kalıcı devre dışına alınır; istekler
/// sıradaki hazır anahtarla (round-robin) devam eder — biri bitince diğeri
/// kaldığı yerden sürdürür.
/// </summary>
public sealed class ApiKeyPool
{
    private static readonly TimeSpan DefaultCooldown = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan MaxCooldown = TimeSpan.FromHours(1);

    private readonly object _sync = new();
    private readonly List<ApiKeyEntry> _entries = new();
    private int _cursor;

    public ApiKeyPool(IEnumerable<string> keys)
    {
        foreach (var key in keys)
            Add(key, notify: false);
    }

    /// <summary>Havuz değiştiğinde (add/remove) çağrılır; kalıcı kayıt için kullanılır.</summary>
    public Action<IReadOnlyList<string>>? KeysChanged { get; set; }

    public int Count
    {
        get { lock (_sync) return _entries.Count; }
    }

    public IReadOnlyList<ApiKeyEntry> Entries
    {
        get { lock (_sync) return _entries.ToArray(); }
    }

    public IReadOnlyList<string> Keys
    {
        get { lock (_sync) return _entries.Select(e => e.Key).ToArray(); }
    }

    public int ReadyCount
    {
        get { lock (_sync) return _entries.Count(e => e.State != ApiKeyState.Disabled); }
    }

    /// <summary>Anahtar ekler; boş ya da mükerrer ise false döner.</summary>
    public bool Add(string key, bool notify = true)
    {
        key = key.Trim();
        if (key.Length == 0)
            return false;

        lock (_sync)
        {
            if (_entries.Any(e => e.Key == key))
                return false;
            _entries.Add(new ApiKeyEntry(key));
        }

        if (notify)
            Notify();
        return true;
    }

    /// <summary>1 tabanlı indeksle anahtar siler.</summary>
    public bool RemoveAt(int index)
    {
        lock (_sync)
        {
            if (index < 1 || index > _entries.Count)
                return false;
            _entries.RemoveAt(index - 1);
            if (_cursor >= _entries.Count)
                _cursor = 0;
        }

        Notify();
        return true;
    }

    /// <summary>Geçici/kalıcı tüm engelleri kaldırır (ör. kota yenilendikten sonra).</summary>
    public void ResetStates()
    {
        lock (_sync)
        {
            foreach (var entry in _entries)
            {
                entry.State = ApiKeyState.Ready;
                entry.Failures = 0;
                entry.LastError = null;
            }
        }
    }

    /// <summary>Sıradaki hazır anahtarı döner (round-robin); hiçbiri hazır değilse null.</summary>
    public string? Acquire()
    {
        var now = DateTime.UtcNow;

        lock (_sync)
        {
            if (_entries.Count == 0)
                return null;

            for (var i = 0; i < _entries.Count; i++)
            {
                var entry = _entries[(_cursor + i) % _entries.Count];
                if (entry.State == ApiKeyState.Disabled)
                    continue;
                if (entry.State == ApiKeyState.Cooling && entry.RetryAfterUtc > now)
                    continue;

                // Dinlenmesi bitmiş anahtar yeniden hazır hale gelir.
                entry.State = ApiKeyState.Ready;
                entry.Uses++;
                _cursor = (_cursor + i + 1) % _entries.Count;
                return entry.Key;
            }

            return null;
        }
    }

    /// <summary>Dinlenmedeki en erken anahtarın yeniden kullanılabilir olacağı zaman.</summary>
    public DateTime? NextAvailableUtc
    {
        get
        {
            lock (_sync)
                return _entries
                    .Where(e => e.State == ApiKeyState.Cooling)
                    .Select(e => (DateTime?)e.RetryAfterUtc)
                    .Min();
        }
    }

    /// <summary>İstek başarılı olduğunda çağrılır.</summary>
    public void ReportSuccess(string key)
    {
        lock (_sync)
        {
            var entry = Find(key);
            if (entry is not null)
                entry.LastError = null;
        }
    }

    /// <summary>Anahtar kota/limit doldu (429 ya da 403); geçici dinlenmeye alınır.</summary>
    public void ReportRateLimited(string key, TimeSpan? cooldown, string? detail)
    {
        lock (_sync)
        {
            var entry = Find(key);
            if (entry is null)
                return;

            var wait = cooldown is { } c && c > TimeSpan.Zero ? c : DefaultCooldown;
            if (wait > MaxCooldown)
                wait = MaxCooldown;

            entry.State = ApiKeyState.Cooling;
            entry.RetryAfterUtc = DateTime.UtcNow + wait;
            entry.Failures++;
            entry.LastError = detail;
        }
    }

    /// <summary>Anahtar geçersiz (401); kalıcı olarak devre dışı bırakılır.</summary>
    public void ReportDisabled(string key, string? detail)
    {
        lock (_sync)
        {
            var entry = Find(key);
            if (entry is null)
                return;

            entry.State = ApiKeyState.Disabled;
            entry.Failures++;
            entry.LastError = detail;
        }
    }

    /// <summary>Tek satırlık özet: "2 anahtar: 1 hazır, 1 beklemede".</summary>
    public string StatusLine()
    {
        lock (_sync)
        {
            if (_entries.Count == 0)
                return "API anahtarı yok.";

            var ready = _entries.Count(e => e.State == ApiKeyState.Ready);
            var cooling = _entries.Count(e => e.State == ApiKeyState.Cooling);
            var off = _entries.Count(e => e.State == ApiKeyState.Disabled);

            var parts = new List<string>();
            if (ready > 0) parts.Add($"{ready} hazır");
            if (cooling > 0) parts.Add($"{cooling} beklemede");
            if (off > 0) parts.Add($"{off} devre dışı");

            return $"{_entries.Count} anahtar: {string.Join(", ", parts)}";
        }
    }

    /// <summary>Her anahtarın maskeli, durumlu satırı; <paramref name="color"/> ise ANSI rengi de eklenir.</summary>
    public string EntryLines(bool color)
    {
        var now = DateTime.UtcNow;
        var lines = new List<string>();

        lock (_sync)
        {
            foreach (var (entry, i) in _entries.Select((e, i) => (e, i)))
            {
                var state = entry.State switch
                {
                    ApiKeyState.Ready => "hazır",
                    ApiKeyState.Cooling => $"beklemede ({FormatRemaining(entry.RetryAfterUtc - now)})",
                    _ => "devre dışı"
                };

                var line = $"  [{i + 1}] {entry.Masked} — {state} · {entry.Uses} çağrı";
                if (entry.Failures > 0)
                    line += $", {entry.Failures} hata";
                if (entry.State != ApiKeyState.Ready && !string.IsNullOrWhiteSpace(entry.LastError))
                    line += $" · {Truncate(entry.LastError!, 70)}";

                if (!color)
                {
                    lines.Add(line);
                    continue;
                }

                lines.Add(entry.State switch
                {
                    ApiKeyState.Ready => "\u001b[32m" + line + "\u001b[0m",
                    ApiKeyState.Cooling => "\u001b[33m" + line + "\u001b[0m",
                    _ => "\u001b[31m" + line + "\u001b[0m"
                });
            }
        }

        return lines.Count == 0 ? "  (anahtar yok)" : string.Join("\n", lines);
    }

    private static string Truncate(string text, int max) =>
        text.Length <= max ? text : text[..max] + "…";

    /// <summary>Kalan süreyi "45sn" / "12dk" / "1s 5dk" biçiminde yazar.</summary>
    private static string FormatRemaining(TimeSpan remaining)
    {
        var seconds = (int)Math.Ceiling(remaining.TotalSeconds);
        if (seconds < 0) seconds = 0;

        if (seconds >= 3600)
            return $"{seconds / 3600}s {seconds % 3600 / 60}dk";
        return seconds >= 60 ? $"{(int)Math.Ceiling(seconds / 60.0)}dk" : $"{seconds}sn";
    }

    private ApiKeyEntry? Find(string key) => _entries.FirstOrDefault(e => e.Key == key);

    private void Notify() => KeysChanged?.Invoke(Keys);

    /// <summary>Log/görüntüleme için güvenli maskeleme.</summary>
    public static string Mask(string key) =>
        key.Length <= 12 ? new string('*', Math.Min(key.Length, 8)) : $"{key[..10]}\u2026{key[^4..]}";
}
