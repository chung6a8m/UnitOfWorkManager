using System.Data;
using System.Data.Common;
using System.Globalization;
using RepoDb;
using RepoDb.Interfaces;

namespace UnitOfWork.Sample.WebApi.MinimalApi.Infrastructure;

/// <summary>
/// Adapts RepoDb's SQLite schema discovery to a transaction-bound connection.
/// RepoDb's default SQLite helper opens PRAGMA table_info and then executes a
/// second command before disposing that reader. The Unit of Work connection
/// intentionally rejects that overlapping command, so this adapter performs the
/// identity lookup first and opens the schema reader afterwards.
/// </summary>
public sealed class TransactionBoundSqliteDbHelper : IDbHelper
{
    private const string ProviderName = "MSSQLITE";

    private readonly IDbHelper _inner;

    public TransactionBoundSqliteDbHelper(IDbHelper inner)
    {
        _inner = inner ?? throw new ArgumentNullException(nameof(inner));
    }

    public IResolver<string, Type> DbTypeResolver => _inner.DbTypeResolver;

    public IEnumerable<DbField> GetFields(
        IDbConnection connection,
        string tableName,
        IDbTransaction? transaction = null)
    {
        var normalizedTableName = NormalizeTableName(tableName);
        var identityFieldName = GetIdentityFieldName(connection, normalizedTableName);

        using var command = CreateCommand(
            connection,
            $"PRAGMA table_info(\"{EscapeIdentifier(normalizedTableName)}\");");
        using var reader = command.ExecuteReader();

        var fields = new List<DbField>();
        while (reader.Read())
            fields.Add(ReadField(reader, identityFieldName));

        return fields;
    }

    public async Task<IEnumerable<DbField>> GetFieldsAsync(
        IDbConnection connection,
        string tableName,
        IDbTransaction? transaction = null,
        CancellationToken cancellationToken = default)
    {
        var normalizedTableName = NormalizeTableName(tableName);
        var identityFieldName = await GetIdentityFieldNameAsync(
            connection,
            normalizedTableName,
            cancellationToken).ConfigureAwait(false);

        await using var command = CreateCommand(
            connection,
            $"PRAGMA table_info(\"{EscapeIdentifier(normalizedTableName)}\");");
        await using var reader = await command.ExecuteReaderAsync(cancellationToken)
            .ConfigureAwait(false);

        var fields = new List<DbField>();
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            fields.Add(ReadField(reader, identityFieldName));

        return fields;
    }

    public T GetScopeIdentity<T>(
        IDbConnection connection,
        IDbTransaction? transaction = null) =>
        _inner.GetScopeIdentity<T>(connection, transaction!);

    public Task<T> GetScopeIdentityAsync<T>(
        IDbConnection connection,
        IDbTransaction? transaction = null,
        CancellationToken cancellationToken = default) =>
        _inner.GetScopeIdentityAsync<T>(
            connection,
            transaction!,
            cancellationToken);

    public void DynamicHandler<TEventInstance>(TEventInstance instance, string key) =>
        _inner.DynamicHandler(instance, key);

    private static string? GetIdentityFieldName(
        IDbConnection connection,
        string tableName)
    {
        using var command = CreateIdentityCommand(connection, tableName);
        var createTableSql = Convert.ToString(
            command.ExecuteScalar(),
            CultureInfo.InvariantCulture);
        return ParseIdentityFieldName(createTableSql);
    }

    private static async Task<string?> GetIdentityFieldNameAsync(
        IDbConnection connection,
        string tableName,
        CancellationToken cancellationToken)
    {
        await using var command = CreateIdentityCommand(connection, tableName);
        var value = await command.ExecuteScalarAsync(cancellationToken)
            .ConfigureAwait(false);
        var createTableSql = Convert.ToString(value, CultureInfo.InvariantCulture);
        return ParseIdentityFieldName(createTableSql);
    }

