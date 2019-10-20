﻿/* This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at http://mozilla.org/MPL/2.0/. */

using System;
using System.Collections.Generic;
using System.Collections.Concurrent;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

using Nito.AsyncEx;
using NLog;

using Butterfly.Db.Event;

using Dict = System.Collections.Generic.Dictionary<string, object>;

namespace Butterfly.Db.Dynamic {
    /// <summary>
    /// Represents a collection of <see cref="DynamicView"/> instances.  Often a
    /// <see cref="DynamicViewSet"/> instance will represent all the data that should be 
    /// replicated to a specific client.
    /// </summary>
    public class DynamicViewSet : IDisposable {
        private static readonly Logger logger = LogManager.GetCurrentClassLogger();

        protected readonly string id;
        protected readonly IDatabase database;

        protected readonly Action<DataEventTransaction> listener;
        protected readonly Func<DataEventTransaction, Task> asyncListener;

        protected readonly List<DynamicView> dynamicViews = new List<DynamicView>();
        protected readonly ConcurrentQueue<DataEventTransaction> incomingDataEventTransactions = new ConcurrentQueue<DataEventTransaction>();

        protected readonly CancellationTokenSource runCancellationTokenSource = new CancellationTokenSource();
        protected readonly AsyncMonitor monitor = new AsyncMonitor();

        protected readonly List<IDisposable> disposables = new List<IDisposable>();

        public DynamicViewSet(IDatabase database, Action<DataEventTransaction> listener) {
            this.id = Guid.NewGuid().ToString();
            this.database = database;
            this.listener = listener;
        }

        public DynamicViewSet(IDatabase database, Func<DataEventTransaction, Task> asyncListener) {
            this.id = Guid.NewGuid().ToString();
            this.database = database;
            this.asyncListener = asyncListener;
        }

        public string Id => this.id;

        public IDatabase Database => this.database;

        /// <summary>
        /// Creates an instance of a DynamicView. Must call <see cref="StartAsync"/> to send 
        /// initial <see cref="DataEventTransaction"/> instance and listen for new <see cref="DataEventTransaction"/>instances.
        /// </summary>
        public DynamicView CreateDynamicView(string sql, dynamic values = null, string name = null, string[] keyFieldNames = null, int limit = -1) {
            DynamicView dynamicQuery = new DynamicView(this, sql, values, name, keyFieldNames, limit: limit);
            this.dynamicViews.Add(dynamicQuery);
            return dynamicQuery;
        }

        protected bool isStarted = false;
        /// <summary>
        /// Send an initial <see cref="DataEventTransaction"/> to the registered listener and
        /// sends new <see cref="DataEventTransaction"/> instances when any data in the underlying
        /// <see cref="DynamicView"/> instances changes. Stops listening <see cref="Dispose"/> is called.
        /// </summary>
        /// <returns></returns>
        public async Task<DynamicViewSet> StartAsync() {
            logger.Debug("StartAsync");
            if (this.isStarted) throw new Exception("Dynamic Select Group is already started");
            if (this.runCancellationTokenSource.IsCancellationRequested) throw new Exception("Cannot restart a stopped DynamicViewSet");

            this.isStarted = true;

            DataEvent[] dataEvents = await this.RequeryDynamicViewsAsync(false);
            await this.SendToListenerAsync(new DataEventTransaction(DateTime.Now, dataEvents));

            var uncommittedTransactionDisposable = this.Database.OnNewUncommittedTransaction(this.ProcessUncommittedDataEventTransactionAsync);
            this.disposables.Add(uncommittedTransactionDisposable);

            var committedTransactionDisposable = this.Database.OnNewCommittedTransaction(this.ProcessCommittedDataEventTransactionAsync);
            this.disposables.Add(committedTransactionDisposable);

            Task backgroundTask = Task.Run(this.RunAsync);

            return this;
        }

        protected async Task ProcessUncommittedDataEventTransactionAsync(DataEventTransaction dataEventTransaction) {
            await this.StoreImpactedRecordsInDataEventTransaction(TransactionState.Uncommitted, dataEventTransaction);
        }

