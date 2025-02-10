/*
Copyright(C) 2018

This program is free software: you can redistribute it and/or modify
it under the terms of the GNU General Public License as published by
the Free Software Foundation, either version 3 of the License.

This program is distributed in the hope that it will be useful,
but WITHOUT ANY WARRANTY; without even the implied warranty of
MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE. See the
GNU General Public License for more details.

You should have received a copy of the GNU General Public License
along with this program. If not, see<http://www.gnu.org/licenses/>.
*/

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using Microsoft.Extensions.Logging;
using SQLitePCL;
using SQLitePCL.pretty;

namespace Jellyfin.Plugin.PlaybackReporting.Data
{
    // TODO use the _same_ implementation from core???
    public abstract class BaseSqliteRepository : IDisposable
    {
        protected string? DbFilePath { get; set; }
        protected ReaderWriterLockSlim WriteLock;

        protected ILogger<BaseSqliteRepository> Logger { get; private set; }

        protected BaseSqliteRepository(ILogger<BaseSqliteRepository> logger)
        {
            Logger = logger;

            WriteLock = new ReaderWriterLockSlim(LockRecursionPolicy.NoRecursion);
        }

        protected static TransactionMode TransactionMode
        {
            get { return TransactionMode.Deferred; }
        }

        internal static int ThreadSafeMode { get; set; }

        static BaseSqliteRepository()
        {
            SQLite3.EnableSharedCache = false;

            _ = raw.sqlite3_config(raw.SQLITE_CONFIG_MEMSTATUS, 0);
            //CheckOk(rc);

            _ = raw.sqlite3_config(raw.SQLITE_CONFIG_MULTITHREAD, 1);
            //rc = raw.sqlite3_config(raw.SQLITE_CONFIG_SINGLETHREAD, 1);
            //rc = raw.sqlite3_config(raw.SQLITE_CONFIG_SERIALIZED, 1);
            //CheckOk(rc);

            _ = raw.sqlite3_enable_shared_cache(1);

            ThreadSafeMode = raw.sqlite3_threadsafe();
            _ = raw.sqlite3_initialize();
        }

        private static bool _versionLogged;

        private string? _defaultWal;
        protected ManagedConnection? _connection;

        protected virtual bool EnableSingleConnection
        {
            get { return true; }
        }

        protected ManagedConnection CreateConnection(bool isReadOnly = false)
        {
            if (_connection != null)
            {
                return _connection;
            }

            lock (WriteLock)
            {
                if (!_versionLogged)
                {
                    _versionLogged = true;
                    Logger.LogInformation("Sqlite version: " + SQLite3.Version);
                    Logger.LogInformation("Sqlite compiler options: " + string.Join(",", SQLite3.CompilerOptions.ToArray()));
                }

                ConnectionFlags connectionFlags;

                if (isReadOnly)
                {
                    //Logger.LogInformation("Opening read connection");
                    //connectionFlags = ConnectionFlags.ReadOnly;
                    connectionFlags = ConnectionFlags.Create;
                    connectionFlags |= ConnectionFlags.ReadWrite;
                }
                else
                {
                    //Logger.LogInformation("Opening write connection");
                    connectionFlags = ConnectionFlags.Create;
                    connectionFlags |= ConnectionFlags.ReadWrite;
                }

                if (EnableSingleConnection)
                {
                    connectionFlags |= ConnectionFlags.PrivateCache;
                }
                else
                {
                    connectionFlags |= ConnectionFlags.SharedCached;
                }

                connectionFlags |= ConnectionFlags.NoMutex;

                var db = SQLite3.Open(DbFilePath, connectionFlags, null);

                try
                {
                    if (string.IsNullOrWhiteSpace(_defaultWal))
                    {
                        _defaultWal = db.Query("PRAGMA journal_mode").SelectScalarString().First();

                        Logger.LogInformation("Default journal_mode for {0} is {1}", DbFilePath, _defaultWal);
                    }

                    var queries = new List<string>
                    {
                        //"PRAGMA cache size=-10000"
                        //"PRAGMA read_uncommitted = true",
                        "PRAGMA synchronous=Normal"
                    };

                    if (CacheSize.HasValue)
                    {
                        queries.Add("PRAGMA cache_size=" + CacheSize.Value.ToString(CultureInfo.InvariantCulture));
                    }

                    if (EnableTempStoreMemory)
                    {
                        queries.Add("PRAGMA temp_store = memory");
                    }
                    else
                    {
                        queries.Add("PRAGMA temp_store = file");
                    }

                    foreach (var query in queries)
                    {
                        db.Execute(query);
                    }
                }
                catch
                {
                    db.Dispose();
                    throw;
                }

                _connection = new ManagedConnection(db, false);

                return _connection;
            }
        }
        protected virtual bool EnableTempStoreMemory
        {
            get
            {
                return false;
            }
        }

        protected virtual int? CacheSize => null;

        public void Dispose()
        {
            Dispose(true);
            GC.SuppressFinalize(this);
        }

        private readonly object _disposeLock = new object();

        /// <summary>
        /// Releases unmanaged and - optionally - managed resources.
        /// </summary>
        /// <param name="dispose"><c>true</c> to release both managed and unmanaged resources; <c>false</c> to release only unmanaged resources.</param>
        protected virtual void Dispose(bool dispose)
        {
            if (dispose)
            {   
                DisposeConnection();
                _ = raw.sqlite3_shutdown();
            }
        }

        private void DisposeConnection()
        {
            try
            {
                lock (_disposeLock)
                {
                    using (WriteLock.Write())
                    {
                        if (_connection != null)
                        {
                            using (_connection)
                            {
                                _connection.Close();
                            }
                            _connection = null;
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Logger.LogError(ex, "Error disposing database");
            }
        }

    }

    public static class ReaderWriterLockSlimExtensions
    {
        private sealed class WriteLockToken : IDisposable
        {
            private readonly ReaderWriterLockSlim _sync;
            public WriteLockToken(ReaderWriterLockSlim sync)
            {
                _sync = sync;
                sync.EnterWriteLock();
            }
            public void Dispose()
            {
                if (_sync != null)
                {
                    _sync.ExitWriteLock();
                }
            }
        }

        public static IDisposable Read(this ReaderWriterLockSlim obj)
        {
            //if (BaseSqliteRepository.ThreadSafeMode > 0)
            //{
            //    return new DummyToken();
            //}
            return new WriteLockToken(obj);
        }
        public static IDisposable Write(this ReaderWriterLockSlim obj)
        {
            //if (BaseSqliteRepository.ThreadSafeMode > 0)
            //{
            //    return new DummyToken();
            //}
            return new WriteLockToken(obj);
        }
    }
}
