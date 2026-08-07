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

    public PostgreSqlDatabaseBootstrapper(IConfiguration configuration, ILogger<PostgreSqlDatabaseBootstrapper> logger)
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

    private async Task EnsureDatabaseExistsAsync(string connectionString, CancellationToken cancellationToken)
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

    private static async Task<NpgsqlConnection> OpenAdminConnectionAsync(string targetConnectionString, CancellationToken cancellationToken)
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

    /// <summary>
    /// Создаёт основную схему TechMES и таблицы расчётного модуля.
    /// Все изменения выполняются одной транзакцией.
    /// </summary>
    private async Task EnsureMainSchemaAsync(string connectionString, CancellationToken cancellationToken)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);

        try
        {
            await ExecuteSchemaAsync(connection, transaction, MainSchemaSql, cancellationToken);
            await ExecuteSchemaAsync(connection, transaction, CalcSchemaSql, cancellationToken);

             // equip_param_tune существовал до Test Kp. ADD COLUMN IF NOT EXISTS обновляет старую БД и безопасно выполняется повторно.
            await EnsureParamTuneExtensionSchemaAsync(connection, transaction, cancellationToken);
            await transaction.CommitAsync(cancellationToken);
        }
        catch
        {
            await transaction.RollbackAsync(cancellationToken);
            throw;
        }

        _logger.LogInformation("PostgreSQL main TechMES and Calc schemas are ready.");
    }

    /// <summary>
    /// Добавляет новые поля PID Tune без отдельной системы миграций.
    /// DDL идемпотентен и подходит как для старой, так и для новой БД.
    /// </summary>
    private static async Task EnsureParamTuneExtensionSchemaAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, CancellationToken cancellationToken)
    {
        const string sql = """
        ALTER TABLE public.equip_param_tune
            ADD COLUMN IF NOT EXISTS test_kp_tag text NULL;
        """;

        await ExecuteSchemaAsync(connection, transaction, sql, cancellationToken);
    }

    /// <summary>
    /// Выполняет один блок DDL внутри общей транзакции bootstrapper.
    /// </summary>
    private static async Task ExecuteSchemaAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, string sql, CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(sql, connection, transaction);
        await command.ExecuteNonQueryAsync(cancellationToken);
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
        id           bigserial PRIMARY KEY,
        file_name    text NOT NULL,
        display_name text NOT NULL,
        file_hash    text NOT NULL,
        file_data    bytea NOT NULL,
        updated_at   timestamp without time zone NOT NULL DEFAULT now(),
        CONSTRAINT uq_equip_photo_hash UNIQUE (file_hash)
    );

    CREATE TABLE IF NOT EXISTS public.equip_instruction
    (
        id           bigserial PRIMARY KEY,
        file_name    text NOT NULL,
        display_name text NOT NULL,
        file_hash    text NOT NULL,
        file_data    bytea NOT NULL,
        updated_at   timestamp without time zone NOT NULL DEFAULT now(),
        CONSTRAINT uq_equip_instruction_hash UNIQUE (file_hash)
    );

    CREATE TABLE IF NOT EXISTS public.equip_scheme
    (
        id           bigserial PRIMARY KEY,
        file_name    text NOT NULL,
        display_name text NOT NULL,
        file_hash    text NOT NULL,
        file_data    bytea NOT NULL,
        station      text NULL,
        group_names  text NULL,
        equipments   text NULL,
        updated_at   timestamp without time zone NOT NULL DEFAULT now(),
        CONSTRAINT uq_equip_scheme_hash UNIQUE (file_hash)
    );

    COMMENT ON COLUMN public.equip_scheme.station
    IS 'SCHEME station targets. Several values can be separated with comma or semicolon.';

    COMMENT ON COLUMN public.equip_scheme.group_names
    IS 'SCHEME group targets. Several values can be separated with comma or semicolon.';

    COMMENT ON COLUMN public.equip_scheme.equipments
    IS 'SCHEME equipment targets. Several values can be separated with comma or semicolon.';

    CREATE TABLE IF NOT EXISTS public.equip_info_photo
    (
        equip_name text NOT NULL
            REFERENCES public.equip_info(equip_name)
            ON DELETE CASCADE,

        photo_id bigint NOT NULL
            REFERENCES public.equip_photo(id)
            ON DELETE CASCADE,

        sort_order integer NOT NULL DEFAULT 0,

        CONSTRAINT pk_equip_info_photo
            PRIMARY KEY (equip_name, photo_id)
    );

    CREATE TABLE IF NOT EXISTS public.equip_info_instruction
    (
        equip_name text NOT NULL
            REFERENCES public.equip_info(equip_name)
            ON DELETE CASCADE,

        instruction_id bigint NOT NULL
            REFERENCES public.equip_instruction(id)
            ON DELETE CASCADE,

        sort_order integer NOT NULL DEFAULT 0,

        CONSTRAINT pk_equip_info_instruction
            PRIMARY KEY (equip_name, instruction_id)
    );

    CREATE TABLE IF NOT EXISTS public.equip_info_scheme
    (
        equip_name text NOT NULL
            REFERENCES public.equip_info(equip_name)
            ON DELETE CASCADE,

        scheme_id bigint NOT NULL
            REFERENCES public.equip_scheme(id)
            ON DELETE CASCADE,

        sort_order integer NOT NULL DEFAULT 0,

        CONSTRAINT pk_equip_info_scheme
            PRIMARY KEY (equip_name, scheme_id)
    );

    CREATE TABLE IF NOT EXISTS public.equip_info_pdf_view
    (
        equip_name     text NOT NULL
            REFERENCES public.equip_info(equip_name)
            ON DELETE CASCADE,

        info_page_kind text NOT NULL,
        file_id        bigint NOT NULL,
        file_name      text NOT NULL,
        page_number    integer NOT NULL DEFAULT 1,
        zoom_factor    double precision NOT NULL DEFAULT 100,
        anchor_x       double precision NOT NULL DEFAULT 0,
        anchor_y       double precision NOT NULL DEFAULT 0,
        updated_at     timestamp without time zone NOT NULL DEFAULT now(),

        CONSTRAINT pk_equip_info_pdf_view
            PRIMARY KEY (equip_name, info_page_kind, file_id)
    );

    CREATE TABLE IF NOT EXISTS public.equip_favorite
    (
        device_name text NOT NULL,
        equip_name  text NOT NULL,
        updated_at  timestamp without time zone NOT NULL DEFAULT now(),

        CONSTRAINT pk_equip_favorite
            PRIMARY KEY (device_name, equip_name)
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
        supplier_id  bigint NULL
            REFERENCES public.equip_supplier(id)
            ON DELETE SET NULL,

        description text NULL,
        source      text NULL,
        image       text NULL,
        updated_at  timestamp without time zone NOT NULL DEFAULT now()
    );

    CREATE TABLE IF NOT EXISTS public.equip_note
    (
        id         bigserial PRIMARY KEY,
        equip_name text NOT NULL
            REFERENCES public.equip_info(equip_name)
            ON DELETE CASCADE,

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

    CREATE INDEX IF NOT EXISTS ix_equip_photo_lower_file_name
        ON public.equip_photo (lower(file_name));

    CREATE INDEX IF NOT EXISTS ix_equip_instruction_lower_file_name
        ON public.equip_instruction (lower(file_name));

    CREATE INDEX IF NOT EXISTS ix_equip_scheme_lower_file_name
        ON public.equip_scheme (lower(file_name));

    CREATE INDEX IF NOT EXISTS ix_equip_photo_display_name
        ON public.equip_photo (display_name);

    CREATE INDEX IF NOT EXISTS ix_equip_instruction_display_name
        ON public.equip_instruction (display_name);

    CREATE INDEX IF NOT EXISTS ix_equip_scheme_display_name
        ON public.equip_scheme (display_name);

    CREATE INDEX IF NOT EXISTS ix_equip_pdf_view_lookup
        ON public.equip_info_pdf_view
        (equip_name, info_page_kind, file_id);

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
        ON public.equip_note
        (equip_name, created_at DESC, id DESC);
    """;

    /// <summary>
    /// Таблицы эксплуатационной конфигурации расчётов.
    ///
    /// Формулы и коэффициенты здесь не хранятся.
    /// Они остаются в версионируемом проекте TechMES.Calc.
    /// </summary>
    private const string CalcSchemaSql = """
        CREATE TABLE IF NOT EXISTS public.calc_job
        (
            id                 bigserial PRIMARY KEY,
            equipment_name     text NULL,
            name               text NOT NULL,
            description        text NULL,
            definition_code    text NOT NULL,
            definition_version text NOT NULL,
            enabled            boolean NOT NULL DEFAULT false,
            period_ms          integer NOT NULL DEFAULT 5000,
            write_enabled      boolean NOT NULL DEFAULT false,
            sort_order         integer NOT NULL DEFAULT 0,
            revision           bigint NOT NULL DEFAULT 1,
            created_by         text NULL,
            updated_by         text NULL,
            created_at         timestamp with time zone NOT NULL DEFAULT now(),
            updated_at         timestamp with time zone NOT NULL DEFAULT now(),

            CONSTRAINT ck_calc_job_name_not_empty
                CHECK (NULLIF(btrim(name), '') IS NOT NULL),

            CONSTRAINT ck_calc_job_definition_code_not_empty
                CHECK (NULLIF(btrim(definition_code), '') IS NOT NULL),

            CONSTRAINT ck_calc_job_definition_version_not_empty
                CHECK (NULLIF(btrim(definition_version), '') IS NOT NULL),

            CONSTRAINT ck_calc_job_period_positive
                CHECK (period_ms > 0),

            CONSTRAINT ck_calc_job_revision_positive
                CHECK (revision > 0)
        );

        CREATE TABLE IF NOT EXISTS public.calc_job_input
        (
            id                bigserial PRIMARY KEY,
            job_id            bigint NOT NULL
                REFERENCES public.calc_job(id)
                ON DELETE CASCADE,

            parameter_key     text NOT NULL,
            source_type       text NOT NULL,
            tag_name          text NULL,
            constant_value    jsonb NULL,

            source_job_id     bigint NULL
                REFERENCES public.calc_job(id)
                ON DELETE RESTRICT,

            source_output_key text NULL,
            max_age_seconds   integer NULL,
            sort_order        integer NOT NULL DEFAULT 0,

            CONSTRAINT uq_calc_job_input_parameter
                UNIQUE (job_id, parameter_key),

            CONSTRAINT ck_calc_job_input_parameter_not_empty
                CHECK (NULLIF(btrim(parameter_key), '') IS NOT NULL),

            CONSTRAINT ck_calc_job_input_source_type
                CHECK (source_type IN ('Tag', 'Constant', 'CalculationOutput')),

            CONSTRAINT ck_calc_job_input_max_age
                CHECK (max_age_seconds IS NULL OR max_age_seconds > 0),

            CONSTRAINT ck_calc_job_input_not_self_reference
                CHECK (source_job_id IS NULL OR source_job_id <> job_id),

            CONSTRAINT ck_calc_job_input_source_fields
                CHECK
                (
                    (
                        source_type = 'Tag'
                        AND NULLIF(btrim(tag_name), '') IS NOT NULL
                        AND constant_value IS NULL
                        AND source_job_id IS NULL
                        AND source_output_key IS NULL
                    )
                    OR
                    (
                        source_type = 'Constant'
                        AND tag_name IS NULL
                        AND constant_value IS NOT NULL
                        AND source_job_id IS NULL
                        AND source_output_key IS NULL
                    )
                    OR
                    (
                        source_type = 'CalculationOutput'
                        AND tag_name IS NULL
                        AND constant_value IS NULL
                        AND source_job_id IS NOT NULL
                        AND NULLIF(btrim(source_output_key), '') IS NOT NULL
                    )
                )
        );

        CREATE TABLE IF NOT EXISTS public.calc_job_output
        (
            id            bigserial PRIMARY KEY,
            job_id        bigint NOT NULL
                REFERENCES public.calc_job(id)
                ON DELETE CASCADE,

            output_key    text NOT NULL,
            tag_name      text NULL,
            write_enabled boolean NOT NULL DEFAULT false,
            scale         double precision NOT NULL DEFAULT 1,
            offset_value  double precision NOT NULL DEFAULT 0,
            sort_order    integer NOT NULL DEFAULT 0,

            CONSTRAINT uq_calc_job_output_key
                UNIQUE (job_id, output_key),

            CONSTRAINT ck_calc_job_output_key_not_empty
                CHECK (NULLIF(btrim(output_key), '') IS NOT NULL),

            CONSTRAINT ck_calc_job_output_write_target
                CHECK
                (
                    NOT write_enabled
                    OR NULLIF(btrim(tag_name), '') IS NOT NULL
                )
        );

        CREATE TABLE IF NOT EXISTS public.calc_job_state
        (
            job_id                bigint PRIMARY KEY
                REFERENCES public.calc_job(id)
                ON DELETE CASCADE,

            status                text NOT NULL DEFAULT 'NeverRun',
            reason_code           text NULL,
            reason_message        text NULL,
            definition_version    text NULL,
            configuration_revision bigint NULL,
            cycle_number          bigint NOT NULL DEFAULT 0,
            last_started_at       timestamp with time zone NULL,
            last_completed_at     timestamp with time zone NULL,
            last_success_at       timestamp with time zone NULL,
            last_duration_ms      bigint NULL,
            last_inputs           jsonb NULL,
            last_outputs          jsonb NULL,
            updated_at            timestamp with time zone NOT NULL DEFAULT now(),

            CONSTRAINT ck_calc_job_state_status
                CHECK (status IN ('NeverRun', 'Running', 'Success', 'Skipped', 'Error')),

            CONSTRAINT ck_calc_job_state_cycle
                CHECK (cycle_number >= 0),

            CONSTRAINT ck_calc_job_state_duration
                CHECK (last_duration_ms IS NULL OR last_duration_ms >= 0)
        );

        COMMENT ON TABLE public.calc_job
        IS 'Configured calculation jobs. Mathematical algorithms are stored in TechMES.Calc code.';

        COMMENT ON COLUMN public.calc_job.definition_version
        IS 'Expected mathematical behavior version. A mismatch blocks job execution.';

        COMMENT ON COLUMN public.calc_job.write_enabled
        IS 'Master write permission. SCADA writing remains disabled unless both job and output allow it.';

        COMMENT ON COLUMN public.calc_job_input.constant_value
        IS 'Simple JSON value for constant parameters: number, integer, boolean or string.';

        COMMENT ON COLUMN public.calc_job_input.source_type
        IS 'Tag and Constant are supported first. CalculationOutput is reserved for dependency graph support.';

        CREATE INDEX IF NOT EXISTS ix_calc_job_enabled_period
            ON public.calc_job (enabled, period_ms, sort_order);

        CREATE INDEX IF NOT EXISTS ix_calc_job_equipment
            ON public.calc_job (equipment_name)
            WHERE equipment_name IS NOT NULL;

        CREATE INDEX IF NOT EXISTS ix_calc_job_definition
            ON public.calc_job (definition_code, definition_version);

        CREATE INDEX IF NOT EXISTS ix_calc_job_input_tag
            ON public.calc_job_input (lower(tag_name))
            WHERE tag_name IS NOT NULL;

        CREATE INDEX IF NOT EXISTS ix_calc_job_input_source_job
            ON public.calc_job_input (source_job_id)
            WHERE source_job_id IS NOT NULL;

        CREATE INDEX IF NOT EXISTS ix_calc_job_output_tag
            ON public.calc_job_output (lower(tag_name))
            WHERE tag_name IS NOT NULL;

        CREATE INDEX IF NOT EXISTS ix_calc_job_state_status
            ON public.calc_job_state (status, updated_at DESC);
        """;
}