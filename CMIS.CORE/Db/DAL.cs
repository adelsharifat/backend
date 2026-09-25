using System.Data;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Configuration;

namespace CMIS.Core.Db;

/// <summary>
/// Base ADO.NET data-access layer for CMIS modules.
/// Warehouse and other modules can inherit from this type to execute SQL Server
/// stored procedures or SQL commands through the shared CMIS connection.
/// </summary>
public class DAL
{
    protected string ConnectionString { get; }

    public DAL(IConfiguration configuration)
    {
        ConnectionString = configuration.GetConnectionString("Default")
            ?? throw new InvalidOperationException("The 'Default' connection string is not configured.");
    }

    protected virtual SqlConnection CreateConnection() => new(ConnectionString);

    protected virtual SqlCommand CreateCommand(
        SqlConnection connection,
        string commandText,
        CommandType commandType,
        SqlParameter[] parameters,
        SqlTransaction? transaction = null)
    {
        var command = new SqlCommand(commandText, connection)
        {
            CommandType = commandType,
            Transaction = transaction
        };

        if (parameters.Length > 0)
            command.Parameters.AddRange(parameters);

        return command;
    }

    /// <summary>
    /// Runs database work in one transaction. The transaction commits only when
    /// <paramref name="work"/> completes successfully; otherwise it is rolled back.
    /// </summary>
    public virtual void ExecuteInTransaction(Action<SqlConnection, SqlTransaction> work)
    {
        ExecuteInTransaction<object?>((connection, transaction) =>
        {
            work(connection, transaction);
            return null;
        });
    }

    /// <summary>
    /// Runs database work in one transaction and returns its result.
    /// </summary>
    public virtual T ExecuteInTransaction<T>(Func<SqlConnection, SqlTransaction, T> work)
    {
        using var connection = CreateConnection();
        connection.Open();
        using var transaction = connection.BeginTransaction();

        try
        {
            var result = work(connection, transaction);
            transaction.Commit();
            return result;
        }
        catch
        {
            transaction.Rollback();
            throw;
        }
    }

    /// <summary>
    /// Asynchronously runs database work in one transaction. The transaction commits
    /// only when <paramref name="work"/> completes successfully; otherwise it is rolled back.
    /// </summary>
    public virtual Task ExecuteInTransactionAsync(
        Func<SqlConnection, SqlTransaction, CancellationToken, Task> work,
        CancellationToken cancellationToken = default) =>
        ExecuteInTransactionAsync<object?>(async (connection, transaction, token) =>
        {
            await work(connection, transaction, token);
            return null;
        }, cancellationToken);