        protected async Task ProcessCommittedDataEventTransactionAsync(DataEventTransaction dataEventTransaction) {
            logger.Trace($"ProcessCommittedDataEventTransactionAsync():dataEventTransaction={dataEventTransaction}");
            await this.StoreImpactedRecordsInDataEventTransaction(TransactionState.Committed, dataEventTransaction);
            this.incomingDataEventTransactions.Enqueue(dataEventTransaction);
            this.monitor.PulseAll();
        }

        protected async Task StoreImpactedRecordsInDataEventTransaction(TransactionState transactionState, DataEventTransaction dataEventTransaction) {
            foreach (var dynamicView in this.dynamicViews) {
                foreach (var dataEvent in dataEventTransaction.dataEvents) {
                    if (dataEvent is KeyValueDataEvent keyValueDataEvent && dynamicView.TryGetDynamicStatementFromRef(keyValueDataEvent.name, out StatementFromRef dynamicStatementFromRef)) {
                        if (HasImpactedRecords(transactionState, keyValueDataEvent, dynamicStatementFromRef.joinType)) {
                            Dict[] impactedRecords = await dynamicView.GetImpactedRecordsAsync(keyValueDataEvent);
                            if (impactedRecords != null && impactedRecords.Length > 0) {
                                string storageKey = GetImpactedRecordsStorageKey(dynamicView, dataEvent, transactionState);
                                dataEventTransaction.Store(storageKey, impactedRecords);
                            }
                        }
                    }
                }
            }
        }

        protected string GetImpactedRecordsStorageKey(DynamicView dynamicView, DataEvent dataEvent, TransactionState transactionState) {
            return $"{dynamicView.Id} {dataEvent.id} {transactionState}";
        }

        protected bool HasImpactedRecords(TransactionState transactionState, DataEvent dataEvent, JoinType joinType) {
            if (joinType == JoinType.Inner || joinType == JoinType.None) {
                switch (transactionState) {
                    case TransactionState.Uncommitted:
                        return dataEvent.dataEventType == DataEventType.Update || dataEvent.dataEventType == DataEventType.Delete;
                    case TransactionState.Committed:
                        return dataEvent.dataEventType == DataEventType.Update || dataEvent.dataEventType == DataEventType.Insert;
                }
                return false;
            }
            else {
                return true;
            }
        }

        /// <summary>
        /// Processes queued data change transactions (runs on a background thread)
        /// </summary>
        /// <returns></returns>
        protected async Task RunAsync() {
            while (!this.runCancellationTokenSource.IsCancellationRequested) {
                try {
                    if (this.incomingDataEventTransactions.TryDequeue(out DataEventTransaction dataEventTransaction)) {
                        logger.Trace($"RunAsync():dataEventTransaction={dataEventTransaction}");

                        var newRecordDataEvents = this.dynamicViews.AsParallel().SelectMany(x => CreateDynamicViewDataEvents(dataEventTransaction, x)).ToList();
                        logger.Trace($"RunAsync():newRecordDataEvents={string.Join(",", newRecordDataEvents)}");

                        DataEvent[] initialDataEvents = await this.RequeryDynamicViewsAsync(true);
                        logger.Trace($"RunAsync():initialDataEvents={string.Join(",", initialDataEvents.ToList())}");
                        newRecordDataEvents.AddRange(initialDataEvents);

                        if (newRecordDataEvents.Count > 0) {
                            await this.SendToListenerAsync(new DataEventTransaction(dataEventTransaction.dateTime, newRecordDataEvents.ToArray()));
                        }
                    }
                    else {
                        using (var monitorWait = await this.monitor.EnterAsync(this.runCancellationTokenSource.Token)) {
                            await this.monitor.WaitAsync();
                        }
                    }
                }
                catch (Exception e) {
                    logger.Debug(e);
                    await Task.Delay(100);
                }
            }
        }

