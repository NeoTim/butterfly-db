﻿/* This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at http://mozilla.org/MPL/2.0/. */

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

using NLog;

using Butterfly.Db.Dynamic;
using Butterfly.Db.Event;
using Butterfly.Util;

using Dict = System.Collections.Generic.Dictionary<string, object>;

namespace Butterfly.Db {

    /// <inheritdoc/>
    /// <summary>
    /// Base class implementing <see cref="IDatabase"/>. New implementations will normally extend this class.
    /// </summary>
    public abstract class BaseDatabase : IDatabase {
        private static readonly Logger logger = LogManager.GetCurrentClassLogger();

        protected readonly Dictionary<string, Table> tableByName = new Dictionary<string, Table>();

        protected static readonly Regex SQL_COMMENT = new Regex(@"^\-\-(.*)$", RegexOptions.Multiline);

        protected BaseDatabase(string connectionString) {
            this.ConnectionString = connectionString;
            this.LoadSchemaAsync().Wait();
        }

        /// <summary>
        /// Gets or sets the connection string
        /// </summary>
        /// <value>
        /// The connection string
        /// </value>
        public string ConnectionString {
            get;
            protected set;
        }

        public Dictionary<string, Table> TableByName => this.tableByName;

        public abstract bool CanJoin { get; }

        public abstract bool CanFieldAlias { get; }

        public int SelectCount {
            get;
            internal set;
        }

        public int TransactionCount {
            get;
            internal set;
        }

        public int InsertCount {
            get;
            internal set;
        }

        public int UpdateCount {
            get;
            internal set;
        }

        public int DeleteCount {
            get;
            internal set;
        }


        protected abstract Task LoadSchemaAsync();

        protected abstract Task<Table> LoadTableSchemaAsync(string tableName);

        // Manage data event transaction listeners
        protected readonly List<DataEventTransactionListener> uncommittedTransactionListeners = new List<DataEventTransactionListener>();
        public IDisposable OnNewUncommittedTransaction(Action<DataEventTransaction> listener) => new ListItemDisposable<DataEventTransactionListener>(uncommittedTransactionListeners, new DataEventTransactionListener(listener));
        public IDisposable OnNewUncommittedTransaction(Func<DataEventTransaction, Task> listener) => new ListItemDisposable<DataEventTransactionListener>(uncommittedTransactionListeners, new DataEventTransactionListener(listener));

        protected readonly List<DataEventTransactionListener> committedTransactionListeners = new List<DataEventTransactionListener>();
        public IDisposable OnNewCommittedTransaction(Action<DataEventTransaction> listener) => new ListItemDisposable<DataEventTransactionListener>(committedTransactionListeners, new DataEventTransactionListener(listener));
        public IDisposable OnNewCommittedTransaction(Func<DataEventTransaction, Task> listener) => new ListItemDisposable<DataEventTransactionListener>(committedTransactionListeners, new DataEventTransactionListener(listener));

        internal void PostDataEventTransaction(TransactionState transactionState, DataEventTransaction dataEventTransaction) {
            // Use ToArray() to avoid the collection being modified during the loop
            DataEventTransactionListener[] listeners = transactionState == TransactionState.Uncommitted ? this.uncommittedTransactionListeners.ToArray() : this.committedTransactionListeners.ToArray();

            listeners.Where(x => x.listener != null).AsParallel().ForAll(x => x.listener(dataEventTransaction));
            Task[] tasks = listeners.Where(x => x.listenerAsync != null).Select(x => x.listenerAsync(dataEventTransaction)).ToArray();
            Task.WaitAll(tasks.ToArray());
        }

        internal async Task PostDataEventTransactionAsync(TransactionState transactionState, DataEventTransaction dataEventTransaction) {
            // Use ToArray() to avoid the collection being modified during the loop
            DataEventTransactionListener[] listeners = transactionState == TransactionState.Uncommitted ? this.uncommittedTransactionListeners.ToArray() : this.committedTransactionListeners.ToArray();

            listeners.Where(x => x!=null && x.listener != null).AsParallel().ForAll(x => x.listener(dataEventTransaction));
            Task[] tasks = listeners.Where(x => x!=null && x.listenerAsync != null).Select(x => x.listenerAsync(dataEventTransaction)).ToArray();
            await Task.WhenAll(tasks.ToArray());
        }

