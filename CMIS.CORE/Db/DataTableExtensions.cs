using System.ComponentModel.DataAnnotations.Schema;
using System.Data;
using System.Reflection;

namespace CMIS.Core.Db;

/// <summary>Copies model values into a DataTable using the SQL column names.</summary>
public static class DataTableExtensions
{
    /// <summary>
    /// Replaces the table rows with values from <paramref name="source"/>.
    /// Public properties are matched by name or by their <see cref="ColumnAttribute"/> name.
    /// </summary>
    public static void CopyFrom<T>(this DataTable table, IEnumerable<T> source)
    {
        ArgumentNullException.ThrowIfNull(table);
        ArgumentNullException.ThrowIfNull(source);

        var properties = typeof(T)
            .GetProperties(BindingFlags.Instance | BindingFlags.Public)
            .Where(property => property.CanRead && property.GetIndexParameters().Length == 0)
            .ToDictionary(
                property => property.GetCustomAttribute<ColumnAttribute>()?.Name ?? property.Name,
                StringComparer.OrdinalIgnoreCase);

        var columnProperties = new PropertyInfo[table.Columns.Count];
        for (var i = 0; i < table.Columns.Count; i++)
        {
            var columnName = table.Columns[i].ColumnName;
            if (!properties.TryGetValue(columnName, out var property))
                throw new InvalidOperationException(
                    $"Type '{typeof(T).Name}' has no readable property mapped to DataTable column '{columnName}'.");

            columnProperties[i] = property;
        }

        table.Rows.Clear();

        foreach (var item in source)
        {
            if (item is null)
                throw new ArgumentException("The source collection cannot contain null items.", nameof(source));

            var row = table.NewRow();
            for (var i = 0; i < columnProperties.Length; i++)
                row[i] = columnProperties[i].GetValue(item) ?? DBNull.Value;

            table.Rows.Add(row);
        }
    }
}
