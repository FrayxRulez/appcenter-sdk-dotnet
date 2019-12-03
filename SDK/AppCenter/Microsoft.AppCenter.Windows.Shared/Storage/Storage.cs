// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License.

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AppCenter.Ingestion.Models;
using Microsoft.AppCenter.Ingestion.Models.Serialization;
using Microsoft.AppCenter.Utils;
using Microsoft.AppCenter.Windows.Shared.Storage;
using Newtonsoft.Json;
using SQLitePCL;

namespace Microsoft.AppCenter.Storage
{
    /// <summary>
    /// Manages the database of App Center logs on disk
    /// </summary>
    internal sealed class Storage : IStorage
    {
        internal class LogEntry
        {
            public int Id { get; set; }

            // The name of the channel that emitted the log
            public string Channel { get; set; }

            // The serialized json text of the log
            public string Log { get; set; }
        }

        // Const for storage data.
        private const string TableName = "LogEntry";
        private const string ColumnChannelName = "Channel";
        private const string ColumnLogName = "Log";
        private const string ColumnIdName = "Id";

        private readonly IStorageAdapter _storageAdapter;
        private const string DbIdentifierDelimiter = "@";

        private readonly Dictionary<string, List<long>> _pendingDbIdentifierGroups = new Dictionary<string, List<long>>();
        private readonly HashSet<long> _pendingDbIdentifiers = new HashSet<long>();

        // Blocking collection is thread safe
        private readonly BlockingCollection<Task> _queue = new BlockingCollection<Task>();
        private readonly SemaphoreSlim _flushSemaphore = new SemaphoreSlim(0);
        private readonly Task _queueFlushTask;

        /// <summary>
        /// Creates an instance of Storage
        /// </summary>
        public Storage() : this(DefaultAdapter())
        {
        }

        /// <summary>
        /// Creates an instance of Storage given a connection object
        /// </summary>
        internal Storage(IStorageAdapter adapter)
        {
            _storageAdapter = adapter;
            _queue.Add(new Task(() => InitializeDatabaseAsync().GetAwaiter().GetResult()));
            _queueFlushTask = Task.Run(FlushQueueAsync);
        }

        private static IStorageAdapter DefaultAdapter()
        {
            try
            {
                return new StorageAdapter(Constants.AppCenterDatabasePath);
            }
            catch (Exception e)
            {
                throw new StorageException($"Cannot initialize SQLite library.", e);
            }
        }

        /// <summary>
        /// Asynchronously adds a log to storage
        /// </summary>
        /// <param name="channelName">The name of the channel associated with the log</param>
        /// <param name="log">The log to add</param>
        /// <exception cref="StorageException"/>
        public Task PutLog(string channelName, Log log)
        {
            return AddTaskToQueue(() =>
            {
                var logJsonString = LogSerializer.Serialize(log);
                var columnsMapList = new List<List<ColumnValueMap>>(){ new List<ColumnValueMap>()
                {
                    new ColumnValueMap() { ColumnName = ColumnChannelName, ColumnValue = channelName, ColumnType = raw.SQLITE_TEXT },
                    new ColumnValueMap() { ColumnName = ColumnLogName, ColumnValue = logJsonString, ColumnType = raw.SQLITE_TEXT }
                }
            };
                _storageAdapter.InsertAsync(TableName, columnsMapList).GetAwaiter().GetResult();
            });
        }

        /// <summary>
        /// Asynchronously deletes all logs in a particular batch
        /// </summary>
        /// <param name="channelName">The name of the channel associated with the batch</param>
        /// <param name="batchId">The batch identifier</param>
        /// <exception cref="StorageException"/>
        public Task DeleteLogs(string channelName, string batchId)
        {
            return AddTaskToQueue(() =>
            {
                try
                {
                    AppCenterLog.Debug(AppCenterLog.LogTag,
                        $"Deleting logs from storage for channel '{channelName}' with batch id '{batchId}'");
                    var identifiers = _pendingDbIdentifierGroups[GetFullIdentifier(channelName, batchId)];
                    _pendingDbIdentifierGroups.Remove(GetFullIdentifier(channelName, batchId));
                    var deletedIdsMessage = "The IDs for deleting log(s) is/ are:";
                    foreach (var id in identifiers)
                    {
                        deletedIdsMessage += "\n\t" + id;
                        _pendingDbIdentifiers.Remove(id);
                    }
                    AppCenterLog.Debug(AppCenterLog.LogTag, deletedIdsMessage);
                    _storageAdapter.DeleteAsync(TableName, $"{ColumnChannelName} = \'{channelName}\' AND {ColumnIdName} IN ({string.Join(",", identifiers)})").GetAwaiter().GetResult();
                }
                catch (KeyNotFoundException e)
                {
                    throw new StorageException(e);
                }
            });
        }