        internal async Task<DataEventTransaction> GetInitialDataEventTransactionAsync(string statementSql, dynamic statementParams = null) {
            SelectStatement statement = new SelectStatement(this, statementSql);
            DataEvent[] initialDataEvents = await this.GetInitialDataEventsAsync(statement.StatementFromRefs[0].table.Name, statement.StatementFromRefs[0].table.Indexes[0].FieldNames, statement, statementParams);
            return new DataEventTransaction(DateTime.Now, initialDataEvents);
        }

        public async Task<DataEvent[]> GetInitialDataEventsAsync(string dataEventName, string[] keyFieldNames, SelectStatement selectStatement, dynamic statementParams = null) {
            logger.Trace($"GetInitialDataEvents():sql={selectStatement.Sql}");

            List<DataEvent> dataEvents = new List<DataEvent>();
            dataEvents.Add(new InitialBeginDataEvent(dataEventName, keyFieldNames));

            Dict[] rows = await this.SelectRowsAsync(selectStatement, statementParams);
            RecordDataEvent[] changeDataEvents = rows.Select(x => new RecordDataEvent(DataEventType.Initial, dataEventName, x.GetKeyValue(keyFieldNames), x)).ToArray();
            dataEvents.AddRange(changeDataEvents);

            return dataEvents.ToArray();
        }

        public async Task<T> SelectValueAsync<T>(string sql, dynamic vars = null, T defaultValue = default(T)) {
            Dict row = await this.SelectRowAsync(sql, vars);
            if (row == null) return defaultValue;
            else return row.GetAs(row.Keys.First(), defaultValue);

            //if (row == null || !row.TryGetValue(row.Keys.First(), out object value) || value==null) return defaultValue;
            //return (T)Convert.ChangeType(value, typeof(T));
        }

        public async Task<T[]> SelectValuesAsync<T>(string sql, dynamic vars = null) {
            Dict[] rows = await this.SelectRowsAsync(sql, vars);
            return rows.Select(row => {
                return row.GetAs(row.Keys.First(), default(T));
            }).ToArray();
        }

        public async Task<Dict> SelectRowAsync(string statementSql, dynamic vars = null) {
            SelectStatement statement = new SelectStatement(this, statementSql, limit: 1);
            Dict[] rows = await this.SelectRowsAsync(statement, vars: vars);
            if (rows.Length == 0) return null;
            else if (rows.Length > 1) throw new Exception("SelectRow returned more than one row");
            return rows.First();
        }

        public async Task<Dict[]> SelectRowsAsync(string statementSql, dynamic vars = null) {
            SelectStatement statement = new SelectStatement(this, statementSql);
            return await this.SelectRowsAsync(statement, vars);
        }

        public Task<Dict[]> SelectRowsAsync(SelectStatement statement, dynamic vars) {
            Dict varsDict = statement.ConvertParamsToDict(vars);
            (string executableSql, Dict executableParams) = statement.GetExecutableSqlAndParams(varsDict);
            this.SelectCount++;
            return this.DoSelectRowsAsync(executableSql, executableParams, statement.limit);
        }

        protected abstract Task<Dict[]> DoSelectRowsAsync(string executableSql, Dict executableParams, int limit);

        public async Task<T> QueryValueAsync<T>(string storedProcedureName, dynamic vars = null, T defaultValue = default(T)) {
            Dict row = await this.QueryRowAsync(storedProcedureName, vars);
            if (row == null || !row.TryGetValue(row.Keys.First(), out object value) || value == null) return defaultValue;

            return (T)Convert.ChangeType(value, typeof(T));
        }

        public async Task<Dict> QueryRowAsync(string storedProcedureName, dynamic vars = null) {
            Dict[] rows = await this.QueryRowsAsync(storedProcedureName, vars);
            if (rows.Length == 0) return null;
            else if (rows.Length > 1) throw new Exception("QueryRow returned more than one row");
            return rows.First();
        }

        public Task<Dict[]> QueryRowsAsync(string storedProcedureName, dynamic vars = null) {
            Dict executableParams;

            // If statementParams is null, return empty dictionary
            if (vars == null) {
                executableParams = new Dict();
            }

            // If statementParams is already a dictionary, return the dictionary
            else if (vars is Dict d) {
                executableParams = new Dict(d);
            }

            // Otherwise, convert statementParams to a dictionary
            else {
                executableParams = DynamicX.ToDictionary(vars);
            }

            return this.DoQueryRowsAsync(storedProcedureName, executableParams);
        }

        protected abstract Task<Dict[]> DoQueryRowsAsync(string storedProcedureName, Dict executableParams);

