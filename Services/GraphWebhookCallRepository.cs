using all1box.io.Models;
using Microsoft.Data.SqlClient;

namespace all1box.io.Services;

public sealed class GraphWebhookCallRepository
{
    private static readonly SemaphoreSlim SchemaLock = new(1, 1);
    private static volatile bool _schemaReady;

    private readonly IConfiguration _configuration;
    private readonly ILogger<GraphWebhookCallRepository> _logger;

    public GraphWebhookCallRepository(IConfiguration configuration, ILogger<GraphWebhookCallRepository> logger)
    {
        _configuration = configuration;
        _logger = logger;
    }

    public async Task InsertAsync(GraphWebhookCallRecord record, CancellationToken cancellationToken)
    {
        var connectionString = _configuration.GetConnectionString("BPDConn");
        if (string.IsNullOrWhiteSpace(connectionString) || connectionString.Contains("YOUR_", StringComparison.OrdinalIgnoreCase))
        {
            connectionString = _configuration.GetConnectionString("WebOSConn");
            if (string.IsNullOrWhiteSpace(connectionString) || connectionString.Contains("YOUR_", StringComparison.OrdinalIgnoreCase))
            {
                _logger.LogWarning("Skipping webhook call persistence because neither ConnectionStrings:BPDConn nor ConnectionStrings:WebOSConn is configured.");
                return;
            }
        }

        await EnsureSchemaAsync(connectionString, cancellationToken);

        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken);

        const string sql = """
            INSERT INTO dbo.GraphWebhookCalls
            (
                ReceivedAtUtc,
                ReceivedAtPacific,
                Method,
                Path,
                QueryString,
                RemoteIpAddress,
                UserAgent,
                ValidationToken,
                SubscriptionId,
                ChangeType,
                Resource,
                ResourceDataId,
                ClientStateValid,
                Payload
            )
            VALUES
            (
                @ReceivedAtUtc,
                @ReceivedAtPacific,
                @Method,
                @Path,
                @QueryString,
                @RemoteIpAddress,
                @UserAgent,
                @ValidationToken,
                @SubscriptionId,
                @ChangeType,
                @Resource,
                @ResourceDataId,
                @ClientStateValid,
                @Payload
            );
            """;

