namespace GameTracker;

using Dapper;

using MySqlConnector;

class LocalConfig
{
    public static async Task SetAsync(string key, string value)
    {
        // Console.WriteLine($"LocalConfig setting `{key}` to `{value}`");
        await using var connection = await Database.GetConnectionAsync();
        await connection.ExecuteAsync(@"
                INSERT INTO `LocalConfig` (`Key`, `Value`)
                    VALUES (@Key, @Value)
                    ON DUPLICATE KEY UPDATE `Value` = VALUES(`Value`)",
                    new
                    {
                        Key = key,
                        Value = value
                    });
    }

    public static async Task DeleteAsync(string key)
    {
        // Console.WriteLine($"LocalConfig deleting `{key}`");
        await using var connection = await Database.GetConnectionAsync();
        await connection.ExecuteAsync(@"DELETE FROM `LocalConfig` WHERE `Key` = @Key", new { Key = key, });
    }

    public static void Set(string key, string value)
    {
        // Console.WriteLine($"LocalConfig setting `{key}` to `{value}`");
        using var connection = Database.GetConnection();
        connection.Execute(@"
                INSERT INTO `LocalConfig` (`Key`, `Value`)
                    VALUES (@Key, @Value)
                    ON DUPLICATE KEY UPDATE `Value` = VALUES(`Value`)",
                    new
                    {
                        Key = key,
                        Value = value
                    });
    }

    // TODO: Fix these to use dapper or something
    public static async Task<T> GetAsync<T>(string key)
    {
        await using var connection = await Database.GetConnectionAsync();
        return connection.ExecuteScalar<T>($"SELECT `Value` FROM `LocalConfig` WHERE `Key` = \"{key}\"");
    }

    public static T Get<T>(string key)
    {
        using var connection = Database.GetConnection();
        return connection.ExecuteScalar<T>($"SELECT `Value` FROM `LocalConfig` WHERE `Key` = \"{key}\"");
    }
}