        public async Task<T> InsertAndCommitAsync<T>(string insertStatement, dynamic vars, bool ignoreIfDuplicate = false) {
            T result;
            using (var transaction = await this.BeginTransactionAsync()) {
                result = await transaction.InsertAsync<T>(insertStatement, vars, ignoreIfDuplicate);
                await transaction.CommitAsync();
            }
            return result;
        }

        public async Task<int> UpdateAndCommitAsync(string updateStatement, dynamic vars) {
            int count;
            using (var transaction = await this.BeginTransactionAsync()) {
                count = await transaction.UpdateAsync(updateStatement, vars);
                await transaction.CommitAsync();
            }
            return count;
        }

        public async Task<int> DeleteAndCommitAsync(string deleteStatement, dynamic vars) {
            int count;
            using (var transaction = await this.BeginTransactionAsync()) {
                count = await transaction.DeleteAsync(deleteStatement, vars);
                await transaction.CommitAsync();
            }
            return count;
        }

        public ITransaction BeginTransaction() {
            var transaction = this.CreateTransaction();
            transaction.Begin();
            return transaction;
        }

        public async Task<ITransaction> BeginTransactionAsync() {
            var transaction = this.CreateTransaction();
            await transaction.BeginAsync();
            return transaction;
        }

        protected abstract BaseTransaction CreateTransaction();

        protected readonly Dictionary<string, Func<string, object>> getDefaultValueByFieldName = new Dictionary<string, Func<string, object>>();

        public void SetDefaultValue(string fieldName, Func<string, object> getValue, string tableName = null) {
            if (tableName == null) {
                this.getDefaultValueByFieldName[fieldName] = getValue;
            }
            else {
                if (!this.TableByName.TryGetValue(tableName, out Table table)) throw new Exception($"Invalid table name '{tableName}'");
                table.SetDefaultValue(fieldName, getValue);
            }
        }

        internal Dict GetDefaultValues(Table table) {
            Dictionary<string, object> defaultValues = new Dict();
            foreach ((string fieldName, Func<string, object> getDefaultValue) in table.GetDefaultValueByFieldName) {
                TableFieldDef fieldDef = table.FindFieldDef(fieldName);
                if (fieldDef!=null && !defaultValues.ContainsKey(fieldDef.name)) defaultValues[fieldDef.name] = getDefaultValue(table.Name);
            }
            foreach ((string fieldName, Func<string, object> getDefaultValue) in this.getDefaultValueByFieldName) {
                TableFieldDef fieldDef = table.FindFieldDef(fieldName);
                if (fieldDef != null && !defaultValues.ContainsKey(fieldDef.name)) defaultValues[fieldDef.name] = getDefaultValue(table.Name);
            }
            return defaultValues;
        }

        protected readonly Dictionary<string, Func<string, object>> getOverrideValueByFieldName = new Dictionary<string, Func<string, object>>();

        public void SetOverrideValue(string fieldName, Func<string, object> getValue, string tableName = null) {
            if (tableName == null) {
                this.getOverrideValueByFieldName[fieldName] = getValue;
            }
            else {
                if (!this.TableByName.TryGetValue(tableName, out Table table)) throw new Exception($"Invalid table name '{tableName}'");
                table.SetOverrideValue(fieldName, getValue);
            }
        }

        internal Dict GetOverrideValues(Table table) {
            Dictionary<string, object> overrideValues = new Dict();
            foreach ((string fieldName, Func<string, object> getValue) in table.GetOverrideValueByFieldName) {
                TableFieldDef fieldDef = table.FindFieldDef(fieldName);
                if (fieldDef != null && !overrideValues.ContainsKey(fieldDef.name)) overrideValues[fieldDef.name] = getValue(table.Name);
            }
            foreach ((string fieldName, Func<string, object> getValue) in this.getOverrideValueByFieldName) {
                TableFieldDef fieldDef = table.FindFieldDef(fieldName);
                if (fieldDef != null && !overrideValues.ContainsKey(fieldDef.name)) overrideValues[fieldDef.name] = getValue(table.Name);
            }
            return overrideValues;
        }

        protected readonly List<Action<string, Dict>> inputPreprocessors = new List<Action<string, Dict>>();

        public void AddInputPreprocessor(Action<string, Dict> inputPreprocessor) {
            this.inputPreprocessors.Add(inputPreprocessor);
        }

        internal void PreprocessInput(string tableName, Dict input) {
            foreach (var inputPreprocessor in this.inputPreprocessors) {
                inputPreprocessor(tableName, input);
            }
        }

