using System.Security.Cryptography;
using Npgsql;
using TechMES.Maintenance.ViewModels;

namespace TechMES.Maintenance.Services;

/// <summary>
/// Read/write доступ Maintenance к справочникам Info-модуля в PostgreSQL.
/// Здесь намеренно повторяется логика старого WPF-импорта: supplier хранится отдельно, а order ссылается на supplier_id.
/// </summary>
public sealed class InfoImportEditStore
{
    /// <summary>
    /// Загружает вкладку SUPPLIER из public.equip_supplier.
    /// Бинарный logo_data не читается полностью, UI показывает только факт наличия логотипа и имя файла.
    /// </summary>
    public async Task<IReadOnlyList<ImportSupplierRowViewModel>> LoadSuppliersAsync(
        string connectionString,
        CancellationToken cancellationToken = default)
    {
        const string sql = """
            SELECT
                name,
                COALESCE(logo_file_name, '') AS logo_file_name,
                logo_data,
                logo_data IS NOT NULL AND octet_length(logo_data) > 0 AS has_logo
            FROM public.equip_supplier
            ORDER BY name;
            """;

        var result = new List<ImportSupplierRowViewModel>();
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken);

        await using var command = new NpgsqlCommand(sql, connection);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);

        while (await reader.ReadAsync(cancellationToken))
        {
            var hasLogo = reader.GetBoolean(3);
            var row = new ImportSupplierRowViewModel
            {
                Supplier = reader.GetString(0),
                LogoFileName = reader.GetString(1),
                LogoStatus = hasLogo ? "Stored" : "No logo"
            };

            if (!reader.IsDBNull(2))
                row.SetLogoPreview((byte[])reader.GetValue(2));

            result.Add(row);
        }

        return result;
    }

    /// <summary>
    /// Сохраняет строки SUPPLIER.
    /// Если логотип не выбирался заново, существующие logo_data/logo_hash в БД не трогаются.
    /// </summary>
    public async Task<int> SaveSuppliersAsync(
        string connectionString,
        IEnumerable<ImportSupplierRowViewModel> suppliers,
        CancellationToken cancellationToken = default)
    {
        var clean = suppliers
            .Where(x => !string.IsNullOrWhiteSpace(x.Supplier))
            .GroupBy(x => x.Supplier.Trim(), StringComparer.OrdinalIgnoreCase)
            .Select(x => x.Last())
            .ToList();

        if (clean.Count == 0)
            return 0;

        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);

        try
        {
            foreach (var item in clean)
            {
                if (item.LogoChanged)
                    await SaveSupplierWithLogoAsync(connection, transaction, item, cancellationToken);
                else
                    await SaveSupplierMetadataAsync(connection, transaction, item, cancellationToken);
            }

            await transaction.CommitAsync(cancellationToken);
            return clean.Count;
        }
        catch
        {
            await transaction.RollbackAsync(cancellationToken);
            throw;
        }
    }

    /// <summary>
    /// Удаляет поставщиков, которые оператор убрал из SUPPLIER-таблицы и подтвердил кнопкой Save.
    /// Если поставщик связан с ORDERS или другими таблицами внешним ключом, PostgreSQL вернет ошибку, а Maintenance покажет ее оператору.
    /// </summary>
    public async Task<int> DeleteSuppliersAsync(
        string connectionString,
        IEnumerable<string> supplierNames,
        CancellationToken cancellationToken = default)
    {
        var clean = supplierNames
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .Select(x => x.Trim().ToLowerInvariant())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        if (clean.Length == 0)
            return 0;

        const string sql = """
            DELETE FROM public.equip_supplier
            WHERE lower(name) = ANY(@names);
            """;

        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken);

        await using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.AddWithValue("names", clean);
        return await command.ExecuteNonQueryAsync(cancellationToken);
    }

    /// <summary>
    /// Загружает вкладку ORDERS из public.equip_order и показывает supplier по имени, а не по supplier_id.
    /// </summary>
    public async Task<IReadOnlyList<ImportOrderRowViewModel>> LoadOrdersAsync(
        string connectionString,
        CancellationToken cancellationToken = default)
    {
        const string sql = """
            SELECT
                COALESCE(o.type, '') AS type,
                o.product_code,
                COALESCE(s.name, '') AS supplier,
                COALESCE(o.source, '') AS source,
                COALESCE(o.description, '') AS description,
                COALESCE(o.image, '') AS image
            FROM public.equip_order o
            LEFT JOIN public.equip_supplier s ON s.id = o.supplier_id
            ORDER BY o.type NULLS LAST, o.product_code;
            """;

        var result = new List<ImportOrderRowViewModel>();
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken);

        await using var command = new NpgsqlCommand(sql, connection);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);

        while (await reader.ReadAsync(cancellationToken))
        {
            result.Add(new ImportOrderRowViewModel
            {
                Type = reader.GetString(0),
                ProductCode = reader.GetString(1),
                Supplier = reader.GetString(2),
                Source = reader.GetString(3),
                Description = reader.GetString(4),
                Image = reader.GetString(5)
            });
        }

        return result;
    }

    /// <summary>
    /// Сохраняет вкладку ORDERS.
    /// Для каждой строки с непустым Supplier сначала создаётся/находится поставщик, затем order обновляется по product_code.
    /// </summary>
    public async Task<int> SaveOrdersAsync(
        string connectionString,
        IEnumerable<ImportOrderRowViewModel> orders,
        CancellationToken cancellationToken = default)
    {
        var clean = orders
            .Where(x => !string.IsNullOrWhiteSpace(x.ProductCode))
            .GroupBy(x => x.ProductCode.Trim(), StringComparer.OrdinalIgnoreCase)
            .Select(x => x.Last())
            .ToList();

        if (clean.Count == 0)
            return 0;

        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);

        try
        {
            foreach (var item in clean)
            {
                var supplierId = await ResolveSupplierIdAsync(connection, transaction, item.Supplier, cancellationToken);
                await SaveOrderAsync(connection, transaction, item, supplierId, cancellationToken);
            }

            await transaction.CommitAsync(cancellationToken);
            return clean.Count;
        }
        catch
        {
            await transaction.RollbackAsync(cancellationToken);
            throw;
        }
    }

    /// <summary>
    /// Обновляет только имя поставщика и имя файла, не затрагивая существующие бинарные данные логотипа.
    /// </summary>
    private static async Task SaveSupplierMetadataAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        ImportSupplierRowViewModel item,
        CancellationToken cancellationToken)
    {
        const string sql = """
            INSERT INTO public.equip_supplier (name, logo_file_name, updated_at)
            VALUES (@name, NULLIF(@logo_file_name, ''), now())
            ON CONFLICT (name)
            DO UPDATE SET
                logo_file_name = COALESCE(NULLIF(EXCLUDED.logo_file_name, ''), public.equip_supplier.logo_file_name),
                updated_at = now();
            """;

        await using var command = new NpgsqlCommand(sql, connection, transaction);
        command.Parameters.AddWithValue("name", item.Supplier.Trim());
        command.Parameters.AddWithValue("logo_file_name", item.LogoFileName?.Trim() ?? "");
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    /// <summary>
    /// Сохраняет поставщика вместе с новым logo_data и SHA-256 hash, чтобы поведение совпадало со старым WPF-импортом.
    /// </summary>
    private static async Task SaveSupplierWithLogoAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        ImportSupplierRowViewModel item,
        CancellationToken cancellationToken)
    {
        const string sql = """
            INSERT INTO public.equip_supplier
            (
                name,
                logo_file_name,
                logo_file_hash,
                logo_data,
                updated_at
            )
            VALUES
            (
                @name,
                NULLIF(@logo_file_name, ''),
                @logo_file_hash,
                @logo_data,
                now()
            )
            ON CONFLICT (name)
            DO UPDATE SET
                logo_file_name = EXCLUDED.logo_file_name,
                logo_file_hash = EXCLUDED.logo_file_hash,
                logo_data = EXCLUDED.logo_data,
                updated_at = now();
            """;

        var logoData = item.PendingLogoData ?? [];
        await using var command = new NpgsqlCommand(sql, connection, transaction);
        command.Parameters.AddWithValue("name", item.Supplier.Trim());
        command.Parameters.AddWithValue("logo_file_name", item.LogoFileName?.Trim() ?? "");
        command.Parameters.AddWithValue("logo_file_hash", logoData.Length == 0 ? DBNull.Value : ComputeSha256(logoData));
        command.Parameters.AddWithValue("logo_data", logoData.Length == 0 ? DBNull.Value : logoData);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    /// <summary>
    /// Возвращает id поставщика для ORDERS.
    /// Если поставщик указан, но его ещё нет в public.equip_supplier, создаёт пустую строку поставщика.
    /// </summary>
    private static async Task<long?> ResolveSupplierIdAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        string supplier,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(supplier))
            return null;

        const string sql = """
            INSERT INTO public.equip_supplier (name, updated_at)
            VALUES (@name, now())
            ON CONFLICT (name)
            DO UPDATE SET updated_at = public.equip_supplier.updated_at
            RETURNING id;
            """;

        await using var command = new NpgsqlCommand(sql, connection, transaction);
        command.Parameters.AddWithValue("name", supplier.Trim());
        var scalar = await command.ExecuteScalarAsync(cancellationToken);
        return scalar is null or DBNull ? null : Convert.ToInt64(scalar);
    }

    /// <summary>
    /// Обновляет заказ по product_code. Это тот же ключ, который использовался в старом Excel-импорте.
    /// </summary>
    private static async Task SaveOrderAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        ImportOrderRowViewModel item,
        long? supplierId,
        CancellationToken cancellationToken)
    {
        const string sql = """
            INSERT INTO public.equip_order
            (
                type,
                product_code,
                supplier_id,
                description,
                source,
                image,
                updated_at
            )
            VALUES
            (
                NULLIF(@type, ''),
                @product_code,
                @supplier_id,
                NULLIF(@description, ''),
                NULLIF(@source, ''),
                NULLIF(@image, ''),
                now()
            )
            ON CONFLICT (product_code)
            DO UPDATE SET
                type = EXCLUDED.type,
                supplier_id = EXCLUDED.supplier_id,
                description = EXCLUDED.description,
                source = EXCLUDED.source,
                image = EXCLUDED.image,
                updated_at = now();
            """;

        await using var command = new NpgsqlCommand(sql, connection, transaction);
        command.Parameters.AddWithValue("type", item.Type?.Trim() ?? "");
        command.Parameters.AddWithValue("product_code", item.ProductCode.Trim());
        command.Parameters.AddWithValue("supplier_id", supplierId is null ? DBNull.Value : supplierId.Value);
        command.Parameters.AddWithValue("description", item.Description?.Trim() ?? "");
        command.Parameters.AddWithValue("source", item.Source?.Trim() ?? "");
        command.Parameters.AddWithValue("image", item.Image?.Trim() ?? "");
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    /// <summary>
    /// Считает SHA-256 в hex-формате, как устойчивый признак выбранного файла логотипа.
    /// </summary>
    private static string ComputeSha256(byte[] data)
    {
        return Convert.ToHexString(SHA256.HashData(data)).ToLowerInvariant();
    }
}
