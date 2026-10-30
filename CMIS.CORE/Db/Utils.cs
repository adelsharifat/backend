using System.ComponentModel.DataAnnotations.Schema;
using System.Data;
using System.Reflection;

namespace CMIS.Core.Db;

/// <summary>Creates DataTables from generated row contracts and their SQL column mappings.</summary>
public static class Utils
{
    /// <summary>
    /// Creates a DataTable whose columns follow the public properties of <typeparamref name="T"/>.
    /// SQL column names are read from <see cref="ColumnAttribute"/> when present.
    /// </summary>
    public static DataTable ModelToDataTable<T>(IEnumerable<T> models, string? tableName = null)
    {
        ArgumentNullException.ThrowIfNull(models);

        var properties = typeof(T)
            .GetProperties(BindingFlags.Instance | BindingFlags.Public)
            .Where(property => property.CanRead && property.GetIndexParameters().Length == 0)
            .OrderBy(property => property.MetadataToken)
            .ToArray();

        if (properties.Length == 0)
            throw new InvalidOperationException($"Type '{typeof(T).FullName}' has no readable public properties.");

        var nullability = new NullabilityInfoContext();
        var table = new DataTable(tableName ?? typeof(T).Name);

        foreach (var property in properties)
        {
            var columnName = property.GetCustomAttribute<ColumnAttribute>()?.Name;
            if (string.IsNullOrWhiteSpace(columnName))
                columnName = property.Name;

            var propertyType = Nullable.GetUnderlyingType(property.PropertyType) ?? property.PropertyType;
            var allowDBNull = property.PropertyType.IsValueType
                ? Nullable.GetUnderlyingType(property.PropertyType) is not null
                : nullability.Create(property).ReadState != NullabilityState.NotNull;

            table.Columns.Add(new DataColumn(columnName, propertyType)
            {
                AllowDBNull = allowDBNull
            });
        }

        foreach (var model in models)
        {
            if (model is null)
                throw new ArgumentException("The model collection cannot contain null items.", nameof(models));

            var row = table.NewRow();
            for (var i = 0; i < properties.Length; i++)
                row[i] = properties[i].GetValue(model) ?? DBNull.Value;

            table.Rows.Add(row);
        }

        return table;
    }
}
