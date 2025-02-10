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
using SQLitePCL.pretty;

namespace Jellyfin.Plugin.PlaybackReporting.Data
{
    public class ManagedConnection : IDisposable
    {
        private readonly SQLiteDatabaseConnection _db;
        private readonly bool _closeOnDispose;

        public ManagedConnection(SQLiteDatabaseConnection db, bool closeOnDispose)
        {
            _db = db;
            _closeOnDispose = closeOnDispose;
        }

        public IStatement PrepareStatement(string sql)
        {
            return _db.PrepareStatement(sql);
        }


        public void Execute(string sql, params object[] values)
        {
            _db.Execute(sql, values);
        }

        public int GetChangeCount()
        {
            return _db.TotalChanges;
        }

        public void RunInTransaction(Action<IDatabaseConnection> action, TransactionMode mode)
        {
            _db.RunInTransaction(action, mode);
        }

        public IEnumerable<IReadOnlyList<ResultSetValue>> Query(string sql)
        {
            return _db.Query(sql);
        }

        public void Close()
        {
            _db.Dispose();
        }

        public void Dispose()
        {
            if (_closeOnDispose)
            {
                Close();
            }
            GC.SuppressFinalize(this);
        }

    }

}
