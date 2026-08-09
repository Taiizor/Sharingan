using Sharingan.Abstractions;
using Sharingan.Internal;
using Sharingan.Serialization;
using System.Collections.Concurrent;
using Tomlyn;
using Tomlyn.Model;

namespace Sharingan.Providers.Toml;

/// <summary>
/// A TOML file-based settings provider using the Tomlyn library.
/// TOML offers a semantically clear, human-readable configuration format.
/// </summary>
/// <remarks>
/// <para>
/// The <see cref="TomlFileSettingsProvider"/> stores settings in TOML format,
/// which provides excellent readability and explicit typing:
/// <list type="bullet">
/// <item><description>Clean, indentation-free syntax with explicit typing</description></item>
/// <item><description>Native support for dates, arrays, and nested tables</description></item>
/// <item><description>Comments for documentation</description></item>
/// <item><description>Popular in modern DevOps and configuration management</description></item>
/// </list>
/// </para>
/// <para>
/// Hierarchical keys (e.g., "database.connection.host") are stored as nested TOML tables.
/// </para>
/// </remarks>
/// <example>
/// Using the TOML provider:
/// <code>
/// var provider = new TomlFileSettingsProvider(new TomlProviderOptions
/// {
///     FilePath = "config.toml"
/// });
/// 
/// provider.Set("database.host", "localhost");
/// provider.Set("database.port", 5432);
/// provider.Flush();
/// // Creates:
/// // [database]
/// // host = "localhost"
/// // port = 5432
/// </code>
/// </example>
/// <seealso cref="TomlProviderOptions"/>
/// <seealso cref="SharinganBuilderExtensions.UseTomlFile"/>
public class TomlFileSettingsProvider : ISettingsProvider
{
    /// <summary>
    /// Thread-safe cache of settings.
    /// </summary>
    private readonly ConcurrentDictionary<string, object?> _cache = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Semaphore for exclusive file access.
    /// </summary>
    private readonly SemaphoreSlim _fileLock = new(1, 1);

    /// <summary>
    /// Configuration options for this provider.
    /// </summary>
    private readonly TomlProviderOptions _options;

    /// <summary>
    /// Serializer for complex type conversion.
    /// </summary>
    private readonly ISettingsSerializer _serializer;

    /// <summary>
    /// The resolved absolute file path.
    /// </summary>
    private readonly string _filePath;

    /// <summary>
    /// Tracks whether this instance has been disposed.
    /// </summary>
    private bool _disposed;

    /// <summary>
    /// Tracks whether there are unsaved changes.
    /// </summary>
    private bool _isDirty;

    /// <inheritdoc />
    /// <value>Returns "TomlFile:{filename}" for identification.</value>
    public string Name => $"TomlFile:{Path.GetFileName(_filePath)}";

    /// <inheritdoc />
    /// <value>Always returns <c>false</c> since TOML files are writable.</value>
    public bool IsReadOnly => false;

    /// <inheritdoc />
    public int Priority => _options.Priority;

    /// <inheritdoc />
    public SettingsScope Scope => _options.Scope;

    /// <inheritdoc />
    public int Count => _cache.Count;

    /// <inheritdoc />
    public event EventHandler<SettingsChangedEventArgs>? SettingsChanged;

    /// <summary>
    /// Initializes a new instance of the <see cref="TomlFileSettingsProvider"/> class.
    /// </summary>
    /// <param name="options">Configuration options. If null, default options are used.</param>
    /// <param name="serializer">Optional custom serializer. If null, uses the default JSON serializer.</param>
    public TomlFileSettingsProvider(TomlProviderOptions? options = null, ISettingsSerializer? serializer = null)
    {
        _options = options ?? new TomlProviderOptions();
        _serializer = serializer ?? _options.Serializer ?? JsonSettingsSerializer.Default;
        _filePath = PathResolver.GetFilePath(_options.FilePath, _options.Scope, _options.ApplicationName, _options.OrganizationName);

        LoadFromFile();
    }