        protected DataEvent[] CreateDynamicViewDataEvents(DataEventTransaction dataEventTransaction, DynamicView dynamicView) {
            logger.Trace($"RunAsync():dynamicView.name={dynamicView.Name}");
            List<DataEvent> newRecordDataEvents = new List<DataEvent>();

            // Don't send data events if DynamicView has dirty params because
            // the DynamicView will be requeried anyways
            if (!dynamicView.HasDirtyParams) {
                HashSet<string> newRecordDataEventFullKeys = new HashSet<string>();
                foreach (var dataEvent in dataEventTransaction.dataEvents) {
                    if (dataEvent is KeyValueDataEvent keyValueDataEvent && dynamicView.TryGetDynamicStatementFromRef(keyValueDataEvent.name, out StatementFromRef dynamicStatementFromRef)) {
                        // Fetch the preCommitImpactedRecords
                        Dict[] preCommitImpactedRecords = null;
                        if (HasImpactedRecords(TransactionState.Uncommitted, dataEvent, dynamicStatementFromRef.joinType)) {
                            string storageKey = GetImpactedRecordsStorageKey(dynamicView, dataEvent, TransactionState.Uncommitted);
                            preCommitImpactedRecords = (Dict[])dataEventTransaction.Fetch(storageKey);
                        }

                        // Fetch the postCommitImpactedRecords
                        Dict[] postCommitImpactedRecords = null;
                        if (HasImpactedRecords(TransactionState.Committed, dataEvent, dynamicStatementFromRef.joinType)) {
                            string storageKey = GetImpactedRecordsStorageKey(dynamicView, dataEvent, TransactionState.Committed);
                            postCommitImpactedRecords = (Dict[])dataEventTransaction.Fetch(storageKey);
                        }

                        // Determine the changes from each data event on each dynamic select
                        RecordDataEvent[] recordDataEvents = dynamicView.ProcessDataChange(dataEvent, preCommitImpactedRecords, postCommitImpactedRecords);
                        if (recordDataEvents != null) {
                            dynamicView.UpdateChildDynamicParams(recordDataEvents);
                            foreach (var recordDataEvent in recordDataEvents) {
                                string fullKey = $"{recordDataEvent.name}:{recordDataEvent.keyValue}";
                                if (!newRecordDataEventFullKeys.Contains(fullKey)) {
                                    newRecordDataEventFullKeys.Add(fullKey);
                                    newRecordDataEvents.Add(recordDataEvent);
                                }
                            }
                        }
                    }
                }
            }

            return newRecordDataEvents.ToArray();
        }

        protected async Task SendToListenerAsync(DataEventTransaction dataEventTransaction) {
            if (logger.IsTraceEnabled) logger.Trace($"SendToListenerAsync():dataEventTransaction={dataEventTransaction}");
            else if (logger.IsDebugEnabled) logger.Debug($"SendToListenerAsync():dataEventTransaction.dataEvents.Length={dataEventTransaction.dataEvents.Length}");

            if (this.listener != null) {
                this.listener(dataEventTransaction);
            }
            if (this.asyncListener != null) {
                await asyncListener(dataEventTransaction);
            }
        }

        /// <summary>
        /// Return the initial query results if any of the query parameters have changed or if passed force=true
        /// </summary>
        /// <param name="onlyIfDirtyParams"></param>
        /// <returns></returns>
        protected async Task<DataEvent[]> RequeryDynamicViewsAsync(bool onlyIfDirtyParams) {
            logger.Debug($"RequeryDynamicViewsAsync():id={this.Id},onlyIfDirtyParams={onlyIfDirtyParams}");
            List<Task<DataEvent[]>> tasks = new List<Task<DataEvent[]>>();
            foreach (var dynamicView in this.dynamicViews) {
                if (!onlyIfDirtyParams || dynamicView.HasDirtyParams) {
                    tasks.Add(GetInitialDataEventsAsync(dynamicView));
                }
            }
            List<DataEvent> dataEvents = (await Task.WhenAll(tasks)).SelectMany(x => x).ToList();
            bool hasInitialBegin = dataEvents.Any(x => x.dataEventType==DataEventType.InitialBegin);
            if (hasInitialBegin) {
                dataEvents.Add(new InitialEndDataEvent());
            }
            return dataEvents.ToArray();
        }

        protected async Task<DataEvent[]> GetInitialDataEventsAsync(DynamicView dynamicView) {
            DataEvent[] initialDataEvents = await dynamicView.GetInitialDataEventsAsync();
            dynamicView.ResetDirtyParams();
            dynamicView.UpdateChildDynamicParams(initialDataEvents);
            return initialDataEvents;
        }

        public void Dispose() {
            logger.Debug($"Dispose():id={this.Id}");
            foreach (var disposable in this.disposables) {
                disposable.Dispose();
            }
            this.runCancellationTokenSource.Cancel();
            this.runCancellationTokenSource.Dispose();
        }

    }
}
