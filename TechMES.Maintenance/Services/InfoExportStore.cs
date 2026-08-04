using System.Data;
using Npgsql;
using TechMES.Maintenance.Models;

namespace TechMES.Maintenance.Services;

/// <summary>
/// Читает полный экспортный снимок Info-модуля непосредственно из PostgreSQL.
///
/// Store не использует коллекции UI, поэтому фильтрация и состояние вкладок
/// Import/Edit никак не влияют на экспорт.
/// </summary>
public sealed class InfoExportStore
{
    /// <summary>
    /// Загружает все данные, необходимые для создания переносимого пакета.
    /// </summary>
    public async Task<InfoExportDatabaseSnapshot> LoadAsync(string connectionString, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            throw new InvalidOperationException("PostgreSQL connection string is empty.");
        }

        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken);
        var suppliers = await LoadSuppliersAsync(connection, cancellationToken);
        var orders = await LoadOrdersAsync(connection, cancellationToken);
        var equipmentInfo = await LoadEquipmentInfoAsync(connection, cancellationToken);
        var instructionFiles = await LoadBinaryFilesAsync(connection, "public.equip_instruction", cancellationToken);
        var photoFiles = await LoadBinaryFilesAsync(connection, "public.equip_photo", cancellationToken);
        var schemeFiles = await LoadSchemeFilesAsync(connection, cancellationToken);

        return new InfoExportDatabaseSnapshot(suppliers, orders, equipmentInfo, instructionFiles, photoFiles, schemeFiles);
    }

    /// <summary>
    /// Загружает поставщиков и бинарные данные логотипов.
    ///
    /// При использовании CommandBehavior.SequentialAccess
    /// столбцы обязательно читаются строго слева направо:
    /// 0 → 1 → 2 → 3.
    /// </summary>
    private static async Task<IReadOnlyList<InfoExportSupplierRow>>LoadSuppliersAsync(NpgsqlConnection connection, CancellationToken cancellationToken)
    {
        const string sql = """
        SELECT
            name,
            COALESCE(logo_file_name, ''),
            COALESCE(logo_file_hash, ''),
            logo_data
        FROM public.equip_supplier
        ORDER BY
            lower(name),
            id;
        """;

        var result = new List<InfoExportSupplierRow>();
        await using var command = new NpgsqlCommand(sql, connection);
        await using var reader = await command.ExecuteReaderAsync(CommandBehavior.SequentialAccess, cancellationToken);

        while (await reader.ReadAsync(cancellationToken))
        {
            // При SequentialAccess нельзя сначала прочитать колонку 3, а затем вернуться к колонкам 0, 1 или 2.
            // Поэтому сначала читаем все текстовые поля, а бинарный logo_data читаем последним.

            var supplierName = reader.GetString(0);
            var logoFileName = reader.GetString(1);
            var logoFileHash = reader.GetString(2);
            byte[]? logoData = null;

            if (!reader.IsDBNull(3))
                logoData = reader.GetFieldValue<byte[]>(3);

            result.Add(new InfoExportSupplierRow(supplierName, logoFileName, logoFileHash, logoData));
        }

        return result;
    }

    /// <summary>
    /// Загружает ORDERS с именем поставщика.
    /// </summary>
    private static async Task<IReadOnlyList<InfoExportOrderRow>>LoadOrdersAsync(NpgsqlConnection connection, CancellationToken cancellationToken)
    {
        const string sql = """
            SELECT
                COALESCE(order_row.type, ''),
                order_row.product_code,
                COALESCE(supplier.name, ''),
                COALESCE(order_row.source, ''),
                COALESCE(order_row.description, ''),
                COALESCE(order_row.image, '')
            FROM public.equip_order order_row
            LEFT JOIN public.equip_supplier supplier
                ON supplier.id = order_row.supplier_id
            ORDER BY
                lower(COALESCE(order_row.type, '')),
                lower(order_row.product_code),
                order_row.id;
            """;

        var result =
            new List<InfoExportOrderRow>();

        await using var command =
            new NpgsqlCommand(sql, connection);

        await using var reader =
            await command.ExecuteReaderAsync(cancellationToken);

        while (await reader.ReadAsync(cancellationToken))
        {
            result.Add(
                new InfoExportOrderRow(
                    reader.GetString(0),
                    reader.GetString(1),
                    reader.GetString(2),
                    reader.GetString(3),
                    reader.GetString(4),
                    reader.GetString(5)));
        }

        return result;
    }

    /// <summary>
    /// Загружает equip_info и определяет, есть ли у строки
    /// реальные связи с INSTRUCTION или PHOTO.
    /// </summary>
    private static async Task<IReadOnlyList<InfoExportEquipmentInfoRow>>LoadEquipmentInfoAsync(NpgsqlConnection connection, CancellationToken cancellationToken)
    {
        const string sql = """
            SELECT
                info.equip_name,
                COALESCE(info.product_code, ''),
                COALESCE(info.supplier, ''),
                COALESCE(info.description, ''),

                EXISTS
                (
                    SELECT 1
                    FROM public.equip_info_instruction link
                    WHERE link.equip_name = info.equip_name
                ) AS has_instruction_links,

                EXISTS
                (
                    SELECT 1
                    FROM public.equip_info_photo link
                    WHERE link.equip_name = info.equip_name
                ) AS has_photo_links

            FROM public.equip_info info
            ORDER BY lower(info.equip_name);
            """;

        var result =
            new List<InfoExportEquipmentInfoRow>();

        await using var command =
            new NpgsqlCommand(sql, connection);

        await using var reader =
            await command.ExecuteReaderAsync(cancellationToken);

        while (await reader.ReadAsync(cancellationToken))
        {
            result.Add(
                new InfoExportEquipmentInfoRow(
                    reader.GetString(0),
                    reader.GetString(1),
                    reader.GetString(2),
                    reader.GetString(3),
                    reader.GetBoolean(4),
                    reader.GetBoolean(5)));
        }

        return result;
    }

    /// <summary>
    /// Загружает одну библиотеку бинарных файлов.
    /// </summary>
    private static async Task<IReadOnlyList<InfoExportBinaryFile>>LoadBinaryFilesAsync(NpgsqlConnection connection, string tableName, CancellationToken cancellationToken)
    {
        tableName = tableName switch
        {
            "public.equip_instruction" =>
                "public.equip_instruction",

            "public.equip_photo" =>
                "public.equip_photo",

            _ => throw new ArgumentOutOfRangeException(
                nameof(tableName),
                tableName,
                "Unsupported export library table.")
        };

        var sql = $"""
            SELECT
                id,
                file_name,
                file_hash,
                file_data
            FROM {tableName}
            ORDER BY lower(file_name), id;
            """;

        var result =
            new List<InfoExportBinaryFile>();

        await using var command =
            new NpgsqlCommand(sql, connection);

        await using var reader =
            await command.ExecuteReaderAsync(
                CommandBehavior.SequentialAccess,
                cancellationToken);

        while (await reader.ReadAsync(cancellationToken))
        {
            result.Add(
                new InfoExportBinaryFile(
                    reader.GetInt64(0),
                    reader.GetString(1),
                    reader.GetString(2),
                    reader.GetFieldValue<byte[]>(3)));
        }

        return result;
    }

    /// <summary>
    /// Загружает SCHEME вместе с бинарными данными и областями назначения.
    ///
    /// Для старых строк с пустым equipments используется fallback
    /// из public.equip_info_scheme.
    /// </summary>
    private static async Task<IReadOnlyList<InfoExportSchemeFile>>LoadSchemeFilesAsync(NpgsqlConnection connection, CancellationToken cancellationToken)
    {
        const string sql = """
            SELECT
                scheme.id,
                scheme.file_name,
                scheme.file_hash,
                scheme.file_data,
                COALESCE(scheme.station, ''),
                COALESCE(scheme.group_names, ''),

                COALESCE
                (
                    NULLIF(btrim(scheme.equipments), ''),
                    linked_equipment.equipments,
                    ''
                ) AS equipments

            FROM public.equip_scheme scheme

            LEFT JOIN LATERAL
            (
                SELECT
                    string_agg(
                        link.equip_name,
                        '; '
                        ORDER BY lower(link.equip_name)
                    ) AS equipments
                FROM public.equip_info_scheme link
                WHERE link.scheme_id = scheme.id
            ) linked_equipment
                ON TRUE

            ORDER BY
                lower(NULLIF(btrim(scheme.station), '')) NULLS LAST,
                lower(scheme.file_name),
                scheme.id;
            """;

        var result = new List<InfoExportSchemeFile>();
        await using var command = new NpgsqlCommand(sql, connection);
        await using var reader = await command.ExecuteReaderAsync(CommandBehavior.SequentialAccess, cancellationToken);

        while (await reader.ReadAsync(cancellationToken))
        {
            result.Add(
                new InfoExportSchemeFile(
                    reader.GetInt64(0),
                    reader.GetString(1),
                    reader.GetString(2),
                    reader.GetFieldValue<byte[]>(3),
                    reader.GetString(4),
                    reader.GetString(5),
                    reader.GetString(6)));
        }

        return result;
    }
}