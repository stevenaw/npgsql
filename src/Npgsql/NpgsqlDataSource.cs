using System;
using System.Collections.Generic;
using System.Data.Common;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Linq;
using System.Net.Security;
using System.Security.Cryptography.X509Certificates;
using System.Threading;
using System.Threading.Tasks;
using System.Transactions;
using Microsoft.Extensions.Logging;
using Npgsql.Internal;
using Npgsql.Internal.Resolvers;
using Npgsql.Properties;
using Npgsql.Util;

namespace Npgsql;

/// <inheritdoc />
public abstract class NpgsqlDataSource : DbDataSource
{
    /// <inheritdoc />
    public override string ConnectionString { get; }

    /// <summary>
    /// Contains the connection string returned to the user from <see cref="NpgsqlConnection.ConnectionString"/>
    /// after the connection has been opened. Does not contain the password unless Persist Security Info=true.
    /// </summary>
    internal NpgsqlConnectionStringBuilder Settings { get; }

    internal NpgsqlDataSourceConfiguration Configuration { get; }
    internal NpgsqlLoggingConfiguration LoggingConfiguration { get; }

    readonly IPgTypeInfoResolver _resolver;
    internal PgSerializerOptions SerializerOptions { get; private set; } = null!; // Initialized at bootstrapping

    /// <summary>
    /// Information about PostgreSQL and PostgreSQL-like databases (e.g. type definitions, capabilities...).
    /// </summary>
    internal NpgsqlDatabaseInfo DatabaseInfo { get; private set; } = null!; // Initialized at bootstrapping

    internal TransportSecurityHandler TransportSecurityHandler { get; }
    internal RemoteCertificateValidationCallback? UserCertificateValidationCallback { get; }
    internal Action<X509CertificateCollection>? ClientCertificatesCallback { get; }

    readonly Func<NpgsqlConnectionStringBuilder, CancellationToken, ValueTask<string>>? _periodicPasswordProvider;
    readonly TimeSpan _periodicPasswordSuccessRefreshInterval, _periodicPasswordFailureRefreshInterval;

    internal IntegratedSecurityHandler IntegratedSecurityHandler { get; }

    internal Action<NpgsqlConnection>? ConnectionInitializer { get; }
    internal Func<NpgsqlConnection, Task>? ConnectionInitializerAsync { get; }

    readonly Timer? _passwordProviderTimer;
    readonly CancellationTokenSource? _timerPasswordProviderCancellationTokenSource;
    readonly Task _passwordRefreshTask = null!;
    string? _password;

    bool _isBootstrapped;

    volatile DatabaseStateInfo _databaseStateInfo = new();

    // Note that while the dictionary is protected by locking, we assume that the lists it contains don't need to be
    // (i.e. access to connectors of a specific transaction won't be concurrent)
    private protected readonly Dictionary<Transaction, List<NpgsqlConnector>> _pendingEnlistedConnectors
        = new();

    internal MetricsReporter MetricsReporter { get; }
    internal string Name { get; }

    internal abstract (int Total, int Idle, int Busy) Statistics { get; }

    volatile int _isDisposed;

    readonly ILogger _connectionLogger;

    /// <summary>
    /// Semaphore to ensure we don't perform type loading and mapping setup concurrently for this data source.
    /// </summary>
    readonly SemaphoreSlim _setupMappingsSemaphore = new(1);

    readonly INpgsqlNameTranslator _defaultNameTranslator;

    internal List<HackyEnumTypeMapping>? _hackyEnumTypeMappings;