        /// <summary>
        /// Asynchronously deletes all logs for a particular channel
        /// </summary>
        /// <param name="channelName">Name of the channel to delete logs for</param>
        /// <exception cref="StorageException"/>
        public Task DeleteLogs(string channelName)
        {
            return AddTaskToQueue(() =>
            {
                try
                {
                    AppCenterLog.Debug(AppCenterLog.LogTag,
                        $"Deleting all logs from storage for channel '{channelName}'");
                    ClearPendingLogStateWithoutEnqueue(channelName);
                    var values = new List<object>() { channelName };
                    _storageAdapter.DeleteAsync(TableName, $"{ColumnChannelName} = \'{channelName}\'")
                        .GetAwaiter().GetResult();
                }
                catch (KeyNotFoundException e)
                {
                    throw new StorageException(e);
                }
            });
        }

        /// <summary>
        /// Asynchronously counts the number of logs stored for a particular channel
        /// </summary>
        /// <param name="channelName">The name of the channel to count logs for</param>
        /// <returns>The number of logs found in storage</returns>
        /// <exception cref="StorageException"/>
        public Task<int> CountLogsAsync(string channelName)
        {
            return AddTaskToQueue(() =>
            {
                string whereClause = $"{ColumnChannelName} = \"{channelName}\"";
                return _storageAdapter.CountAsync(TableName, whereClause).GetAwaiter().GetResult();
            });
        }

        /// <summary>
        /// Asynchronously clears the stored state of logs that have been retrieved
        /// </summary>
        /// <param name="channelName"></param>
        public Task ClearPendingLogState(string channelName)
        {
            return AddTaskToQueue(() =>
            {
                ClearPendingLogStateWithoutEnqueue(channelName);
                AppCenterLog.Debug(AppCenterLog.LogTag, $"Clear pending log states for channel {channelName}");
            });
        }

        private void ClearPendingLogStateWithoutEnqueue(string channelName)
        {
            var fullIdentifiers = new List<string>();

            foreach (var fullIdentifier in _pendingDbIdentifierGroups.Keys)
            {
                if (!ChannelMatchesIdentifier(channelName, fullIdentifier))
                {
                    continue;
                }
                foreach (var id in _pendingDbIdentifierGroups[fullIdentifier])
                {
                    _pendingDbIdentifiers.Remove(id);
                }
                fullIdentifiers.Add(fullIdentifier);
            }
            foreach (var fullIdentifier in fullIdentifiers)
            {
                _pendingDbIdentifierGroups.Remove(fullIdentifier);
            }
        }