        public static Action<string, Dict> RemapTypeInputPreprocessor<T>(Func<T, object> convert) {
            return (tableName, input) => {
                foreach (var pair in input.ToArray()) {
                    if (pair.Value is T) {
                        input[pair.Key] = convert((T)pair.Value);
                    }
                }
            };
        }

        public static Action<string, Dict> CopyFieldValueInputPreprocessor(string token, string sourceFieldName) {
            return (tableName, input) => {
                foreach (var pair in input.ToArray()) {
                    string stringValue = pair.Value as string;
                    if (stringValue == token) {
                        input[pair.Key] = input[sourceFieldName];
                    }
                }
            };
        }

        public DynamicViewSet CreateDynamicViewSet(Action<DataEventTransaction> listener) {
            return new DynamicViewSet(this, listener);
        }

        public DynamicViewSet CreateDynamicViewSet(Func<DataEventTransaction, Task> asyncListener) {
            return new DynamicViewSet(this, asyncListener);
        }

        public async Task<DynamicViewSet> CreateAndStartDynamicViewAsync(string sql, Action<DataEventTransaction> listener, dynamic values = null, string name = null, string[] keyFieldNames = null) {
            var dynamicViewSet = this.CreateDynamicViewSet(listener);
            dynamicViewSet.CreateDynamicView(sql, values, name, keyFieldNames);
            return await dynamicViewSet.StartAsync();
        }

        public async Task<DynamicViewSet> CreateAndStartDynamicViewAsync(string sql, Func<DataEventTransaction, Task> asyncListener, dynamic values = null, string name = null, string[] keyFieldNames = null) {
            var dynamicViewSet = this.CreateDynamicViewSet(asyncListener);
            dynamicViewSet.CreateDynamicView(sql, values, name, keyFieldNames);
            return await dynamicViewSet.StartAsync();
        }

        protected readonly List<ForeignKey> foreignKeys = new List<ForeignKey>();

        public void RegisterForeignKey(string childTableName, string childFieldNames, string parentTableName, string parentFieldNames, params ForeignKeyRule[] foreignKeyRules) {
            if (!this.tableByName.TryGetValue(childTableName, out Table childTable)) throw new Exception($"Unknown child table {childTableName}");
            var _childFieldNames = childFieldNames.Split(',');
            foreach (var fieldName in _childFieldNames) {
                if (childTable.FindFieldDef(fieldName) == null) throw new Exception($"Invalid child field name {fieldName}");
            }

            if (!this.tableByName.TryGetValue(parentTableName, out Table parentTable)) throw new Exception($"Unknown parent table {parentTableName}");
            var _parentFieldNames = parentFieldNames.Split(',');
            foreach (var fieldName in _parentFieldNames) {
                if (parentTable.FindFieldDef(fieldName) == null) throw new Exception($"Invalid parent field name {fieldName}");
            }

            if (_childFieldNames.Length != _parentFieldNames.Length) throw new Exception("Must have same number of child and parent field names");

            foreach (var foreignKeyRule in foreignKeyRules) {
                this.foreignKeys.Add(new ForeignKey(childTable, _childFieldNames, parentTable, _parentFieldNames, foreignKeyRule));
            }
        }

        internal ForeignKey[] GetForeignKeys(string parentTableName, params ForeignKeyRule[] foreignKeyRules) {
            return this.foreignKeys.Where(x => x.parentTable.Name == parentTableName && foreignKeyRules.Contains(x.foreignKeyRule)).ToArray();
        }
    }

    public class DatabaseException : Exception {
        public DatabaseException(string message) : base(message) {
        }
    }

    public class DuplicateKeyDatabaseException : DatabaseException {
        public DuplicateKeyDatabaseException(string message) : base(message) {
        }
    }

    public class UnableToConnectDatabaseException : DatabaseException {
        public UnableToConnectDatabaseException(string message) : base(message) {
        }
    }

    public class ForeignKey {
        public readonly Table childTable;
        public readonly string[] childFieldNames;

        public readonly Table parentTable;
        public readonly string[] parentFieldNames;

        public readonly ForeignKeyRule foreignKeyRule;

        public ForeignKey(Table childTable, string[] childFieldNames, Table parentTable, string[] parentFieldNames, ForeignKeyRule foreignKeyRule) {
            this.childTable = childTable;
            this.childFieldNames = childFieldNames;

            this.parentTable = parentTable;
            this.parentFieldNames = parentFieldNames;

            this.foreignKeyRule = foreignKeyRule;
        }
    }
}