    /// <inheritdoc />
    public T Get<T>(string key, T defaultValue)
    {
        if (_cache.TryGetValue(key, out object? value) && value is not null)
        {
            return ConvertValue<T>(value, defaultValue);
        }
        return defaultValue;
    }

    /// <inheritdoc />
    public T? GetOrDefault<T>(string key)
    {
        return Get<T>(key, default!);
    }

    /// <inheritdoc />
    public bool TryGet<T>(string key, out T? value)
    {
        if (_cache.TryGetValue(key, out object? stored) && stored is not null)
        {
            value = ConvertValue<T>(stored, default);
            return true;
        }
        value = default;
        return false;
    }

    /// <inheritdoc />
    public T GetOrCreate<T>(string key, Func<T> factory)
    {
        if (TryGet<T>(key, out T? existing) && existing is not null)
        {
            return existing;
        }

        T? value = factory();
        Set(key, value);
        return value;
    }

    /// <inheritdoc />
    public void Set<T>(string key, T value)
    {
        _cache[key] = value;
        _isDirty = true;
        OnSettingsChanged(new SettingsChangedEventArgs(key, SettingsChangeType.Modified, null, value, Name));
    }

    /// <inheritdoc />
    public bool Remove(string key)
    {
        if (_cache.TryRemove(key, out _))
        {
            _isDirty = true;
            OnSettingsChanged(new SettingsChangedEventArgs(key, SettingsChangeType.Removed, null, null, Name));
            return true;
        }
        return false;
    }

    /// <inheritdoc />
    public void Clear()
    {
        _cache.Clear();
        _isDirty = true;
        OnSettingsChanged(SettingsChangedEventArgs.Cleared(Name));
    }

    /// <inheritdoc />
    public bool ContainsKey(string key)
    {
        return _cache.ContainsKey(key);
    }

    /// <inheritdoc />
    public IEnumerable<string> GetAllKeys()
    {
        return _cache.Keys;
    }

    /// <inheritdoc />
    public void Flush()
    {
        if (!_isDirty)
        {
            return;
        }

        _fileLock.Wait();
        try { SaveToFile(); }
        finally { _fileLock.Release(); }
    }

    /// <inheritdoc />
    public void Reload()
    {
        _fileLock.Wait();
        try { LoadFromFileInternal(); }
        finally { _fileLock.Release(); }
    }

    public Task<T> GetAsync<T>(string key, T defaultValue, CancellationToken ct = default)
    {
        return Task.FromResult(Get(key, defaultValue));
    }

    public Task<T?> GetOrDefaultAsync<T>(string key, CancellationToken ct = default)
    {
        return Task.FromResult(GetOrDefault<T>(key));
    }

    public Task<T> GetOrCreateAsync<T>(string key, Func<T> factory, CancellationToken ct = default)
    {
        return Task.FromResult(GetOrCreate(key, factory));
    }

    public async Task<T> GetOrCreateAsync<T>(string key, Func<CancellationToken, Task<T>> factory, CancellationToken ct = default)
    {
        if (TryGet<T>(key, out T? existing) && existing is not null)
        {
            return existing;
        }

        T? value = await factory(ct).ConfigureAwait(false);
        Set(key, value);
        return value;
    }
    public Task SetAsync<T>(string key, T value, CancellationToken ct = default) { Set(key, value); return Task.CompletedTask; }
    public Task<bool> RemoveAsync(string key, CancellationToken ct = default)
    {
        return Task.FromResult(Remove(key));
    }

    public Task ClearAsync(CancellationToken ct = default) { Clear(); return Task.CompletedTask; }
    public async Task FlushAsync(CancellationToken ct = default)
    {
        if (!_isDirty)
        {
            return;
        }

        await _fileLock.WaitAsync(ct).ConfigureAwait(false);
        try { SaveToFile(); }
        finally { _fileLock.Release(); }
    }
    public async Task ReloadAsync(CancellationToken ct = default)
    {
        await _fileLock.WaitAsync(ct).ConfigureAwait(false);
        try { LoadFromFileInternal(); }
        finally { _fileLock.Release(); }
    }

