using System;
using System.IO;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace TechEquipments
{
    /// <summary>
    /// Сохраняет/читает UserState в JSON в %AppData%\TechEquipments\user-state.json
    /// </summary>
    public sealed class JsonUserStateService : IUserStateService
    {
        private readonly JsonSerializerOptions _json = new()
        {
            WriteIndented = true,
            PropertyNameCaseInsensitive = true
        };

        private readonly SemaphoreSlim _gate = new(1, 1);

        public string StateFilePath { get; }

        public JsonUserStateService()
        {
            var dir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "TechEquipments");

            Directory.CreateDirectory(dir);

            StateFilePath = Path.Combine(dir, "user-state.json");
        }

        public async Task<UserState?> LoadAsync(CancellationToken ct = default)
        {
            try
            {
                if (!File.Exists(StateFilePath))
                    return null;

                await using var fs = File.OpenRead(StateFilePath);
                return await JsonSerializer.DeserializeAsync<UserState>(fs, _json, ct);
            }
            catch
            {
                // Если файл битый/не читается — просто не валим приложение.
                return null;
            }
        }

        public async Task SaveAsync(UserState state, CancellationToken ct = default)
        {
            if (state is null) throw new ArgumentNullException(nameof(state));

            await _gate.WaitAsync(ct);
            try
            {
                // Пишем атомарно: сначала во временный файл, потом заменяем основной.
                var tmp = StateFilePath + ".tmp";

                await using (var fs = File.Create(tmp))
                {
                    await JsonSerializer.SerializeAsync(fs, state, _json, ct);
                }

                // На Windows File.Replace делает атомарную замену.
                if (File.Exists(StateFilePath))
                    File.Replace(tmp, StateFilePath, null);
                else
                    File.Move(tmp, StateFilePath);
            }
            finally
            {
                _gate.Release();
            }
        }
    }

}