        /// <summary>
        /// Asynchronously retrieves logs from storage and flags them to avoid duplicate retrievals on subsequent calls
        /// </summary>
        /// <param name="channelName">Name of the channel to retrieve logs from</param>
        /// <param name="limit">The maximum number of logs to retrieve</param>
        /// <param name="logs">A list to which the retrieved logs will be added</param>
        /// <returns>A batch ID for the set of returned logs; null if no logs are found</returns>
        /// <exception cref="StorageException"/>
        public Task<string> GetLogsAsync(string channelName, int limit, List<Log> logs)
        {
            return AddTaskToQueue(() =>
            {
                logs?.Clear();
                var retrievedLogs = new List<Log>();
                AppCenterLog.Debug(AppCenterLog.LogTag,
                    $"Trying to get up to {limit} logs from storage for {channelName}");
                var idPairs = new List<Tuple<Guid?, long>>();
                var failedToDeserializeALog = false;
                var pendingExcludeClause = string.Empty;
                if (_pendingDbIdentifiers != null && _pendingDbIdentifiers.Count > 0)
                {
                    pendingExcludeClause = $" AND {ColumnIdName} NOT IN ({string.Join(",", _pendingDbIdentifiers)})";
                }
                var whereClause = $"{ColumnChannelName} = \'{channelName}\' {pendingExcludeClause}";
                var objectdEntries = _storageAdapter.GetAsync(TableName, whereClause, limit).GetAwaiter().GetResult();
                var retrievedEntries = objectdEntries.Select(entries =>
                    new LogEntry()
                    {
                        Id = (int)entries[0],
                        Channel = (string)entries[1],
                        Log = (string)entries[2]
                    }
                ).ToList();
                foreach (var entry in retrievedEntries)
                {
                    try
                    {
                        var log = LogSerializer.DeserializeLog(entry.Log);
                        retrievedLogs.Add(log);
                        idPairs.Add(Tuple.Create(log.Sid, Convert.ToInt64(entry.Id)));
                    }
                    catch (JsonException e)
                    {
                        AppCenterLog.Error(AppCenterLog.LogTag, "Cannot deserialize a log in storage", e);
                        failedToDeserializeALog = true;
                        var values = new List<object> { entry.Id };
                        _storageAdapter.DeleteAsync(TableName, $"{ColumnIdName} = {entry.Id}")
                            .GetAwaiter().GetResult();
                    }
                }
                if (failedToDeserializeALog)
                {
                    AppCenterLog.Warn(AppCenterLog.LogTag, "Deleted logs that could not be deserialized");
                }
                if (idPairs.Count == 0)
                {
                    AppCenterLog.Debug(AppCenterLog.LogTag,
                        $"No available logs in storage for channel '{channelName}'");
                    return null;
                }

                // Process the results
                var batchId = Guid.NewGuid().ToString();
                ProcessLogIds(channelName, batchId, idPairs);
                logs?.AddRange(retrievedLogs);
                return batchId;
            });
        }

        private void ProcessLogIds(string channelName, string batchId, IEnumerable<Tuple<Guid?, long>> idPairs)
        {
            var ids = new List<long>();
            var message = "The SID/ID pairs for returning logs are:";
            foreach (var idPair in idPairs)
            {
                var sidString = idPair.Item1?.ToString() ?? "(null)";
                message += "\n\t" + sidString + " / " + idPair.Item2;
                _pendingDbIdentifiers.Add(idPair.Item2);
                ids.Add(idPair.Item2);
            }
            _pendingDbIdentifierGroups.Add(GetFullIdentifier(channelName, batchId), ids);
            AppCenterLog.Debug(AppCenterLog.LogTag, message);
        }

        private async Task InitializeDatabaseAsync()
        {
            try
            {
                var scheme = new List<ColumnMap>
                {
                    new ColumnMap { ColumnName = ColumnIdName, ColumnType = raw.SQLITE_INTEGER, IsAutoIncrement = true, IsPrimarykey = true },
                    new ColumnMap { ColumnName = ColumnChannelName, ColumnType = raw.SQLITE_TEXT, IsAutoIncrement = false, IsPrimarykey = false },
                    new ColumnMap { ColumnName = ColumnLogName, ColumnType = raw.SQLITE_TEXT, IsAutoIncrement = false, IsPrimarykey = false }
                };
                await _storageAdapter.InitializeStorageAsync().ConfigureAwait(false);
                await _storageAdapter.CreateTableAsync(TableName, scheme).ConfigureAwait(false);
            }
            catch (Exception e)
            {
                AppCenterLog.Error(AppCenterLog.LogTag, "An error occurred while initializing storage", e);
            }
        }

        /// <summary>
        /// Waits for any running storage operations to complete
        /// </summary>
        /// <param name="timeout">The maximum amount of time to wait for remaining tasks</param>
        /// <returns>True if remaining tasks completed in time; false otherwise</returns>
        public async Task WaitOperationsAsync(TimeSpan timeout)
        {
            var tokenSource = new CancellationTokenSource();
            try
            {
                var emptyQueueTask = AddTaskToQueue(() => { });
                var timeoutTask = Task.Delay(timeout, tokenSource.Token);
                await Task.WhenAny(emptyQueueTask, timeoutTask).ConfigureAwait(false);
            }
            finally
            {
                tokenSource.Cancel();
            }
        }

