using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Npgsql;

namespace TechMES.Infrastructure.PostgreSql;

/// <summary>
/// Инициализирует основную PostgreSQL-БД Runtime/Info-модуля.
/// 
/// Выполняется при старте TechMES.Runtime.Service:
/// 1) проверяет наличие Database:ConnectionString;
/// 2) создает целевую БД, если она отсутствует;
/// 3) создает недостающие таблицы/индексы основного модуля.
/// 
/// Важно:
/// PostgreSQL-сервер должен быть уже установлен и доступен.
/// Если БД отсутствует, пользователь из connection string должен иметь CREATEDB.
/// </summary>
public sealed class PostgreSqlDatabaseBootstrapper
{
    private const string InvalidCatalogSqlState = "3D000";

    private readonly IConfiguration _configuration;
    private readonly ILogger<PostgreSqlDatabaseBootstrapper> _logger;

    public PostgreSqlDatabaseBootstrapper(
        IConfiguration configuration,
        ILogger<PostgreSqlDatabaseBootstrapper> logger)
    {
        _configuration = configuration;
        _logger = logger;
    }

    public async Task InitializeMainDatabaseAsync(CancellationToken cancellationToken = default)
    {
        var provider = _configuration["Database:Provider"] ?? "PostgreSql";
        if (!string.Equals(provider, "PostgreSql", StringComparison.OrdinalIgnoreCase))
        {
            _logger.LogInformation(
                "PostgreSQL database bootstrap skipped. Database:Provider={Provider}.",
                provider);

            return;
        }

        var connectionString = GetMainConnectionString();

        await EnsureDatabaseExistsAsync(connectionString, cancellationToken);
        await EnsureMainSchemaAsync(connectionString, cancellationToken);
    }

    private string GetMainConnectionString()
    {
        var connectionString =
            _configuration.GetConnectionString("Default")
            ?? _configuration["Database:ConnectionString"];

        if (string.IsNullOrWhiteSpace(connectionString))
        {
            throw new InvalidOperationException(
                "PostgreSQL connection string is not configured. " +
                "Set ConnectionStrings:Default or Database:ConnectionString.");
        }

        return connectionString;
    }

    private async Task EnsureDatabaseExistsAsync(
        string connectionString,
        CancellationToken cancellationToken)
    {
        var targetBuilder = new NpgsqlConnectionStringBuilder(connectionString);
        var databaseName = targetBuilder.Database?.Trim();

        if (string.IsNullOrWhiteSpace(databaseName))
            throw new InvalidOperationException("PostgreSQL connection string does not contain Database name.");

        try
        {
            await using var testConnection = new NpgsqlConnection(connectionString);
            await testConnection.OpenAsync(cancellationToken);

            _logger.LogInformation(
                "PostgreSQL database '{Database}' exists and is accessible.",
                databaseName);

            return;
        }
        catch (PostgresException ex) when (ex.SqlState == InvalidCatalogSqlState)
        {
            _logger.LogWarning(
                "PostgreSQL database '{Database}' does not exist. Trying to create it.",
                databaseName);
        }

        await using var adminConnection = await OpenAdminConnectionAsync(
            connectionString,
            cancellationToken);

        await using (var existsCommand = new NpgsqlCommand(
            """
            SELECT 1
            FROM pg_database
            WHERE datname = @database_name
            LIMIT 1;
            """,
            adminConnection))
        {
            existsCommand.Parameters.AddWithValue("database_name", databaseName);

            var exists = await existsCommand.ExecuteScalarAsync(cancellationToken);
            if (exists is not null)
            {
                _logger.LogInformation(
                    "PostgreSQL database '{Database}' already exists.",
                    databaseName);

                return;
            }
        }

        var createDatabaseSql = $"CREATE DATABASE {QuoteIdentifier(databaseName)};";

        await using (var createCommand = new NpgsqlCommand(
            createDatabaseSql,
            adminConnection))
        {
            await createCommand.ExecuteNonQueryAsync(cancellationToken);
        }

        _logger.LogInformation(
            "PostgreSQL database '{Database}' was created.",
            databaseName);
    }