    private static DbCommand CreateIdentityCommand(
        IDbConnection connection,
        string tableName)
    {
        var command = CreateCommand(
            connection,
            "SELECT sql FROM sqlite_master WHERE name = @TableName AND type = 'table';");
        var parameter = command.CreateParameter();
        parameter.ParameterName = "@TableName";
        parameter.Value = tableName;
        command.Parameters.Add(parameter);
        return command;
    }

    private static DbCommand CreateCommand(
        IDbConnection connection,
        string commandText)
    {
        if (connection.CreateCommand() is not DbCommand command)
        {
            throw new NotSupportedException(
                "The RepoDb SQLite adapter requires a DbCommand-based connection.");
        }

        command.CommandText = commandText;
        return command;
    }

    private DbField ReadField(DbDataReader reader, string? identityFieldName)
    {
        var name = reader.GetString(1);
        var databaseType = reader.IsDBNull(2) ? "text" : reader.GetString(2);
        if (string.IsNullOrWhiteSpace(databaseType))
            databaseType = "text";

        var isPrimary = !reader.IsDBNull(5) && ToBoolean(reader.GetValue(5));
        var isNullable = reader.IsDBNull(3) || !ToBoolean(reader.GetValue(3));

        return new DbField(
            name,
            isPrimary,
            string.Equals(name, identityFieldName, StringComparison.OrdinalIgnoreCase),
            isNullable,
            DbTypeResolver.Resolve(databaseType),
            size: null,
            precision: null,
            scale: null,
            databaseType: null!,
            hasDefaultValue: !reader.IsDBNull(4),
            provider: ProviderName);
    }

    private static bool ToBoolean(object value) =>
        Convert.ToInt64(value, CultureInfo.InvariantCulture) != 0;

    private static string NormalizeTableName(string tableName)
    {
        if (string.IsNullOrWhiteSpace(tableName))
            throw new ArgumentException("A table name is required.", nameof(tableName));

        var normalized = tableName.Trim();
        if (normalized.Length >= 2)
        {
            if ((normalized[0] == '"' && normalized[^1] == '"') ||
                (normalized[0] == '`' && normalized[^1] == '`') ||
                (normalized[0] == '[' && normalized[^1] == ']'))
            {
                normalized = normalized[1..^1];
            }
        }

        return normalized;
    }

    private static string EscapeIdentifier(string identifier) =>
        identifier.Replace("\"", "\"\"", StringComparison.Ordinal);

    private static string? ParseIdentityFieldName(string? createTableSql)
    {
        if (string.IsNullOrWhiteSpace(createTableSql))
            return null;

        var openingParenthesis = createTableSql.IndexOf('(');
        var closingParenthesis = createTableSql.LastIndexOf(')');
        if (openingParenthesis < 0 || closingParenthesis <= openingParenthesis)
            return null;

        var definitions = createTableSql[
            (openingParenthesis + 1)..closingParenthesis];

        foreach (var definition in definitions.Split(
            ',',
            StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (!IsIdentityDefinition(definition))
                continue;

            var fieldName = definition;
            if (fieldName.StartsWith("PRIMARY KEY", StringComparison.OrdinalIgnoreCase))
            {
                fieldName = fieldName["PRIMARY KEY".Length..].Trim();
            }
            else
            {
                var separator = fieldName.IndexOfAny([' ', '\t', '\r', '\n']);
                if (separator > 0)
                    fieldName = fieldName[..separator];
            }

            return UnquoteIdentifier(fieldName.Trim().Trim('(', ')'));
        }

        return null;
    }

    private static bool IsIdentityDefinition(string definition) =>
        definition.Contains("AUTOINCREMENT", StringComparison.OrdinalIgnoreCase) ||
        (definition.Contains("INTEGER", StringComparison.OrdinalIgnoreCase) &&
         definition.Contains("PRIMARY KEY", StringComparison.OrdinalIgnoreCase));

    private static string UnquoteIdentifier(string identifier)
    {
        if (identifier.Length >= 2 &&
            ((identifier[0] == '"' && identifier[^1] == '"') ||
             (identifier[0] == '`' && identifier[^1] == '`') ||
             (identifier[0] == '[' && identifier[^1] == ']')))
        {
            return identifier[1..^1];
        }

        return identifier;
    }
}