    /// <summary>
    /// Asynchronously runs database work in one transaction and returns its result.
    /// </summary>
    public virtual async Task<T> ExecuteInTransactionAsync<T>(
        Func<SqlConnection, SqlTransaction, CancellationToken, Task<T>> work,
        CancellationToken cancellationToken = default)
    {
        await using var connection = CreateConnection();
        await connection.OpenAsync(cancellationToken);
        await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync(cancellationToken);

        try
        {
            var result = await work(connection, transaction, cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return result;
        }
        catch
        {
            await transaction.RollbackAsync(CancellationToken.None);
            throw;
        }
    }

    /// <summary>
    /// Executes a command using an existing transaction. This lets derived module DALs
    /// combine multiple helper calls in an <see cref="ExecuteInTransaction"/> scope.
    /// </summary>
    public virtual int Execute(
        SqlTransaction transaction,
        string commandText,
        CommandType commandType,
        params SqlParameter[] parameters)
    {
        var connection = transaction.Connection
            ?? throw new InvalidOperationException("The transaction has no active connection.");
        using var command = CreateCommand(connection, commandText, commandType, parameters, transaction);
        return command.ExecuteNonQuery();
    }

    public virtual int Execute(
        SqlTransaction transaction,
        string commandText,
        params SqlParameter[] parameters) =>
        Execute(transaction, commandText, CommandType.StoredProcedure, parameters);

    public virtual async Task<int> ExecuteAsync(
        SqlTransaction transaction,
        string commandText,
        CommandType commandType,
        CancellationToken cancellationToken,
        params SqlParameter[] parameters)
    {
        var connection = transaction.Connection
            ?? throw new InvalidOperationException("The transaction has no active connection.");
        await using var command = CreateCommand(connection, commandText, commandType, parameters, transaction);
        return await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public virtual Task<int> ExecuteAsync(
        SqlTransaction transaction,
        string commandText,
        params SqlParameter[] parameters) =>
        ExecuteAsync(transaction, commandText, CommandType.StoredProcedure, CancellationToken.None, parameters);

    public virtual int Execute(
        string commandText,
        CommandType commandType,
        params SqlParameter[] parameters)
    {
        using var connection = CreateConnection();
        connection.Open();
        using var command = CreateCommand(connection, commandText, commandType, parameters);
        return command.ExecuteNonQuery();
    }

    public virtual async Task<int> ExecuteAsync(
        string commandText,
        CommandType commandType,
        CancellationToken cancellationToken,
        params SqlParameter[] parameters)
    {
        await using var connection = CreateConnection();
        await connection.OpenAsync(cancellationToken);
        await using var command = CreateCommand(connection, commandText, commandType, parameters);
        return await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public virtual int Execute(string commandText, params SqlParameter[] parameters) =>
        Execute(commandText, CommandType.StoredProcedure, parameters);

    public virtual Task<int> ExecuteAsync(string commandText, params SqlParameter[] parameters) =>
        ExecuteAsync(commandText, CommandType.StoredProcedure, CancellationToken.None, parameters);

    public virtual T? ExecuteScalar<T>(
        string commandText,
        CommandType commandType,
        params SqlParameter[] parameters)
    {
        using var connection = CreateConnection();
        connection.Open();
        using var command = CreateCommand(connection, commandText, commandType, parameters);
        var result = command.ExecuteScalar();
        return result is null or DBNull ? default : (T)Convert.ChangeType(result, typeof(T));
    }

    public virtual async Task<T?> ExecuteScalarAsync<T>(
        string commandText,
        CommandType commandType,
        CancellationToken cancellationToken,
        params SqlParameter[] parameters)
    {
        await using var connection = CreateConnection();
        await connection.OpenAsync(cancellationToken);
        await using var command = CreateCommand(connection, commandText, commandType, parameters);
        var result = await command.ExecuteScalarAsync(cancellationToken);
        return result is null or DBNull ? default : (T)Convert.ChangeType(result, typeof(T));
    }

    public virtual T? ExecuteScalar<T>(string commandText, params SqlParameter[] parameters) =>
        ExecuteScalar<T>(commandText, CommandType.StoredProcedure, parameters);

    public virtual Task<T?> ExecuteScalarAsync<T>(string commandText, params SqlParameter[] parameters) =>
        ExecuteScalarAsync<T>(commandText, CommandType.StoredProcedure, CancellationToken.None, parameters);

    public virtual T QuerySingle<T>(
        string commandText,
        Func<IDataRecord, T> mapper,
        CommandType commandType,
        params SqlParameter[] parameters)
    {
        using var connection = CreateConnection();
        connection.Open();
        using var command = CreateCommand(connection, commandText, commandType, parameters);
        using var reader = command.ExecuteReader();

        return reader.Read()
            ? mapper(reader)
            : throw new InvalidOperationException("No rows returned.");
    }

    public virtual async Task<T> QuerySingleAsync<T>(
        string commandText,
        Func<IDataRecord, T> mapper,
        CommandType commandType,
        CancellationToken cancellationToken,
        params SqlParameter[] parameters)
    {
        await using var connection = CreateConnection();
        await connection.OpenAsync(cancellationToken);
        await using var command = CreateCommand(connection, commandText, commandType, parameters);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);

        return await reader.ReadAsync(cancellationToken)
            ? mapper(reader)
            : throw new InvalidOperationException("No rows returned.");
    }

    public virtual T QuerySingle<T>(string commandText, Func<IDataRecord, T> mapper, params SqlParameter[] parameters) =>
        QuerySingle(commandText, mapper, CommandType.StoredProcedure, parameters);

    public virtual Task<T> QuerySingleAsync<T>(string commandText, Func<IDataRecord, T> mapper, params SqlParameter[] parameters) =>
        QuerySingleAsync(commandText, mapper, CommandType.StoredProcedure, CancellationToken.None, parameters);

    public virtual List<T> Query<T>(
        string commandText,
        Func<IDataRecord, T> mapper,
        CommandType commandType,
        params SqlParameter[] parameters)
    {
        var results = new List<T>();
        using var connection = CreateConnection();
        connection.Open();
        using var command = CreateCommand(connection, commandText, commandType, parameters);
        using var reader = command.ExecuteReader();

        while (reader.Read())
            results.Add(mapper(reader));

        return results;
    }

    public virtual async Task<List<T>> QueryAsync<T>(
        string commandText,
        Func<IDataRecord, T> mapper,
        CommandType commandType,
        CancellationToken cancellationToken,
        params SqlParameter[] parameters)
    {
        var results = new List<T>();
        await using var connection = CreateConnection();
        await connection.OpenAsync(cancellationToken);
        await using var command = CreateCommand(connection, commandText, commandType, parameters);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);

        while (await reader.ReadAsync(cancellationToken))
            results.Add(mapper(reader));

        return results;
    }

    public virtual List<T> Query<T>(string commandText, Func<IDataRecord, T> mapper, params SqlParameter[] parameters) =>
        Query(commandText, mapper, CommandType.StoredProcedure, parameters);

    public virtual Task<List<T>> QueryAsync<T>(string commandText, Func<IDataRecord, T> mapper, params SqlParameter[] parameters) =>
        QueryAsync(commandText, mapper, CommandType.StoredProcedure, CancellationToken.None, parameters);

    public virtual DataTable QueryTable(
        string commandText,
        CommandType commandType,
        params SqlParameter[] parameters)
    {
        var table = new DataTable();
        using var connection = CreateConnection();
        connection.Open();
        using var command = CreateCommand(connection, commandText, commandType, parameters);
        using var reader = command.ExecuteReader();
        table.Load(reader);
        return table;
    }

    public virtual async Task<DataTable> QueryTableAsync(
        string commandText,
        CommandType commandType,
        CancellationToken cancellationToken,
        params SqlParameter[] parameters)
    {
        var table = new DataTable();
        await using var connection = CreateConnection();
        await connection.OpenAsync(cancellationToken);
        await using var command = CreateCommand(connection, commandText, commandType, parameters);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        table.Load(reader);
        return table;
    }

    public virtual DataTable QueryTable(string commandText, params SqlParameter[] parameters) =>
        QueryTable(commandText, CommandType.StoredProcedure, parameters);

    public virtual Task<DataTable> QueryTableAsync(string commandText, params SqlParameter[] parameters) =>
        QueryTableAsync(commandText, CommandType.StoredProcedure, CancellationToken.None, parameters);
}
