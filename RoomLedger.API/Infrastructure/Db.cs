using Dapper;
using Microsoft.Data.SqlClient;

namespace RoomLedger.API.Infrastructure;

public interface IDb
{
    Task<T> QuerySingleAsync<T>(string sql, object? param = null);
    Task<T?> QueryFirstOrDefaultAsync<T>(string sql, object? param = null);
    Task<IEnumerable<T>> QueryAsync<T>(string sql, object? param = null);
    Task<int> ExecuteAsync(string sql, object? param = null);
    Task<T> QueryScalarAsync<T>(string sql, object? param = null);
}

public class Db : IDb
{
    private readonly string _cs;
    public Db(IConfiguration config) => _cs = config.GetConnectionString("DefaultConnection")!;

    private SqlConnection Open()
    {
        var conn = new SqlConnection(_cs);
        conn.Open();
        return conn;
    }

    public async Task<T> QuerySingleAsync<T>(string sql, object? param = null)
    { using var c = Open(); return await c.QuerySingleAsync<T>(sql, param); }

    public async Task<T?> QueryFirstOrDefaultAsync<T>(string sql, object? param = null)
    { using var c = Open(); return await c.QueryFirstOrDefaultAsync<T>(sql, param); }

    public async Task<IEnumerable<T>> QueryAsync<T>(string sql, object? param = null)
    { using var c = Open(); return await c.QueryAsync<T>(sql, param); }

    public async Task<int> ExecuteAsync(string sql, object? param = null)
    { using var c = Open(); return await c.ExecuteAsync(sql, param); }

    public async Task<T> QueryScalarAsync<T>(string sql, object? param = null)
    {
        using var c = Open(); 
        return await c.ExecuteScalarAsync<T>(sql, param)!;
    }
}
