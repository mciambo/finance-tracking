using Finance.Core;
using Microsoft.Data.Sqlite;
using System.Globalization;

namespace Finance.Infrastructure;

public sealed class Database(string path)
{
    static Database() => SQLitePCL.Batteries_V2.Init();
    public string Path { get; } = System.IO.Path.GetFullPath(path);
    public static string DefaultPath => System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "FinanceTracking", "finance.db");
    public SqliteConnection Open(bool readOnly = false)
    {
        if (readOnly && !File.Exists(Path)) throw new FinanceException("DATABASE_MISSING", "Database assente: eseguire init o importare un Excel.");
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = Path, Mode = readOnly ? SqliteOpenMode.ReadOnly : SqliteOpenMode.ReadWriteCreate,
            DefaultTimeout = 10, Pooling = false
        }.ToString());
        connection.Open();
        Execute(connection, null, "PRAGMA foreign_keys=ON; PRAGMA busy_timeout=10000;");
        return connection;
    }
    public void Initialize()
    {
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(Path)!);
        using var connection = Open();
        var version = Convert.ToInt32(Scalar(connection, null, "PRAGMA user_version"));
        if (version > 3) throw new FinanceException("DATABASE_VERSION", "Database creato con una versione più recente dell'app.");
        if (version == 3) return;
        if (version == 1)
        {
            Backup();
            using var migration=connection.BeginTransaction();
            Execute(connection,migration,"CREATE TABLE manual_expenses(id TEXT PRIMARY KEY,value_date TEXT NOT NULL,description TEXT NOT NULL,amount_cents INTEGER NOT NULL CHECK(amount_cents>=0),deleted INTEGER NOT NULL DEFAULT 0); PRAGMA user_version=2;");
            Execute(connection,migration,"INSERT OR IGNORE INTO metadata VALUES('generation',$generation)",("$generation",Guid.NewGuid().ToString("N")));
            migration.Commit();version=2;
        }
        if(version==2)
        {
            Backup();
            using var migration=connection.BeginTransaction();
            Execute(connection,migration,"""
                CREATE TABLE rule_cancellations(expense_movement_id TEXT PRIMARY KEY REFERENCES movements(id),income_movement_id TEXT NOT NULL UNIQUE REFERENCES movements(id),rule_id TEXT NOT NULL);
                DROP VIEW v_active_alerts;
                CREATE VIEW v_active_alerts AS SELECT m.id,m.value_date AS date,m.description,m.income_cents AS amount_cents FROM alerts a JOIN movements m ON m.id=a.movement_id WHERE a.handled_at IS NULL AND NOT EXISTS(SELECT 1 FROM rule_cancellations r WHERE r.income_movement_id=m.id);
                PRAGMA user_version=3;
                """);
            migration.Commit();return;
        }
        if (Convert.ToInt32(Scalar(connection, null, "SELECT count(*) FROM sqlite_master WHERE type='table'")) > 0)
            throw new FinanceException("DATABASE_VERSION", "Database non riconosciuto; nessuna modifica eseguita.");
        Execute(connection, null, "PRAGMA journal_mode=WAL;");
        using var tx = connection.BeginTransaction();
        Execute(connection, tx, """
            CREATE TABLE metadata(key TEXT PRIMARY KEY, value TEXT NOT NULL);
            INSERT INTO metadata VALUES('revision','0'),('rules_version','');
            CREATE TABLE imports(id TEXT PRIMARY KEY, filename TEXT NOT NULL, imported_at TEXT NOT NULL, count INTEGER NOT NULL);
            CREATE TABLE movements(id TEXT PRIMARY KEY, sequence INTEGER NOT NULL UNIQUE, value_date TEXT NOT NULL,
              income_cents INTEGER, expense_cents INTEGER, description TEXT NOT NULL, import_id TEXT NOT NULL REFERENCES imports(id), excel_row INTEGER NOT NULL);
            CREATE TABLE months(month TEXT PRIMARY KEY, state TEXT NOT NULL CHECK(state IN ('closed','reopened')));
            CREATE TABLE overrides(expense_id TEXT PRIMARY KEY, value_date TEXT, description TEXT, amount_cents INTEGER CHECK(amount_cents>=0),
              deleted INTEGER NOT NULL DEFAULT 0, members TEXT);
            CREATE TABLE expenses(id TEXT PRIMARY KEY, value_date TEXT NOT NULL, description TEXT NOT NULL, amount_cents INTEGER NOT NULL CHECK(amount_cents>=0), rule_id TEXT);
            CREATE TABLE manual_expenses(id TEXT PRIMARY KEY,value_date TEXT NOT NULL,description TEXT NOT NULL,amount_cents INTEGER NOT NULL CHECK(amount_cents>=0),deleted INTEGER NOT NULL DEFAULT 0);
            CREATE TABLE expense_sources(expense_id TEXT NOT NULL REFERENCES expenses(id) ON DELETE CASCADE,
              movement_id TEXT NOT NULL REFERENCES movements(id), PRIMARY KEY(expense_id,movement_id));
            CREATE TABLE alerts(movement_id TEXT PRIMARY KEY REFERENCES movements(id), handled_at TEXT);
            CREATE TABLE rule_cancellations(expense_movement_id TEXT PRIMARY KEY REFERENCES movements(id),income_movement_id TEXT NOT NULL UNIQUE REFERENCES movements(id),rule_id TEXT NOT NULL);
            CREATE VIEW v_expenses AS SELECT id,value_date AS date,description,amount_cents,rule_id,substr(value_date,1,7) AS month FROM expenses;
            CREATE VIEW v_monthly_totals AS SELECT month,sum(amount_cents) AS total_cents,count(*) AS expense_count FROM v_expenses GROUP BY month;
            CREATE VIEW v_active_alerts AS SELECT m.id,m.value_date AS date,m.description,m.income_cents AS amount_cents FROM alerts a JOIN movements m ON m.id=a.movement_id WHERE a.handled_at IS NULL AND NOT EXISTS(SELECT 1 FROM rule_cancellations r WHERE r.income_movement_id=m.id);
            PRAGMA user_version=3;
            """);
        Execute(connection, tx, "INSERT INTO metadata VALUES('generation',$generation)",("$generation",Guid.NewGuid().ToString("N")));
        tx.Commit();
    }
    public string Backup(string? target = null)
    {
        if (target is not null && System.IO.Path.GetFullPath(target).Equals(Path, StringComparison.OrdinalIgnoreCase))
            throw new FinanceException("VALIDATION", "Il backup deve avere un percorso diverso dal database.");
        var directory = System.IO.Path.Combine(System.IO.Path.GetDirectoryName(Path)!, "backups");
        var automatic = target is null;
        target ??= System.IO.Path.Combine(directory, $"finance-{DateTime.UtcNow:yyyyMMdd-HHmmss-fffffff}-{Guid.NewGuid():N}.db");
        target = System.IO.Path.GetFullPath(target);
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(target)!);
        if (File.Exists(target)) throw new FinanceException("VALIDATION", "Il file di backup esiste già.");
        using var source = Open(true);
        using var destination = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = target, Pooling = false }.ToString());
        destination.Open();
        source.BackupDatabase(destination);
        destination.Close();
        if (automatic)
            foreach (var old in new DirectoryInfo(directory).GetFiles("finance-*.db").OrderByDescending(f => f.Name).Skip(20)) old.Delete();
        return target;
    }
    public void Restore(string sourcePath)
    {
        sourcePath = System.IO.Path.GetFullPath(sourcePath);
        if (sourcePath.Equals(Path, StringComparison.OrdinalIgnoreCase)) throw new FinanceException("VALIDATION", "Sorgente e destinazione coincidono.");
        var sourceDb = new Database(sourcePath);
        using var source = sourceDb.Open(true);
        if (Convert.ToInt32(Scalar(source, null, "PRAGMA user_version")) is not (1 or 2 or 3)
            || Convert.ToString(Scalar(source, null, "PRAGMA integrity_check")) != "ok"
            || Convert.ToInt32(Scalar(source, null, "SELECT count(*) FROM sqlite_master WHERE name IN ('movements','overrides','expenses','metadata','months')")) != 5)
            throw new FinanceException("BACKUP_INVALID", "Backup non valido o non compatibile.");
        if (File.Exists(Path)) Backup();
        else Directory.CreateDirectory(System.IO.Path.GetDirectoryName(Path)!);
        using var destination = Open();
        try
        {
            source.BackupDatabase(destination);
            using var tx=destination.BeginTransaction();
            Execute(destination,tx,"INSERT INTO metadata VALUES('generation',$generation) ON CONFLICT(key) DO UPDATE SET value=excluded.value",("$generation",Guid.NewGuid().ToString("N")));
            tx.Commit();
        }
        catch (SqliteException e) { throw new FinanceException("DATABASE_BUSY", $"Ripristino non riuscito; chiudere l'app e riprovare. {e.Message}"); }
        Initialize();
    }

    internal static SqliteCommand Command(SqliteConnection connection, SqliteTransaction? tx, string sql, params (string, object?)[] args)
    {
        var command = connection.CreateCommand(); command.CommandText = sql; command.Transaction = tx;
        foreach (var (key, value) in args) command.Parameters.AddWithValue(key, value ?? DBNull.Value);
        return command;
    }
    internal static void Execute(SqliteConnection c, SqliteTransaction? tx, string sql, params (string, object?)[] args)
    { using var cmd = Command(c, tx, sql, args); cmd.ExecuteNonQuery(); }
    internal static object? Scalar(SqliteConnection c, SqliteTransaction? tx, string sql, params (string, object?)[] args)
    { using var cmd = Command(c, tx, sql, args); return cmd.ExecuteScalar(); }
    internal static List<T> Rows<T>(SqliteConnection c, SqliteTransaction? tx, string sql, Func<SqliteDataReader,T> map, params (string, object?)[] args)
    { using var cmd = Command(c, tx, sql, args); using var reader = cmd.ExecuteReader(); var result = new List<T>(); while (reader.Read()) result.Add(map(reader)); return result; }
    internal static DateOnly ParseDate(string s) => DateOnly.ParseExact(s,"yyyy-MM-dd",CultureInfo.InvariantCulture);
}