        /// <summary>
        /// Waits for any running storage operations to complete and prevents subsequent storage operations from running
        /// </summary>
        /// <param name="timeout">The maximum amount of time to wait for remaining tasks</param>
        /// <returns>True if remaining tasks completed in time; false otherwise</returns>
        public async Task<bool> ShutdownAsync(TimeSpan timeout)
        {
            _queue.CompleteAdding();
            _flushSemaphore.Release();
            var tokenSource = new CancellationTokenSource();
            try
            {
                var timeoutTask = Task.Delay(timeout, tokenSource.Token);
                return await Task.WhenAny(_queueFlushTask, timeoutTask).ConfigureAwait(false) != timeoutTask;
            }
            finally
            {
                tokenSource.Cancel();
            }
        }

        private static string GetFullIdentifier(string channelName, string identifier)
        {
            return channelName + DbIdentifierDelimiter + identifier;
        }

        private static bool ChannelMatchesIdentifier(string channelName, string identifier)
        {
            var lastDelimiterIndex = identifier.LastIndexOf(DbIdentifierDelimiter, StringComparison.Ordinal);
            return identifier.Substring(0, lastDelimiterIndex) == channelName;
        }

        private Task AddTaskToQueue(Action action)
        {
            var task = new Task(() =>
            {
                try
                {
                    action();
                }
                catch (Exception e)
                {
                    // If we use await on a task that returns an exception, it makes the app crash, however, using the awaiter works...
                    throw HandleStorageRelatedExceptionAsync(e).GetAwaiter().GetResult();
                }
            });
            AddTaskToQueue(task);
            return task;
        }

        private Task<T> AddTaskToQueue<T>(Func<T> action)
        {
            var task = new Task<T>(() =>
            {
                try
                {
                    return action();
                }
                catch (Exception e)
                {
                    // And regarding the comment in other variant of this function, async lambda + await cannot work with a Func anyway.
                    throw HandleStorageRelatedExceptionAsync(e).GetAwaiter().GetResult();
                }
            });
            AddTaskToQueue(task);
            return task;
        }

        private async Task<Exception> HandleStorageRelatedExceptionAsync(Exception e)
        {
            // Check if database is corrupted, we have evidence (https://github.com/microsoft/appcenter-sdk-dotnet/issues/1184)
            // that it's not always originated by a proper SQLiteException (which would then be converted to StorageException in StorageAdapter).
            // If it was always the right type then the exception would not have been unobserved in that application before we changed the re-throw logic here.
            // But the message is definitely "Corrupt" and thus unfortunately that is the only check we seem to be able to do as opposed to type/property checking.
            if (e.Message == "Corrupt" || e.InnerException?.Message == "Corrupt")
            {
                AppCenterLog.Error(AppCenterLog.LogTag, "Database corruption detected, deleting the file and starting fresh...", e);
                await _storageAdapter.DeleteDatabaseFileAsync().ConfigureAwait(false);
                await InitializeDatabaseAsync().ConfigureAwait(false);
            }

            // Return exception to re-throw.
            if (e is StorageException)
            {
                // This is the expected case, storage adapter already wraps exception as StorageException, so return as is.
                return e;
            }

            // Tasks should already be throwing only storage exceptions, but in case any are missed, 
            // which has happened (the Corrupt exception mentioned previously), catch them here and wrap in a storage exception. This will prevent 
            // the exception from being unobserved.
            return new StorageException(e);
        }

        private void AddTaskToQueue(Task task)
        {
            try
            {
                _queue.Add(task);
            }
            catch (InvalidOperationException)
            {
                throw new StorageException("The operation has been canceled");
            }
            _flushSemaphore.Release();
        }

        // Flushes the queue
        private async Task FlushQueueAsync()
        {
            while (true)
            {
                while (_queue.Count == 0)
                {
                    if (_queue.IsAddingCompleted)
                    {
                        return;
                    }
                    await _flushSemaphore.WaitAsync();
                }
                var t = _queue.Take();
                t.Start();
                try
                {
                    await t.ConfigureAwait(false);
                }
                catch
                {
                    // Can't throw exceptions here because it will cause the FlushQueue to stop
                    // processing, but if the task faults, the exception will be thrown again 
                    // because the original creator of this task will await it too.
                }
            }
        }

        /// <summary>
        /// Disposes the storage object
        /// </summary>
        public void Dispose()
        {
            _queue.CompleteAdding();
        }
    }
}
