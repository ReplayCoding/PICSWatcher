namespace GameTracker;

using MySqlConnector;

class Database
{
    public static async Task<MySqlConnection> GetConnectionAsync()
    {
        var connection = new MySqlConnection(Program.Config.DbConnectionString);
        await connection.OpenAsync();

        return connection;
    }

    public static MySqlConnection GetConnection()
    {
        return new MySqlConnection(Program.Config.DbConnectionString);
    }
}