        await using var command = new SqlCommand(sql, connection);
        command.Parameters.AddWithValue("@ReceivedAtUtc", record.ReceivedAtUtc.UtcDateTime);
        command.Parameters.AddWithValue("@ReceivedAtPacific", record.ReceivedAtPacific);
        command.Parameters.AddWithValue("@Method", record.Method);
        command.Parameters.AddWithValue("@Path", record.Path);
        command.Parameters.AddWithValue("@QueryString", record.QueryString);
        command.Parameters.AddWithValue("@RemoteIpAddress", (object?)record.RemoteIpAddress ?? DBNull.Value);
        command.Parameters.AddWithValue("@UserAgent", (object?)record.UserAgent ?? DBNull.Value);
        command.Parameters.AddWithValue("@ValidationToken", (object?)record.ValidationToken ?? DBNull.Value);
        command.Parameters.AddWithValue("@SubscriptionId", (object?)record.SubscriptionId ?? DBNull.Value);
        command.Parameters.AddWithValue("@ChangeType", (object?)record.ChangeType ?? DBNull.Value);
        command.Parameters.AddWithValue("@Resource", (object?)record.Resource ?? DBNull.Value);
        command.Parameters.AddWithValue("@ResourceDataId", (object?)record.ResourceDataId ?? DBNull.Value);
        command.Parameters.AddWithValue("@ClientStateValid", (object?)record.ClientStateValid ?? DBNull.Value);
        command.Parameters.AddWithValue("@Payload", (object?)record.Payload ?? DBNull.Value);

        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task EnsureSchemaAsync(string connectionString, CancellationToken cancellationToken)
    {
        if (_schemaReady)
        {
            return;
        }

        await SchemaLock.WaitAsync(cancellationToken);
        try
        {
            if (_schemaReady)
            {
                return;
            }

            await using var connection = new SqlConnection(connectionString);
            await connection.OpenAsync(cancellationToken);

            const string sql = """
                IF OBJECT_ID(N'dbo.GraphWebhookCalls', N'U') IS NULL
                BEGIN
                    CREATE TABLE dbo.GraphWebhookCalls
                    (
                        Id BIGINT IDENTITY(1,1) NOT NULL CONSTRAINT PK_GraphWebhookCalls PRIMARY KEY,
                        ReceivedAtUtc DATETIME2(7) NOT NULL CONSTRAINT DF_GraphWebhookCalls_ReceivedAtUtc DEFAULT SYSUTCDATETIME(),
                        ReceivedAtPacific DATETIME2(7) NOT NULL CONSTRAINT DF_GraphWebhookCalls_ReceivedAtPacific DEFAULT CONVERT(DATETIME2(7), SYSUTCDATETIME() AT TIME ZONE 'UTC' AT TIME ZONE 'Pacific Standard Time'),
                        Method NVARCHAR(16) NOT NULL,
                        Path NVARCHAR(512) NOT NULL,
                        QueryString NVARCHAR(2048) NOT NULL,
                        RemoteIpAddress NVARCHAR(64) NULL,
                        UserAgent NVARCHAR(512) NULL,
                        ValidationToken NVARCHAR(MAX) NULL,
                        SubscriptionId NVARCHAR(128) NULL,
                        ChangeType NVARCHAR(64) NULL,
                        Resource NVARCHAR(1024) NULL,
                        ResourceDataId NVARCHAR(512) NULL,
                        ClientStateValid BIT NULL,
                        Payload NVARCHAR(MAX) NULL
                    );

                    CREATE INDEX IX_GraphWebhookCalls_ReceivedAtUtc
                        ON dbo.GraphWebhookCalls(ReceivedAtUtc DESC);

                    CREATE INDEX IX_GraphWebhookCalls_SubscriptionId
                        ON dbo.GraphWebhookCalls(SubscriptionId, ReceivedAtUtc DESC);
                END;

                IF COL_LENGTH(N'dbo.GraphWebhookCalls', N'ReceivedAtPacific') IS NULL
                BEGIN
                    ALTER TABLE dbo.GraphWebhookCalls
                        ADD ReceivedAtPacific DATETIME2(7) NULL;
                END;

                EXEC sp_executesql N'
                    UPDATE dbo.GraphWebhookCalls
                        SET ReceivedAtPacific = CONVERT(DATETIME2(7), ReceivedAtUtc AT TIME ZONE ''UTC'' AT TIME ZONE ''Pacific Standard Time'')
                        WHERE ReceivedAtPacific IS NULL;
                ';

                IF EXISTS (
                    SELECT 1
                    FROM sys.columns
                    WHERE object_id = OBJECT_ID(N'dbo.GraphWebhookCalls')
                        AND name = N'ReceivedAtPacific'
                        AND is_nullable = 1
                )
                BEGIN
                    ALTER TABLE dbo.GraphWebhookCalls
                        ALTER COLUMN ReceivedAtPacific DATETIME2(7) NOT NULL;
                END;

                IF OBJECT_ID(N'DF_GraphWebhookCalls_ReceivedAtPacific', N'D') IS NULL
                BEGIN
                    ALTER TABLE dbo.GraphWebhookCalls
                        ADD
                            CONSTRAINT DF_GraphWebhookCalls_ReceivedAtPacific
                            DEFAULT CONVERT(DATETIME2(7), SYSUTCDATETIME() AT TIME ZONE 'UTC' AT TIME ZONE 'Pacific Standard Time')
                            FOR ReceivedAtPacific;
                END;
                """;

            await using var command = new SqlCommand(sql, connection);
            await command.ExecuteNonQueryAsync(cancellationToken);
            _schemaReady = true;
        }
        finally
        {
            SchemaLock.Release();
        }
    }
}