    private void LoadFromFile()
    {
        _fileLock.Wait();
        try { LoadFromFileInternal(); }
        finally { _fileLock.Release(); }
    }

    private void LoadFromFileInternal()
    {
        _cache.Clear();
        if (!File.Exists(_filePath))
        {
            if (_options.CreateIfNotExists)
            {
                PathResolver.EnsureDirectoryExists(_filePath);
                File.WriteAllText(_filePath, "# Settings\n");
            }
            return;
        }

        try
        {
            string toml = File.ReadAllText(_filePath);
            if (string.IsNullOrWhiteSpace(toml))
            {
                return;
            }

            TomlTable? model = TomlSerializer.Deserialize<TomlTable>(toml);
            if (model != null)
            {
                FlattenTable(model, string.Empty);
            }
        }
        catch { /* Ignore parse errors */ }
        _isDirty = false;
    }

    private void FlattenTable(TomlTable table, string prefix)
    {
        foreach (KeyValuePair<string, object> kvp in table)
        {
            string key = string.IsNullOrEmpty(prefix) ? kvp.Key : $"{prefix}.{kvp.Key}";

            if (kvp.Value is TomlTable nestedTable)
            {
                FlattenTable(nestedTable, key);
            }
            else
            {
                _cache[key] = kvp.Value;
            }
        }
    }

    private void SaveToFile()
    {
        PathResolver.EnsureDirectoryExists(_filePath);

        TomlTable table = [];

        foreach (KeyValuePair<string, object?> kvp in _cache.OrderBy(k => k.Key))
        {
            SetNestedValue(table, kvp.Key.Split('.'), kvp.Value);
        }

        string toml = TomlSerializer.Serialize(table);

        if (_options.UseAtomicWrites)
        {
            string temp = _filePath + ".tmp";
            File.WriteAllText(temp, toml);
            FileHelper.MoveWithOverwrite(temp, _filePath);
        }
        else
        {
            File.WriteAllText(_filePath, toml);
        }
        _isDirty = false;
    }

    private static void SetNestedValue(TomlTable table, string[] parts, object? value)
    {
        TomlTable current = table;
        for (int i = 0; i < parts.Length - 1; i++)
        {
            if (!current.ContainsKey(parts[i]))
            {
                current[parts[i]] = new TomlTable();
            }
            current = (TomlTable)current[parts[i]];
        }
#if NETSTANDARD2_1_OR_GREATER || NETCOREAPP3_0_OR_GREATER
        current[parts[^1]] = ConvertToTomlValue(value);
#else
        current[parts[parts.Length - 1]] = ConvertToTomlValue(value);
#endif
    }

    private static object? ConvertToTomlValue(object? value)
    {
        return value switch
        {
            null => null,
            string s => s,
            bool b => b,
            int i => (long)i,
            long l => l,
            float f => (double)f,
            double d => d,
            DateTime dt => dt,
            DateTimeOffset dto => dto.DateTime,
            _ => value.ToString()
        };
    }

    private T? ConvertValue<T>(object value, T? defaultValue)
    {
        if (value is T typed)
        {
            return typed;
        }

        // Handle numeric conversions
        if (typeof(T) == typeof(int) && value is long l)
        {
            return (T)(object)(int)l;
        }

        if (typeof(T) == typeof(float) && value is double d)
        {
            return (T)(object)(float)d;
        }

        if (value is string str)
        {
            if (typeof(T) == typeof(string))
            {
                return (T)(object)str;
            }

            try { return _serializer.Deserialize<T>(str) ?? defaultValue; } catch { return defaultValue; }
        }

        try
        {
            string json = _serializer.Serialize(value);
            return _serializer.Deserialize<T>(json) ?? defaultValue;
        }
        catch { return defaultValue; }
    }

    protected virtual void OnSettingsChanged(SettingsChangedEventArgs e)
    {
        SettingsChanged?.Invoke(this, e);
    }

    public void Dispose()
    {
        if (!_disposed) { Flush(); _fileLock.Dispose(); _disposed = true; }
        GC.SuppressFinalize(this);
    }
}
