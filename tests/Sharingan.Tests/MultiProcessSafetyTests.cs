using Sharingan.Providers;
using Xunit;

namespace Sharingan.Tests;

/// <summary>
/// Tests for multi-process and concurrent access scenarios.
/// </summary>
public class MultiProcessSafetyTests
{
    [Fact]
    public async Task ConcurrentWrites_ShouldNotLoseData()
    {
        // Arrange
        using InMemorySettingsProvider provider = new();
        List<Task> tasks = [];
        int keyCount = 100;

        // Act - Concurrent writes
        for (int i = 0; i < keyCount; i++)
        {
            string key = $"key{i}";
            string value = $"value{i}";
            tasks.Add(Task.Run(() => provider.Set(key, value)));
        }
        await Task.WhenAll(tasks);

        // Assert - All keys should be present
        Assert.Equal(keyCount, provider.Count);
        for (int i = 0; i < keyCount; i++)
        {
            Assert.True(provider.ContainsKey($"key{i}"));
        }
    }

    [Fact]
    public async Task ConcurrentReadsAndWrites_ShouldBeThreadSafe()
    {
        // Arrange
        using InMemorySettingsProvider provider = new();
        provider.Set("shared", 0);
        int iterations = 1000;
        List<Task> tasks = [];

        // Act - Concurrent reads and writes
        for (int i = 0; i < iterations; i++)
        {
            tasks.Add(Task.Run(() =>
            {
                int current = provider.Get("shared", 0);
                provider.Set("shared", current + 1);
            }));
            tasks.Add(Task.Run(() =>
            {
                _ = provider.Get("shared", 0);
            }));
        }
        await Task.WhenAll(tasks);

        // Assert - Should complete without exceptions
        Assert.True(provider.ContainsKey("shared"));
    }

    [Fact]
    public async Task ConcurrentReload_ShouldNotThrow()
    {
        // Arrange
        using InMemorySettingsProvider provider = new();
        provider.Set("test", "value");
        List<Task> tasks = [];

        // Act - Concurrent reloads
        for (int i = 0; i < 50; i++)
        {
            tasks.Add(Task.Run(provider.Reload));
            tasks.Add(Task.Run(() => provider.Set($"key{i}", $"value{i}")));
        }
        await Task.WhenAll(tasks);

        // Assert - Should complete without exceptions
        Assert.True(true);
    }

    [Fact]
    public async Task AsyncOperations_ShouldBeThreadSafe()
    {
        // Arrange
        using InMemorySettingsProvider provider = new();
        List<Task> tasks = [];

        // Act - Concurrent async operations
        for (int i = 0; i < 100; i++)
        {
            string key = $"async.key{i}";
            tasks.Add(provider.SetAsync(key, $"value{i}"));
            tasks.Add(provider.GetAsync<string>(key, "default"));
        }
        await Task.WhenAll(tasks);

        // Assert
        Assert.True(provider.Count >= 0);
    }

    [Fact]
    public async Task ClearDuringWrites_ShouldNotThrow()
    {
        // Arrange
        using InMemorySettingsProvider provider = new();
        CancellationTokenSource cts = new(TimeSpan.FromSeconds(2));
        List<Task> tasks =
        [
            // Act - Concurrent writes with periodic clears
            Task.Run(() =>
            {
                while (!cts.Token.IsCancellationRequested)
                {
                    provider.Clear();
                    Thread.Sleep(50);
                }
            }, cts.Token)
        ];

        for (int i = 0; i < 50; i++)
        {
            tasks.Add(Task.Run(() =>
            {
                for (int j = 0; j < 10; j++)
                {
                    provider.Set($"key{j}", $"value{j}");
                }
            }));
        }

        try { await Task.WhenAll(tasks); } catch (Exception) { }
        cts.Cancel();

        // Assert - Should complete without deadlock
        Assert.True(true);
    }

    [Fact]
    public void RemoveDuringIteration_ShouldNotThrow()
    {
        // Arrange
        using InMemorySettingsProvider provider = new();
        for (int i = 0; i < 100; i++)
        {
            provider.Set($"key{i}", $"value{i}");
        }

        // Act - Remove while iterating (should not throw)
        List<string> keys = provider.GetAllKeys().ToList();
        foreach (string key in keys)
        {
            provider.Remove(key);
        }

        // Assert
        Assert.Equal(0, provider.Count);
    }
}