    private static async Task<NpgsqlConnection> OpenAdminConnectionAsync(
        string targetConnectionString,
        CancellationToken cancellationToken)
    {
        var adminBuilder = new NpgsqlConnectionStringBuilder(targetConnectionString)
        {
            Database = "postgres"
        };

        var connection = new NpgsqlConnection(adminBuilder.ConnectionString);

        try
        {
            await connection.OpenAsync(cancellationToken);
            return connection;
        }
        catch (PostgresException ex) when (ex.SqlState == InvalidCatalogSqlState)
        {
            await connection.DisposeAsync();
        }

        adminBuilder.Database = "template1";
        connection = new NpgsqlConnection(adminBuilder.ConnectionString);
        await connection.OpenAsync(cancellationToken);

        return connection;
    }

    private async Task EnsureMainSchemaAsync(
        string connectionString,
        CancellationToken cancellationToken)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken);

        await using var command = new NpgsqlCommand(MainSchemaSql, connection);
        await command.ExecuteNonQueryAsync(cancellationToken);

        _logger.LogInformation("PostgreSQL main TechMES schema is ready.");
    }

    private static string QuoteIdentifier(string value)
    {
        return "\"" + value.Replace("\"", "\"\"") + "\"";
    }

    private const string MainSchemaSql = """
        CREATE TABLE IF NOT EXISTS public.equip_info
        (
            equip_name   text PRIMARY KEY,
            product_code text NULL,
            supplier     text NULL,
            description  text NULL,
            updated_at   timestamp without time zone NOT NULL DEFAULT now()
        );

        CREATE TABLE IF NOT EXISTS public.equip_photo
        (
            id               bigserial PRIMARY KEY,
            equip_type_group text NULL,
            file_name        text NOT NULL,
            display_name     text NOT NULL,
            file_hash        text NOT NULL,
            file_data        bytea NOT NULL,
            updated_at       timestamp without time zone NOT NULL DEFAULT now(),
            CONSTRAINT uq_equip_photo_hash UNIQUE (file_hash)
        );

        CREATE TABLE IF NOT EXISTS public.equip_instruction
        (
            id               bigserial PRIMARY KEY,
            equip_type_group text NULL,
            file_name        text NOT NULL,
            display_name     text NOT NULL,
            file_hash        text NOT NULL,
            file_data        bytea NOT NULL,
            updated_at       timestamp without time zone NOT NULL DEFAULT now(),
            CONSTRAINT uq_equip_instruction_hash UNIQUE (file_hash)
        );

        CREATE TABLE IF NOT EXISTS public.equip_scheme
        (
            id               bigserial PRIMARY KEY,
            equip_type_group text NULL,
            file_name        text NOT NULL,
            display_name     text NOT NULL,
            file_hash        text NOT NULL,
            file_data        bytea NOT NULL,
            updated_at       timestamp without time zone NOT NULL DEFAULT now(),
            CONSTRAINT uq_equip_scheme_hash UNIQUE (file_hash)
        );

        CREATE TABLE IF NOT EXISTS public.equip_info_photo
        (
            equip_name text NOT NULL REFERENCES public.equip_info(equip_name) ON DELETE CASCADE,
            photo_id   bigint NOT NULL REFERENCES public.equip_photo(id) ON DELETE CASCADE,
            sort_order integer NOT NULL DEFAULT 0,
            CONSTRAINT pk_equip_info_photo PRIMARY KEY (equip_name, photo_id)
        );

        CREATE TABLE IF NOT EXISTS public.equip_info_instruction
        (
            equip_name     text NOT NULL REFERENCES public.equip_info(equip_name) ON DELETE CASCADE,
            instruction_id bigint NOT NULL REFERENCES public.equip_instruction(id) ON DELETE CASCADE,
            sort_order     integer NOT NULL DEFAULT 0,
            CONSTRAINT pk_equip_info_instruction PRIMARY KEY (equip_name, instruction_id)
        );

        CREATE TABLE IF NOT EXISTS public.equip_info_scheme
        (
            equip_name text NOT NULL REFERENCES public.equip_info(equip_name) ON DELETE CASCADE,
            scheme_id  bigint NOT NULL REFERENCES public.equip_scheme(id) ON DELETE CASCADE,
            sort_order integer NOT NULL DEFAULT 0,
            CONSTRAINT pk_equip_info_scheme PRIMARY KEY (equip_name, scheme_id)
        );

        CREATE TABLE IF NOT EXISTS public.equip_info_pdf_view
        (
            equip_name     text NOT NULL REFERENCES public.equip_info(equip_name) ON DELETE CASCADE,
            info_page_kind text NOT NULL,
            file_id        bigint NOT NULL,
            file_name      text NOT NULL,
            page_number    integer NOT NULL DEFAULT 1,
            zoom_factor    double precision NOT NULL DEFAULT 100,
            anchor_x       double precision NOT NULL DEFAULT 0,
            anchor_y       double precision NOT NULL DEFAULT 0,
            updated_at     timestamp without time zone NOT NULL DEFAULT now(),
            CONSTRAINT pk_equip_info_pdf_view PRIMARY KEY (equip_name, info_page_kind, file_id)
        );

        CREATE TABLE IF NOT EXISTS public.equip_favorite
        (
            device_name text NOT NULL,
            equip_name  text NOT NULL,
            updated_at  timestamp without time zone NOT NULL DEFAULT now(),
            CONSTRAINT pk_equip_favorite PRIMARY KEY (device_name, equip_name)
        );

        COMMENT ON COLUMN public.equip_favorite.device_name
        IS 'Logical favorite owner. Stores Windows user name for Web favorites; column name is kept for compatibility.';

        CREATE TABLE IF NOT EXISTS public.equip_supplier
        (
            id             bigserial PRIMARY KEY,
            name           text NOT NULL UNIQUE,
            logo_file_name text NULL,
            logo_file_hash text NULL,
            logo_data      bytea NULL,
            updated_at     timestamp without time zone NOT NULL DEFAULT now()
        );

        CREATE TABLE IF NOT EXISTS public.equip_order
        (
            id           bigserial PRIMARY KEY,
            type         text NULL,
            product_code text NOT NULL UNIQUE,
            supplier_id  bigint NULL REFERENCES public.equip_supplier(id) ON DELETE SET NULL,
            description  text NULL,
            source       text NULL,
            image        text NULL,
            updated_at   timestamp without time zone NOT NULL DEFAULT now()
        );

        CREATE TABLE IF NOT EXISTS public.equip_note
        (
            id         bigserial PRIMARY KEY,
            equip_name text NOT NULL REFERENCES public.equip_info(equip_name) ON DELETE CASCADE,
            note_text  text NOT NULL,
            created_by text NOT NULL,
            created_at timestamp without time zone NOT NULL DEFAULT now(),
            updated_by text NULL,
            updated_at timestamp without time zone NULL
        );

        CREATE TABLE IF NOT EXISTS public.equip_param_tune
        (
            equipment_name text PRIMARY KEY,
            pv             text NULL,
            pv_min         double precision NULL,
            pv_max         double precision NULL,
            sp             text NULL,
            sp_min         double precision NULL,
            sp_max         double precision NULL,
            kp             double precision NULL,
            ti             double precision NULL,
            td             double precision NULL,
            updated_at     timestamp without time zone NOT NULL DEFAULT now()
        );

        CREATE INDEX IF NOT EXISTS ix_equip_photo_type
            ON public.equip_photo (equip_type_group, display_name);

        CREATE INDEX IF NOT EXISTS ix_equip_instruction_type
            ON public.equip_instruction (equip_type_group, display_name);

        CREATE INDEX IF NOT EXISTS ix_equip_scheme_type
            ON public.equip_scheme (equip_type_group, display_name);

        CREATE INDEX IF NOT EXISTS ix_equip_photo_type_lower_file_name
            ON public.equip_photo (equip_type_group, lower(file_name));

        CREATE INDEX IF NOT EXISTS ix_equip_instruction_type_lower_file_name
            ON public.equip_instruction (equip_type_group, lower(file_name));

        CREATE INDEX IF NOT EXISTS ix_equip_scheme_type_lower_file_name
            ON public.equip_scheme (equip_type_group, lower(file_name));

        CREATE INDEX IF NOT EXISTS ix_equip_pdf_view_lookup
            ON public.equip_info_pdf_view (equip_name, info_page_kind, file_id);

        CREATE INDEX IF NOT EXISTS ix_equip_favorite_equip
            ON public.equip_favorite (equip_name);

        CREATE INDEX IF NOT EXISTS ix_equip_favorite_device
            ON public.equip_favorite (device_name);

        CREATE INDEX IF NOT EXISTS ix_equip_supplier_name
            ON public.equip_supplier (name);

        CREATE INDEX IF NOT EXISTS ix_equip_order_product_code
            ON public.equip_order (product_code);

        CREATE INDEX IF NOT EXISTS ix_equip_order_supplier_id
            ON public.equip_order (supplier_id);

        CREATE INDEX IF NOT EXISTS ix_equip_note_equip_created
            ON public.equip_note (equip_name, created_at DESC, id DESC);
        """;
}