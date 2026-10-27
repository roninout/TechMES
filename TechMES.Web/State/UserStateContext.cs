using System.Text.Json;
using System.Text.Json.Nodes;
using Radzen;
using TechMES.Contracts.Users;
using TechMES.Web.Clients;

namespace TechMES.Web.State;

/// <summary>Один снимок и одна очередь сохранения на Blazor-сессию.</summary>
public sealed class UserStateContext(UserStateApiClient api, ILogger<UserStateContext> logger, NotificationService notifications) : IDisposable
{
    private readonly CancellationTokenSource _stop = new();
    private Task? _loading;
    private Task _saving = Task.CompletedTask;
    private bool _disposed;

    public UserStateDto Value { get; private set; } = new();
    public bool IsLoaded { get; private set; }

    // Equipment и переключатель темы используют один запрос первоначального чтения.
    public Task EnsureLoadedAsync() => _loading ??= LoadAsync();
    public Task FlushAsync() => _saving;

    public async Task UpdateAsync(object changes)
    {
        await EnsureLoadedAsync();
        Update(changes);
    }

    private async Task LoadAsync()
    {
        try
        {
            using var loading = CancellationTokenSource.CreateLinkedTokenSource(_stop.Token);
            loading.CancelAfter(TimeSpan.FromSeconds(5));

            Value = await api.GetAsync(loading.Token);
            IsLoaded = true;
        }
        catch (Exception ex)
        {
            if (!_disposed)
            {
                logger.LogWarning(ex, "Could not restore user state.");
                notifications.Notify(NotificationSeverity.Warning, "Settings", "User settings could not be loaded.", 4000);
            }
        }
    }

    /// <summary>Сохраняет только изменённые поля; восстановление выполняется без вызова Update.</summary>
    public void Update(object changes)
    {
        if (_disposed || !IsLoaded) return;

        var patch = JsonSerializer.SerializeToElement(changes);
        var current = JsonSerializer.SerializeToNode(Value)!.AsObject();
        var merged = current.DeepClone().AsObject();

        foreach (var item in patch.EnumerateObject())
        {
            if (item.Name == nameof(UserStateDto.LastEquipmentsByFilter))
            {
                if (merged[item.Name] is not JsonObject remembered)
                {
                    remembered = new JsonObject();
                    merged[item.Name] = remembered;
                }

                foreach (var pair in item.Value.EnumerateObject())
                    remembered[pair.Name] = JsonNode.Parse(pair.Value.GetRawText());
            }
            else
            {
                merged[item.Name] = JsonNode.Parse(item.Value.GetRawText());
            }
        }

        if (JsonNode.DeepEquals(current, merged)) return;

        Value = merged.Deserialize<UserStateDto>()!;
        _saving = SaveAsync(_saving, patch, _stop.Token);
    }

    private async Task SaveAsync(Task previous, JsonElement changes, CancellationToken ct)
    {
        try
        {
            await previous;
            ct.ThrowIfCancellationRequested();

            await api.PatchAsync(changes, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            if (!_disposed)
            {
                logger.LogWarning(ex, "Could not save user state.");
                notifications.Notify(NotificationSeverity.Warning, "Settings", "User settings could not be saved.", 4000);
            }
        }
    }

    public void Dispose()
    {
        _disposed = true;
        _stop.Cancel();
        _stop.Dispose();
    }
}