using Microsoft.Data.SqlClient;

namespace Pdv.Infrastructure.Persistence;

/// <summary>
/// Backup/restauração nativos do SQL Server. Os caminhos são relativos à máquina onde o SQL Server roda
/// e a conta do serviço precisa ter permissão de escrita/leitura na pasta.
/// </summary>
public sealed class BackupService(string connectionString)
{
    public async Task BackupAsync(string backupFilePath, CancellationToken ct = default)
    {
        var builder = new SqlConnectionStringBuilder(connectionString);
        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync(ct);
        await using var command = connection.CreateCommand();
        command.CommandText = $"BACKUP DATABASE {Quote(builder.InitialCatalog)} TO DISK = @path WITH INIT, CHECKSUM, FORMAT";
        command.Parameters.AddWithValue("@path", backupFilePath);
        command.CommandTimeout = 0;
        await command.ExecuteNonQueryAsync(ct);
    }

    /// <summary>Substitui o banco pelo conteúdo do backup. Desconecta os demais usuários.</summary>
    public async Task RestoreAsync(string backupFilePath, CancellationToken ct = default)
    {
        var builder = new SqlConnectionStringBuilder(connectionString);
        var database = Quote(builder.InitialCatalog);
        builder.InitialCatalog = "master";

        SqlConnection.ClearAllPools();
        await using var connection = new SqlConnection(builder.ConnectionString);
        await connection.OpenAsync(ct);
        await using var command = connection.CreateCommand();
        command.CommandTimeout = 0;
        command.Parameters.AddWithValue("@path", backupFilePath);
        command.CommandText = $"""
            IF DB_ID(@name) IS NOT NULL ALTER DATABASE {database} SET SINGLE_USER WITH ROLLBACK IMMEDIATE;
            RESTORE DATABASE {database} FROM DISK = @path WITH REPLACE, CHECKSUM;
            ALTER DATABASE {database} SET MULTI_USER;
            """;
        command.Parameters.AddWithValue("@name", new SqlConnectionStringBuilder(connectionString).InitialCatalog);
        await command.ExecuteNonQueryAsync(ct);
    }

    private static string Quote(string identifier) => "[" + identifier.Replace("]", "]]") + "]";
}
