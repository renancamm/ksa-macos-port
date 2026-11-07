// SQLite through the copy macOS ships (/usr/lib/libsqlite3.dylib), for a run's store (Store.cs): the few calls the store
// makes, so that the port ships no SQLite and the game's process loads no copy a mod might bring too.
using System;
using System.Runtime.InteropServices;

namespace KsaPort.Common.Benchmark;

public sealed class SqliteException(string message) : Exception(message);

public sealed class Sqlite : IDisposable
{
    private const string Lib = "/usr/lib/libsqlite3.dylib";
    private const int Ok = 0, Row = 100, Done = 101;
    private const int OpenReadOnly = 0x1, OpenReadWrite = 0x2, OpenCreate = 0x4, OpenNoMutex = 0x8000;
    // SQLITE_TRANSIENT: SQLite copies a bound text before the call returns
    private static readonly IntPtr Transient = new(-1);

    [DllImport(Lib)]
    private static extern int sqlite3_open_v2([MarshalAs(UnmanagedType.LPUTF8Str)] string name, out IntPtr db, int flags, IntPtr vfs);

    [DllImport(Lib)]
    private static extern int sqlite3_close_v2(IntPtr db);

    [DllImport(Lib)]
    private static extern int sqlite3_busy_timeout(IntPtr db, int ms);

    [DllImport(Lib)]
    private static extern IntPtr sqlite3_errmsg(IntPtr db);

    [DllImport(Lib)]
    private static extern int sqlite3_prepare_v2(IntPtr db, [MarshalAs(UnmanagedType.LPUTF8Str)] string sql, int bytes, out IntPtr stmt, IntPtr tail);

    [DllImport(Lib)]
    private static extern int sqlite3_finalize(IntPtr stmt);

    [DllImport(Lib)]
    private static extern int sqlite3_reset(IntPtr stmt);

    [DllImport(Lib)]
    private static extern int sqlite3_clear_bindings(IntPtr stmt);

    [DllImport(Lib)]
    private static extern int sqlite3_step(IntPtr stmt);

    [DllImport(Lib)]
    private static extern int sqlite3_bind_double(IntPtr stmt, int index, double value);

    [DllImport(Lib)]
    private static extern int sqlite3_bind_int64(IntPtr stmt, int index, long value);

    [DllImport(Lib)]
    private static extern int sqlite3_bind_null(IntPtr stmt, int index);

    [DllImport(Lib)]
    private static extern int sqlite3_bind_text(IntPtr stmt, int index, [MarshalAs(UnmanagedType.LPUTF8Str)] string text, int bytes, IntPtr destructor);

    [DllImport(Lib)]
    private static extern int sqlite3_column_count(IntPtr stmt);

    [DllImport(Lib)]
    private static extern int sqlite3_column_type(IntPtr stmt, int column);

    [DllImport(Lib)]
    private static extern double sqlite3_column_double(IntPtr stmt, int column);

    [DllImport(Lib)]
    private static extern long sqlite3_column_int64(IntPtr stmt, int column);

    [DllImport(Lib)]
    private static extern IntPtr sqlite3_column_text(IntPtr stmt, int column);

    [DllImport(Lib)]
    private static extern IntPtr sqlite3_column_name(IntPtr stmt, int column);

    private IntPtr _db;

    private Sqlite(IntPtr db) => _db = db;

    // A database file, made if it isn't there (readOnly: never made, never written)
    public static Sqlite Open(string path, bool readOnly = false)
    {
        int flags = (readOnly ? OpenReadOnly : OpenReadWrite | OpenCreate) | OpenNoMutex;
        int rc = sqlite3_open_v2(path, out var db, flags, IntPtr.Zero);
        if (rc != Ok)
        {
            string message = db != IntPtr.Zero ? Marshal.PtrToStringUTF8(sqlite3_errmsg(db)) ?? "" : $"code {rc}";
            if (db != IntPtr.Zero)
                sqlite3_close_v2(db);
            throw new SqliteException($"{path}: {message}");
        }
        // The game writes its file while the host reads it
        sqlite3_busy_timeout(db, 2000);
        return new Sqlite(db);
    }

    public void Exec(string sql)
    {
        using var s = Prepare(sql);
        while (s.Step())
        {
        }
    }

    public Statement Prepare(string sql)
    {
        if (sqlite3_prepare_v2(_db, sql, -1, out var stmt, IntPtr.Zero) != Ok)
            throw Error(sql);
        return new Statement(this, stmt);
    }

    internal SqliteException Error(string what) => new($"{what}: {Marshal.PtrToStringUTF8(sqlite3_errmsg(_db))}");

    public void Dispose()
    {
        if (_db != IntPtr.Zero)
            sqlite3_close_v2(_db);
        _db = IntPtr.Zero;
    }

    public sealed class Statement : IDisposable
    {
        private readonly Sqlite _owner;
        private IntPtr _stmt;

        internal Statement(Sqlite owner, IntPtr stmt) => (_owner, _stmt) = (owner, stmt);

        // Binds the values in order, from parameter 1: double, float, long, int, uint, ulong, bool, string, or null
        public Statement Bind(params object?[] values)
        {
            sqlite3_reset(_stmt);
            sqlite3_clear_bindings(_stmt);
            for (int i = 0; i < values.Length; i++)
            {
                int n = i + 1;
                int rc = values[i] switch
                {
                    null => sqlite3_bind_null(_stmt, n),
                    double d => sqlite3_bind_double(_stmt, n, d),
                    float f => sqlite3_bind_double(_stmt, n, f),
                    long l => sqlite3_bind_int64(_stmt, n, l),
                    int x => sqlite3_bind_int64(_stmt, n, x),
                    uint u => sqlite3_bind_int64(_stmt, n, u),
                    ulong u => sqlite3_bind_int64(_stmt, n, (long)u),
                    bool b => sqlite3_bind_int64(_stmt, n, b ? 1 : 0),
                    string s => sqlite3_bind_text(_stmt, n, s, -1, Transient),
                    var other => throw new ArgumentException($"can't bind a {other.GetType().Name}"),
                };
                if (rc != Ok)
                    throw _owner.Error($"binding parameter {n}");
            }
            return this;
        }

        // Runs it to the next row: true while there is one
        public bool Step()
        {
            int rc = sqlite3_step(_stmt);
            if (rc == Row)
                return true;
            if (rc != Done)
                throw _owner.Error("step");
            return false;
        }

        // Runs a statement that returns no rows, with these values
        public void Run(params object?[] values)
        {
            Bind(values);
            while (Step())
            {
            }
        }

        public int Columns => sqlite3_column_count(_stmt);

        public string Name(int column) => Marshal.PtrToStringUTF8(sqlite3_column_name(_stmt, column)) ?? "";

        // SQLITE_NULL is 5
        public bool IsNull(int column) => sqlite3_column_type(_stmt, column) == 5;

        public double Double(int column) => sqlite3_column_double(_stmt, column);

        public long Long(int column) => sqlite3_column_int64(_stmt, column);

        public string Text(int column) => IsNull(column) ? "" : Marshal.PtrToStringUTF8(sqlite3_column_text(_stmt, column)) ?? "";

        public double? NullableDouble(int column) => IsNull(column) ? null : Double(column);

        public long? NullableLong(int column) => IsNull(column) ? null : Long(column);

        public string? NullableText(int column) => IsNull(column) ? null : Text(column);

        public void Dispose()
        {
            if (_stmt != IntPtr.Zero)
                sqlite3_finalize(_stmt);
            _stmt = IntPtr.Zero;
        }
    }
}
