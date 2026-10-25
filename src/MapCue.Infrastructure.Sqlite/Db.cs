using System.Globalization;
using MapCue.Application.Graph;
using Microsoft.Data.Sqlite;

namespace MapCue.Infrastructure.Sqlite;

/// <summary>Small ADO.NET helpers: parameters, round-trip time format, enum and JSON columns.</summary>
internal static class Db
{
    public static string Time(DateTimeOffset value) => value.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture);

    public static string? Time(DateTimeOffset? value) => value is null ? null : Time(value.Value);

    public static DateTimeOffset ReadTime(SqliteDataReader reader, int ordinal) =>
        DateTimeOffset.Parse(reader.GetString(ordinal), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);

    public static DateTimeOffset? ReadTimeOrNull(SqliteDataReader reader, int ordinal) =>
        reader.IsDBNull(ordinal) ? null : ReadTime(reader, ordinal);

    public static string? ReadStringOrNull(SqliteDataReader reader, int ordinal) => reader.IsDBNull(ordinal) ? null : reader.GetString(ordinal);

    public static TEnum ReadEnum<TEnum>(SqliteDataReader reader, int ordinal)
        where TEnum : struct, Enum =>
        Enum.Parse<TEnum>(reader.GetString(ordinal));

    public static TEnum? ReadEnumOrNull<TEnum>(SqliteDataReader reader, int ordinal)
        where TEnum : struct, Enum =>
        reader.IsDBNull(ordinal) ? null : Enum.Parse<TEnum>(reader.GetString(ordinal));

    public static string Json<T>(T value) => GraphJson.Serialize(value);

    public static T ReadJson<T>(SqliteDataReader reader, int ordinal) => GraphJson.Deserialize<T>(reader.GetString(ordinal));

    public static SqliteCommand Command(SqliteConnection connection, SqliteTransaction transaction, string sql, params (string Name, object? Value)[] parameters)
    {
        var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        foreach (var (name, value) in parameters)
        {
            command.Parameters.AddWithValue(name, value ?? DBNull.Value);
        }

        return command;
    }
}