    internal NpgsqlDataSource(
        NpgsqlConnectionStringBuilder settings,
        NpgsqlDataSourceConfiguration dataSourceConfig)
    {
        Settings = settings;
        ConnectionString = settings.PersistSecurityInfo
            ? settings.ToString()
            : settings.ToStringWithoutPassword();

        Configuration = dataSourceConfig;

        (var name,
                LoggingConfiguration,
                TransportSecurityHandler,
                IntegratedSecurityHandler,
                UserCertificateValidationCallback,
                ClientCertificatesCallback,
                _periodicPasswordProvider,
                _periodicPasswordSuccessRefreshInterval,
                _periodicPasswordFailureRefreshInterval,
                var resolverChain,
                _hackyEnumTypeMappings,
                _defaultNameTranslator,
                ConnectionInitializer,
                ConnectionInitializerAsync)
            = dataSourceConfig;
        _connectionLogger = LoggingConfiguration.ConnectionLogger;

        // TODO probably want this on the options so it can devirt unconditionally.
        _resolver = new TypeInfoResolverChain(resolverChain);
        _password = settings.Password;

        if (_periodicPasswordSuccessRefreshInterval != default)
        {
            Debug.Assert(_periodicPasswordProvider is not null);

            _timerPasswordProviderCancellationTokenSource = new();

            // Create the timer, but don't start it; the manual run below will will schedule the first refresh.
            _passwordProviderTimer = new Timer(state => _ = RefreshPassword(), null, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
            // Trigger the first refresh attempt right now, outside the timer; this allows us to capture the Task so it can be observed
            // in GetPasswordAsync.
            _passwordRefreshTask = Task.Run(RefreshPassword);
        }

        Name = name ?? ConnectionString;
        MetricsReporter = new MetricsReporter(this);
    }

    /// <inheritdoc cref="DbDataSource.CreateConnection" />
    public new NpgsqlConnection CreateConnection()
        => NpgsqlConnection.FromDataSource(this);

    /// <inheritdoc cref="DbDataSource.OpenConnection" />
    public new NpgsqlConnection OpenConnection()
    {
        var connection = CreateConnection();

        try
        {
            connection.Open();
            return connection;
        }
        catch
        {
            connection.Dispose();
            throw;
        }
    }

    /// <inheritdoc />
    protected override DbConnection OpenDbConnection()
        => OpenConnection();

    /// <inheritdoc cref="DbDataSource.OpenConnectionAsync" />
    public new async ValueTask<NpgsqlConnection> OpenConnectionAsync(CancellationToken cancellationToken = default)
    {
        var connection = CreateConnection();

        try
        {
            await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
            return connection;
        }
        catch
        {
            await connection.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    /// <inheritdoc />
    protected override async ValueTask<DbConnection> OpenDbConnectionAsync(CancellationToken cancellationToken = default)
        => await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);

    /// <inheritdoc />
    protected override DbConnection CreateDbConnection()
        => CreateConnection();

    /// <inheritdoc />
    protected override DbCommand CreateDbCommand(string? commandText = null)
        => CreateCommand(commandText);

    /// <inheritdoc />
    protected override DbBatch CreateDbBatch()
        => CreateBatch();

    /// <summary>
    /// Creates a command ready for use against this <see cref="NpgsqlDataSource" />.
    /// </summary>
    /// <param name="commandText">An optional SQL for the command.</param>
    public new NpgsqlCommand CreateCommand(string? commandText = null)
        => new NpgsqlDataSourceCommand(CreateConnection()) { CommandText = commandText };

    /// <summary>
    /// Creates a batch ready for use against this <see cref="NpgsqlDataSource" />.
    /// </summary>
    public new NpgsqlBatch CreateBatch()
        => new NpgsqlDataSourceBatch(CreateConnection());

    /// <summary>
    /// Creates a new <see cref="NpgsqlDataSource" /> for the given <paramref name="connectionString" />.
    /// </summary>
    [RequiresUnreferencedCode("NpgsqlDataSource uses reflection to handle various PostgreSQL types like records, unmapped enums etc. Use NpgsqlSlimDataSourceBuilder to start with a reduced - reflection free - set and opt into what your app specifically requires.")]
    [RequiresDynamicCode("NpgsqlDataSource uses reflection to handle various PostgreSQL types like records, unmapped enums. This can require creating new generic types or methods, which requires creating code at runtime. This may not work when AOT compiling.")]
    public static NpgsqlDataSource Create(string connectionString)
        => new NpgsqlDataSourceBuilder(connectionString).Build();

    /// <summary>
    /// Creates a new <see cref="NpgsqlDataSource" /> for the given <paramref name="connectionStringBuilder" />.
    /// </summary>
    [RequiresUnreferencedCode("NpgsqlDataSource uses reflection to handle various PostgreSQL types like records, unmapped enums etc. Use NpgsqlSlimDataSourceBuilder to start with a reduced - reflection free - set and opt into what your app specifically requires.")]
    [RequiresDynamicCode("NpgsqlDataSource uses reflection to handle various PostgreSQL types like records, unmapped enums. This can require creating new generic types or methods, which requires creating code at runtime. This may not work when AOT compiling.")]
    public static NpgsqlDataSource Create(NpgsqlConnectionStringBuilder connectionStringBuilder)
        => Create(connectionStringBuilder.ToString());

    internal async Task Bootstrap(
        NpgsqlConnector connector,
        NpgsqlTimeout timeout,
        bool forceReload,
        bool async,
        CancellationToken cancellationToken)
    {
        if (_isBootstrapped && !forceReload)
            return;

        var hasSemaphore = async
            ? await _setupMappingsSemaphore.WaitAsync(timeout.CheckAndGetTimeLeft(), cancellationToken).ConfigureAwait(false)
            : _setupMappingsSemaphore.Wait(timeout.CheckAndGetTimeLeft(), cancellationToken);

        if (!hasSemaphore)
            throw new TimeoutException();

        try
        {
            if (_isBootstrapped && !forceReload)
                return;

            // The type loading below will need to send queries to the database, and that depends on a type mapper being set up (even if its
            // empty). So we set up a minimal version here, and then later inject the actual DatabaseInfo.
            connector.SerializerOptions =
                new(PostgresMinimalDatabaseInfo.DefaultTypeCatalog)
                {
                    TextEncoding = connector.TextEncoding,
                    TypeInfoResolver = AdoTypeInfoResolver.Instance
                };

            NpgsqlDatabaseInfo databaseInfo;

            using (connector.StartUserAction(ConnectorState.Executing, cancellationToken))
                databaseInfo = await NpgsqlDatabaseInfo.Load(connector, timeout, async).ConfigureAwait(false);

            connector.DatabaseInfo = DatabaseInfo = databaseInfo;
            connector.SerializerOptions = SerializerOptions =
                new(databaseInfo, CreateTimeZoneProvider(connector.Timezone))
                {
                    ArrayNullabilityMode = Settings.ArrayNullabilityMode,
                    EnableDateTimeInfinityConversions = !Statics.DisableDateTimeInfinityConversions,
                    TextEncoding = connector.TextEncoding,
                    TypeInfoResolver = _resolver,
                    DefaultNameTranslator = _defaultNameTranslator
                };

            _isBootstrapped = true;
        }
        finally
        {
            _setupMappingsSemaphore.Release();
        }

        // Func in a static function to make sure we don't capture state that might not stay around, like a connector.
        static Func<string> CreateTimeZoneProvider(string postgresTimeZone)
            => () =>
            {
                if (string.Equals(postgresTimeZone, "localtime", StringComparison.OrdinalIgnoreCase))
                    throw new TimeZoneNotFoundException(
                        "The special PostgreSQL timezone 'localtime' is not supported when reading values of type 'timestamp with time zone'. " +
                        "Please specify a real timezone in 'postgresql.conf' on the server, or set the 'PGTZ' environment variable on the client.");

                return postgresTimeZone;
            };
    }

    #region Password management

    /// <summary>
    /// Manually sets the password to be used the next time a physical connection is opened.
    /// Consider using <see cref="NpgsqlDataSourceBuilder.UsePeriodicPasswordProvider" /> instead.
    /// </summary>
    public string Password
    {
        set
        {
            if (_periodicPasswordProvider is not null)
                throw new NotSupportedException(NpgsqlStrings.CannotSetBothPasswordProviderAndPassword);

            _password = value;
        }
    }

    internal async ValueTask<string?> GetPassword(bool async, CancellationToken cancellationToken = default)
    {
        // A periodic password provider is configured, but the first refresh hasn't completed yet (race condition).
        // Wait until it completes.
        if (_password is null && _periodicPasswordProvider is not null)
        {
            if (async)
                await _passwordRefreshTask.ConfigureAwait(false);
            else
                _passwordRefreshTask.GetAwaiter().GetResult();

            Debug.Assert(_password is not null);
        }

        return _password;
    }

    async Task RefreshPassword()
    {
        try
        {
            _password = await _periodicPasswordProvider!(Settings, _timerPasswordProviderCancellationTokenSource!.Token).ConfigureAwait(false);

            _passwordProviderTimer!.Change(_periodicPasswordSuccessRefreshInterval, Timeout.InfiniteTimeSpan);
        }
        catch (Exception e)
        {
            _connectionLogger.LogError(e, "Periodic password provider threw an exception");

            _passwordProviderTimer!.Change(_periodicPasswordFailureRefreshInterval, Timeout.InfiniteTimeSpan);

            throw new NpgsqlException("An exception was thrown from the periodic password provider", e);
        }
    }

    #endregion Password management

    internal abstract ValueTask<NpgsqlConnector> Get(
        NpgsqlConnection conn, NpgsqlTimeout timeout, bool async, CancellationToken cancellationToken);

    internal abstract bool TryGetIdleConnector([NotNullWhen(true)] out NpgsqlConnector? connector);

    internal abstract ValueTask<NpgsqlConnector?> OpenNewConnector(
        NpgsqlConnection conn, NpgsqlTimeout timeout, bool async, CancellationToken cancellationToken);

    internal abstract void Return(NpgsqlConnector connector);

    internal abstract void Clear();

    internal abstract bool OwnsConnectors { get; }

    #region Database state management

    internal DatabaseState GetDatabaseState(bool ignoreExpiration = false)
    {
        Debug.Assert(this is not NpgsqlMultiHostDataSource);

        var databaseStateInfo = _databaseStateInfo;

        return ignoreExpiration || !databaseStateInfo.Timeout.HasExpired
            ? databaseStateInfo.State
            : DatabaseState.Unknown;
    }

    internal DatabaseState UpdateDatabaseState(
        DatabaseState newState,
        DateTime timeStamp,
        TimeSpan stateExpiration,
        bool ignoreTimeStamp = false)
    {
        Debug.Assert(this is not NpgsqlMultiHostDataSource);

        var databaseStateInfo = _databaseStateInfo;

        if (!ignoreTimeStamp && timeStamp <= databaseStateInfo.TimeStamp)
            return _databaseStateInfo.State;

        _databaseStateInfo = new(newState, new NpgsqlTimeout(stateExpiration), timeStamp);

        return newState;
    }

    #endregion Database state management

    #region Pending Enlisted Connections

    internal virtual void AddPendingEnlistedConnector(NpgsqlConnector connector, Transaction transaction)
    {
        lock (_pendingEnlistedConnectors)
        {
            if (!_pendingEnlistedConnectors.TryGetValue(transaction, out var list))
                list = _pendingEnlistedConnectors[transaction] = new List<NpgsqlConnector>(1);
            list.Add(connector);
        }
    }

    internal virtual bool TryRemovePendingEnlistedConnector(NpgsqlConnector connector, Transaction transaction)
    {
        lock (_pendingEnlistedConnectors)
        {
            if (!_pendingEnlistedConnectors.TryGetValue(transaction, out var list))
                return false;
            list.Remove(connector);
            if (list.Count == 0)
                _pendingEnlistedConnectors.Remove(transaction);
            return true;
        }
    }

    internal virtual bool TryRentEnlistedPending(Transaction transaction, NpgsqlConnection connection,
        [NotNullWhen(true)] out NpgsqlConnector? connector)
    {
        lock (_pendingEnlistedConnectors)
        {
            if (!_pendingEnlistedConnectors.TryGetValue(transaction, out var list))
            {
                connector = null;
                return false;
            }
            connector = list[list.Count - 1];
            list.RemoveAt(list.Count - 1);
            if (list.Count == 0)
                _pendingEnlistedConnectors.Remove(transaction);
            return true;
        }
    }

    #endregion

    #region Dispose

    /// <inheritdoc />
    protected sealed override void Dispose(bool disposing)
    {
        if (disposing && Interlocked.CompareExchange(ref _isDisposed, 1, 0) == 0)
            DisposeBase();
    }

    /// <inheritdoc cref="Dispose" />
    protected virtual void DisposeBase()
    {
        var cancellationTokenSource = _timerPasswordProviderCancellationTokenSource;
        if (cancellationTokenSource is not null)
        {
            cancellationTokenSource.Cancel();
            cancellationTokenSource.Dispose();
        }

        _passwordProviderTimer?.Dispose();
        _setupMappingsSemaphore.Dispose();
        MetricsReporter.Dispose(); // TODO: This is probably too early, dispose only when all connections have been closed?

        Clear();
    }

    /// <inheritdoc />
    protected sealed override ValueTask DisposeAsyncCore()
    {
        if (Interlocked.CompareExchange(ref _isDisposed, 1, 0) == 0)
            return DisposeAsyncBase();

        return default;
    }

#pragma warning disable CS1998
    /// <inheritdoc cref="DisposeAsyncCore" />
    protected virtual async ValueTask DisposeAsyncBase()
    {
        var cancellationTokenSource = _timerPasswordProviderCancellationTokenSource;
        if (cancellationTokenSource is not null)
        {
            cancellationTokenSource.Cancel();
            cancellationTokenSource.Dispose();
        }

        if (_passwordProviderTimer is not null)
        {
#if NET5_0_OR_GREATER
            await _passwordProviderTimer.DisposeAsync().ConfigureAwait(false);
#else
            _passwordProviderTimer.Dispose();
#endif
        }

        _setupMappingsSemaphore.Dispose();

        // TODO: async Clear, #4499
        Clear();
    }
#pragma warning restore CS1998

    private protected void CheckDisposed()
    {
        if (_isDisposed == 1)
            ThrowHelper.ThrowObjectDisposedException(GetType().FullName);
    }

    #endregion

    sealed class DatabaseStateInfo
    {
        internal readonly DatabaseState State;
        internal readonly NpgsqlTimeout Timeout;
        // While the TimeStamp is not strictly required, it does lower the risk of overwriting the current state with an old value
        internal readonly DateTime TimeStamp;

        public DatabaseStateInfo() : this(default, default, default) { }

        public DatabaseStateInfo(DatabaseState state, NpgsqlTimeout timeout, DateTime timeStamp)
            => (State, Timeout, TimeStamp) = (state, timeout, timeStamp);
    }
}
